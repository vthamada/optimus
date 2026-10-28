using System.Text.Json;
using Optimus.Application;
using Optimus.Domain;
using Optimus.Infrastructure.Devices;

namespace Optimus.Tests;

public sealed class DeviceDriverTests
{
    [Fact]
    public async Task FixedQueryPreservesDifferentIdsWithEqualNamesAndDoesNotJoinDriverByName()
    {
        var raw = Envelope(Query([Device("USB\\A", "Equal name"), Device("USB\\B", "Equal name")]),
            Query([Driver("USB\\C", "Equal name")]));
        var runner = new QueryRunner(Completed(raw));
        var evidence = Assert.Single(await new DeviceDriverCollector(runner).CollectAsync(CancellationToken.None));

        Assert.Equal(WindowsSystemQuery.DeviceInventory, runner.Query);
        Assert.Equal(MaintenanceCollectors.DeviceDrivers, evidence.CollectorId);
        Assert.Equal(EvidenceStatus.Complete, evidence.Status);
        Assert.Equal("USB\\A", Fact(evidence, "devices.0.DeviceID").TextValue);
        Assert.Equal("USB\\B", Fact(evidence, "devices.1.DeviceID").TextValue);
        Assert.Equal("USB\\C", Fact(evidence, "drivers.0.DeviceID").TextValue);
        Assert.Equal("Equal name", Fact(evidence, "devices.0.Name").TextValue);
        Assert.Equal("Equal name", Fact(evidence, "devices.1.Name").TextValue);
        Assert.Equal(raw, Fact(evidence, "sourceJson").TextValue);
        Assert.True(Fact(evidence, "sourceJson").Detailed);
    }

    [Fact]
    public async Task DriverManufacturerDateRemainsAStringAndSignatureIsOnlyReported()
    {
        var driver = Driver("ID", "Driver");
        driver["DriverDate"] = "1-25-2001";
        driver["IsSigned"] = false;
        var evidence = await Collect(Envelope(Query([]), Query([driver])));

        Assert.Equal("1-25-2001", Fact(evidence, "drivers.0.DriverDate").TextValue);
        Assert.Equal("false", Fact(evidence, "drivers.0.IsSigned").TextValue);
        Assert.Contains("não verificada", Fact(evidence, "drivers.0.IsSigned").Label);
        Assert.DoesNotContain(evidence.Facts, fact => fact.Key.Contains("healthy", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("System.DateTime", "Utc", "2006-06-21T00:00:00.0000000Z")]
    [InlineData("System.DateTime", "Local", "2006-06-20T21:00:00.0000000-03:00")]
    [InlineData("System.DateTime", "Unspecified", "2006-06-21T00:00:00.0000000")]
    [InlineData("System.DateTimeOffset", "Offset", "2006-06-21T00:00:00.0000000+05:45")]
    public async Task TypedDriverDatePreservesOriginalTextAndDeclaredKindWithoutTimezoneInference(string sourceType, string kind, string text)
    {
        var driver = Driver("ID", "Driver");
        driver["DriverDate"] = new { sourceType, value = text, kind };
        var raw = Envelope(Query([]), Query([driver]));
        var evidence = await Collect(raw);

        Assert.Equal(EvidenceStatus.Complete, evidence.Status);
        Assert.Equal(text, Fact(evidence, "drivers.0.DriverDate").TextValue);
        Assert.Equal(sourceType, Fact(evidence, "drivers.0.DriverDate.sourceType").TextValue);
        Assert.Equal(kind, Fact(evidence, "drivers.0.DriverDate.kind").TextValue);
        Assert.True(Fact(evidence, "drivers.0.DriverDate.sourceType").Detailed);
        Assert.True(Fact(evidence, "drivers.0.DriverDate.kind").Detailed);
        Assert.Equal(raw, Fact(evidence, "sourceJson").TextValue);
        if (kind == "Unspecified") Assert.Contains("fuso não informado", Fact(evidence, "drivers.0.DriverDate").Label);
    }

    [Theory]
    [InlineData("System.DateTime", "Utc", "2006-06-21T00:00:00.0000000")]
    [InlineData("System.DateTime", "Unspecified", "2006-06-21T00:00:00.0000000+00:00")]
    [InlineData("System.DateTime", "Local", "not-a-date")]
    [InlineData("System.DateTimeOffset", "Offset", "2006-06-21T00:00:00.0000000Z")]
    [InlineData("System.DateTimeOffset", "Utc", "2006-06-21T00:00:00.0000000+00:00")]
    [InlineData("Unknown.Type", "Local", "2006-06-21T00:00:00.0000000-03:00")]
    public async Task InconsistentTypedDateBecomesUnknownWithRawRepresentationRetained(string sourceType, string kind, string text)
    {
        var driver = Driver("ID", "Driver");
        driver["DriverDate"] = new { sourceType, value = text, kind };
        var raw = Envelope(Query([]), Query([driver]));
        var evidence = await Collect(raw);

        Assert.Equal(EvidenceStatus.Partial, evidence.Status);
        Assert.Null(Fact(evidence, "drivers.0.DriverDate").TextValue);
        Assert.Equal(raw, Fact(evidence, "sourceJson").TextValue);
        Assert.Contains(evidence.Issues, issue => issue.Code == IssueCode.ReadFailed && issue.Message.Contains("DriverDate", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TypedDateEnvelopeMustBeExactAndDoesNotExpandAcceptanceToOtherFields()
    {
        var driver = Driver("ID", "Driver");
        driver["DriverDate"] = new { sourceType = "System.DateTime", value = "2006-06-21T00:00:00.0000000Z", kind = "Utc", unknown = true };
        var evidence = await Collect(Envelope(Query([]), Query([driver])));
        Assert.Equal(EvidenceStatus.Partial, evidence.Status);
        Assert.Null(Fact(evidence, "drivers.0.DriverDate").TextValue);

        driver["DriverDate"] = new { sourceType = "System.DateTime", value = "2006-06-21T00:00:00.0000000Z" };
        evidence = await Collect(Envelope(Query([]), Query([driver])));
        Assert.Equal(EvidenceStatus.Partial, evidence.Status);
        Assert.Null(Fact(evidence, "drivers.0.DriverDate").TextValue);

        driver["DriverDate"] = new { sourceType = "System.DateTime", value = "2006-06-21T00:00:00.0000000Z", kind = "Utc" };
        driver["DeviceName"] = driver["DriverDate"];
        evidence = await Collect(Envelope(Query([]), Query([driver])));
        Assert.Equal(EvidenceStatus.Partial, evidence.Status);
        Assert.Null(Fact(evidence, "drivers.0.DeviceName").TextValue);
        Assert.Equal("2006-06-21T00:00:00.0000000Z", Fact(evidence, "drivers.0.DriverDate").TextValue);
    }

    [Fact]
    public async Task DuplicateTypedDatePropertyInvalidatesTheEnvelope()
    {
        var driver = Driver("ID", "Driver");
        driver["DriverDate"] = new { sourceType = "System.DateTime", value = "2006-06-21T00:00:00.0000000Z", kind = "Utc" };
        var raw = Envelope(Query([]), Query([driver])).Replace("\"kind\":\"Utc\"", "\"kind\":\"Utc\",\"kind\":\"Local\"", StringComparison.Ordinal);

        Assert.Equal(EvidenceStatus.Unavailable, (await Collect(raw)).Status);
    }

    [Fact]
    public async Task LargeDeviceCatalogCannotConsumeTheDriversProjectionBudget()
    {
        var devices = Enumerable.Range(0, 200).Select(index => Device("device" + index, "Device")).ToArray();
        var driver = Driver("distinct-driver", "Driver");
        driver["DriverDate"] = new { sourceType = "System.DateTime", value = "2006-06-20T21:00:00.0000000-03:00", kind = "Local" };
        var evidence = await Collect(Envelope(Query(devices), Query([driver])));

        Assert.Equal(EvidenceStatus.Partial, evidence.Status);
        Assert.True(evidence.Coverage.LimitReached);
        Assert.True(Fact(evidence, "devices.projected").NumberValue < 200);
        Assert.Equal(1, Fact(evidence, "drivers.projected").NumberValue);
        Assert.Equal("distinct-driver", Fact(evidence, "drivers.0.DeviceID").TextValue);
        Assert.Equal("2006-06-20T21:00:00.0000000-03:00", Fact(evidence, "drivers.0.DriverDate").TextValue);
    }

    [Fact]
    public async Task UnknownCodesAndNullFieldsAreNeverConvertedToAHealthyDevice()
    {
        var device = Device("ID", "Selected");
        device["ConfigManagerErrorCode"] = uint.MaxValue;
        device["Present"] = null;
        device["Status"] = null;
        var evidence = await Collect(Envelope(Query([device]), Query([])));

        Assert.Equal((decimal)uint.MaxValue, Fact(evidence, "devices.0.ConfigManagerErrorCode").NumberValue);
        Assert.Null(Fact(evidence, "devices.0.Present").TextValue);
        Assert.Null(Fact(evidence, "devices.0.Status").TextValue);
    }

    [Fact]
    public async Task MalformedFieldRetainsItsRawRepresentationAndOtherFacts()
    {
        var device = Device("ID", "Original");
        device["ConfigManagerErrorCode"] = "zero";
        device["Present"] = "yes";
        var raw = Envelope(Query([device]), Query([]));
        var evidence = await Collect(raw);

        Assert.Equal(EvidenceStatus.Partial, evidence.Status);
        Assert.Null(Fact(evidence, "devices.0.ConfigManagerErrorCode").NumberValue);
        Assert.Null(Fact(evidence, "devices.0.Present").TextValue);
        Assert.Equal("Original", Fact(evidence, "devices.0.Name").TextValue);
        Assert.Equal(raw, Fact(evidence, "sourceJson").TextValue);
        Assert.Contains(evidence.Issues, item => item.Message.Contains("devices.0.ConfigManagerErrorCode", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FailureOfOneQueryRetainsTheOtherAndEarlierRecords()
    {
        var evidence = await Collect(Envelope(Query([Device("prior", "Prior")], succeeded: false,
                issues: [new { code = "ReadFailed", message = "CIM late failure" }]), Query([Driver("driver", "Driver")])));

        Assert.Equal(EvidenceStatus.Partial, evidence.Status);
        Assert.Equal("prior", Fact(evidence, "devices.0.DeviceID").TextValue);
        Assert.Equal("driver", Fact(evidence, "drivers.0.DeviceID").TextValue);
        Assert.Equal("false", Fact(evidence, "devices.querySucceeded").TextValue);
        Assert.Contains(evidence.Issues, item => item.Message.Contains("CIM late failure", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BothFailedQueriesDoNotBecomeAnEmptySuccessfulInventory()
    {
        var evidence = await Collect(Envelope(Query([], succeeded: false), Query([], succeeded: false)));

        Assert.Equal(EvidenceStatus.Unavailable, evidence.Status);
        Assert.NotEmpty(evidence.Issues);
        Assert.Equal(0, evidence.Coverage.ExaminedItems);
    }

    [Fact]
    public async Task LimitProbePreservesCountAndOmissionWithoutInventingRecords()
    {
        var records = Enumerable.Range(0, 256).Select(index => Device("id" + index, "Name")).ToArray();
        var evidence = await Collect(Envelope(Query(records, examined: 257, limitReached: true), Query([])));

        Assert.Equal(EvidenceStatus.Partial, evidence.Status);
        Assert.True(evidence.Coverage.LimitReached);
        Assert.Equal(257, evidence.Coverage.ExaminedItems);
        Assert.Equal(256, Fact(evidence, "devices.retained").NumberValue);
        Assert.DoesNotContain(evidence.Facts, item => item.Key == "devices.256.DeviceID");
    }

    [Theory]
    [InlineData("{\"kind\":\"optimus.device-inventory\",\"kind\":\"other\",\"version\":1}")]
    [InlineData("not-json")]
    [InlineData("{\"kind\":\"optimus.device-inventory\",\"version\":2,\"devices\":{},\"drivers\":{}}")]
    public async Task MalformedEnvelopeCannotPublishInterpretedRecords(string raw)
    {
        var evidence = await Collect(raw);

        Assert.Equal(EvidenceStatus.Unavailable, evidence.Status);
        Assert.Equal(raw, Fact(evidence, "sourceJson").TextValue);
        Assert.DoesNotContain(evidence.Facts, item => item.Key.StartsWith("devices.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnknownRecordPropertyAndDuplicateNestedValueAreRejected()
    {
        var device = Device("id", "name");
        device["UnexpectedAction"] = "install-driver";
        Assert.Equal(EvidenceStatus.Unavailable, (await Collect(Envelope(Query([device]), Query([])))).Status);
        var raw = Envelope(Query([Device("id", "name")]), Query([]));
        raw = raw.Replace("\"Present\":true", "\"Present\":{\"value\":true,\"value\":false}", StringComparison.Ordinal);
        Assert.Equal(EvidenceStatus.Unavailable, (await Collect(raw)).Status);
    }

    [Theory]
    [InlineData(ToolRunStatus.TimedOut)]
    [InlineData(ToolRunStatus.OutputLimitExceeded)]
    [InlineData(ToolRunStatus.Cancelled)]
    public async Task FailedExecutionNeverAcceptsOtherwiseValidJson(ToolRunStatus status)
    {
        var runner = new QueryRunner(Completed(Envelope(Query([]), Query([]))) with { Status = status, Error = new(ErrorCode.ToolFailed, "primary reason") });
        var evidence = Assert.Single(await new DeviceDriverCollector(runner).CollectAsync(CancellationToken.None));

        Assert.Equal(EvidenceStatus.Unavailable, evidence.Status);
        Assert.Contains(evidence.Issues, issue => issue.Message.Contains("primary reason", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnErrorWithExitZeroCannotBeAcceptedAsACompletedQuery()
    {
        var runner = new QueryRunner(Completed(Envelope(Query([]), Query([]))) with { Error = new(ErrorCode.PermissionDenied, "denied") });
        var evidence = Assert.Single(await new DeviceDriverCollector(runner).CollectAsync(CancellationToken.None));

        Assert.Equal(EvidenceStatus.Unavailable, evidence.Status);
        Assert.Equal(IssueCode.PermissionDenied, Assert.Single(evidence.Issues).Code);
    }

    [Fact]
    public async Task CancellationBeforeOrAfterQueryDoesNotPublishANewSnapshot()
    {
        using var before = new CancellationTokenSource();
        before.Cancel();
        var never = new QueryRunner(Completed(Envelope(Query([]), Query([]))));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new DeviceDriverCollector(never).CollectAsync(before.Token));
        Assert.Equal(0, never.Calls);
        using var after = new CancellationTokenSource();
        var late = new QueryRunner(Completed(Envelope(Query([]), Query([])))) { OnRun = () => after.Cancel() };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new DeviceDriverCollector(late).CollectAsync(after.Token));
    }

    [Fact]
    public async Task OversizedTextBecomesExplicitUnknownWhileOriginalJsonIsRetained()
    {
        var device = Device("id", new string('x', 2049));
        var raw = Envelope(Query([device]), Query([]));
        var evidence = await Collect(raw);

        Assert.Equal(EvidenceStatus.Partial, evidence.Status);
        Assert.Null(Fact(evidence, "devices.0.Name").TextValue);
        Assert.True(evidence.Coverage.LimitReached);
        Assert.Equal(raw, Fact(evidence, "sourceJson").TextValue);
    }

    [Fact]
    public async Task OmittedRecordsCannotBePresentedAsACompleteSuccessfulQuery()
    {
        var evidence = await Collect(Envelope(Query([], examined: 1), Query([])));

        Assert.Equal(EvidenceStatus.Unavailable, evidence.Status);
        Assert.Contains(evidence.Issues, item => item.Message.Contains("Cobertura", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CleanupFailureRetainsExactStderrPrimaryReasonAndPendingResources()
    {
        const string stderr = "stderr original λ\nsem prefixo inventado";
        const string reason = "Falha primária original; fechamento pendente";
        string[] pending = ["native-job:owned-id", @"D:\recurso próprio\arquivo d'água"];
        var raw = Envelope(Query([Device("id", "Valid data must not be interpreted")]), Query([]));
        var result = Completed(raw) with
        {
            Status = ToolRunStatus.CleanupFailed,
            StandardError = stderr,
            Error = new(ErrorCode.ToolFailed, reason, pending),
            StandardErrorBytes = System.Text.Encoding.UTF8.GetBytes(stderr),
            OutputCodePage = 65001
        };
        var evidence = Assert.Single(await new DeviceDriverCollector(new QueryRunner(result)).CollectAsync(CancellationToken.None));

        Assert.Equal(EvidenceStatus.Unavailable, evidence.Status);
        Assert.Equal(raw, Fact(evidence, "sourceJson").TextValue);
        Assert.Equal(stderr, Fact(evidence, "toolStderr").TextValue);
        Assert.Equal(reason, Fact(evidence, "toolErrorMessage").TextValue);
        Assert.Equal("ToolFailed", Fact(evidence, "toolErrorCode").TextValue);
        Assert.Equal(2, Fact(evidence, "toolPendingCleanupCount").NumberValue);
        using var retained = JsonDocument.Parse(Fact(evidence, "toolPendingCleanupPaths").TextValue!);
        Assert.Equal(pending, retained.RootElement.EnumerateArray().Select(item => item.GetString()));
        Assert.Equal(result.StandardErrorBytes, Convert.FromBase64String(Fact(evidence, "toolStderrBytesBase64").TextValue!));
        Assert.Contains(evidence.Issues, item => item.Message.Contains(reason, StringComparison.Ordinal));
        Assert.DoesNotContain(evidence.Facts, item => item.Key.StartsWith("devices.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TransportRetentionLimitIsExplicitAndDoesNotPublishATruncatedReplacement()
    {
        var result = Completed(Envelope(Query([]), Query([]))) with
        {
            Status = ToolRunStatus.CleanupFailed,
            StandardError = new string('x', 32_769),
            Error = new(ErrorCode.ToolFailed, "primary preserved", [new string('p', 16_385)])
        };
        var evidence = Assert.Single(await new DeviceDriverCollector(new QueryRunner(result)).CollectAsync(CancellationToken.None));

        Assert.Equal(EvidenceStatus.Unavailable, evidence.Status);
        Assert.True(evidence.Coverage.LimitReached);
        Assert.Null(Fact(evidence, "toolStderr").TextValue);
        Assert.Equal(32_769, Fact(evidence, "toolStderr.utf8Bytes").NumberValue);
        Assert.Null(Fact(evidence, "toolPendingCleanupPaths").TextValue);
        Assert.Equal(1, Fact(evidence, "toolPendingCleanupCount").NumberValue);
        Assert.Equal("primary preserved", Fact(evidence, "toolErrorMessage").TextValue);
        Assert.Contains(evidence.Issues, item => item.Code == IssueCode.LimitReached && item.Message.Contains("toolStderr", StringComparison.Ordinal));
        Assert.Contains(evidence.Issues, item => item.Code == IssueCode.LimitReached && item.Message.Contains("lista pendente", StringComparison.Ordinal));
    }

    private static DiagnosticFact Fact(DiagnosticEvidence evidence, string key) => Assert.Single(evidence.Facts, item => item.Key == key);
    private static async Task<DiagnosticEvidence> Collect(string raw) => Assert.Single(await new DeviceDriverCollector(new QueryRunner(Completed(raw))).CollectAsync(CancellationToken.None));
    private static ToolRunResult Completed(string raw) => new(ToolRunStatus.Exited, 0, raw, "", new(TimeSpan.Zero));
    private static string Envelope(object devices, object drivers) => JsonSerializer.Serialize(new { kind = "optimus.device-inventory", version = 1, devices, drivers });
    private static object Query(IReadOnlyList<Dictionary<string, object?>> records, bool succeeded = true, int? examined = null,
        bool limitReached = false, object[]? issues = null) => new { succeeded, examined = examined ?? records.Count, limitReached, records, issues = issues ?? [] };
    private static Dictionary<string, object?> Device(string id, string name) => new()
    {
        ["DeviceID"] = id,
        ["Name"] = name,
        ["PNPClass"] = "USB",
        ["Manufacturer"] = "Fixture",
        ["Status"] = "OK",
        ["ConfigManagerErrorCode"] = 0,
        ["Present"] = true
    };
    private static Dictionary<string, object?> Driver(string id, string name) => new()
    {
        ["DeviceID"] = id,
        ["DeviceName"] = name,
        ["DriverVersion"] = "1.0",
        ["DriverProviderName"] = "Fixture",
        ["DriverDate"] = "not-a-normalized-date",
        ["IsSigned"] = true,
        ["Signer"] = "Reported signer",
        ["InfName"] = "fixture.inf"
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
