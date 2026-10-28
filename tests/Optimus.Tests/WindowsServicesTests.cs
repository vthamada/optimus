using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Optimus.Application;
using Optimus.Domain;
using Optimus.Infrastructure.Services;

namespace Optimus.Tests;

public sealed class WindowsServicesTests
{
    private const string ObservedAt = "2026-10-27T09:10:11.1234567-03:00";

    [Fact]
    public async Task NamedQueryRetainsSourceIdentityFieldsDateAndVisibleObservations()
    {
        var record = Record("Service-A");
        record["PathName"] = "\"D:\\Apps\\d'água\\service.exe\" --argument \"$(untrusted)\"";
        record["ProcessId"] = uint.MaxValue;
        var json = Document([record]);
        var runner = new QueryRunner(_ => Completed(json));

        var evidence = Assert.Single(await new WindowsServicesCollector(runner).CollectAsync(CancellationToken.None));

        Assert.Equal(WindowsSystemQuery.ServiceInventory, runner.Query);
        Assert.Equal(1, runner.Calls);
        Assert.Equal(MaintenanceCollectors.Services, evidence.CollectorId);
        Assert.Equal(EvidenceStatus.Complete, evidence.Status);
        Assert.Equal(DateTimeOffset.Parse(ObservedAt), evidence.CapturedAt);
        Assert.Equal(TimeSpan.FromHours(-3), evidence.CapturedAt.Offset);
        Assert.Equal(ObservedAt, Fact(evidence, "sourceObservedAt").TextValue);
        Assert.Equal(json, Fact(evidence, "sourceJson").TextValue);
        Assert.True(Fact(evidence, "sourceJson").Detailed);
        Assert.Equal(record["PathName"], Fact(evidence, "services.0.PathName").TextValue);
        Assert.True(Fact(evidence, "services.0.PathName").Detailed);
        Assert.Equal((decimal)uint.MaxValue, Fact(evidence, "services.0.ProcessId").NumberValue);
        Assert.Null(Fact(evidence, "services.0.ProcessId").Unit);
        Assert.Equal("Service-A", Fact(evidence, "services.0.Name").TextValue);
        Assert.Equal("Stopped", Fact(evidence, "services.0.State").TextValue);
        Assert.Equal("Manual", Fact(evidence, "services.0.StartMode").TextValue);
        Assert.False(Fact(evidence, "services.0.Name").Detailed);
        Assert.False(Fact(evidence, "services.0.State").Detailed);
        Assert.False(Fact(evidence, "services.0.StartMode").Detailed);
        Assert.Equal(1m, Fact(evidence, "examinedRecords").NumberValue);
        Assert.Equal(1m, Fact(evidence, "retainedRecords").NumberValue);
        Assert.Equal(1m, Fact(evidence, "projectedRecords").NumberValue);
        Assert.Contains("não comprovam identidade atual", evidence.Coverage.Scope);
        Assert.DoesNotContain(evidence.Facts, fact => fact.Key is "healthy" or "repairRequired");
        Assert.True(SessionValidation.IsValid(new(Guid.NewGuid(), evidence.CapturedAt, evidence.CapturedAt, SessionStatus.Complete, [evidence], [])));
    }

    [Fact]
    public async Task SameDisplayNameAndRepeatedNamesAreNotCollapsedIntoOneService()
    {
        var evidence = await Collect(Document([Record("A"), Record("B"), Record("A")]));

        Assert.Equal(EvidenceStatus.Complete, evidence.Status);
        Assert.Equal("A", Fact(evidence, "services.0.Name").TextValue);
        Assert.Equal("B", Fact(evidence, "services.1.Name").TextValue);
        Assert.Equal("A", Fact(evidence, "services.2.Name").TextValue);
        Assert.Equal(3m, Fact(evidence, "projectedRecords").NumberValue);
    }

    [Fact]
    public async Task EmptySuccessfulScopeIsCompleteWithoutAHealthConclusion()
    {
        var evidence = await Collect(Document([]));

        Assert.Equal(EvidenceStatus.Complete, evidence.Status);
        Assert.Equal(0, evidence.Coverage.ExaminedItems);
        Assert.Equal(0m, Fact(evidence, "retainedRecords").NumberValue);
        Assert.Equal("true", Fact(evidence, "querySucceeded").TextValue);
        Assert.Empty(evidence.Issues);
        Assert.DoesNotContain(evidence.Facts, fact => fact.Key.StartsWith("services.", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProviderFailureRetainsEarlierRecordsAndNeverClaimsCompleteness(bool earlierRecord)
    {
        var json = Document(earlierRecord ? [Record()] : [], succeeded: false,
            issues: [Note("PermissionDenied", "provider denied access after enumeration")]);

        var evidence = await Collect(json);

        Assert.Equal(earlierRecord ? EvidenceStatus.Partial : EvidenceStatus.Unavailable, evidence.Status);
        Assert.Equal(json, Fact(evidence, "sourceJson").TextValue);
        Assert.Equal("false", Fact(evidence, "querySucceeded").TextValue);
        Assert.Contains(evidence.Issues, issue => issue.Code == IssueCode.PermissionDenied && issue.Message.Contains("provider denied", StringComparison.Ordinal));
        if (earlierRecord) Assert.Equal("FixtureService", Fact(evidence, "services.0.Name").TextValue);
    }

    [Theory]
    [InlineData("ProcessId", "-1")]
    [InlineData("ProcessId", "4294967296")]
    [InlineData("ProcessId", "\"0\"")]
    [InlineData("ProcessId", "null")]
    [InlineData("State", "false")]
    [InlineData("Name", "{\"sourceType\":\"unexpected\",\"omitted\":true}")]
    public async Task UnexpectedFieldTypesStayUnknownWhileOtherObservationsSurvive(string field, string raw)
    {
        using var value = JsonDocument.Parse(raw);
        var record = Record();
        record[field] = value.RootElement.Clone();
        var json = Document([record]);

        var evidence = await Collect(json);

        Assert.Equal(EvidenceStatus.Partial, evidence.Status);
        Assert.Null(Fact(evidence, "services.0." + field).TextValue);
        Assert.Null(Fact(evidence, "services.0." + field).NumberValue);
        Assert.Equal("Manual", Fact(evidence, "services.0.StartMode").TextValue);
        Assert.Equal(json, Fact(evidence, "sourceJson").TextValue);
        Assert.Contains(evidence.Issues, issue => issue.Message.Contains(field, StringComparison.Ordinal));
    }

    [Fact]
    public async Task MissingFieldAndBlankIdentityCannotBecomeAnInventedIdentityOrEmptyHealthyState()
    {
        var record = Record("   ");
        record.Remove("State");

        var evidence = await Collect(Document([record]));

        Assert.Equal(EvidenceStatus.Partial, evidence.Status);
        Assert.Equal("   ", Fact(evidence, "services.0.Name").TextValue);
        Assert.Null(Fact(evidence, "services.0.State").TextValue);
        Assert.Contains(evidence.Issues, issue => issue.Code == IssueCode.NotSupported);
    }

    [Fact]
    public async Task ExcessiveFieldIsNotTruncatedOrReplacedWithASuccessfulEmptyString()
    {
        var record = Record();
        record["PathName"] = new string('x', 2049);
        var json = Document([record]);

        var evidence = await Collect(json);

        Assert.Equal(EvidenceStatus.Partial, evidence.Status);
        Assert.True(evidence.Coverage.LimitReached);
        Assert.Null(Fact(evidence, "services.0.PathName").TextValue);
        Assert.Equal(json, Fact(evidence, "sourceJson").TextValue);
        Assert.Equal("Stopped", Fact(evidence, "services.0.State").TextValue);
    }

    [Fact]
    public async Task ProbeRecordIsCountedWithoutProjectingItOrCallingTheCatalogComplete()
    {
        var records = Enumerable.Range(0, 256).Select(index => (object)Record("Service" + index)).ToArray();

        var evidence = await Collect(Document(records, examined: 257, limited: true));

        Assert.Equal(EvidenceStatus.Partial, evidence.Status);
        Assert.True(evidence.Coverage.LimitReached);
        Assert.Equal(257, evidence.Coverage.ExaminedItems);
        Assert.Equal(256m, Fact(evidence, "retainedRecords").NumberValue);
        Assert.DoesNotContain(evidence.Facts, fact => fact.Key.StartsWith("services.256.", StringComparison.Ordinal));
        Assert.Contains(evidence.Issues, issue => issue.Code == IssueCode.LimitReached);
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(258, true)]
    [InlineData(0, false)]
    [InlineData(2, false)]
    [InlineData(257, false)]
    public async Task InconsistentCoverageCannotPublishInterpretedRecords(int examined, bool limited)
    {
        var json = Document([Record()], examined: examined, limited: limited);

        var evidence = await Collect(json);

        Assert.Equal(EvidenceStatus.Unavailable, evidence.Status);
        Assert.Equal(json, Fact(evidence, "sourceJson").TextValue);
        Assert.DoesNotContain(evidence.Facts, fact => fact.Key.StartsWith("services.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ProjectionBudgetKeepsWholeEntriesAndRetainsTheCompleteBoundedSource()
    {
        var records = LargeRecords(25);
        var json = Document(records);

        var evidence = await Collect(json);

        Assert.Equal(EvidenceStatus.Partial, evidence.Status);
        Assert.True(evidence.Coverage.LimitReached);
        Assert.Equal(25m, Fact(evidence, "retainedRecords").NumberValue);
        int projected = (int)Assert.IsType<decimal>(Fact(evidence, "projectedRecords").NumberValue);
        Assert.InRange(projected, 1, 24);
        Assert.Equal(projected * 8, evidence.Facts.Count(fact => fact.Key.StartsWith("services.", StringComparison.Ordinal)));
        Assert.Equal(json, Fact(evidence, "sourceJson").TextValue);
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(new
        {
            facts = evidence.Facts.Where(fact => !fact.Detailed),
            evidence.Issues
        }).Length < 131_072);
    }

    [Fact]
    public async Task InvalidRecordAfterProjectionBudgetIsStillRejectedByTheProtocolOwner()
    {
        var records = LargeRecords(25).Concat(new object[] { new { unknown = "not-a-service-record" } }).ToArray();
        var json = Document(records);

        var evidence = await Collect(json);

        Assert.Equal(EvidenceStatus.Unavailable, evidence.Status);
        Assert.DoesNotContain(evidence.Facts, fact => fact.Key.StartsWith("services.", StringComparison.Ordinal));
        Assert.Equal(json, Fact(evidence, "sourceJson").TextValue);
    }

    [Fact]
    public async Task ProviderNotesShareTheProjectionBudgetWithAnExplicitOmissionCount()
    {
        var notes = Enumerable.Range(0, 40).Select(_ => Note("ReadFailed", new string('x', 2048))).ToArray();
        var json = Document([Record()], issues: notes);

        var evidence = await Collect(json);

        Assert.Equal(EvidenceStatus.Partial, evidence.Status);
        Assert.True(evidence.Coverage.LimitReached);
        Assert.Equal(json, Fact(evidence, "sourceJson").TextValue);
        Assert.Contains(evidence.Issues, issue => issue.Code == IssueCode.LimitReached && issue.Message.Contains("não foram projetadas", StringComparison.Ordinal));
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(evidence.Issues).Length < 131_072);
    }

    [Theory]
    [InlineData("{not-json")]
    [InlineData("[]")]
    [InlineData("{\"kind\":\"optimus.service-inventory\",\"version\":1,\"observedAt\":\"2026-10-27T01:02:03Z\",\"query\":{\"succeeded\":true,\"examined\":0,\"limitReached\":false,\"records\":[],\"issues\":[]},\"query\":{}}")]
    public async Task MalformedAndDuplicateDocumentsPreserveRawSourceWithoutPublishingFacts(string json)
    {
        var evidence = await Collect(json);

        Assert.Equal(EvidenceStatus.Unavailable, evidence.Status);
        Assert.Equal(json, Fact(evidence, "sourceJson").TextValue);
        Assert.DoesNotContain(evidence.Facts, fact => fact.Key == "querySucceeded");
        Assert.NotEmpty(evidence.Issues);
    }

    [Theory]
    [InlineData("version")]
    [InlineData("kind")]
    [InlineData("offset")]
    [InlineData("extra")]
    [InlineData("duplicateRecordField")]
    [InlineData("nonobjectRecord")]
    [InlineData("invalidIssue")]
    public async Task VersionedEnvelopeRejectsUnknownShapesAndMissingTimezone(string mutation)
    {
        var json = Document([Record()]);
        json = mutation switch
        {
            "version" => json.Replace("\"version\":1", "\"version\":2", StringComparison.Ordinal),
            "kind" => json.Replace("optimus.service-inventory", "different-kind", StringComparison.Ordinal),
            "offset" => json.Replace(ObservedAt, "2026-10-27T09:10:11.1234567", StringComparison.Ordinal),
            "extra" => json.Insert(1, "\"unexpected\":true,"),
            "duplicateRecordField" => json.Replace("\"Name\":", "\"Name\":\"first\",\"Name\":", StringComparison.Ordinal),
            "nonobjectRecord" => Document(["not-a-record"]),
            "invalidIssue" => Document([Record()], issues: [new { code = "ReadFailed", message = "why", extra = true }]),
            _ => throw new InvalidOperationException()
        };

        var evidence = await Collect(json);

        Assert.Equal(EvidenceStatus.Unavailable, evidence.Status);
        Assert.Equal(json, Fact(evidence, "sourceJson").TextValue);
        Assert.DoesNotContain(evidence.Facts, fact => fact.Key.StartsWith("services.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NoteBeyondTheProjectionBudgetMustStillHaveAValidProtocolShape()
    {
        var notes = Enumerable.Range(0, 40).Select(_ => Note("ReadFailed", new string('x', 2048)))
            .Concat(new object[] { new { code = "unrecognized", message = "last note" } }).ToArray();

        var evidence = await Collect(Document([Record()], issues: notes));

        Assert.Equal(EvidenceStatus.Unavailable, evidence.Status);
        Assert.DoesNotContain(evidence.Facts, fact => fact.Key == "retainedRecords");
    }

    [Theory]
    [InlineData(ToolRunStatus.TimedOut, null)]
    [InlineData(ToolRunStatus.OutputLimitExceeded, null)]
    [InlineData(ToolRunStatus.CleanupFailed, 0)]
    [InlineData(ToolRunStatus.Cancelled, null)]
    [InlineData(ToolRunStatus.StartFailed, null)]
    [InlineData(ToolRunStatus.Exited, 1)]
    public async Task FailedTransportNeverPromotesAnOtherwiseValidDocument(ToolRunStatus status, int? exit)
    {
        var json = Document([Record()]);
        var execution = new ToolRunResult(status, exit, json, "native failure", new(TimeSpan.FromSeconds(2)));

        var evidence = await Collect(execution);

        Assert.Equal(EvidenceStatus.Unavailable, evidence.Status);
        Assert.Equal(json, Fact(evidence, "sourceJson").TextValue);
        Assert.Equal("native failure", Fact(evidence, "toolStderr").TextValue);
        Assert.DoesNotContain(evidence.Facts, fact => fact.Key.StartsWith("services.", StringComparison.Ordinal));
        if (status == ToolRunStatus.OutputLimitExceeded) Assert.True(evidence.Coverage.LimitReached);
    }

    [Fact]
    public async Task ErrorAlongsideExitZeroKeepsPrimaryErrorPendingCleanupAndOriginalBytes()
    {
        var execution = Completed(Document([Record()])) with
        {
            Error = new(ErrorCode.PermissionDenied, "primary failure", ["owned-resource:services-test"]),
            StandardOutputBytes = [0xFF, 0xFE, 0x00],
            StandardErrorBytes = [0x80],
            OutputCodePage = 65001
        };

        var evidence = await Collect(execution);

        Assert.Equal(EvidenceStatus.Unavailable, evidence.Status);
        Assert.Equal("primary failure", Fact(evidence, "toolErrorMessage").TextValue);
        Assert.Equal("PermissionDenied", Fact(evidence, "toolErrorCode").TextValue);
        Assert.Equal("[\"owned-resource:services-test\"]", Fact(evidence, "toolPendingCleanupPaths").TextValue);
        Assert.Equal(1m, Fact(evidence, "toolPendingCleanupCount").NumberValue);
        Assert.Equal(Convert.ToBase64String(execution.StandardOutputBytes!), Fact(evidence, "toolStdoutBytesBase64").TextValue);
        Assert.Equal(Convert.ToBase64String(execution.StandardErrorBytes!), Fact(evidence, "toolStderrBytesBase64").TextValue);
        Assert.Contains(evidence.Issues, issue => issue.Code == IssueCode.PermissionDenied);
    }

    [Fact]
    public async Task SuccessfulTransportWithStderrRemainsPartialAndKeepsTheSource()
    {
        var evidence = await Collect(Completed(Document([Record()])) with { StandardError = "provider warning" });

        Assert.Equal(EvidenceStatus.Partial, evidence.Status);
        Assert.Equal("FixtureService", Fact(evidence, "services.0.Name").TextValue);
        Assert.Equal("provider warning", Fact(evidence, "toolStderr").TextValue);
    }

    [Fact]
    public async Task JobMetricsAreNumericExactAndMissingMeasurementsStayUnknown()
    {
        var measured = await Collect(Completed(Document([])) with
        {
            Metrics = new(TimeSpan.FromTicks(1_234_567), TimeSpan.FromTicks(567_890), 12_345)
        });
        var unknown = await Collect(Document([]));

        Assert.Equal(0.1234567m, Fact(measured, "elapsedSeconds").NumberValue);
        Assert.Equal(0.056789m, Fact(measured, "cpuSeconds").NumberValue);
        Assert.Equal(12_345m, Fact(measured, "peakProcessFamilyMemoryBytes").NumberValue);
        Assert.Equal("s", Fact(measured, "elapsedSeconds").Unit);
        Assert.Equal("bytes", Fact(measured, "peakProcessFamilyMemoryBytes").Unit);
        Assert.True(Fact(measured, "cpuSeconds").Detailed);
        Assert.Null(Fact(unknown, "cpuSeconds").NumberValue);
        Assert.Null(Fact(unknown, "peakProcessFamilyMemoryBytes").NumberValue);
    }

    [Fact]
    public async Task JsonSizeLimitCountsUtf8BytesBeforeRetentionAndDoesNotPublishAPrefix()
    {
        var oversized = new string('ç', WindowsServicesCollector.MaximumJsonBytes / 2 + 1);

        var evidence = await Collect(oversized);

        Assert.Equal(EvidenceStatus.Unavailable, evidence.Status);
        Assert.True(evidence.Coverage.LimitReached);
        Assert.Equal((decimal)Encoding.UTF8.GetByteCount(oversized), Fact(evidence, "sourceJsonUtf8Bytes").NumberValue);
        Assert.DoesNotContain(evidence.Facts, fact => fact.Key == "sourceJson");
    }

    [Fact]
    public async Task ExcessiveFailureMetadataKeepsItsCountsWithoutPretendingEmptyOrLosingOwnership()
    {
        var execution = Completed(Document([])) with
        {
            StandardError = new string('x', 16_385),
            Error = new(ErrorCode.ToolFailed, new string('e', 8193), Enumerable.Range(0, 129).Select(index => "owned:" + index).ToArray())
        };

        var evidence = await Collect(execution);

        Assert.Equal(EvidenceStatus.Unavailable, evidence.Status);
        Assert.True(evidence.Coverage.LimitReached);
        Assert.Null(Fact(evidence, "toolStderr").TextValue);
        Assert.Null(Fact(evidence, "toolErrorMessage").TextValue);
        Assert.Null(Fact(evidence, "toolPendingCleanupPaths").TextValue);
        Assert.Equal(129m, Fact(evidence, "toolPendingCleanupCount").NumberValue);
        Assert.Equal(16_385m, Fact(evidence, "toolStderr.utf8Bytes").NumberValue);
        Assert.Contains(evidence.Issues, issue => issue.Message.Contains("executor continua dono", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CancellationBeforeAndAfterTheQueryCannotPublishAnObservation()
    {
        using var before = new CancellationTokenSource();
        before.Cancel();
        var unused = new QueryRunner(_ => throw new InvalidOperationException("must not run"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new WindowsServicesCollector(unused).CollectAsync(before.Token));
        Assert.Equal(0, unused.Calls);

        using var during = new CancellationTokenSource();
        var late = new QueryRunner(_ => { during.Cancel(); return Completed(Document([])); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new WindowsServicesCollector(late).CollectAsync(during.Token));
        Assert.Equal(1, late.Calls);
    }

    [Fact]
    public void CompiledQueryStreamsOnlyLocalServicePropertiesAndFitsTheEncodedCommandBudget()
    {
        using var stream = typeof(WindowsServicesCollector).Assembly.GetManifestResourceStream("Optimus.Infrastructure.SystemTools.Scripts.ServiceInventory.ps1");
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!);
        var script = reader.ReadToEnd();

        Assert.Contains("CimCmdlets\\Get-CimInstance -ClassName Win32_Service", script);
        Assert.Contains("Select-Object -First 257", script);
        Assert.Contains("Join-Path $optimusSystemDirectory", script);
        Assert.Contains("CimCmdlets\\CimCmdlets.psd1", script);
        Assert.Contains("$remaining = 238000", script);
        Assert.Contains("$value.Length -le 2048", script);
        Assert.True(script.Length < 8000);
        Assert.DoesNotContain("-ComputerName", script);
        Assert.DoesNotContain("-CimSession", script);
        Assert.DoesNotContain("-AsJob", script);
        Assert.DoesNotContain("Win32_Product", script);
        Assert.DoesNotContain("$env:", script);
        Assert.False(Regex.IsMatch(script, @"\b(?:(?:Start|Stop|Set|Restart|Suspend|Resume|New|Remove)-Service|Invoke-CimMethod|Invoke-Expression)\b", RegexOptions.CultureInvariant));
    }

    private static DiagnosticFact Fact(DiagnosticEvidence evidence, string key) => Assert.Single(evidence.Facts, fact => fact.Key == key);
    private static Task<DiagnosticEvidence> Collect(string json) => Collect(Completed(json));
    private static async Task<DiagnosticEvidence> Collect(ToolRunResult result) => Assert.Single(await new WindowsServicesCollector(new QueryRunner(_ => result)).CollectAsync(CancellationToken.None));
    private static ToolRunResult Completed(string json) => new(ToolRunStatus.Exited, 0, json, "", new(TimeSpan.FromMilliseconds(1)));
    private static object Note(string code, string message) => new { code, message };
    private static Dictionary<string, object?> Record(string name = "FixtureService") => new(StringComparer.Ordinal)
    {
        ["Name"] = name,
        ["DisplayName"] = "Shared display name",
        ["State"] = "Stopped",
        ["StartMode"] = "Manual",
        ["Status"] = "Unknown",
        ["ProcessId"] = 0u,
        ["PathName"] = "D:\\Apps\\fixture.exe --registered-only",
        ["StartName"] = "LocalSystem"
    };
    private static object[] LargeRecords(int count) => Enumerable.Range(0, count).Select(index =>
    {
        var record = Record("LongService" + index);
        record["DisplayName"] = new string('d', 2000);
        record["PathName"] = new string('p', 2000);
        return (object)record;
    }).ToArray();
    private static string Document(object[] records, bool succeeded = true, int? examined = null, bool limited = false, object[]? issues = null) => JsonSerializer.Serialize(new
    {
        kind = "optimus.service-inventory",
        version = 1,
        observedAt = ObservedAt,
        query = new { succeeded, examined = examined ?? records.Length, limitReached = limited, records, issues = issues ?? [] }
    });
    private sealed class QueryRunner(Func<CancellationToken, ToolRunResult> execute) : IWindowsSystemQueryRunner
    {
        public int Calls { get; private set; }
        public WindowsSystemQuery? Query { get; private set; }
        public Task<ToolRunResult> RunAsync(WindowsSystemQuery query, CancellationToken cancellationToken)
        { Calls++; Query = query; return Task.FromResult(execute(cancellationToken)); }
    }
}
