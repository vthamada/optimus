using System.Text;
using System.Text.Json;
using Optimus.Application;
using Optimus.Domain;
using Optimus.Infrastructure.Storage;

namespace Optimus.Tests;

public sealed class StorageDevicesTests
{
    private const string Observed = "2026-10-28T10:20:30.1234567-03:00";

    [Fact]
    public async Task NamedLocalSnapshotPreservesUInt64IdentityDateAndIndependentSourceIndices()
    {
        var drive = Drive();
        drive["Size"] = ulong.MaxValue;
        drive["Index"] = uint.MaxValue;
        drive["DeviceID"] = "\\\\.\\PHYSICALDRIVE7";
        drive["SerialNumber"] = "  serial Ω with spaces  ";
        var partition = Partition();
        partition["Size"] = 9007199254740993UL;
        partition["StartingOffset"] = ulong.MaxValue;
        partition["DiskIndex"] = 7u;
        var json = Document([drive], [partition]);
        var runner = new QueryRunner(_ => Completed(json));

        var evidence = Assert.Single(await new StorageDevicesCollector(runner).CollectAsync(CancellationToken.None));

        Assert.Equal(WindowsSystemQuery.StorageInventory, runner.Query);
        Assert.Equal(1, runner.Calls);
        Assert.Equal(MaintenanceCollectors.Storage, evidence.CollectorId);
        Assert.Equal(EvidenceStatus.Complete, evidence.Status);
        Assert.Equal(DateTimeOffset.Parse(Observed), evidence.CapturedAt);
        Assert.Equal(TimeSpan.FromHours(-3), evidence.CapturedAt.Offset);
        Assert.Equal(json, Fact(evidence, "sourceJson").TextValue);
        Assert.Equal(Observed, Fact(evidence, "sourceObservedAt").TextValue);
        Assert.Equal((decimal)ulong.MaxValue, Fact(evidence, "drives.0.Size").NumberValue);
        Assert.Equal("bytes", Fact(evidence, "drives.0.Size").Unit);
        Assert.Equal((decimal)uint.MaxValue, Fact(evidence, "drives.0.Index").NumberValue);
        Assert.Equal(0m, Fact(evidence, "drives.0.sourceIndex").NumberValue);
        Assert.Equal(drive["DeviceID"], Fact(evidence, "drives.0.DeviceID").TextValue);
        Assert.Equal(drive["SerialNumber"], Fact(evidence, "drives.0.SerialNumber").TextValue);
        Assert.Equal(9007199254740993m, Fact(evidence, "partitions.0.Size").NumberValue);
        Assert.Equal((decimal)ulong.MaxValue, Fact(evidence, "partitions.0.StartingOffset").NumberValue);
        Assert.Equal(7m, Fact(evidence, "partitions.0.DiskIndex").NumberValue);
        Assert.Equal("false", Fact(evidence, "diskPartitionAssociationResolved").TextValue);
        Assert.Equal("false", Fact(evidence, "physicalHealthTested").TextValue);
        Assert.False(Fact(evidence, "drives.0.Model").Detailed);
        Assert.False(Fact(evidence, "partitions.0.DeviceID").Detailed);
        Assert.True(Fact(evidence, "drives.0.SerialNumber").Detailed);
        Assert.DoesNotContain(evidence.Facts, fact => fact.Key is "healthy" or "totalCapacity" or "drivePartitionLink");
        Assert.True(SessionValidation.IsValid(new(Guid.NewGuid(), evidence.CapturedAt, evidence.CapturedAt, SessionStatus.Complete, [evidence], [])));
    }

    [Fact]
    public async Task KnownEmptySourcesRemainDistinctFromUnavailableDiscovery()
    {
        var evidence = await Collect(Document([], []));
        Assert.Equal(EvidenceStatus.Complete, evidence.Status);
        Assert.Equal(0, evidence.Coverage.ExaminedItems);
        Assert.Equal(0m, Fact(evidence, "drives.retained").NumberValue);
        Assert.Equal(0m, Fact(evidence, "partitions.retained").NumberValue);
        Assert.Empty(evidence.Issues);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OneFailedQueryPreservesTheOtherSourceWithoutInferringAssociations(bool driveFails)
    {
        var json = Document(driveFails ? [] : [Drive()], driveFails ? [Partition()] : [], driveSucceeded: !driveFails, partitionSucceeded: driveFails);
        var evidence = await Collect(json);
        Assert.Equal(EvidenceStatus.Partial, evidence.Status);
        Assert.Contains(evidence.Issues, issue => issue.Code == IssueCode.ReadFailed);
        Assert.Equal(json, Fact(evidence, "sourceJson").TextValue);
        Assert.Equal(driveFails ? "false" : "true", Fact(evidence, "drives.querySucceeded").TextValue);
        Assert.Equal(driveFails ? "true" : "false", Fact(evidence, "partitions.querySucceeded").TextValue);
        Assert.NotNull(Fact(evidence, driveFails ? "partitions.0.DeviceID" : "drives.0.Model").TextValue);
    }

    [Fact]
    public async Task BothFailedSourcesWithNoEarlierRecordsRemainUnavailable()
    {
        var evidence = await Collect(Document([], [], driveSucceeded: false, partitionSucceeded: false));
        Assert.Equal(EvidenceStatus.Unavailable, evidence.Status);
        Assert.Equal("false", Fact(evidence, "drives.querySucceeded").TextValue);
        Assert.Equal("false", Fact(evidence, "partitions.querySucceeded").TextValue);
    }

    [Fact]
    public async Task EarlierRecordsSurviveLateQueryFailureAndRepeatedIdsAreNotCollapsed()
    {
        var evidence = await Collect(Document([Drive(), Drive()], [Partition(), Partition()], driveSucceeded: false));
        Assert.Equal(EvidenceStatus.Partial, evidence.Status);
        Assert.Equal(2m, Fact(evidence, "drives.retained").NumberValue);
        Assert.Equal(2m, Fact(evidence, "partitions.retained").NumberValue);
        Assert.Equal(Fact(evidence, "drives.0.DeviceID").TextValue, Fact(evidence, "drives.1.DeviceID").TextValue);
        Assert.Equal(1m, Fact(evidence, "partitions.1.sourceIndex").NumberValue);
    }

    [Theory]
    [InlineData("Size", "-1")]
    [InlineData("Size", "18446744073709551616")]
    [InlineData("Size", "\"1000\"")]
    [InlineData("Size", "null")]
    [InlineData("Index", "4294967296")]
    [InlineData("Partitions", "false")]
    [InlineData("Model", "{\"sourceType\":\"Unexpected\",\"omitted\":true}")]
    public async Task DivergentScalarTypesStayUnknownInsteadOfBeingNormalized(string field, string raw)
    {
        var record = Drive();
        using var value = JsonDocument.Parse(raw);
        record[field] = value.RootElement.Clone();
        var json = Document([record], [Partition()]);
        var evidence = await Collect(json);
        Assert.Equal(EvidenceStatus.Partial, evidence.Status);
        var fact = Fact(evidence, "drives.0." + field);
        Assert.Null(fact.TextValue);
        Assert.Null(fact.NumberValue);
        Assert.Equal(json, Fact(evidence, "sourceJson").TextValue);
        Assert.Equal("\\\\.\\PHYSICALDRIVE0", Fact(evidence, "drives.0.DeviceID").TextValue);
    }

    [Fact]
    public async Task ZeroAndFalseRemainReportedValuesWhileMissingFieldsRemainUnknown()
    {
        var drive = Drive(); drive["Size"] = 0UL; drive.Remove("SerialNumber");
        var partition = Partition(); partition["BootPartition"] = false; partition["PrimaryPartition"] = null;
        var evidence = await Collect(Document([drive], [partition]));
        Assert.Equal(EvidenceStatus.Partial, evidence.Status);
        Assert.Equal(0m, Fact(evidence, "drives.0.Size").NumberValue);
        Assert.Equal("false", Fact(evidence, "partitions.0.BootPartition").TextValue);
        Assert.Null(Fact(evidence, "drives.0.SerialNumber").TextValue);
        Assert.Null(Fact(evidence, "partitions.0.PrimaryPartition").TextValue);
        Assert.Equal("OK", Fact(evidence, "drives.0.Status").TextValue);
        Assert.Equal("false", Fact(evidence, "physicalHealthTested").TextValue);
    }

    [Fact]
    public async Task OversizedFieldsAreNotPublishedAsTruncatedOriginals()
    {
        var record = Drive(); record["Model"] = new string('M', 2049);
        var json = Document([record], [Partition()]);
        var evidence = await Collect(json);
        Assert.Equal(EvidenceStatus.Partial, evidence.Status);
        Assert.Null(Fact(evidence, "drives.0.Model").TextValue);
        Assert.Equal(json, Fact(evidence, "sourceJson").TextValue);
        Assert.Contains(evidence.Issues, issue => issue.Code == IssueCode.LimitReached);
    }

    [Fact]
    public async Task SourceBudgetUsesUtf8BytesBeforeParsing()
    {
        string output = new('Ω', StorageDevicesCollector.MaximumResponseBytes);
        var evidence = await Collect(output);
        Assert.Equal(EvidenceStatus.Unavailable, evidence.Status);
        Assert.DoesNotContain(evidence.Facts, fact => fact.Key == "sourceJson");
        Assert.Equal(2m * StorageDevicesCollector.MaximumResponseBytes, Fact(evidence, "sourceJsonUtf8Bytes").NumberValue);
        Assert.Contains(evidence.Issues, issue => issue.Code == IssueCode.LimitReached);
    }

    [Theory]
    [InlineData("{\"kind\":\"optimus.storage-inventory\",\"kind\":\"optimus.storage-inventory\"}")]
    [InlineData("{\"kind\":\"optimus.storage-inventory\",\"version\":2,\"observedAt\":\"2026-10-28T00:00:00Z\",\"drives\":{},\"partitions\":{}}")]
    [InlineData("not JSON")]
    public async Task MalformedEnvelopesRemainUnavailableWithOriginalData(string json)
    {
        var evidence = await Collect(json);
        Assert.Equal(EvidenceStatus.Unavailable, evidence.Status);
        Assert.Equal(json, Fact(evidence, "sourceJson").TextValue);
        Assert.DoesNotContain(evidence.Facts, fact => fact.Key.StartsWith("drives.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DuplicateRecordPropertiesInvalidateBothProjectionsButRetainSource()
    {
        string json = Document([Drive()], [Partition()]).Replace("\"Index\":0", "\"Index\":0,\"Index\":0", StringComparison.Ordinal);
        var evidence = await Collect(json);
        Assert.Equal(EvidenceStatus.Unavailable, evidence.Status);
        Assert.Equal(json, Fact(evidence, "sourceJson").TextValue);
        Assert.DoesNotContain(evidence.Facts, fact => fact.Key.StartsWith("partitions.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InconsistentCoverageCannotClaimCompleteCatalog()
    {
        string json = Document([Drive()], [], driveExamined: 2);
        var evidence = await Collect(json);
        Assert.Equal(EvidenceStatus.Unavailable, evidence.Status);
        Assert.DoesNotContain(evidence.Facts, fact => fact.Key.StartsWith("drives.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ProbeIsCountedWithoutAddingAnExtraDriveRecord()
    {
        var drives = Enumerable.Range(0, StorageDevicesCollector.MaximumDriveRecords).Select(_ => Drive()).ToArray();
        var evidence = await Collect(Document(drives, [], driveExamined: StorageDevicesCollector.MaximumDriveRecords + 1, driveLimited: true));
        Assert.Equal(EvidenceStatus.Partial, evidence.Status);
        Assert.Equal(33m, Fact(evidence, "drives.examined").NumberValue);
        Assert.Equal(32m, Fact(evidence, "drives.retained").NumberValue);
        Assert.True(evidence.Coverage.LimitReached);
    }

    [Fact]
    public async Task IndependentProjectionBudgetsCannotStarvePartitionFacts()
    {
        var drives = Enumerable.Range(0, 32).Select(_ => { var drive = Drive(); drive["Model"] = new string('M', 1500); drive["SerialNumber"] = new string('S', 1500); drive["FirmwareRevision"] = new string('F', 1500); return drive; }).ToArray();
        var partitions = Enumerable.Range(0, 128).Select(_ => Partition()).ToArray();
        string json = Document(drives, partitions);
        Assert.True(Encoding.UTF8.GetByteCount(json) < StorageDevicesCollector.MaximumResponseBytes);
        var evidence = await Collect(json);
        Assert.Equal(EvidenceStatus.Partial, evidence.Status);
        Assert.True(Fact(evidence, "drives.projected").NumberValue < 32);
        Assert.True(Fact(evidence, "partitions.projected").NumberValue > 0);
        Assert.Equal(128m, Fact(evidence, "partitions.retained").NumberValue);
        Assert.Equal(json, Fact(evidence, "sourceJson").TextValue);
    }

    [Fact]
    public async Task PrimaryTransportFailureAndPendingResourcesRemainDistinctFromUnavailableInventory()
    {
        const string raw = "{partial original";
        var run = new ToolRunResult(ToolRunStatus.CleanupFailed, 7, raw, "original stderr Ω", new(TimeSpan.FromMilliseconds(1500), TimeSpan.FromMilliseconds(250), 12345),
            new(ErrorCode.ToolFailed, "primary reason Ω", ["D:\\owned Ω"]), Encoding.UTF8.GetBytes(raw), [1, 2], 65001);
        var evidence = Assert.Single(await new StorageDevicesCollector(new QueryRunner(_ => run)).CollectAsync(CancellationToken.None));
        Assert.Equal(EvidenceStatus.Unavailable, evidence.Status);
        Assert.Equal(raw, Fact(evidence, "sourceJson").TextValue);
        Assert.Equal("primary reason Ω", Fact(evidence, "toolErrorMessage").TextValue);
        Assert.Equal("original stderr Ω", Fact(evidence, "toolStderr").TextValue);
        Assert.Contains("owned", Fact(evidence, "toolPendingCleanupPaths").TextValue);
        Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes(raw)), Fact(evidence, "toolStdoutBytesBase64").TextValue);
        Assert.Equal(1.5m, Fact(evidence, "elapsedSeconds").NumberValue);
        Assert.Equal(0.25m, Fact(evidence, "cpuSeconds").NumberValue);
        Assert.Equal(12345m, Fact(evidence, "peakProcessFamilyMemoryBytes").NumberValue);
    }

    [Fact]
    public async Task MissingMetricsStayUnknownAndOversizedTransportTextIsNotTruncated()
    {
        var execution = Completed(Document([], [])) with { StandardError = new string('Ω', 10_000), Error = new(ErrorCode.ToolFailed, new string('X', 9000), Enumerable.Repeat("pending", 129).ToArray()) };
        var evidence = Assert.Single(await new StorageDevicesCollector(new QueryRunner(_ => execution)).CollectAsync(CancellationToken.None));
        Assert.Equal(EvidenceStatus.Unavailable, evidence.Status);
        Assert.Null(Fact(evidence, "cpuSeconds").NumberValue);
        Assert.Null(Fact(evidence, "peakProcessFamilyMemoryBytes").NumberValue);
        Assert.Null(Fact(evidence, "toolStderr").TextValue);
        Assert.Null(Fact(evidence, "toolErrorMessage").TextValue);
        Assert.Null(Fact(evidence, "toolPendingCleanupPaths").TextValue);
        Assert.Equal(20000m, Fact(evidence, "toolStderr.utf8Bytes").NumberValue);
        Assert.Contains(evidence.Issues, issue => issue.Code == IssueCode.LimitReached);
    }

    [Fact]
    public async Task CancellationBeforeAndAfterHostNeverPublishesAnIncompleteSnapshot()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var never = new QueryRunner(_ => throw new InvalidOperationException("Precancel must not query."));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new StorageDevicesCollector(never).CollectAsync(cancellation.Token));
        Assert.Equal(0, never.Calls);
        using var late = new CancellationTokenSource();
        var runner = new QueryRunner(_ => { late.Cancel(); return Completed(Document([Drive()], [])); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new StorageDevicesCollector(runner).CollectAsync(late.Token));
        Assert.Equal(1, runner.Calls);
    }

    private static DiagnosticFact Fact(DiagnosticEvidence evidence, string key) => evidence.Facts.Single(fact => fact.Key == key);
    private static async Task<DiagnosticEvidence> Collect(string json) => Assert.Single(await new StorageDevicesCollector(new QueryRunner(_ => Completed(json))).CollectAsync(CancellationToken.None));
    private static ToolRunResult Completed(string json) => new(ToolRunStatus.Exited, 0, json, "", new(TimeSpan.Zero));
    private static Dictionary<string, object?> Drive() => new()
    {
        ["DeviceID"] = "\\\\.\\PHYSICALDRIVE0",
        ["Index"] = 0u,
        ["Model"] = "Fixture disk",
        ["SerialNumber"] = "Serial-0",
        ["FirmwareRevision"] = "F.1",
        ["InterfaceType"] = "SCSI",
        ["MediaType"] = "Fixed hard disk media",
        ["Size"] = 1_000_000UL,
        ["Partitions"] = 1u,
        ["Status"] = "OK"
    };
    private static Dictionary<string, object?> Partition() => new()
    {
        ["DeviceID"] = "Disk #0, Partition #0",
        ["DiskIndex"] = 0u,
        ["Index"] = 0u,
        ["Size"] = 900_000UL,
        ["StartingOffset"] = 4096UL,
        ["Type"] = "GPT: Basic Data",
        ["BootPartition"] = false,
        ["PrimaryPartition"] = true
    };
    private static string Document(Dictionary<string, object?>[] drives, Dictionary<string, object?>[] partitions,
        bool driveSucceeded = true, bool partitionSucceeded = true, int? driveExamined = null, bool driveLimited = false) => JsonSerializer.Serialize(new
        {
            kind = "optimus.storage-inventory",
            version = 1,
            observedAt = Observed,
            drives = new { succeeded = driveSucceeded, examined = driveExamined ?? drives.Length, limitReached = driveLimited, records = drives, issues = Array.Empty<object>() },
            partitions = new { succeeded = partitionSucceeded, examined = partitions.Length, limitReached = false, records = partitions, issues = Array.Empty<object>() }
        });
    private sealed class QueryRunner(Func<CancellationToken, ToolRunResult> run) : IWindowsSystemQueryRunner
    {
        public WindowsSystemQuery? Query { get; private set; }
        public int Calls { get; private set; }
        public Task<ToolRunResult> RunAsync(WindowsSystemQuery query, CancellationToken cancellationToken)
        { Calls++; Query = query; return Task.FromResult(run(cancellationToken)); }
    }
}
