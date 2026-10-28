using System.Text;
using System.Text.Json;
using Optimus.Application;
using Optimus.Domain;
using Optimus.Infrastructure.Processes;

namespace Optimus.Tests;

public sealed class WindowsProcessesTests
{
    private const string ObservedAt = "2026-10-28T09:10:11.1234567-03:00";

    [Fact]
    public async Task NamedReadRetainsOriginalObservationAndDoesNotClaimProcessOwnership()
    {
        var record = Record();
        record["ExecutablePath"] = @"D:\Apps\d'água Ω\sample.exe";
        var json = Document([record]);
        var runner = new Probe(_ => Completed(json));

        var evidence = Assert.Single(await new WindowsProcessesCollector(runner).CollectAsync(CancellationToken.None));

        Assert.Equal(WindowsSystemQuery.ProcessInventory, runner.Query);
        Assert.Equal(1, runner.Calls);
        Assert.Equal(MaintenanceCollectors.Processes, evidence.CollectorId);
        Assert.Equal(EvidenceStatus.Complete, evidence.Status);
        Assert.Equal(DateTimeOffset.Parse(ObservedAt), evidence.CapturedAt);
        Assert.Equal(TimeSpan.FromHours(-3), evidence.CapturedAt.Offset);
        Assert.Equal(json, Fact(evidence, "sourceJson").TextValue);
        Assert.Equal(ObservedAt, Fact(evidence, "sourceObservedAt").TextValue);
        Assert.Equal("sample.exe", Fact(evidence, "processes.0.Name").TextValue);
        Assert.Equal(record["ExecutablePath"], Fact(evidence, "processes.0.ExecutablePath").TextValue);
        Assert.True(Fact(evidence, "processes.0.ExecutablePath").Detailed);
        Assert.False(Fact(evidence, "processes.0.Name").Detailed);
        Assert.False(Fact(evidence, "processes.0.ProcessId").Detailed);
        Assert.False(Fact(evidence, "processes.0.WorkingSetSize").Detailed);
        Assert.Contains("não comprovam ownership", evidence.Coverage.Scope);
        Assert.Contains("não atômica", evidence.Coverage.Scope);
        Assert.DoesNotContain(evidence.Facts, fact => fact.Key.Contains("percent", StringComparison.OrdinalIgnoreCase));
        Assert.True(SessionValidation.IsValid(new(Guid.NewGuid(), evidence.CapturedAt, evidence.CapturedAt, SessionStatus.Complete, [evidence], [])));
    }

    [Fact]
    public async Task UnsignedCountersRetainAllSixtyFourBitsWithoutPercentageOrLossyConversion()
    {
        var record = Record();
        record["WorkingSetSize"] = ulong.MaxValue;
        record["KernelModeTime"] = 9_007_199_254_740_993UL;
        record["UserModeTime"] = ulong.MaxValue;
        record["ProcessId"] = uint.MaxValue;
        record["ParentProcessId"] = 0U;
        record["ThreadCount"] = uint.MaxValue;

        var evidence = await Collect(Document([record]));

        Assert.Equal(EvidenceStatus.Complete, evidence.Status);
        Assert.Equal(18_446_744_073_709_551_615m, Fact(evidence, "processes.0.WorkingSetSize").NumberValue);
        Assert.Equal("bytes", Fact(evidence, "processes.0.WorkingSetSize").Unit);
        Assert.Equal(9_007_199_254_740_993m, Fact(evidence, "processes.0.KernelModeTime").NumberValue);
        Assert.Equal(18_446_744_073_709_551_615m, Fact(evidence, "processes.0.UserModeTime").NumberValue);
        Assert.Equal("100 ns (qualificador CIM)", Fact(evidence, "processes.0.KernelModeTime").Unit);
        Assert.Equal(4_294_967_295m, Fact(evidence, "processes.0.ProcessId").NumberValue);
        Assert.Equal(0m, Fact(evidence, "processes.0.ParentProcessId").NumberValue);
        Assert.Equal(4_294_967_295m, Fact(evidence, "processes.0.ThreadCount").NumberValue);
        Assert.DoesNotContain(evidence.Facts, fact => fact.Key.StartsWith("processes.", StringComparison.Ordinal) && fact.Unit == "s");
    }

    [Theory]
    [InlineData("System.DateTime", "Utc", "2026-10-28T08:00:00.0000000Z")]
    [InlineData("System.DateTime", "Unspecified", "2026-10-28T08:00:00.0000000")]
    [InlineData("System.DateTime", "Local", "2026-10-28T08:00:00.0000000-03:00")]
    [InlineData("System.DateTimeOffset", "Offset", "2026-10-28T08:00:00.0000000+00:00")]
    public async Task TypedCreationDatePreservesKindAndTextWithoutTimezoneInference(string type, string kind, string value)
    {
        var record = Record();
        record["CreationDate"] = new { sourceType = type, value, kind };

        var evidence = await Collect(Document([record]));

        Assert.Equal(EvidenceStatus.Complete, evidence.Status);
        Assert.Equal(value, Fact(evidence, "processes.0.CreationDate").TextValue);
        Assert.Equal(type, Fact(evidence, "processes.0.CreationDate.sourceType").TextValue);
        Assert.Equal(kind, Fact(evidence, "processes.0.CreationDate.kind").TextValue);
        if (kind == "Unspecified") Assert.Contains("fuso não informado", Fact(evidence, "processes.0.CreationDate").Label);
    }

    [Theory]
    [InlineData("ProcessId", "-1")]
    [InlineData("ParentProcessId", "4294967296")]
    [InlineData("ThreadCount", "\"2\"")]
    [InlineData("WorkingSetSize", "18446744073709551616")]
    [InlineData("KernelModeTime", "-1")]
    [InlineData("UserModeTime", "1.5")]
    [InlineData("WorkingSetSize", "\"123\"")]
    [InlineData("CreationDate", "\"2026-10-28T08:00:00Z\"")]
    [InlineData("CreationDate", "{\"sourceType\":\"System.DateTime\",\"value\":\"2026-10-28T08:00:00.0000000Z\",\"kind\":\"Unspecified\"}")]
    [InlineData("ExecutablePath", "null")]
    [InlineData("Name", "false")]
    public async Task UnsupportedFieldsRemainUnknownAndDoNotEraseOtherProcessFacts(string field, string raw)
    {
        using var document = JsonDocument.Parse(raw);
        var record = Record();
        record[field] = document.RootElement.Clone();
        var json = Document([record]);

        var evidence = await Collect(json);

        Assert.Equal(EvidenceStatus.Partial, evidence.Status);
        Assert.Null(Fact(evidence, "processes.0." + field).TextValue);
        Assert.Null(Fact(evidence, "processes.0." + field).NumberValue);
        Assert.NotEmpty(evidence.Issues);
        Assert.Equal(json, Fact(evidence, "sourceJson").TextValue);
        Assert.Equal(1m, Fact(evidence, "projectedRecords").NumberValue);
    }

    [Fact]
    public async Task DuplicatePidAndNameRemainSeparateObservationsRatherThanOwnershipClaims()
    {
        var evidence = await Collect(Document([Record(), Record()]));
        Assert.Equal(2m, Fact(evidence, "projectedRecords").NumberValue);
        Assert.Equal(Fact(evidence, "processes.0.ProcessId").NumberValue, Fact(evidence, "processes.1.ProcessId").NumberValue);
        Assert.Equal(2, evidence.Coverage.ExaminedItems);
    }

    [Fact]
    public async Task ProjectionOrdersOnlyRetainedRowsByKnownWorkingSetAndPreservesSourceIndexes()
    {
        var records = Enumerable.Range(0, 5).Select(_ => Record()).ToArray();
        records[0]["WorkingSetSize"] = null;
        records[1]["WorkingSetSize"] = 2UL;
        records[2]["WorkingSetSize"] = 100UL;
        records[3]["WorkingSetSize"] = 100UL;
        records[4]["WorkingSetSize"] = 0UL;
        var json = Document(records);

        var evidence = await Collect(json);

        var displayedNames = evidence.Facts.Where(item => item.Key.EndsWith(".Name", StringComparison.Ordinal)).Select(item => item.Key).ToArray();
        Assert.Equal(["processes.2.Name", "processes.3.Name", "processes.1.Name", "processes.4.Name", "processes.0.Name"], displayedNames);
        Assert.Equal(2m, Fact(evidence, "processes.2.sourceIndex").NumberValue);
        Assert.Equal(100m, Fact(evidence, "processes.2.WorkingSetSize").NumberValue);
        Assert.Null(Fact(evidence, "processes.0.WorkingSetSize").NumberValue);
        Assert.Equal(json, Fact(evidence, "sourceJson").TextValue);
        Assert.Equal(EvidenceStatus.Partial, evidence.Status);
        Assert.Contains("Não é ranking de todos", Fact(evidence, "projectionOrder").TextValue!);
    }

    [Fact]
    public async Task EmptySuccessfulQueryIsDistinctFromAnUnavailableQuery()
    {
        var empty = await Collect(Document([]));
        var failed = await Collect(Document([], succeeded: false));
        Assert.Equal(EvidenceStatus.Complete, empty.Status);
        Assert.Equal(0m, Fact(empty, "retainedRecords").NumberValue);
        Assert.Equal(EvidenceStatus.Unavailable, failed.Status);
        Assert.Equal("false", Fact(failed, "querySucceeded").TextValue);
    }

    [Fact]
    public async Task ProbeAndProjectionLimitsKeepRetainedSourceAndExplicitOmissions()
    {
        var records = Enumerable.Range(0, 256).Select(_ => Record()).ToArray();
        var json = Document(records, examined: 257, limited: true);
        var evidence = await Collect(json);
        Assert.Equal(EvidenceStatus.Partial, evidence.Status);
        Assert.Equal(257, evidence.Coverage.ExaminedItems);
        Assert.True(evidence.Coverage.LimitReached);
        Assert.Equal(256m, Fact(evidence, "retainedRecords").NumberValue);
        Assert.InRange(Fact(evidence, "projectedRecords").NumberValue!.Value, 1m, 255m);
        Assert.Equal(json, Fact(evidence, "sourceJson").TextValue);
        Assert.Contains(evidence.Issues, issue => issue.Code == IssueCode.LimitReached);
    }

    [Fact]
    public async Task LateProviderFailureKeepsEarlierRecordsAsPartialEvidence()
    {
        var json = Document([Record()], succeeded: false, issues: [new { code = "PermissionDenied", message = "CIM fixture denied continuation" }]);
        var evidence = await Collect(json);
        Assert.Equal(EvidenceStatus.Partial, evidence.Status);
        Assert.Equal("sample.exe", Fact(evidence, "processes.0.Name").TextValue);
        Assert.Contains(evidence.Issues, issue => issue.Code == IssueCode.PermissionDenied);
        Assert.Equal(json, Fact(evidence, "sourceJson").TextValue);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("{\"kind\":\"optimus.process-inventory\",\"version\":1}")]
    [InlineData("{\"kind\":\"optimus.process-inventory\",\"kind\":\"other\",\"version\":1,\"observedAt\":\"2026-10-28T08:00:00Z\",\"query\":{}}")]
    public async Task MalformedOrAmbiguousEnvelopeNeverProjectsTrustedProcessFacts(string json)
    {
        var evidence = await Collect(json);
        Assert.Equal(EvidenceStatus.Unavailable, evidence.Status);
        Assert.Equal(json, Fact(evidence, "sourceJson").TextValue);
        Assert.DoesNotContain(evidence.Facts, item => item.Key.StartsWith("processes.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnUnselectedFieldOrNestedDuplicateInvalidatesTheEnvelope()
    {
        var record = Record();
        record["CommandLine"] = "must not be selected";
        Assert.Equal(EvidenceStatus.Unavailable, (await Collect(Document([record]))).Status);
        var json = Document([Record()]).Replace("\"ProcessId\":17", "\"ProcessId\":17,\"ProcessId\":18", StringComparison.Ordinal);
        Assert.Equal(EvidenceStatus.Unavailable, (await Collect(json)).Status);
    }

    [Fact]
    public async Task OversizedUtf8ResponseIsOmittedWithoutInterpretingAPrefix()
    {
        var json = new string('Ω', WindowsProcessesCollector.MaximumJsonBytes / 2 + 1);
        var evidence = await Collect(json);
        Assert.Equal(EvidenceStatus.Unavailable, evidence.Status);
        Assert.DoesNotContain(evidence.Facts, item => item.Key == "sourceJson");
        Assert.Equal(WindowsProcessesCollector.MaximumJsonBytes + 2m, Fact(evidence, "sourceJsonUtf8Bytes").NumberValue);
        Assert.Contains(evidence.Issues, issue => issue.Code == IssueCode.LimitReached);
    }

    [Fact]
    public async Task OversizedFieldIsUnknownRatherThanATruncatedPath()
    {
        var record = Record();
        record["ExecutablePath"] = new string('x', 2049);
        var evidence = await Collect(Document([record]));
        Assert.Equal(EvidenceStatus.Partial, evidence.Status);
        Assert.Null(Fact(evidence, "processes.0.ExecutablePath").TextValue);
        Assert.Contains(evidence.Issues, issue => issue.Code == IssueCode.LimitReached);
    }

    [Fact]
    public async Task FailureRetainsPrimaryErrorStderrRawBytesAndCleanupOwnership()
    {
        var execution = new ToolRunResult(ToolRunStatus.CleanupFailed, null, "bounded raw", "stderr Ω", new(TimeSpan.FromSeconds(2)),
            new(ErrorCode.ToolFailed, "original fixture failure", [@"D:\fixture\owned-resource"]), [1, 2], [3, 4]);
        var evidence = Assert.Single(await new WindowsProcessesCollector(new Probe(_ => execution)).CollectAsync(CancellationToken.None));
        Assert.Equal(EvidenceStatus.Unavailable, evidence.Status);
        Assert.Equal("original fixture failure", Fact(evidence, "toolErrorMessage").TextValue);
        Assert.Equal("stderr Ω", Fact(evidence, "toolStderr").TextValue);
        Assert.Equal("bounded raw", Fact(evidence, "sourceJson").TextValue);
        Assert.Equal("AQI=", Fact(evidence, "toolStdoutBytesBase64").TextValue);
        Assert.Equal("AwQ=", Fact(evidence, "toolStderrBytesBase64").TextValue);
        Assert.Contains(@"owned-resource", Fact(evidence, "toolPendingCleanupPaths").TextValue!);
    }

    [Fact]
    public async Task JobMetricsStayDetailedAndMissingCountersStayUnknown()
    {
        var execution = Completed(Document([])) with { Metrics = new(TimeSpan.FromTicks(15), TimeSpan.FromTicks(9), 1234) };
        var evidence = Assert.Single(await new WindowsProcessesCollector(new Probe(_ => execution)).CollectAsync(CancellationToken.None));
        Assert.Equal(0.0000015m, Fact(evidence, "elapsedSeconds").NumberValue);
        Assert.Equal(0.0000009m, Fact(evidence, "cpuSeconds").NumberValue);
        Assert.Equal(1234m, Fact(evidence, "peakProcessFamilyMemoryBytes").NumberValue);
        Assert.True(Fact(evidence, "cpuSeconds").Detailed);
        var missing = await Collect(Document([]));
        Assert.Null(Fact(missing, "cpuSeconds").NumberValue);
        Assert.Null(Fact(missing, "peakProcessFamilyMemoryBytes").NumberValue);
    }

    [Fact]
    public async Task CancellationBeforeAndAfterTheHostDoesNotPublishACollection()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var untouched = new Probe(_ => throw new InvalidOperationException("Pre-cancelled query must not run"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new WindowsProcessesCollector(untouched).CollectAsync(cancelled.Token));
        Assert.Equal(0, untouched.Calls);
        using var late = new CancellationTokenSource();
        var host = new Probe(_ => { late.Cancel(); return Completed(Document([Record()])); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new WindowsProcessesCollector(host).CollectAsync(late.Token));
    }

    private static Dictionary<string, object?> Record() => new(StringComparer.Ordinal)
    {
        ["Name"] = "sample.exe",
        ["ProcessId"] = 17U,
        ["ParentProcessId"] = 9U,
        ["CreationDate"] = new { sourceType = "System.DateTime", value = "2026-10-28T08:00:00.0000000", kind = "Unspecified" },
        ["WorkingSetSize"] = 123456UL,
        ["KernelModeTime"] = 100UL,
        ["UserModeTime"] = 200UL,
        ["ExecutablePath"] = @"D:\Apps\sample.exe",
        ["ThreadCount"] = 4U
    };
    private static string Document(IReadOnlyList<Dictionary<string, object?>> records, bool succeeded = true, int? examined = null, bool limited = false, object[]? issues = null) =>
        JsonSerializer.Serialize(new
        {
            kind = "optimus.process-inventory",
            version = 1,
            observedAt = ObservedAt,
            query = new { succeeded, examined = examined ?? records.Count, limitReached = limited, records, issues = issues ?? [] }
        });
    private static ToolRunResult Completed(string json) => new(ToolRunStatus.Exited, 0, json, "", new(TimeSpan.Zero));
    private static async Task<DiagnosticEvidence> Collect(string json) => Assert.Single(await new WindowsProcessesCollector(new Probe(_ => Completed(json))).CollectAsync(CancellationToken.None));
    private static DiagnosticFact Fact(DiagnosticEvidence evidence, string key) => evidence.Facts.Single(item => item.Key == key);
    private sealed class Probe(Func<WindowsSystemQuery, ToolRunResult> run) : IWindowsSystemQueryRunner
    {
        public WindowsSystemQuery? Query { get; private set; }
        public int Calls { get; private set; }
        public Task<ToolRunResult> RunAsync(WindowsSystemQuery query, CancellationToken cancellationToken)
        { Query = query; Calls++; return Task.FromResult(run(query)); }
    }
}
