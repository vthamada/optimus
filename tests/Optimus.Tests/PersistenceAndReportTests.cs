using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Optimus.Application;
using Optimus.Domain;
using Optimus.Infrastructure;

namespace Optimus.Tests;

public sealed class PersistenceAndReportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "optimus-tests", Guid.NewGuid().ToString("N"));
    public PersistenceAndReportTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task DatabaseRoundTripPreservesEvidenceIdentityUnicodeAndCallerPaths()
    {
        const string path = @"C:\Usuários\João\password.txt";
        var evidence = Samples.Evidence() with { Facts = [new("selectedPath", "Arquivo selecionado", path)] };
        var session = Samples.Session(evidence);
        var store = new SqliteSessionStore(Path.Combine(_root, "sessions.db"));

        Assert.True((await store.SaveAsync(session, CancellationToken.None)).IsSuccess);
        var restored = await store.GetAsync(session.Id, CancellationToken.None);

        Assert.True(restored.IsSuccess);
        Assert.Equal(evidence.Id, Assert.Single(restored.Value!.Evidence).Id);
        Assert.Equal(path, Assert.Single(restored.Value.Evidence[0].Facts).TextValue);
        Assert.Equal(session.Id, Assert.Single((await store.ListAsync(CancellationToken.None)).Value!).Id);
    }

    [Fact]
    public async Task SavingDuplicateSessionDoesNotReplacePreviousEvidence()
    {
        var store = new SqliteSessionStore(Path.Combine(_root, "sessions.db"));
        var session = Samples.Session();
        Assert.True((await store.SaveAsync(session, CancellationToken.None)).IsSuccess);

        var duplicate = await store.SaveAsync(session with { Evidence = [Samples.Evidence(1000, 0)] }, CancellationToken.None);

        Assert.False(duplicate.IsSuccess);
        var restored = (await store.GetAsync(session.Id, CancellationToken.None)).Value!;
        Assert.Equal(session.Evidence[0].Id, restored.Evidence[0].Id);
    }

    [Fact]
    public async Task ForeignDatabaseIsRejectedWithoutAdoptingOrChangingItsBytes()
    {
        var path = Path.Combine(_root, "foreign.db");
        using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE important (value TEXT); INSERT INTO important VALUES ('preserve me'); PRAGMA user_version = 17;";
            command.ExecuteNonQuery();
        }
        var before = SHA256.HashData(await File.ReadAllBytesAsync(path));

        var result = await new SqliteSessionStore(path).ListAsync(CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCode.UnrecognizedDatabase, result.Error!.Code);
        Assert.Equal(before, SHA256.HashData(await File.ReadAllBytesAsync(path)));
    }

    [Fact]
    public async Task FutureOwnedDatabaseVersionIsPreservedAndRejected()
    {
        var path = Path.Combine(_root, "sessions.db");
        var store = new SqliteSessionStore(path);
        Assert.True((await store.SaveAsync(Samples.Session(), CancellationToken.None)).IsSuccess);
        using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version = 99";
            command.ExecuteNonQuery();
        }

        var result = await store.ListAsync(CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCode.UnsupportedSchema, result.Error!.Code);
    }

    [Fact]
    public async Task SessionWithDanglingEvidenceReferenceIsNotStored()
    {
        var session = Samples.Session() with
        {
            Findings = [new("bad-reference", FindingSeverity.Warning, "Bad", "Bad", "Bad", [Guid.NewGuid()])]
        };

        var result = await new SqliteSessionStore(Path.Combine(_root, "sessions.db")).SaveAsync(session, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCode.InvalidData, result.Error!.Code);
        Assert.False(File.Exists(Path.Combine(_root, "sessions.db")));
    }

    [Fact]
    public async Task OversizedEvidenceFailsBeforeCreatingTheDatabase()
    {
        var evidence = Samples.Evidence() with
        {
            Facts = [new("large", "Large", new string('x', SqliteSessionStore.MaximumSessionBytes + 1))]
        };

        var result = await new SqliteSessionStore(Path.Combine(_root, "sessions.db")).SaveAsync(Samples.Session(evidence), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCode.InvalidData, result.Error!.Code);
        Assert.False(File.Exists(Path.Combine(_root, "sessions.db")));
    }

    [Theory]
    [InlineData("evidence", "[null]")]
    [InlineData("findings", "[null]")]
    public async Task MalformedStoredCollectionsReturnInvalidDataInsteadOfEscapingTheContract(string property, string replacement)
    {
        var path = Path.Combine(_root, "sessions.db");
        var session = Samples.Session();
        var store = new SqliteSessionStore(path);
        Assert.True((await store.SaveAsync(session, CancellationToken.None)).IsSuccess);
        var payload = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(session, DiagnosticJson.Options))!;
        payload[property] = System.Text.Json.Nodes.JsonNode.Parse(replacement);
        using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE sessions SET payload = $payload WHERE id = $id";
            command.Parameters.AddWithValue("$payload", payload.ToJsonString());
            command.Parameters.AddWithValue("$id", session.Id.ToString());
            command.ExecuteNonQuery();
        }

        var result = await store.GetAsync(session.Id, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCode.InvalidData, result.Error!.Code);
    }

    [Fact]
    public async Task StoredPayloadAboveLimitIsRejectedAtTheSqliteBoundary()
    {
        var path = Path.Combine(_root, "sessions.db");
        var session = Samples.Session();
        var store = new SqliteSessionStore(path);
        Assert.True((await store.SaveAsync(session, CancellationToken.None)).IsSuccess);
        using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE sessions SET payload = $payload";
            command.Parameters.AddWithValue("$payload", "{\"large\":\"" + new string('x', SqliteSessionStore.MaximumSessionBytes) + "\"}");
            command.ExecuteNonQuery();
        }

        var result = await store.GetAsync(session.Id, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCode.InvalidData, result.Error!.Code);
    }

    [Fact]
    public async Task CancelledFirstAccessDoesNotReserveAnUnrecognizedDatabase()
    {
        var path = Path.Combine(_root, "sessions.db");
        var store = new SqliteSessionStore(path);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveAsync(Samples.Session(), cancellation.Token));

        Assert.True((await store.SaveAsync(Samples.Session(), CancellationToken.None)).IsSuccess);
        Assert.Empty(Directory.GetFiles(_root, ".optimus-db-*.tmp"));
    }

    [Fact]
    public async Task DatabaseLockDoesNotBlockTheCallingThread()
    {
        var path = Path.Combine(_root, "sessions.db");
        var store = new SqliteSessionStore(path);
        Assert.True((await store.SaveAsync(Samples.Session(), CancellationToken.None)).IsSuccess);
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "BEGIN EXCLUSIVE";
        command.ExecuteNonQuery();

        var timer = System.Diagnostics.Stopwatch.StartNew();
        var listing = store.ListAsync(CancellationToken.None);
        timer.Stop();
        command.CommandText = "ROLLBACK";
        command.ExecuteNonQuery();

        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(1), $"The call blocked for {timer.Elapsed}.");
        Assert.True((await listing).IsSuccess);
    }

    [Theory]
    [InlineData("oops")]
    [InlineData(-1L)]
    [InlineData(long.MaxValue)]
    public async Task LegacyOrCorruptMetadataCannotBecomeZeroOrEscapeTheResultContract(object count)
    {
        var path = Path.Combine(_root, "sessions.db");
        var store = new SqliteSessionStore(path);
        Assert.True((await store.SaveAsync(Samples.Session(), CancellationToken.None)).IsSuccess);
        using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                ALTER TABLE sessions RENAME TO preserved;
                CREATE TABLE sessions (id TEXT, started_at TEXT, status TEXT, finding_count, payload TEXT);
                INSERT INTO sessions SELECT id, started_at, status, $count, payload FROM preserved;
                """;
            command.Parameters.AddWithValue("$count", count);
            command.ExecuteNonQuery();
        }

        var result = await store.ListAsync(CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCode.InvalidData, result.Error!.Code);
    }

    [Fact]
    public async Task HtmlReportEscapesSourceTextAndIncludesCoverage()
    {
        const string payload = "<script>alert('x')</script> & C:\\João\\password.txt";
        var evidence = Samples.Evidence() with { Facts = [new("name", "Nome", payload)] };
        var path = Path.Combine(_root, "report.html");

        var result = await new ReportExporter().ExportAsync(Samples.Session(evidence), path, ReportFormat.Html, CancellationToken.None);

        Assert.True(result.IsSuccess);
        var html = await File.ReadAllTextAsync(path);
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.Contains(evidence.Coverage.Scope, html);
        Assert.Contains(evidence.Id.ToString(), html);
    }

    [Fact]
    public async Task JsonReportPreservesOriginalFactsAndReferences()
    {
        var session = Samples.Session(Samples.Evidence(1000, 1));
        var path = Path.Combine(_root, "report.json");

        Assert.True((await new ReportExporter().ExportAsync(session, path, ReportFormat.Json, CancellationToken.None)).IsSuccess);
        var restored = JsonSerializer.Deserialize<DiagnosticSession>(await File.ReadAllTextAsync(path), DiagnosticJson.Options)!;

        Assert.Equal(session.Evidence[0].Id, restored.Evidence[0].Id);
        Assert.Equal(restored.Evidence[0].Id, Assert.Single(Assert.Single(restored.Findings).EvidenceIds));
        Assert.Equal(1, restored.Evidence[0].Facts.Single(fact => fact.Key == "availableBytes").NumberValue);
    }

    [Fact]
    public async Task ExistingReportIsNeverOverwritten()
    {
        var path = Path.Combine(_root, "existing.html");
        await File.WriteAllTextAsync(path, "preserve existing report");

        var result = await new ReportExporter().ExportAsync(Samples.Session(), path, ReportFormat.Html, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCode.OutputExists, result.Error!.Code);
        Assert.Equal("preserve existing report", await File.ReadAllTextAsync(path));
        Assert.Empty(Directory.GetFiles(_root, ".optimus-*.tmp"));
    }

    [Fact]
    public async Task CancelledExportDoesNotPublishAnyFile()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var path = Path.Combine(_root, "cancelled.html");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new ReportExporter().ExportAsync(Samples.Session(), path, ReportFormat.Html, cancellation.Token));

        Assert.Empty(Directory.GetFiles(_root));
    }

    public void Dispose()
    {
        // This exact random directory is created and owned by this fixture.
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
