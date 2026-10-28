using System.Text;
using Optimus.Domain;
using Optimus.Infrastructure.Files;

namespace Optimus.Tests;

public sealed class AtomicPublicationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "optimus-atomic-tests", Guid.NewGuid().ToString("N"));
    public AtomicPublicationTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task UnicodeAndLiteralQuotesArePublishedWithoutChangingExistingFiles()
    {
        var path = Path.Combine(_directory, "relatório d'água Ω.json");
        const string content = "{\"nome\":\"João\",\"literal\":\"C:\\\\a\\\\b\",\"aspas\":\"' < > &\"}";

        var result = await AtomicNewFileWriter.PublishUtf8Async(path, content, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(path, result.Value);
        Assert.Equal(content, await File.ReadAllTextAsync(path, Encoding.UTF8));
        Assert.Empty(Directory.GetFiles(_directory, ".optimus-atomic-*.tmp"));
    }

    [Fact]
    public async Task ExistingDestinationIsPreservedWithoutOpeningAStagingFile()
    {
        var path = Path.Combine(_directory, "existing.txt");
        await File.WriteAllTextAsync(path, "existing source");

        var result = await AtomicNewFileWriter.PublishUtf8Async(path, "replacement", CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCode.OutputExists, result.Error!.Code);
        Assert.Equal("existing source", await File.ReadAllTextAsync(path));
        Assert.Empty(Directory.GetFiles(_directory, ".optimus-atomic-*.tmp"));
    }

    [Fact]
    public async Task HeldStagingHandleBlocksSubstitutionAndWritesFromOtherHandles()
    {
        var path = Path.Combine(_directory, "published.txt");
        bool renameBlocked = false;
        bool writeBlocked = false;
        var observer = new InlineProgress(value =>
        {
            if (value.Stage != AtomicPublicationStage.Reserved) return;
            try { File.Move(value.StagingPath, value.StagingPath + ".replacement"); }
            catch (IOException) { renameBlocked = true; }
            catch (UnauthorizedAccessException) { renameBlocked = true; }
            try { using var stream = new FileStream(value.StagingPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete); }
            catch (IOException) { writeBlocked = true; }
            catch (UnauthorizedAccessException) { writeBlocked = true; }
        });

        var result = await AtomicNewFileWriter.PublishUtf8Async(path, "owned original", CancellationToken.None, progress: observer);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.True(renameBlocked);
        Assert.True(writeBlocked);
        Assert.Equal("owned original", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task DestinationCreatedAfterFlushCannotBeOverwrittenByPublication()
    {
        var path = Path.Combine(_directory, "race.txt");
        var observer = new InlineProgress(value =>
        {
            if (value.Stage == AtomicPublicationStage.Flushed) File.WriteAllText(path, "competing destination");
        });

        var result = await AtomicNewFileWriter.PublishUtf8Async(path, "owned source", CancellationToken.None, progress: observer);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCode.OutputExists, result.Error!.Code);
        Assert.Equal("competing destination", await File.ReadAllTextAsync(path));
        Assert.Empty(Directory.GetFiles(_directory, ".optimus-atomic-*.tmp"));
    }

    [Fact]
    public async Task CancellationAfterFlushDeletesOnlyTheStillOwnedHandle()
    {
        using var cancellation = new CancellationTokenSource();
        var path = Path.Combine(_directory, "cancelled.txt");
        var observer = new InlineProgress(value =>
        {
            if (value.Stage == AtomicPublicationStage.Flushed) cancellation.Cancel();
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            AtomicNewFileWriter.PublishUtf8Async(path, "owned source", cancellation.Token, progress: observer));

        Assert.Empty(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task CancellationAfterRenameRetainsTheObservedPublishedResult()
    {
        using var cancellation = new CancellationTokenSource();
        var path = Path.Combine(_directory, "published-before-cancel.txt");
        var observer = new InlineProgress(value =>
        {
            if (value.Stage == AtomicPublicationStage.Published) cancellation.Cancel();
        });

        var result = await AtomicNewFileWriter.PublishUtf8Async(path, "already published", cancellation.Token, progress: observer);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCode.Cancelled, result.Error!.Code);
        Assert.Equal(path, result.Value);
        Assert.Equal("already published", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task PrimaryPublicationConflictAndCleanupFailureRemainTogetherUntilHandleRetry()
    {
        var path = Path.Combine(_directory, "foreign.txt");
        string? staging = null;
        string? cleanupId = null;
        var observer = new InlineProgress(value =>
        {
            if (value.Stage != AtomicPublicationStage.Flushed) return;
            staging = value.StagingPath;
            File.WriteAllText(path, "foreign preserved");
            File.SetAttributes(staging, FileAttributes.ReadOnly);
        });
        try
        {
            var result = await AtomicNewFileWriter.PublishUtf8Async(path, "own temporary", CancellationToken.None, progress: observer);
            cleanupId = result.Error?.PendingCleanupPaths?.Single(item => item.StartsWith("atomic-file:", StringComparison.Ordinal));

            Assert.False(result.IsSuccess);
            Assert.Equal(ErrorCode.OutputExists, result.Error!.Code);
            Assert.Contains("destino", result.Error.Message);
            Assert.Contains("Limpeza própria pendente", result.Error.Message);
            Assert.NotNull(cleanupId);
            Assert.Contains(staging!, result.Error.PendingCleanupPaths!);
            Assert.Equal("foreign preserved", await File.ReadAllTextAsync(path));

            File.SetAttributes(staging!, FileAttributes.Normal);
            Assert.True((await AtomicNewFileWriter.RetryCleanupAsync(cleanupId!, CancellationToken.None)).IsSuccess);
            Assert.False(File.Exists(staging));
            Assert.Equal("foreign preserved", await File.ReadAllTextAsync(path));
        }
        finally
        {
            if (staging is not null && File.Exists(staging)) File.SetAttributes(staging, FileAttributes.Normal);
            if (cleanupId is not null) await AtomicNewFileWriter.RetryCleanupAsync(cleanupId, CancellationToken.None);
        }
    }

    [Fact]
    public async Task Utf8ExpansionIsBoundedBeforeAFileIsReserved()
    {
        var result = await AtomicNewFileWriter.PublishUtf8Async(Path.Combine(_directory, "too-large.txt"), "éé", CancellationToken.None, maximumBytes: 3);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCode.InvalidData, result.Error!.Code);
        Assert.Empty(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task UnsupportedDoubleQuoteInWindowsFilenameFailsWithoutNormalizingTheSelection()
    {
        var result = await AtomicNewFileWriter.PublishUtf8Async(Path.Combine(_directory, "invalid\"name.txt"), "data", CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCode.InvalidData, result.Error!.Code);
        Assert.Empty(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task HeldAncestorHandlesBlockDirectorySubstitutionUntilPublicationCompletes()
    {
        var ancestor = Path.Combine(_directory, "ancestor");
        var parent = Path.Combine(ancestor, "selected");
        Directory.CreateDirectory(parent);
        var path = Path.Combine(parent, "published.txt");
        bool substitutionBlocked = false;
        var observer = new InlineProgress(value =>
        {
            if (value.Stage != AtomicPublicationStage.Reserved) return;
            try { Directory.Move(ancestor, ancestor + ".replaced"); }
            catch (IOException) { substitutionBlocked = true; }
            catch (UnauthorizedAccessException) { substitutionBlocked = true; }
        });

        var result = await AtomicNewFileWriter.PublishUtf8Async(path, "selected directory", CancellationToken.None, progress: observer);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.True(substitutionBlocked);
        Assert.Equal("selected directory", await File.ReadAllTextAsync(path));
        Directory.Move(ancestor, ancestor + ".released");
        Assert.True(File.Exists(Path.Combine(ancestor + ".released", "selected", "published.txt")));
    }
    private sealed class InlineProgress(Action<AtomicPublicationProgress> observe) : IProgress<AtomicPublicationProgress>
    {
        public void Report(AtomicPublicationProgress value) => observe(value);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
