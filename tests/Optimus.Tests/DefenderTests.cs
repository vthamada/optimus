using System.Text.Json;
using System.Text.RegularExpressions;
using Optimus.Application;
using Optimus.Domain;
using Optimus.Infrastructure.Security;

namespace Optimus.Tests;

public sealed class DefenderTests
{
    private const string ObservedAt = "2026-10-27T10:04:05.0000000+02:00";
    private const string SignatureDate = "2026-10-20T02:03:04.0000000-03:00";

    [Fact]
    public async Task OnlyTheNamedLocalQueryIsRequestedAndOriginalStatesDatesAndJsonArePreserved()
    {
        var fields = Properties();
        fields["AntivirusEnabled"] = false;
        fields["DefenderSignaturesOutOfDate"] = true;
        fields["ComputerState"] = uint.MaxValue;
        fields["AMRunningMode"] = "Passive Mode";
        var json = Document(fields);
        var runner = new QueryRunner(_ => Completed(json));

        var evidence = Assert.Single(await new DefenderCollector(runner).CollectAsync(CancellationToken.None));

        Assert.Equal(WindowsSystemQuery.DefenderStatus, runner.Query);
        Assert.Equal(1, runner.Calls);
        Assert.Equal(MaintenanceCollectors.Defender, evidence.CollectorId);
        Assert.Equal(EvidenceStatus.Complete, evidence.Status);
        Assert.Equal("false", Fact(evidence, "AntivirusEnabled").TextValue);
        Assert.Equal("true", Fact(evidence, "DefenderSignaturesOutOfDate").TextValue);
        Assert.Equal("Passive Mode", Fact(evidence, "AMRunningMode").TextValue);
        Assert.Equal((decimal)uint.MaxValue, Fact(evidence, "ComputerState").NumberValue);
        Assert.Equal(SignatureDate, Fact(evidence, "AntivirusSignatureLastUpdated").TextValue);
        Assert.Equal(TimeSpan.FromHours(2), evidence.CapturedAt.Offset);
        Assert.Equal(DateTimeOffset.Parse(ObservedAt), evidence.CapturedAt);
        Assert.Equal(json, Fact(evidence, "sourceJson").TextValue);
        Assert.True(Fact(evidence, "sourceJson").Detailed);
        Assert.Contains("não comprovam ausência de malware", evidence.Coverage.Scope);
        Assert.DoesNotContain(evidence.Facts, fact => fact.Key is "healthy" or "malwareFree" or "ComputerID");
        Assert.True(SessionValidation.IsValid(Session(evidence)));
    }

    [Theory]
    [InlineData("\"true\"")]
    [InlineData("1")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    public async Task UnexpectedBooleanRepresentationStaysUnknownWhileOtherFactsSurvive(string raw)
    {
        using var invalid = JsonDocument.Parse(raw);
        var fields = Properties();
        fields["RealTimeProtectionEnabled"] = invalid.RootElement.Clone();
        var json = Document(fields);

        var evidence = await Collect(json);

        Assert.Equal(EvidenceStatus.Partial, evidence.Status);
        Assert.Null(Fact(evidence, "RealTimeProtectionEnabled").TextValue);
        Assert.Null(Fact(evidence, "RealTimeProtectionEnabled").NumberValue);
        Assert.Equal("true", Fact(evidence, "AMServiceEnabled").TextValue);
        Assert.Equal(json, Fact(evidence, "sourceJson").TextValue);
        Assert.Contains(evidence.Issues, issue => issue.Message.Contains("RealTimeProtectionEnabled", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AbsentPropertiesDoNotBecomeFalseOrHealthy()
    {
        var fields = Properties();
        fields.Remove("AntivirusEnabled");
        fields.Remove("IsTamperProtected");

        var evidence = await Collect(Document(fields));

        Assert.Equal(EvidenceStatus.Partial, evidence.Status);
        Assert.Null(Fact(evidence, "AntivirusEnabled").TextValue);
        Assert.Null(Fact(evidence, "IsTamperProtected").TextValue);
        Assert.Equal("true", Fact(evidence, "RealTimeProtectionEnabled").TextValue);
    }

    [Fact]
    public async Task ProviderUnavailableNoteCannotBeOverriddenByAnApparentlyValidValue()
    {
        var json = Document(Properties(), [Note("AntivirusEnabled", "UnsupportedType", "System.String", 4)]);

        var evidence = await Collect(json);

        Assert.Equal(EvidenceStatus.Partial, evidence.Status);
        Assert.Null(Fact(evidence, "AntivirusEnabled").TextValue);
        Assert.Contains(evidence.Issues, issue => issue.Message.Contains("UnsupportedType", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EmptyStatusRemainsUnavailableDespiteExitZero()
    {
        var evidence = await Collect(Document([]));

        Assert.Equal(EvidenceStatus.Unavailable, evidence.Status);
        Assert.All(evidence.Facts.Where(fact => fact.Key.EndsWith("Enabled", StringComparison.Ordinal)), fact => Assert.Null(fact.TextValue));
        Assert.NotEmpty(evidence.Issues);
    }

    [Theory]
    [InlineData("{not-json")]
    [InlineData("{\"kind\":\"optimus.defender-status\",\"version\":1,\"observedAt\":\"2026-10-27T01:00:00Z\",\"properties\":{\"AntivirusEnabled\":true,\"AntivirusEnabled\":false},\"unavailable\":[]}")]
    [InlineData("{\"kind\":\"optimus.defender-status\",\"version\":2,\"observedAt\":\"2026-10-27T01:00:00Z\",\"properties\":{},\"unavailable\":[]}")]
    [InlineData("{\"kind\":\"different-source\",\"version\":1,\"observedAt\":\"2026-10-27T01:00:00Z\",\"properties\":{},\"unavailable\":[]}")]
    [InlineData("{\"kind\":\"optimus.defender-status\",\"version\":1,\"observedAt\":\"2026-10-27T01:00:00Z\",\"properties\":{\"ComputerID\":\"not-allowed\"},\"unavailable\":[]}")]
    [InlineData("{\"kind\":\"optimus.defender-status\",\"version\":1,\"observedAt\":\"2026-10-27T01:00:00Z\",\"properties\":{},\"unavailable\":[],\"extra\":true}")]
    public async Task InvalidOrAmbiguousDocumentsPreserveSourceWithoutAcceptingProtection(string json)
    {
        var evidence = await Collect(json);

        Assert.Equal(EvidenceStatus.Unavailable, evidence.Status);
        Assert.Equal(json, Fact(evidence, "sourceJson").TextValue);
        Assert.DoesNotContain(evidence.Facts, fact => fact.Key == "AntivirusEnabled");
        Assert.NotEmpty(evidence.Issues);
    }

    [Fact]
    public async Task OversizedPropertyIsUnknownAndDoesNotReplaceOtherValidFields()
    {
        var fields = Properties();
        fields["AntivirusSignatureVersion"] = new string('x', 513);
        fields["ComputerState"] = -1;
        fields["AntivirusSignatureLastUpdated"] = "2026-10-20T02:03:04.0000000";

        var evidence = await Collect(Document(fields));

        Assert.Equal(EvidenceStatus.Partial, evidence.Status);
        Assert.Null(Fact(evidence, "AntivirusSignatureVersion").TextValue);
        Assert.Null(Fact(evidence, "ComputerState").NumberValue);
        Assert.Null(Fact(evidence, "AntivirusSignatureLastUpdated").TextValue);
        Assert.Equal("true", Fact(evidence, "AntivirusEnabled").TextValue);
    }

    [Theory]
    [InlineData(ToolRunStatus.TimedOut, null)]
    [InlineData(ToolRunStatus.StartFailed, null)]
    [InlineData(ToolRunStatus.Cancelled, null)]
    [InlineData(ToolRunStatus.OutputLimitExceeded, null)]
    [InlineData(ToolRunStatus.Exited, 1)]
    public async Task FailedExecutionCannotPublishAnOtherwiseValidStatus(ToolRunStatus status, int? exitCode)
    {
        var runner = new QueryRunner(_ => new(status, exitCode, Document(Properties()), "native failure", new(TimeSpan.FromSeconds(1))));

        var evidence = Assert.Single(await new DefenderCollector(runner).CollectAsync(CancellationToken.None));

        Assert.Equal(EvidenceStatus.Unavailable, evidence.Status);
        Assert.DoesNotContain(evidence.Facts, fact => fact.Key == "AntivirusEnabled");
        Assert.Equal("native failure", Fact(evidence, "sourceError").TextValue);
    }

    [Fact]
    public async Task ErrorAlongsideExitZeroPreservesPendingCleanupAndNeverClaimsCoverage()
    {
        var runner = new QueryRunner(_ => Completed(Document(Properties())) with
        {
            Error = new(ErrorCode.PermissionDenied, "access denied; cleanup pending", ["owned-job:test"])
        });

        var evidence = Assert.Single(await new DefenderCollector(runner).CollectAsync(CancellationToken.None));

        Assert.Equal(EvidenceStatus.Unavailable, evidence.Status);
        Assert.Contains(evidence.Issues, issue => issue.Code == IssueCode.PermissionDenied);
        Assert.Contains("owned-job:test", Fact(evidence, "pendingCleanup").TextValue!);
        Assert.DoesNotContain(evidence.Facts, fact => fact.Key == "AntivirusEnabled");
    }

    [Fact]
    public async Task ResponseLimitIsCheckedBeforeRetainingOrParsingTheContent()
    {
        var runner = new QueryRunner(_ => Completed(new string('x', DefenderCollector.MaximumResponseBytes + 1)));

        var evidence = Assert.Single(await new DefenderCollector(runner).CollectAsync(CancellationToken.None));

        Assert.Equal(EvidenceStatus.Unavailable, evidence.Status);
        Assert.DoesNotContain(evidence.Facts, fact => fact.Key == "sourceJson");
        Assert.Contains(evidence.Issues, issue => issue.Code == IssueCode.LimitReached);
    }

    [Fact]
    public async Task CancellationBeforeAndAfterQueryIsPropagatedWithoutPublishingAStatus()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var untouched = new QueryRunner(_ => throw new InvalidOperationException("Must not execute"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new DefenderCollector(untouched).CollectAsync(cancelled.Token));
        Assert.Equal(0, untouched.Calls);

        using var during = new CancellationTokenSource();
        var late = new QueryRunner(_ => { during.Cancel(); return Completed(Document(Properties())); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new DefenderCollector(late).CollectAsync(during.Token));
        Assert.Equal(1, late.Calls);
    }

    [Fact]
    public void EmbeddedScriptOnlyRequestsTheFixedReadStatusAndDoesNotOfferRemoteOrMutationCommands()
    {
        using var stream = typeof(DefenderCollector).Assembly.GetManifestResourceStream("Optimus.Infrastructure.SystemTools.Scripts.DefenderStatus.ps1");
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!);
        var script = reader.ReadToEnd();

        Assert.Contains("Defender\\Get-MpComputerStatus", script);
        Assert.Contains("Import-Module -Name $module", script);
        Assert.DoesNotContain("-CimSession", script);
        Assert.DoesNotContain("-AsJob", script);
        Assert.DoesNotContain("ComputerID", script);
        Assert.DoesNotContain("$env:", script);
        Assert.False(Regex.IsMatch(script, @"\b(?:Start|Set|Update|Add|Remove|Enable|Disable|Clear)-Mp[A-Za-z]+\b", RegexOptions.CultureInvariant));
    }

    private static DiagnosticFact Fact(DiagnosticEvidence evidence, string key) => Assert.Single(evidence.Facts, fact => fact.Key == key);
    private static DiagnosticSession Session(DiagnosticEvidence evidence) => new(Guid.NewGuid(), evidence.CapturedAt,
        evidence.CapturedAt, evidence.Status == EvidenceStatus.Complete ? SessionStatus.Complete : SessionStatus.Partial, [evidence], []);
    private static async Task<DiagnosticEvidence> Collect(string json) => Assert.Single(await new DefenderCollector(new QueryRunner(_ => Completed(json))).CollectAsync(CancellationToken.None));
    private static ToolRunResult Completed(string json) => new(ToolRunStatus.Exited, 0, json, "", new(TimeSpan.FromMilliseconds(1)));
    private static Dictionary<string, object?> Properties() => new(StringComparer.Ordinal)
    {
        ["AMServiceEnabled"] = true,
        ["AntivirusEnabled"] = true,
        ["AntispywareEnabled"] = true,
        ["BehaviorMonitorEnabled"] = true,
        ["IoavProtectionEnabled"] = true,
        ["NISEnabled"] = false,
        ["OnAccessProtectionEnabled"] = true,
        ["RealTimeProtectionEnabled"] = true,
        ["DefenderSignaturesOutOfDate"] = false,
        ["IsTamperProtected"] = true,
        ["AMEngineVersion"] = "1.1.fixture",
        ["AMProductVersion"] = "4.18.fixture",
        ["AMRunningMode"] = "Normal",
        ["AntivirusSignatureVersion"] = "1.2.fixture",
        ["AntivirusSignatureLastUpdated"] = SignatureDate,
        ["ComputerState"] = 0
    };
    private static object Note(string property, string reason, string? sourceType = null, int? reportedCharacters = null) => new { property, reason, sourceType, reportedCharacters };
    private static string Document(Dictionary<string, object?> properties, object[]? unavailable = null) => JsonSerializer.Serialize(new
    {
        kind = "optimus.defender-status",
        version = 1,
        observedAt = ObservedAt,
        properties,
        unavailable = unavailable ?? []
    });

    private sealed class QueryRunner(Func<CancellationToken, ToolRunResult> execute) : IWindowsSystemQueryRunner
    {
        public int Calls { get; private set; }
        public WindowsSystemQuery? Query { get; private set; }
        public Task<ToolRunResult> RunAsync(WindowsSystemQuery query, CancellationToken cancellationToken)
        { Calls++; Query = query; return Task.FromResult(execute(cancellationToken)); }
    }
}
