using System.Text.Json;
using System.Text.Json.Nodes;
using Optimus.Application;
using Optimus.Domain;
using Optimus.Infrastructure.Scheduling;

namespace Optimus.Tests;

public sealed class ScheduledTasksTests
{
    [Fact]
    public async Task NamedLocalQueryPreservesOriginalMetadataAndVisibleFacts()
    {
        var record = Record();
        record["Name"] = "d'água Ω $(not-executed)";
        record["Path"] = @"\Original\d'água Ω $(not-executed)";
        record["LastTaskResult"] = -2147024891;
        var raw = Envelope([record]);
        var runner = new QueryRunner(Completed(raw));
        var evidence = Assert.Single(await new ScheduledTasksCollector(runner).CollectAsync(CancellationToken.None));

        Assert.Equal(WindowsSystemQuery.TaskInventory, runner.Query);
        Assert.Equal(1, runner.Calls);
        Assert.Equal(MaintenanceCollectors.ScheduledTasks, evidence.CollectorId);
        Assert.Equal(EvidenceStatus.Complete, evidence.Status);
        Assert.Equal(DateTimeOffset.Parse("2026-10-28T09:10:11.1234567-03:00"), evidence.CapturedAt);
        Assert.Equal(record["Path"], Fact(evidence, "tasks.0.Path").TextValue);
        Assert.Equal(record["Name"], Fact(evidence, "tasks.0.Name").TextValue);
        Assert.Equal("false", Fact(evidence, "tasks.0.Enabled").TextValue);
        Assert.Equal("TASK_STATE_READY", Fact(evidence, "tasks.0.stateName").TextValue);
        Assert.Equal(-2147024891m, Fact(evidence, "tasks.0.LastTaskResult").NumberValue);
        Assert.Equal("0x80070005", Fact(evidence, "tasks.0.lastTaskResultHex").TextValue);
        foreach (string key in new[] { "tasks.0.Path", "tasks.0.Enabled", "tasks.0.stateName", "tasks.0.LastRunTime", "tasks.0.LastTaskResult" }) Assert.False(Fact(evidence, key).Detailed);
        Assert.Equal(raw, Fact(evidence, "sourceJson").TextValue);
        Assert.True(Fact(evidence, "sourceJson").Detailed);
        Assert.Equal("true", Fact(evidence, "hiddenTasksRequested").TextValue);
        Assert.Equal("false", Fact(evidence, "taskDefinitionsRequested").TextValue);
        Assert.Equal("false", Fact(evidence, "taskChangesRequested").TextValue);
        Assert.Contains("triggers individualmente desabilitados", evidence.Coverage.Scope);
        Assert.True(SessionValidation.IsValid(new(Guid.NewGuid(), evidence.CapturedAt, evidence.CapturedAt, SessionStatus.Complete, [evidence], [])));
    }

    [Fact]
    public async Task KnownEmptyRootDoesNotBecomeAnUnavailableOrHealthyConclusion()
    {
        var evidence = await Collect(Envelope([]));
        Assert.Equal(EvidenceStatus.Complete, evidence.Status);
        Assert.Equal(0m, Fact(evidence, "tasksExamined").NumberValue);
        Assert.Equal(0m, Fact(evidence, "tasksRetained").NumberValue);
        Assert.Equal(0m, Fact(evidence, "tasksProjected").NumberValue);
        Assert.Empty(evidence.Issues);
        Assert.DoesNotContain(evidence.Facts, fact => fact.Key is "healthy" or "repairRequired");
    }

    [Fact]
    public async Task UnavailableRootRetainsRawFailureRatherThanInventingAnEmptyCatalog()
    {
        var node = JsonNode.Parse(Envelope([]))!;
        node["query"]!["connected"] = false;
        node["query"]!["traversalComplete"] = false;
        node["query"]!["foldersVisited"] = 0;
        var raw = node.ToJsonString();
        var evidence = await Collect(raw);
        Assert.Equal(EvidenceStatus.Unavailable, evidence.Status);
        Assert.Equal(raw, Fact(evidence, "sourceJson").TextValue);
        Assert.Contains(evidence.Issues, issue => issue.Message.Contains("nenhum catálogo vazio"));
    }

    [Fact]
    public async Task FailedSubtreePreservesEarlierRecordsAndUnknownDescendants()
    {
        var node = JsonNode.Parse(Envelope([Record()]))!;
        node["query"]!["foldersVisited"] = 2;
        node["query"]!["foldersFailed"] = 1;
        node["query"]!["traversalComplete"] = false;
        node["query"]!["issues"] = JsonSerializer.SerializeToNode(new[] { new { code = "PermissionDenied", message = "original subtree access denied" } });
        var evidence = await Collect(node.ToJsonString());
        Assert.Equal(EvidenceStatus.Partial, evidence.Status);
        Assert.Equal(1m, Fact(evidence, "tasksRetained").NumberValue);
        Assert.Equal(@"\Original\Task-A", Fact(evidence, "tasks.0.Path").TextValue);
        Assert.Contains(evidence.Issues, issue => issue.Code == IssueCode.PermissionDenied && issue.Message.Contains("original subtree access denied"));
    }

    [Theory]
    [InlineData("System.DateTime", "Utc", "2026-10-28T12:15:00.0000000Z")]
    [InlineData("System.DateTime", "Local", "2026-10-28T09:15:00.0000000-03:00")]
    [InlineData("System.DateTime", "Unspecified", "2026-10-28T12:15:00.0000000")]
    [InlineData("System.DateTimeOffset", "Offset", "2026-10-28T17:45:00.0000000+05:30")]
    public async Task DateKindAndSourceTextAreRetainedWithoutTimezoneInference(string type, string kind, string value)
    {
        var record = Record(); record["LastRunTime"] = new { sourceType = type, kind, value };
        var evidence = await Collect(Envelope([record]));
        Assert.Equal(EvidenceStatus.Complete, evidence.Status);
        Assert.Equal(value, Fact(evidence, "tasks.0.LastRunTime").TextValue);
        Assert.Equal(type, Fact(evidence, "tasks.0.LastRunTime.sourceType").TextValue);
        Assert.Equal(kind, Fact(evidence, "tasks.0.LastRunTime.kind").TextValue);
        if (kind == "Unspecified") Assert.Contains("fuso não informado", Fact(evidence, "tasks.0.LastRunTime").Label);
    }

    [Fact]
    public async Task OleDateZeroIsPreservedAsRepresentationAndDoesNotInventExecutionOrTaskResult()
    {
        var record = Record(); record["LastRunTime"] = Date("1899-12-30T00:00:00.0000000");
        var evidence = await Collect(Envelope([record]));
        Assert.Equal(EvidenceStatus.Complete, evidence.Status);
        Assert.Equal("1899-12-30T00:00:00.0000000", Fact(evidence, "tasks.0.LastRunTime").TextValue);
        Assert.Equal("true", Fact(evidence, "tasks.0.LastRunTime.oleDateZeroRepresentation").TextValue);
        Assert.Contains("não prova execução", Fact(evidence, "tasks.0.LastRunTime").Label);
        Assert.DoesNotContain(evidence.Facts, fact => fact.Key == "tasks.0.lastTaskResultName");
    }

    [Fact]
    public async Task DocumentedHasNotRunCodeIsDistinctFromAnApplicationOrTransportFailure()
    {
        var record = Record(); record["LastTaskResult"] = 0x41303;
        var evidence = await Collect(Envelope([record]));
        Assert.Equal(EvidenceStatus.Complete, evidence.Status);
        Assert.Equal("SCHED_S_TASK_HAS_NOT_RUN", Fact(evidence, "tasks.0.lastTaskResultName").TextValue);
        Assert.Equal("0x00041303", Fact(evidence, "tasks.0.lastTaskResultHex").TextValue);
        Assert.Equal(267011m, Fact(evidence, "tasks.0.LastTaskResult").NumberValue);
    }

    [Theory]
    [InlineData("Utc", "2026-10-28T12:15:00.0000000")]
    [InlineData("Unspecified", "2026-10-28T12:15:00.0000000Z")]
    [InlineData("Local", "28/10/2026")]
    public async Task DivergentDateFormsStayUnknownWithRawSourceRetained(string kind, string value)
    {
        var record = Record(); record["LastRunTime"] = new { sourceType = "System.DateTime", kind, value };
        var raw = Envelope([record]); var evidence = await Collect(raw);
        Assert.Equal(EvidenceStatus.Partial, evidence.Status);
        Assert.Null(Fact(evidence, "tasks.0.LastRunTime").TextValue);
        Assert.Equal(raw, Fact(evidence, "sourceJson").TextValue);
    }

    [Fact]
    public async Task UnknownStateAndEnabledKeepOriginalCodeAndNeverAssumeFalse()
    {
        var record = Record(); record["State"] = 99; record["Enabled"] = "false";
        var evidence = await Collect(Envelope([record]));
        Assert.Equal(EvidenceStatus.Partial, evidence.Status);
        Assert.Equal(99m, Fact(evidence, "tasks.0.State").NumberValue);
        Assert.Null(Fact(evidence, "tasks.0.stateName").TextValue);
        Assert.Null(Fact(evidence, "tasks.0.Enabled").TextValue);
        Assert.Contains(evidence.Issues, issue => issue.Code == IssueCode.NotSupported);
    }

    [Fact]
    public async Task OversizedFieldIsOmittedExplicitlyWithoutTruncatedReplacement()
    {
        var record = Record(); record["Path"] = new string('Ω', 2049);
        var raw = Envelope([record]); var evidence = await Collect(raw);
        Assert.Equal(EvidenceStatus.Partial, evidence.Status);
        Assert.Null(Fact(evidence, "tasks.0.Path").TextValue);
        Assert.Equal(raw, Fact(evidence, "sourceJson").TextValue);
        Assert.True(evidence.Coverage.LimitReached);
    }

    [Fact]
    public async Task ProjectionBudgetRetainsEveryReceivedRecordInOriginalJson()
    {
        var records = Enumerable.Range(0, 128).Select(index => { var value = Record(); value["Path"] = @"\Original\Task-" + index; return (object)value; }).ToArray();
        var raw = Envelope(records); var evidence = await Collect(raw);
        Assert.Equal(EvidenceStatus.Partial, evidence.Status);
        Assert.Equal(128m, Fact(evidence, "tasksRetained").NumberValue);
        Assert.InRange(Fact(evidence, "tasksProjected").NumberValue!.Value, 1m, 127m);
        Assert.Equal(raw, Fact(evidence, "sourceJson").TextValue);
        Assert.True(evidence.Coverage.LimitReached);
    }

    [Fact]
    public async Task SourceCapsAndOmittedIssueCountersCannotBeClaimedComplete()
    {
        var node = JsonNode.Parse(Envelope([Record()]))!;
        node["query"]!["traversalComplete"] = false;
        node["query"]!["limitReached"] = true;
        node["query"]!["issuesOmitted"] = 7;
        var evidence = await Collect(node.ToJsonString());
        Assert.Equal(EvidenceStatus.Partial, evidence.Status);
        Assert.Equal(7m, Fact(evidence, "sourceIssuesOmitted").NumberValue);
        Assert.True(evidence.Coverage.LimitReached);
    }

    [Theory]
    [InlineData("foldersVisited", 33)]
    [InlineData("foldersFailed", 97)]
    [InlineData("tasksExamined", 129)]
    [InlineData("issuesOmitted", 4097)]
    public async Task CountsOutsideTheProducerResourceContractAreRejected(string key, int value)
    {
        var node = JsonNode.Parse(Envelope([]))!; node["query"]![key] = value;
        var raw = node.ToJsonString(); var evidence = await Collect(raw);
        Assert.Equal(EvidenceStatus.Unavailable, evidence.Status);
        Assert.Equal(raw, Fact(evidence, "sourceJson").TextValue);
        Assert.DoesNotContain(evidence.Facts, fact => fact.Key.StartsWith("tasks.", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("{\"kind\":\"optimus.task-inventory\",\"kind\":\"ambiguous\"}")]
    public async Task InvalidEnvelopeKeepsRawWithoutPublishingPartiallyTrustedFields(string raw)
    {
        var evidence = await Collect(raw);
        Assert.Equal(EvidenceStatus.Unavailable, evidence.Status);
        Assert.Equal(raw, Fact(evidence, "sourceJson").TextValue);
    }

    [Fact]
    public async Task NestedDuplicatePropertiesAreRejectedBeforeProjection()
    {
        var raw = Envelope([Record()]).Replace("\"State\":3", "\"State\":3,\"State\":4", StringComparison.Ordinal);
        var evidence = await Collect(raw);
        Assert.Equal(EvidenceStatus.Unavailable, evidence.Status);
        Assert.DoesNotContain(evidence.Facts, fact => fact.Key.StartsWith("tasks.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnknownEnvelopeMembersCannotChangeTheSelectedMetadataContract()
    {
        var node = JsonNode.Parse(Envelope([Record()]))!; node["query"]!["actions"] = "not in contract";
        var raw = node.ToJsonString(); var evidence = await Collect(raw);
        Assert.Equal(EvidenceStatus.Unavailable, evidence.Status);
        Assert.Equal(raw, Fact(evidence, "sourceJson").TextValue);
        Assert.DoesNotContain(evidence.Facts, fact => fact.Key.StartsWith("tasks.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CompleteCoverageCannotHideMissingOrUnconnectedRecords()
    {
        var missing = JsonNode.Parse(Envelope([]))!; missing["query"]!["tasksExamined"] = 1;
        Assert.Equal(EvidenceStatus.Unavailable, (await Collect(missing.ToJsonString())).Status);
        var unconnected = JsonNode.Parse(Envelope([Record()]))!; unconnected["query"]!["connected"] = false;
        Assert.Equal(EvidenceStatus.Unavailable, (await Collect(unconnected.ToJsonString())).Status);
    }

    [Fact]
    public async Task ResponseBudgetCountsUtf8BytesAndDoesNotAcceptATruncatedPrefix()
    {
        var raw = new string('Ω', ScheduledTasksCollector.MaximumResponseBytes / 2 + 1);
        var evidence = await Collect(raw);
        Assert.Equal(EvidenceStatus.Unavailable, evidence.Status);
        Assert.DoesNotContain(evidence.Facts, fact => fact.Key == "sourceJson");
        Assert.Equal(262146m, Fact(evidence, "sourceJsonUtf8Bytes").NumberValue);
        Assert.True(evidence.Coverage.LimitReached);
    }

    [Fact]
    public async Task FailedTransportKeepsPrimaryErrorStderrAndPendingResourceNames()
    {
        var result = new ToolRunResult(ToolRunStatus.CleanupFailed, null, "original stdout", "original stderr", new(TimeSpan.FromSeconds(2)),
            new(ErrorCode.PermissionDenied, "original primary reason", [@"D:\owned-pending Ω"]));
        var evidence = Assert.Single(await new ScheduledTasksCollector(new QueryRunner(result)).CollectAsync(CancellationToken.None));
        Assert.Equal(EvidenceStatus.Unavailable, evidence.Status);
        Assert.Equal("original stdout", Fact(evidence, "sourceJson").TextValue);
        Assert.Equal("original stderr", Fact(evidence, "toolStderr").TextValue);
        Assert.Equal("original primary reason", Fact(evidence, "toolPrimaryErrorReason").TextValue);
        Assert.Equal("PermissionDenied", Fact(evidence, "toolPrimaryErrorCode").TextValue);
        Assert.Equal(@"D:\owned-pending Ω", Assert.Single(JsonSerializer.Deserialize<string[]>(Fact(evidence, "toolPendingCleanupPaths").TextValue!)!));
    }

    [Fact]
    public async Task IncompleteComReleaseKeepsNamesAndMarksTheCollectionPartial()
    {
        var node = JsonNode.Parse(Envelope([Record()]))!;
        node["cleanup"]!["succeeded"] = false;
        node["cleanup"]!["pending"] = JsonSerializer.SerializeToNode(new[] { "task#1", "root-folder" });
        var evidence = await Collect(node.ToJsonString());
        Assert.Equal(EvidenceStatus.Partial, evidence.Status);
        Assert.Equal(new[] { "task#1", "root-folder" }, JsonSerializer.Deserialize<string[]>(Fact(evidence, "comCleanupPending").TextValue!));
        Assert.Equal(1m, Fact(evidence, "tasksRetained").NumberValue);
    }

    [Fact]
    public async Task JobMetricsAreDetailedAndMissingMetricsStayUnknown()
    {
        var result = Completed(Envelope([])) with { Metrics = new(TimeSpan.FromMilliseconds(1250), TimeSpan.FromMilliseconds(300), 123456) };
        var evidence = Assert.Single(await new ScheduledTasksCollector(new QueryRunner(result)).CollectAsync(CancellationToken.None));
        Assert.Equal(1.25m, Fact(evidence, "queryJobElapsedSeconds").NumberValue);
        Assert.Equal(0.3m, Fact(evidence, "queryJobCpuSeconds").NumberValue);
        Assert.Equal(123456m, Fact(evidence, "queryJobPeakCommitBytes").NumberValue);
        Assert.True(Fact(evidence, "queryJobCpuSeconds").Detailed);
        var unknown = await Collect(Envelope([]));
        Assert.Null(Fact(unknown, "queryJobCpuSeconds").NumberValue);
        Assert.Null(Fact(unknown, "queryJobPeakCommitBytes").NumberValue);
    }

    [Fact]
    public async Task StderrDoesNotBecomeACompleteCollection()
    {
        var evidence = Assert.Single(await new ScheduledTasksCollector(new QueryRunner(Completed(Envelope([])) with { StandardError = "original warning" })).CollectAsync(CancellationToken.None));
        Assert.Equal(EvidenceStatus.Partial, evidence.Status);
        Assert.Equal("original warning", Fact(evidence, "toolStderr").TextValue);
    }

    [Fact]
    public async Task CancellationBeforeOrAfterTheQueryNeverPublishesASnapshot()
    {
        using var before = new CancellationTokenSource(); before.Cancel();
        var runner = new QueryRunner(Completed(Envelope([])));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ScheduledTasksCollector(runner).CollectAsync(before.Token));
        Assert.Equal(0, runner.Calls);
        using var after = new CancellationTokenSource();
        runner.OnCall = after.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ScheduledTasksCollector(runner).CollectAsync(after.Token));
        Assert.Equal(1, runner.Calls);
    }

    private static object Date(string value = "2026-10-28T09:00:00.0000000") => new { sourceType = "System.DateTime", kind = "Unspecified", value };
    private static Dictionary<string, object?> Record() => new(StringComparer.Ordinal)
    {
        ["Name"] = "Task-A",
        ["Path"] = @"\Original\Task-A",
        ["Enabled"] = false,
        ["State"] = 3,
        ["LastRunTime"] = Date(),
        ["NextRunTime"] = Date("2026-10-29T09:00:00.0000000"),
        ["LastTaskResult"] = 0,
        ["NumberOfMissedRuns"] = 0
    };
    private static string Envelope(object[] records) => JsonSerializer.Serialize(new
    {
        kind = "optimus.task-inventory",
        version = 1,
        observedAt = "2026-10-28T09:10:11.1234567-03:00",
        query = new { connected = true, traversalComplete = true, foldersVisited = 1, foldersFailed = 0, tasksExamined = records.Length, limitReached = false, issuesOmitted = 0, records, issues = Array.Empty<object>() },
        cleanup = new { succeeded = true, pending = Array.Empty<string>(), issues = Array.Empty<object>() }
    });
    private static DiagnosticFact Fact(DiagnosticEvidence evidence, string key) => evidence.Facts.Single(item => item.Key == key);
    private static ToolRunResult Completed(string raw) => new(ToolRunStatus.Exited, 0, raw, "", new(TimeSpan.Zero));
    private static async Task<DiagnosticEvidence> Collect(string raw) => Assert.Single(await new ScheduledTasksCollector(new QueryRunner(Completed(raw))).CollectAsync(CancellationToken.None));
    private sealed class QueryRunner(ToolRunResult result) : IWindowsSystemQueryRunner
    {
        public WindowsSystemQuery? Query { get; private set; }
        public int Calls { get; private set; }
        public Action? OnCall { get; set; }
        public Task<ToolRunResult> RunAsync(WindowsSystemQuery query, CancellationToken cancellationToken)
        { Calls++; Query = query; OnCall?.Invoke(); return Task.FromResult(result); }
    }
}
