using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Optimus.Application;
using Optimus.Domain;
using Optimus.Infrastructure.Updates;

namespace Optimus.Tests;

public sealed class WindowsUpdateHistoryTests
{
    [Fact]
    public async Task NamedQueryExposesHistoryAndRebootWithDistinctIdentityAndVisibleFacts()
    {
        var first = Entry("original-ID-A", 7);
        var second = Entry("original-ID-B", 19);
        var raw = Envelope(History([first, second]), Reboot(true));
        var runner = new QueryRunner(Completed(raw));
        var evidence = Assert.Single(await new WindowsUpdateHistoryCollector(runner).CollectAsync(CancellationToken.None));

        Assert.Equal(WindowsSystemQuery.UpdateHistory, runner.Query);
        Assert.Equal(MaintenanceCollectors.Updates, evidence.CollectorId);
        Assert.Equal(EvidenceStatus.Complete, evidence.Status);
        Assert.Equal("original-ID-A", Fact(evidence, "history.0.updateId").TextValue);
        Assert.Equal("original-ID-B", Fact(evidence, "history.1.updateId").TextValue);
        Assert.Equal(7m, Fact(evidence, "history.0.revisionNumber").NumberValue);
        Assert.Equal(19m, Fact(evidence, "history.1.revisionNumber").NumberValue);
        foreach (string key in new[] { "history.0.title", "history.0.date", "history.0.result", "history.totalCount", "reboot.wuaRequired" })
            Assert.False(Fact(evidence, key).Detailed);
        Assert.Equal("true", Fact(evidence, "reboot.wuaRequired").TextValue);
        Assert.Equal("false", Fact(evidence, "availableUpdatesSearched").TextValue);
        Assert.Equal("false", Fact(evidence, "otherRebootSourcesChecked").TextValue);
        Assert.Equal(raw, Fact(evidence, "sourceJson").TextValue);
        Assert.True(Fact(evidence, "sourceJson").Detailed);
        Assert.Contains("não comprova sistema atualizado", evidence.Coverage.Scope);
        Assert.Contains("não cobre CBS", evidence.Coverage.Scope);
    }

    [Fact]
    public async Task KnownEmptyHistoryAndFalseRebootAreObservedValues()
    {
        var evidence = await Collect(Envelope(History([]), Reboot(false)));
        Assert.Equal(EvidenceStatus.Complete, evidence.Status);
        Assert.Equal(0m, Fact(evidence, "history.totalCount").NumberValue);
        Assert.Equal(0m, Fact(evidence, "history.requested").NumberValue);
        Assert.Equal(0m, Fact(evidence, "history.projected").NumberValue);
        Assert.Equal("false", Fact(evidence, "reboot.wuaRequired").TextValue);
        Assert.Empty(evidence.Issues);
    }

    [Theory]
    [InlineData("System.DateTime", "Utc", "2026-10-27T12:15:00.0000000Z")]
    [InlineData("System.DateTime", "Local", "2026-10-27T09:15:00.0000000-03:00")]
    [InlineData("System.DateTime", "Unspecified", "2026-10-27T12:15:00.0000000")]
    [InlineData("System.DateTimeOffset", "Offset", "2026-10-27T17:45:00.0000000+05:30")]
    public async Task DatesKeepOriginalTypeTextAndKindWithoutTimezoneInference(string type, string kind, string value)
    {
        var entry = Entry();
        entry["date"] = new { sourceType = type, kind, value };
        var evidence = await Collect(Envelope(History([entry]), Reboot(false)));
        Assert.Equal(EvidenceStatus.Complete, evidence.Status);
        Assert.Equal(value, Fact(evidence, "history.0.date").TextValue);
        Assert.Equal(type, Fact(evidence, "history.0.date.sourceType").TextValue);
        Assert.Equal(kind, Fact(evidence, "history.0.date.kind").TextValue);
        if (kind == "Unspecified") Assert.Contains("fuso não informado", Fact(evidence, "history.0.date").Label);
    }

    [Theory]
    [InlineData("System.DateTime", "Utc", "2026-10-27T12:15:00.0000000")]
    [InlineData("System.DateTime", "Unspecified", "2026-10-27T12:15:00.0000000Z")]
    [InlineData("System.DateTime", "Local", "27/10/2026")]
    [InlineData("System.DateTimeOffset", "Offset", "2026-10-27T12:15:00.0000000Z")]
    [InlineData("Unrecognized", "Utc", "2026-10-27T12:15:00.0000000Z")]
    public async Task UnrecognizedDatesRemainUnknownAndRetainOriginalJson(string type, string kind, string value)
    {
        var entry = Entry();
        entry["date"] = new { sourceType = type, kind, value };
        var raw = Envelope(History([entry]), Reboot(false));
        var evidence = await Collect(raw);
        Assert.Equal(EvidenceStatus.Partial, evidence.Status);
        Assert.Null(Fact(evidence, "history.0.date").TextValue);
        Assert.Equal(raw, Fact(evidence, "sourceJson").TextValue);
        Assert.Contains(evidence.Issues, issue => issue.Code == IssueCode.ReadFailed && issue.Message.Contains("history.0.date", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NonIsoDateStringDoesNotBecomeAnInferredTimestamp()
    {
        var entry = Entry();
        entry["date"] = "10/11/2026";
        var evidence = await Collect(Envelope(History([entry]), Reboot(false)));
        Assert.Equal(EvidenceStatus.Partial, evidence.Status);
        Assert.Null(Fact(evidence, "history.0.date").TextValue);
    }

    [Fact]
    public async Task HistoricalFailureIsARecordedOutcomeRatherThanACollectionFailure()
    {
        var entry = Entry();
        entry["operation"] = 2;
        entry["resultCode"] = 4;
        entry["hResult"] = -2145124329;
        var evidence = await Collect(Envelope(History([entry]), Reboot(false)));
        Assert.Equal(EvidenceStatus.Complete, evidence.Status);
        Assert.Equal("uoUninstallation", Fact(evidence, "history.0.operation").TextValue);
        Assert.Equal("orcFailed", Fact(evidence, "history.0.result").TextValue);
        Assert.Equal(-2145124329m, Fact(evidence, "history.0.hResult").NumberValue);
        Assert.Equal("0x80240017", Fact(evidence, "history.0.hResultHex").TextValue);
    }

    [Fact]
    public async Task UnknownEnumsAndMissingFieldsNeverBecomeSuccessOrZero()
    {
        var entry = Entry();
        entry["operation"] = 93;
        entry["resultCode"] = 94;
        entry["hResult"] = null;
        entry["revisionNumber"] = null;
        var evidence = await Collect(Envelope(History([entry]), Reboot(false)));
        Assert.Equal(EvidenceStatus.Partial, evidence.Status);
        Assert.Equal(93m, Fact(evidence, "history.0.operationCode").NumberValue);
        Assert.Equal(94m, Fact(evidence, "history.0.resultCode").NumberValue);
        Assert.Null(Fact(evidence, "history.0.operation").TextValue);
        Assert.Null(Fact(evidence, "history.0.result").TextValue);
        Assert.Null(Fact(evidence, "history.0.hResult").NumberValue);
        Assert.Null(Fact(evidence, "history.0.revisionNumber").NumberValue);
        Assert.Contains(evidence.Issues, issue => issue.Code == IssueCode.NotSupported);
    }

    [Fact]
    public async Task HistoryFailureKeepsIndependentKnownRebootSignal()
    {
        var history = History([], succeeded: false, total: null, after: null);
        history["issues"] = new[] { new { code = "PermissionDenied", message = "local WUA history unavailable" } };
        var evidence = await Collect(Envelope(history, Reboot(true)));
        Assert.Equal(EvidenceStatus.Partial, evidence.Status);
        Assert.Null(Fact(evidence, "history.totalCount").NumberValue);
        Assert.Equal("true", Fact(evidence, "reboot.wuaRequired").TextValue);
        Assert.Contains(evidence.Issues, issue => issue.Code == IssueCode.PermissionDenied);
    }

    [Fact]
    public async Task RebootFailureDoesNotRemoveHistoryAndDoesNotInferFalse()
    {
        var evidence = await Collect(Envelope(History([Entry()]), Reboot(null, false)));
        Assert.Equal(EvidenceStatus.Partial, evidence.Status);
        Assert.Equal("Update with equal title", Fact(evidence, "history.0.title").TextValue);
        Assert.Null(Fact(evidence, "reboot.wuaRequired").TextValue);
    }

    [Fact]
    public async Task BothQueriesUnavailableAreNotReportedAsAnEmptyHealthyHistory()
    {
        var evidence = await Collect(Envelope(History([], false, total: null, after: null), Reboot(null, false)));
        Assert.Equal(EvidenceStatus.Unavailable, evidence.Status);
        Assert.Null(Fact(evidence, "history.totalCount").NumberValue);
        Assert.Null(Fact(evidence, "reboot.wuaRequired").TextValue);
    }

    [Fact]
    public async Task CountChangePreservesEarlierFactsAndMarksDiscoveryIncomplete()
    {
        var evidence = await Collect(Envelope(History([Entry()], false, total: 1, after: 2), Reboot(false)));
        Assert.Equal(EvidenceStatus.Partial, evidence.Status);
        Assert.Equal(1m, Fact(evidence, "history.totalCount").NumberValue);
        Assert.Equal(2m, Fact(evidence, "history.confirmedTotalCount").NumberValue);
        Assert.Equal("Update with equal title", Fact(evidence, "history.0.title").TextValue);
    }

    [Fact]
    public async Task CappedHistoryAndBudgetOmissionRetainBoundedPrefixAndCoverage()
    {
        var evidence = await Collect(Envelope(History([Entry()], total: 500, after: 500, requested: 128, examined: 2, limited: true), Reboot(false)));
        Assert.Equal(EvidenceStatus.Partial, evidence.Status);
        Assert.True(evidence.Coverage.LimitReached);
        Assert.Equal(2, evidence.Coverage.ExaminedItems);
        Assert.Equal(500m, Fact(evidence, "history.totalCount").NumberValue);
        Assert.Equal(1m, Fact(evidence, "history.retained").NumberValue);
        Assert.Contains(evidence.Issues, issue => issue.Code == IssueCode.LimitReached);
    }

    [Fact]
    public async Task ProjectionLimitKeepsSourceAndIndependentRebootVisible()
    {
        var entries = Enumerable.Range(0, 30).Select(index =>
        {
            var entry = Entry("id-" + index);
            entry["title"] = new string('t', 1000);
            entry["clientApplicationId"] = new string('c', 1000);
            return entry;
        }).ToArray();
        var raw = Envelope(History(entries), Reboot(true));
        var evidence = await Collect(raw);
        Assert.Equal(EvidenceStatus.Partial, evidence.Status);
        Assert.True(evidence.Coverage.LimitReached);
        Assert.InRange(Fact(evidence, "history.projected").NumberValue!.Value, 1m, 29m);
        Assert.Equal(30m, Fact(evidence, "history.retained").NumberValue);
        Assert.Equal(raw, Fact(evidence, "sourceJson").TextValue);
        Assert.Equal("true", Fact(evidence, "reboot.wuaRequired").TextValue);
    }

    [Fact]
    public async Task OversizedFieldIsExplicitUnknownWithoutTruncatedReplacement()
    {
        var entry = Entry();
        entry["title"] = new string('t', 2049);
        var raw = Envelope(History([entry]), Reboot(false));
        var evidence = await Collect(raw);
        Assert.Equal(EvidenceStatus.Partial, evidence.Status);
        Assert.Null(Fact(evidence, "history.0.title").TextValue);
        Assert.Equal(raw, Fact(evidence, "sourceJson").TextValue);
        Assert.Contains(evidence.Issues, issue => issue.Code == IssueCode.LimitReached);
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("duplicate")]
    [InlineData("unknownRoot")]
    [InlineData("unknownEntry")]
    [InlineData("duplicateDate")]
    [InlineData("badCount")]
    [InlineData("missingDiscovery")]
    [InlineData("unreportedOmission")]
    [InlineData("unknownIssue")]
    public async Task StrictEnvelopeRejectsMalformedUnknownAndContradictoryContracts(string fault)
    {
        var node = JsonNode.Parse(Envelope(History([Entry()]), Reboot(false)))!.AsObject();
        string raw;
        switch (fault)
        {
            case "malformed": raw = "{"; break;
            case "duplicate": raw = node.ToJsonString().Replace("\"version\":1", "\"version\":1,\"version\":1", StringComparison.Ordinal); break;
            case "unknownRoot": node["unexpected"] = true; raw = node.ToJsonString(); break;
            case "unknownEntry": node["history"]!["entries"]![0]!["unexpected"] = true; raw = node.ToJsonString(); break;
            case "duplicateDate": raw = node.ToJsonString().Replace("\"kind\":\"Unspecified\"", "\"kind\":\"Unspecified\",\"kind\":\"Unspecified\"", StringComparison.Ordinal); break;
            case "badCount": node["history"]!["examined"] = 129; raw = node.ToJsonString(); break;
            case "missingDiscovery": node["history"]!["confirmedTotalCount"] = null; raw = node.ToJsonString(); break;
            case "unreportedOmission": node["history"]!["entries"] = new JsonArray(); raw = node.ToJsonString(); break;
            default: node["history"]!["issues"] = new JsonArray(JsonSerializer.SerializeToNode(new { code = "Unrecognized", message = "source" })); raw = node.ToJsonString(); break;
        }
        var evidence = await Collect(raw);
        Assert.Equal(EvidenceStatus.Unavailable, evidence.Status);
        Assert.Equal(raw, Fact(evidence, "sourceJson").TextValue);
        Assert.DoesNotContain(evidence.Facts, fact => fact.Key.StartsWith("history.", StringComparison.Ordinal));
        Assert.Contains(evidence.Issues, issue => issue.Code == IssueCode.ReadFailed);
    }

    [Fact]
    public async Task SourceResponseOverflowDeclaresOmissionAndDoesNotInterpretPrefix()
    {
        var raw = new string('x', WindowsUpdateHistoryCollector.MaximumResponseBytes + 1);
        var evidence = await Collect(raw);
        Assert.Equal(EvidenceStatus.Unavailable, evidence.Status);
        Assert.True(evidence.Coverage.LimitReached);
        Assert.DoesNotContain(evidence.Facts, fact => fact.Key == "sourceJson");
        Assert.Equal(Encoding.UTF8.GetByteCount(raw), Fact(evidence, "sourceJsonUtf8Bytes").NumberValue);
    }

    [Fact]
    public async Task OwnedComCleanupFailureRetainsEveryPendingNameAndSource()
    {
        string[] pending = ["history[0].identity", "history.session"];
        var raw = Envelope(History([Entry()]), Reboot(false), new { succeeded = false, pending, issues = Array.Empty<object>() });
        var evidence = await Collect(raw);
        Assert.Equal(EvidenceStatus.Partial, evidence.Status);
        using var names = JsonDocument.Parse(Fact(evidence, "comCleanupPending").TextValue!);
        Assert.Equal(pending, names.RootElement.EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(raw, Fact(evidence, "sourceJson").TextValue);
    }

    [Theory]
    [InlineData(ToolRunStatus.CleanupFailed)]
    [InlineData(ToolRunStatus.TimedOut)]
    [InlineData(ToolRunStatus.OutputLimitExceeded)]
    public async Task FailedTransportKeepsExactErrorStderrPendingAndJsonButDoesNotTrustFields(ToolRunStatus status)
    {
        var raw = Envelope(History([Entry()]), Reboot(false));
        string[] pending = [@"D:\owned\pending.json", @"D:\owned\job.lock"];
        var result = Completed(raw) with { Status = status, StandardError = "original stderr Ω", Error = new(ErrorCode.ToolFailed, "original primary reason", pending) };
        var evidence = Assert.Single(await new WindowsUpdateHistoryCollector(new QueryRunner(result)).CollectAsync(CancellationToken.None));
        Assert.Equal(EvidenceStatus.Unavailable, evidence.Status);
        Assert.Equal(raw, Fact(evidence, "sourceJson").TextValue);
        Assert.Equal("original stderr Ω", Fact(evidence, "toolStderr").TextValue);
        Assert.Equal("original primary reason", Fact(evidence, "toolPrimaryErrorReason").TextValue);
        Assert.Equal("ToolFailed", Fact(evidence, "toolPrimaryErrorCode").TextValue);
        using var retained = JsonDocument.Parse(Fact(evidence, "toolPendingCleanupPaths").TextValue!);
        Assert.Equal(pending, retained.RootElement.EnumerateArray().Select(item => item.GetString()));
        Assert.DoesNotContain(evidence.Facts, fact => fact.Key.StartsWith("history.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task JobMetricsAreNumericDetailedAndMissingMetricsRemainUnknown()
    {
        var result = Completed(Envelope(History([]), Reboot(false))) with { Metrics = new(TimeSpan.FromMilliseconds(1250), TimeSpan.FromMilliseconds(250), 4096) };
        var evidence = Assert.Single(await new WindowsUpdateHistoryCollector(new QueryRunner(result)).CollectAsync(CancellationToken.None));
        Assert.Equal(1.25m, Fact(evidence, "queryJobElapsedSeconds").NumberValue);
        Assert.Equal(0.25m, Fact(evidence, "queryJobCpuSeconds").NumberValue);
        Assert.Equal(4096m, Fact(evidence, "queryJobPeakCommitBytes").NumberValue);
        Assert.True(Fact(evidence, "queryJobPeakCommitBytes").Detailed);
        evidence = await Collect(Envelope(History([]), Reboot(false)));
        Assert.Null(Fact(evidence, "queryJobCpuSeconds").NumberValue);
        Assert.Null(Fact(evidence, "queryJobPeakCommitBytes").NumberValue);
    }

    [Fact]
    public async Task TransportEvidenceLimitsAreDeclaredWithoutInventingTruncatedOriginals()
    {
        var result = Completed("{}") with
        {
            Status = ToolRunStatus.CleanupFailed,
            StandardError = new string('x', 32769),
            Error = new(ErrorCode.ToolFailed, "primary reason", [new string('p', 16385)])
        };
        var evidence = Assert.Single(await new WindowsUpdateHistoryCollector(new QueryRunner(result)).CollectAsync(CancellationToken.None));
        Assert.Equal(EvidenceStatus.Unavailable, evidence.Status);
        Assert.Null(Fact(evidence, "toolStderr").TextValue);
        Assert.Equal(32769m, Fact(evidence, "toolStderr.utf8Bytes").NumberValue);
        Assert.Null(Fact(evidence, "toolPendingCleanupPaths").TextValue);
        Assert.Equal(1m, Fact(evidence, "toolPendingCleanupCount").NumberValue);
        Assert.Equal("primary reason", Fact(evidence, "toolPrimaryErrorReason").TextValue);
        Assert.True(evidence.Coverage.LimitReached);
    }

    [Fact]
    public async Task CancellationBeforeHostDoesNotRunAndCancellationAfterHostDoesNotPublish()
    {
        using var before = new CancellationTokenSource();
        before.Cancel();
        var runner = new QueryRunner(Completed("{}"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new WindowsUpdateHistoryCollector(runner).CollectAsync(before.Token));
        Assert.Equal(0, runner.Calls);
        using var after = new CancellationTokenSource();
        runner = new QueryRunner(Completed(Envelope(History([]), Reboot(false)))) { OnRun = after.Cancel };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new WindowsUpdateHistoryCollector(runner).CollectAsync(after.Token));
        Assert.Equal(1, runner.Calls);
    }

    private static DiagnosticFact Fact(DiagnosticEvidence evidence, string key) => Assert.Single(evidence.Facts, fact => fact.Key == key);
    private static async Task<DiagnosticEvidence> Collect(string raw) => Assert.Single(await new WindowsUpdateHistoryCollector(new QueryRunner(Completed(raw))).CollectAsync(CancellationToken.None));
    private static ToolRunResult Completed(string raw) => new(ToolRunStatus.Exited, 0, raw, "", new(TimeSpan.Zero));
    private static string Envelope(object history, object reboot, object? cleanup = null) => JsonSerializer.Serialize(new
    {
        kind = "optimus.update-history",
        version = 1,
        observedAt = "2026-10-27T12:15:00.0000000+00:00",
        history,
        reboot,
        cleanup = cleanup ?? new { succeeded = true, pending = Array.Empty<string>(), issues = Array.Empty<object>() }
    });
    private static Dictionary<string, object?> History(IReadOnlyList<Dictionary<string, object?>> entries, bool succeeded = true,
        int? total = -1, int? after = -1, int? requested = null, int? examined = null, bool limited = false) => new()
        {
            ["succeeded"] = succeeded,
            ["totalCount"] = total == -1 ? entries.Count : total,
            ["confirmedTotalCount"] = after == -1 ? entries.Count : after,
            ["requested"] = requested ?? Math.Min(total == -1 ? entries.Count : total ?? 0, 128),
            ["examined"] = examined ?? entries.Count,
            ["limitReached"] = limited,
            ["entries"] = entries,
            ["issues"] = Array.Empty<object>()
        };
    private static object Reboot(bool? required, bool succeeded = true) => new { succeeded, required, issues = Array.Empty<object>() };
    private static Dictionary<string, object?> Entry(string id = "original-update-id", int revision = 1) => new()
    {
        ["updateId"] = id,
        ["revisionNumber"] = revision,
        ["title"] = "Update with equal title",
        ["date"] = new { sourceType = "System.DateTime", kind = "Unspecified", value = "2026-10-27T12:15:00.0000000" },
        ["operation"] = 1,
        ["resultCode"] = 2,
        ["hResult"] = 0,
        ["clientApplicationId"] = "Fixture client"
    };
    private sealed class QueryRunner(ToolRunResult result) : IWindowsSystemQueryRunner
    {
        public WindowsSystemQuery Query { get; private set; }
        public int Calls { get; private set; }
        public Action? OnRun { get; init; }
        public Task<ToolRunResult> RunAsync(WindowsSystemQuery query, CancellationToken cancellationToken)
        {
            Calls++; Query = query; OnRun?.Invoke();
            return Task.FromResult(result);
        }
    }
}
