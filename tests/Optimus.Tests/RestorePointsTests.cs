using System.Text.Json;
using System.Text.Json.Nodes;
using Optimus.Application;
using Optimus.Domain;
using Optimus.Infrastructure.Recovery;

namespace Optimus.Tests;

public sealed class RestorePointsTests
{
    [Fact]
    public async Task NamedQueryRetainsDescriptionSequenceCodesAndOriginalDmtfWithoutConversion()
    {
        var record = Record();
        record["Description"] = "d'água Ω $(not-executed)";
        record["SequenceNumber"] = uint.MaxValue;
        record["CreationTime"] = "20261028143000.123456-180";
        string raw = Envelope([record]);
        var runner = new Runner(Completed(raw));
        var evidence = Assert.Single(await new RestorePointsCollector(runner).CollectAsync(CancellationToken.None));
        Assert.Equal(WindowsSystemQuery.RestorePoints, runner.Query);
        Assert.Equal("windows.restore-points", evidence.CollectorId);
        Assert.Equal(1, runner.Calls);
        Assert.Equal(EvidenceStatus.Complete, evidence.Status);
        Assert.Equal(record["Description"], Fact(evidence, "restorePoints.0.Description").TextValue);
        Assert.Equal((decimal)uint.MaxValue, Fact(evidence, "restorePoints.0.SequenceNumber").NumberValue);
        Assert.Equal("20261028143000.123456-180", Fact(evidence, "restorePoints.0.CreationTime").TextValue);
        Assert.Equal("System.String", Fact(evidence, "restorePoints.0.CreationTime.sourceType").TextValue);
        Assert.Equal("APPLICATION_INSTALL", Fact(evidence, "restorePoints.0.RestorePointType.name").TextValue);
        Assert.Equal("BEGIN_SYSTEM_CHANGE", Fact(evidence, "restorePoints.0.EventType.name").TextValue);
        Assert.Equal(raw, Fact(evidence, "sourceJson").TextValue);
        Assert.True(Fact(evidence, "sourceJson").Detailed);
        foreach (string key in new[] { "restorePoints.0.Description", "restorePoints.0.SequenceNumber", "restorePoints.0.CreationTime", "restorePoints.0.RestorePointType", "restorePoints.0.EventType" }) Assert.False(Fact(evidence, key).Detailed);
        Assert.Equal("false", Fact(evidence, "protectionConfigurationChecked").TextValue);
        Assert.Equal("false", Fact(evidence, "recoveryTested").TextValue);
        Assert.Equal("false", Fact(evidence, "restorePointChangesRequested").TextValue);
        Assert.DoesNotContain(evidence.Facts, fact => fact.Key.EndsWith("creationTimeUtc", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task KnownEmptyEnumerationDoesNotClaimProtectionDisabledOrRecoveryViability()
    {
        var evidence = await Collect(Envelope([]));
        Assert.Equal(EvidenceStatus.Complete, evidence.Status);
        Assert.Equal(0m, Fact(evidence, "examinedRecords").NumberValue);
        Assert.Equal(0m, Fact(evidence, "retainedRecords").NumberValue);
        Assert.Equal(0m, Fact(evidence, "projectedRecords").NumberValue);
        Assert.Contains("Lista vazia não indica proteção desabilitada/configurada", evidence.Coverage.Scope);
        Assert.Contains("recuperação viável", evidence.Coverage.Scope);
        Assert.DoesNotContain(evidence.Facts, fact => fact.Key is "protectionDisabled" or "canRestore" or "healthy");
    }

    [Theory]
    [InlineData("System.DateTime", "Utc", "2026-10-28T12:00:00.1234567Z")]
    [InlineData("System.DateTime", "Local", "2026-10-28T09:00:00.1234567-03:00")]
    [InlineData("System.DateTime", "Unspecified", "2026-10-28T12:00:00.1234567")]
    [InlineData("System.DateTimeOffset", "Offset", "2026-10-28T17:30:00.1234567+05:30")]
    public async Task CimAdaptedDatesKeepTheirExactTypeKindAndValue(string type, string kind, string value)
    {
        var record = Record(); record["CreationTime"] = new { sourceType = type, kind, value };
        var evidence = await Collect(Envelope([record]));
        Assert.Equal(EvidenceStatus.Complete, evidence.Status);
        Assert.Equal(value, Fact(evidence, "restorePoints.0.CreationTime").TextValue);
        Assert.Equal(type, Fact(evidence, "restorePoints.0.CreationTime.sourceType").TextValue);
        Assert.Equal(kind, Fact(evidence, "restorePoints.0.CreationTime.kind").TextValue);
        if (kind == "Unspecified") Assert.Contains("fuso não informado", Fact(evidence, "restorePoints.0.CreationTime").Label);
    }

    [Theory]
    [InlineData("20261028120000.000000+***")]
    [InlineData("****1028******.000000+***")]
    [InlineData("20260231120000.000000+000")]
    [InlineData("28/10/2026 12:00")]
    public async Task UnspecifiedOrUnrecognizedCreationTextIsPreservedWithoutInventedUtc(string value)
    {
        var record = Record(); record["CreationTime"] = value;
        var evidence = await Collect(Envelope([record]));
        Assert.Equal(EvidenceStatus.Partial, evidence.Status);
        Assert.Equal(value, Fact(evidence, "restorePoints.0.CreationTime").TextValue);
        Assert.Contains(evidence.Issues, item => item.Code == IssueCode.NotSupported);
        Assert.DoesNotContain(evidence.Facts, fact => fact.Key.EndsWith("creationTimeUtc", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("Utc", "2026-10-28T12:00:00.0000000")]
    [InlineData("Unspecified", "2026-10-28T12:00:00.0000000Z")]
    [InlineData("Local", "2026-10-28")]
    public async Task DivergentDateEnvelopeKeepsUnknownRatherThanChangingTheTimezone(string kind, string value)
    {
        var record = Record(); record["CreationTime"] = new { sourceType = "System.DateTime", kind, value };
        var raw = Envelope([record]); var evidence = await Collect(raw);
        Assert.Equal(EvidenceStatus.Partial, evidence.Status);
        Assert.Null(Fact(evidence, "restorePoints.0.CreationTime").TextValue);
        Assert.Equal(raw, Fact(evidence, "sourceJson").TextValue);
    }

    [Fact]
    public async Task UnknownEnumCodesAndDuplicateSequencesArePreservedAsObservations()
    {
        var first = Record(); var second = Record();
        first["RestorePointType"] = uint.MaxValue; first["EventType"] = 77;
        var evidence = await Collect(Envelope([first, second]));
        Assert.Equal(EvidenceStatus.Complete, evidence.Status);
        Assert.Equal((decimal)uint.MaxValue, Fact(evidence, "restorePoints.0.RestorePointType").NumberValue);
        Assert.Equal(77m, Fact(evidence, "restorePoints.0.EventType").NumberValue);
        Assert.Null(Fact(evidence, "restorePoints.0.RestorePointType.name").TextValue);
        Assert.Null(Fact(evidence, "restorePoints.0.EventType.name").TextValue);
        Assert.Equal(Fact(evidence, "restorePoints.0.SequenceNumber").NumberValue, Fact(evidence, "restorePoints.1.SequenceNumber").NumberValue);
        Assert.Equal(2m, Fact(evidence, "retainedRecords").NumberValue);
    }

    [Theory]
    [InlineData("SequenceNumber", -1)]
    [InlineData("RestorePointType", 4294967296L)]
    [InlineData("EventType", -1)]
    public async Task OutOfRangeUnsignedValuesRemainUnknown(string field, long number)
    {
        var record = Record(); record[field] = number;
        var raw = Envelope([record]); var evidence = await Collect(raw);
        Assert.Equal(EvidenceStatus.Partial, evidence.Status);
        Assert.Null(Fact(evidence, "restorePoints.0." + field).NumberValue);
        Assert.Equal(raw, Fact(evidence, "sourceJson").TextValue);
    }

    [Fact]
    public async Task StringNumbersAndMissingFieldsAreNotCoercedIntoIdentityOrZero()
    {
        var record = Record(); record["SequenceNumber"] = "42"; record.Remove("EventType");
        var evidence = await Collect(Envelope([record]));
        Assert.Equal(EvidenceStatus.Partial, evidence.Status);
        Assert.Null(Fact(evidence, "restorePoints.0.SequenceNumber").NumberValue);
        Assert.Null(Fact(evidence, "restorePoints.0.EventType").NumberValue);
    }

    [Fact]
    public async Task PermissionFailureBeforeAnyRecordStaysUnavailableWithOriginalReason()
    {
        var node = JsonNode.Parse(Envelope([]))!; node["query"]!["succeeded"] = false;
        node["query"]!["issues"] = JsonSerializer.SerializeToNode(new[] { new { code = "PermissionDenied", message = "original access denied" } });
        var raw = node.ToJsonString(); var evidence = await Collect(raw);
        Assert.Equal(EvidenceStatus.Unavailable, evidence.Status);
        Assert.Equal(raw, Fact(evidence, "sourceJson").TextValue);
        Assert.Contains(evidence.Issues, issue => issue.Code == IssueCode.PermissionDenied && issue.Message.Contains("original access denied"));
    }

    [Fact]
    public async Task FailureAfterRecordsKeepsEarlierDataAndMarksTheRemainderUnknown()
    {
        var node = JsonNode.Parse(Envelope([Record()]))!; node["query"]!["succeeded"] = false;
        var evidence = await Collect(node.ToJsonString());
        Assert.Equal(EvidenceStatus.Partial, evidence.Status);
        Assert.Equal(1m, Fact(evidence, "retainedRecords").NumberValue);
        Assert.Equal("Original snapshot Ω", Fact(evidence, "restorePoints.0.Description").TextValue);
    }

    [Fact]
    public async Task ProbeCountsSixtyFifthWithoutPresentingItAsARetainedPoint()
    {
        var records = Enumerable.Range(0, 64).Select(index => { var record = Record(); record["SequenceNumber"] = index; return (object)record; }).ToArray();
        var node = JsonNode.Parse(Envelope(records))!; node["query"]!["examined"] = 65; node["query"]!["limitReached"] = true;
        var evidence = await Collect(node.ToJsonString());
        Assert.Equal(EvidenceStatus.Partial, evidence.Status);
        Assert.Equal(65m, Fact(evidence, "examinedRecords").NumberValue);
        Assert.Equal(64m, Fact(evidence, "retainedRecords").NumberValue);
        Assert.True(evidence.Coverage.LimitReached);
        Assert.DoesNotContain(evidence.Facts, fact => fact.Key.StartsWith("restorePoints.64.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ProjectionBudgetKeepsCompleteSourceRecordsAndDoesNotRenumberSkippedRows()
    {
        var records = Enumerable.Range(0, 64).Select(index => { var record = Record(); record["SequenceNumber"] = index; record["Description"] = new string('a', 1024); return (object)record; }).ToArray();
        var raw = Envelope(records);
        Assert.InRange(System.Text.Encoding.UTF8.GetByteCount(raw), 1, 180_000);
        var evidence = await Collect(raw);
        Assert.Equal(EvidenceStatus.Partial, evidence.Status);
        int projected = (int)Fact(evidence, "projectedRecords").NumberValue!.Value;
        Assert.InRange(projected, 1, 63);
        Assert.Equal(raw, Fact(evidence, "sourceJson").TextValue);
        Assert.Equal(64m, Fact(evidence, "retainedRecords").NumberValue);
        for (int index = 0; index < projected; index++) Assert.Equal((decimal)index, Fact(evidence, $"restorePoints.{index}.SequenceNumber").NumberValue);
    }

    [Fact]
    public async Task OversizedFieldIsExplicitUnknownAndItsOriginalRemainsInJson()
    {
        var record = Record(); record["Description"] = new string('Ω', 1025);
        var raw = Envelope([record]); var evidence = await Collect(raw);
        Assert.Equal(EvidenceStatus.Partial, evidence.Status);
        Assert.Null(Fact(evidence, "restorePoints.0.Description").TextValue);
        Assert.True(evidence.Coverage.LimitReached);
        Assert.Equal(raw, Fact(evidence, "sourceJson").TextValue);
    }

    [Fact]
    public async Task SourceNotesShareTheProjectionBudgetAndRemainInOriginalJson()
    {
        var node = JsonNode.Parse(Envelope([]))!;
        node["query"]!["issues"] = JsonSerializer.SerializeToNode(Enumerable.Range(0, 200).Select(index => new { code = "ReadFailed", message = index + ": " + new string('x', 200) }));
        var raw = node.ToJsonString(); var evidence = await Collect(raw);
        Assert.Equal(EvidenceStatus.Partial, evidence.Status);
        Assert.Equal(raw, Fact(evidence, "sourceJson").TextValue);
        Assert.True(evidence.Coverage.LimitReached);
        Assert.Contains(evidence.Issues, issue => issue.Message.Contains("não foram projetadas"));
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("{\"kind\":\"optimus.restore-points\",\"kind\":\"ambiguous\"}")]
    public async Task MalformedOrAmbiguousEnvelopeRetainsRawAndNoProjectedRecords(string raw)
    {
        var evidence = await Collect(raw);
        Assert.Equal(EvidenceStatus.Unavailable, evidence.Status);
        Assert.Equal(raw, Fact(evidence, "sourceJson").TextValue);
        Assert.DoesNotContain(evidence.Facts, fact => fact.Key.StartsWith("restorePoints.", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("unknownMember")]
    [InlineData("nestedDuplicate")]
    [InlineData("wrongIdentity")]
    [InlineData("badCount")]
    public async Task InvalidContractCannotPublishPartiallyTrustedPointFacts(string variant)
    {
        var node = JsonNode.Parse(Envelope([Record()]))!;
        if (variant == "unknownMember") node["query"]!["remoteTarget"] = "not in contract";
        if (variant == "wrongIdentity") node["kind"] = "foreign.restore-points";
        if (variant == "badCount") node["query"]!["examined"] = 66;
        string raw = node.ToJsonString();
        if (variant == "nestedDuplicate") raw = raw.Replace("\"SequenceNumber\":42", "\"SequenceNumber\":42,\"SequenceNumber\":43", StringComparison.Ordinal);
        var evidence = await Collect(raw);
        Assert.Equal(EvidenceStatus.Unavailable, evidence.Status);
        Assert.Equal(raw, Fact(evidence, "sourceJson").TextValue);
        Assert.DoesNotContain(evidence.Facts, fact => fact.Key.StartsWith("restorePoints.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Utf8ResponseCapDoesNotTreatAnOversizedPrefixAsACompleteDocument()
    {
        string raw = new('Ω', RestorePointsCollector.MaximumJsonBytes / 2 + 1);
        var evidence = await Collect(raw);
        Assert.Equal(EvidenceStatus.Unavailable, evidence.Status);
        Assert.DoesNotContain(evidence.Facts, fact => fact.Key == "sourceJson");
        Assert.Equal(262146m, Fact(evidence, "sourceJsonUtf8Bytes").NumberValue);
        Assert.True(evidence.Coverage.LimitReached);
    }

    [Fact]
    public async Task TransportFailurePreservesStderrReasonAndPendingResources()
    {
        var result = new ToolRunResult(ToolRunStatus.CleanupFailed, null, "original stdout", "original stderr", new(TimeSpan.Zero), new(ErrorCode.PermissionDenied, "original primary error", [@"D:\owned pending Ω"]));
        var evidence = Assert.Single(await new RestorePointsCollector(new Runner(result)).CollectAsync(CancellationToken.None));
        Assert.Equal(EvidenceStatus.Unavailable, evidence.Status);
        Assert.Equal("original stdout", Fact(evidence, "sourceJson").TextValue);
        Assert.Equal("original stderr", Fact(evidence, "toolStderr").TextValue);
        Assert.Equal("original primary error", Fact(evidence, "toolErrorMessage").TextValue);
        Assert.Equal(@"D:\owned pending Ω", Assert.Single(JsonSerializer.Deserialize<string[]>(Fact(evidence, "toolPendingCleanupPaths").TextValue!)!));
    }

    [Fact]
    public async Task JobMetricsAreDetailedWithMissingValuesUnknown()
    {
        var known = Completed(Envelope([])) with { Metrics = new(TimeSpan.FromMilliseconds(1250), TimeSpan.FromMilliseconds(300), 12345) };
        var evidence = Assert.Single(await new RestorePointsCollector(new Runner(known)).CollectAsync(CancellationToken.None));
        Assert.Equal(1.25m, Fact(evidence, "elapsedSeconds").NumberValue);
        Assert.Equal(0.3m, Fact(evidence, "cpuSeconds").NumberValue);
        Assert.Equal(12345m, Fact(evidence, "peakProcessFamilyMemoryBytes").NumberValue);
        Assert.True(Fact(evidence, "cpuSeconds").Detailed);
        var unknown = await Collect(Envelope([]));
        Assert.Null(Fact(unknown, "cpuSeconds").NumberValue);
        Assert.Null(Fact(unknown, "peakProcessFamilyMemoryBytes").NumberValue);
    }

    [Fact]
    public async Task CancellationBeforeAndAfterTheHostCannotPublishAPointSnapshot()
    {
        using var before = new CancellationTokenSource(); before.Cancel();
        var runner = new Runner(Completed(Envelope([])));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new RestorePointsCollector(runner).CollectAsync(before.Token));
        Assert.Equal(0, runner.Calls);
        using var after = new CancellationTokenSource(); runner.OnCall = after.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new RestorePointsCollector(runner).CollectAsync(after.Token));
        Assert.Equal(1, runner.Calls);
    }

    private static Dictionary<string, object?> Record() => new(StringComparer.Ordinal)
    { ["Description"] = "Original snapshot Ω", ["RestorePointType"] = 0, ["EventType"] = 100, ["SequenceNumber"] = 42, ["CreationTime"] = "20261028120000.000000+000" };
    private static string Envelope(object[] records) => JsonSerializer.Serialize(new
    {
        kind = "optimus.restore-points",
        version = 1,
        observedAt = "2026-10-28T09:00:00.0000000-03:00",
        query = new { succeeded = true, examined = records.Length, limitReached = false, records, issues = Array.Empty<object>() }
    });
    private static DiagnosticFact Fact(DiagnosticEvidence evidence, string key) => evidence.Facts.Single(fact => fact.Key == key);
    private static ToolRunResult Completed(string raw) => new(ToolRunStatus.Exited, 0, raw, "", new(TimeSpan.Zero));
    private static async Task<DiagnosticEvidence> Collect(string raw) => Assert.Single(await new RestorePointsCollector(new Runner(Completed(raw))).CollectAsync(CancellationToken.None));
    private sealed class Runner(ToolRunResult result) : IWindowsSystemQueryRunner
    {
        public int Calls { get; private set; }
        public WindowsSystemQuery? Query { get; private set; }
        public Action? OnCall { get; set; }
        public Task<ToolRunResult> RunAsync(WindowsSystemQuery query, CancellationToken cancellationToken) { Calls++; Query = query; OnCall?.Invoke(); return Task.FromResult(result); }
    }
}
