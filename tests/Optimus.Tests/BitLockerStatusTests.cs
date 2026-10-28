using System.Text.Json;
using System.Text.Json.Nodes;
using Optimus.Application;
using Optimus.Domain;
using Optimus.Infrastructure.Security;

namespace Optimus.Tests;

public sealed class BitLockerStatusTests
{
    private const string Observed = "2026-10-28T14:15:16.1234567-03:00";

    [Fact]
    public async Task NamedGetterSnapshotPreservesIdentifiersCodesDateAndScopeWithoutKeyMaterial()
    {
        var volume = Volume(); volume["PersistentVolumeID"] = "  original Ω  ";
        string raw = Document([volume]); var host = new Host(_ => Completed(raw));
        var evidence = Assert.Single(await new BitLockerStatusCollector(host).CollectAsync(CancellationToken.None));
        Assert.Equal(WindowsSystemQuery.BitLockerStatus, host.Query);
        Assert.Equal(1, host.Calls);
        Assert.Equal("windows.bitlocker-status", evidence.CollectorId);
        Assert.Equal(EvidenceStatus.Complete, evidence.Status);
        Assert.Equal(DateTimeOffset.Parse(Observed), evidence.CapturedAt);
        Assert.Equal(TimeSpan.FromHours(-3), evidence.CapturedAt.Offset);
        Assert.Equal(raw, Fact(evidence, "sourceJson").TextValue);
        Assert.Equal(volume["DeviceID"], Fact(evidence, "volumes.0.DeviceID").TextValue);
        Assert.Equal("  original Ω  ", Fact(evidence, "volumes.0.PersistentVolumeID").TextValue);
        Assert.Equal(1m, Fact(evidence, "volumes.0.GetConversionStatus.ConversionStatus").NumberValue);
        Assert.Equal("FullyEncrypted", Fact(evidence, "volumes.0.GetConversionStatus.ConversionStatus.name").TextValue);
        Assert.Equal("Protected", Fact(evidence, "volumes.0.GetProtectionStatus.ProtectionStatus.name").TextValue);
        Assert.Equal("XTS_AES_128", Fact(evidence, "volumes.0.GetEncryptionMethod.EncryptionMethod.name").TextValue);
        Assert.Equal("Unlocked", Fact(evidence, "volumes.0.GetLockStatus.LockStatus.name").TextValue);
        Assert.Equal("false", Fact(evidence, "keyMaterialRequested").TextValue);
        Assert.Equal("false", Fact(evidence, "configurationChangesRequested").TextValue);
        Assert.DoesNotContain(evidence.Facts, fact => fact.Key.Contains("RecoveryPassword", StringComparison.Ordinal) || fact.Key.Contains("KeyProtector", StringComparison.Ordinal));
        Assert.Contains("PrecisionFactor 0", evidence.Coverage.Scope);
        Assert.True(SessionValidation.IsValid(new(Guid.NewGuid(), evidence.CapturedAt, evidence.CapturedAt, SessionStatus.Complete, [evidence], [])));
    }

    [Theory]
    [InlineData("GetConversionStatus", "ConversionStatus")]
    [InlineData("GetProtectionStatus", "ProtectionStatus")]
    [InlineData("GetEncryptionMethod", "EncryptionMethod")]
    [InlineData("GetLockStatus", "LockStatus")]
    public async Task UnknownEnumCodesRemainOriginalAndNeverBecomeADefaultState(string getter, string field)
    {
        var volume = Volume(); var selected = Getters(volume); var method = (Dictionary<string, object?>)selected[getter]!;
        ((Dictionary<string, object?>)method["values"]!)[field] = 99u;
        var raw = Document([volume]); var evidence = await Collect(raw);
        Assert.Equal(EvidenceStatus.Partial, evidence.Status);
        Assert.Equal(99m, Fact(evidence, "volumes.0." + getter + "." + field).NumberValue);
        Assert.Null(Fact(evidence, "volumes.0." + getter + "." + field + ".name").TextValue);
        Assert.Equal(raw, Fact(evidence, "sourceJson").TextValue);
        Assert.Contains(evidence.Issues, issue => issue.Code == IssueCode.NotSupported);
    }

    [Theory]
    [InlineData("GetProtectionStatus", "ProtectionStatus", 2u, "Unknown")]
    [InlineData("GetEncryptionMethod", "EncryptionMethod", uint.MaxValue, "UNKNOWN")]
    public async Task DocumentedIndeterminateCodesAreNotProtectionOff(string getter, string field, uint code, string name)
    {
        var volume = Volume(); var selected = (Dictionary<string, object?>)Getters(volume)[getter]!;
        ((Dictionary<string, object?>)selected["values"]!)[field] = code;
        var evidence = await Collect(Document([volume]));
        Assert.Equal(EvidenceStatus.Partial, evidence.Status);
        Assert.Equal((decimal)code, Fact(evidence, "volumes.0." + getter + "." + field).NumberValue);
        Assert.Equal(name, Fact(evidence, "volumes.0." + getter + "." + field + ".name").TextValue);
    }

    [Fact]
    public async Task MethodFailureKeepsOriginalReturnValueButDoesNotInterpretUndefinedOutputAsUnprotected()
    {
        var volume = Volume(); Getters(volume)["GetProtectionStatus"] = Method(true, 0x80070005u, new() { ["ProtectionStatus"] = 0u });
        var raw = Document([volume]); var evidence = await Collect(raw);
        Assert.Equal(EvidenceStatus.Partial, evidence.Status);
        Assert.Equal(2147942405m, Fact(evidence, "volumes.0.GetProtectionStatus.ReturnValue").NumberValue);
        Assert.Equal("0x80070005", Fact(evidence, "volumes.0.GetProtectionStatus.ReturnValueHex").TextValue);
        Assert.Null(Fact(evidence, "volumes.0.GetProtectionStatus.ProtectionStatus").NumberValue);
        Assert.Equal(0m, Fact(evidence, "volumes.0.GetProtectionStatus.ProtectionStatus.reportedValue").NumberValue);
        Assert.DoesNotContain(evidence.Facts, fact => fact.Key == "volumes.0.GetProtectionStatus.ProtectionStatus.name");
        Assert.Equal("XTS_AES_128", Fact(evidence, "volumes.0.GetEncryptionMethod.EncryptionMethod.name").TextValue);
        Assert.Equal(raw, Fact(evidence, "sourceJson").TextValue);
    }

    [Fact]
    public async Task OneHundredPercentCannotOverrideAnInProgressConversionCode()
    {
        var volume = Volume(); Getters(volume)["GetConversionStatus"] = Method(true, 0u,
            new() { ["ConversionStatus"] = 2u, ["EncryptionPercentage"] = 100u, ["EncryptionFlags"] = 0u });
        var evidence = await Collect(Document([volume]));
        Assert.Equal(EvidenceStatus.Complete, evidence.Status);
        Assert.Equal(100m, Fact(evidence, "volumes.0.GetConversionStatus.EncryptionPercentage").NumberValue);
        Assert.Equal("EncryptionInProgress", Fact(evidence, "volumes.0.GetConversionStatus.ConversionStatus.name").TextValue);
        Assert.DoesNotContain(evidence.Facts, fact => fact.TextValue == "FullyEncrypted");
    }

    [Theory]
    [InlineData(101u)]
    [InlineData(uint.MaxValue)]
    public async Task PercentageOutsideZeroToOneHundredRemainsUnknownWithRawValueRetained(uint percentage)
    {
        var volume = Volume(); Getters(volume)["GetConversionStatus"] = Method(true, 0u,
            new() { ["ConversionStatus"] = 1u, ["EncryptionPercentage"] = percentage, ["EncryptionFlags"] = 0u });
        var evidence = await Collect(Document([volume]));
        Assert.Equal(EvidenceStatus.Partial, evidence.Status);
        Assert.Null(Fact(evidence, "volumes.0.GetConversionStatus.EncryptionPercentage").NumberValue);
        Assert.Equal((decimal)percentage, Fact(evidence, "volumes.0.GetConversionStatus.EncryptionPercentage.reportedValue").NumberValue);
    }

    [Fact]
    public async Task NullDriveLetterAndEmptyPersistentIdArePreservedWithoutInventingMountsOrEncryption()
    {
        var volume = Volume(); volume["DriveLetter"] = null; volume["PersistentVolumeID"] = "";
        var evidence = await Collect(Document([volume]));
        Assert.Equal(EvidenceStatus.Complete, evidence.Status);
        Assert.Null(Fact(evidence, "volumes.0.DriveLetter").TextValue);
        Assert.Equal("", Fact(evidence, "volumes.0.PersistentVolumeID").TextValue);
        Assert.Equal("Protected", Fact(evidence, "volumes.0.GetProtectionStatus.ProtectionStatus.name").TextValue);
    }

    [Fact]
    public async Task NamespaceOrPermissionFailureDoesNotBecomeAnEmptyUnprotectedVolumeList()
    {
        var raw = Document([], succeeded: false, notes: [new { code = "PermissionDenied", message = "original provider denial HRESULT -2147024891" }]);
        var evidence = await Collect(raw);
        Assert.Equal(EvidenceStatus.Unavailable, evidence.Status);
        Assert.Equal("false", Fact(evidence, "querySucceeded").TextValue);
        Assert.Equal(0m, Fact(evidence, "retainedVolumes").NumberValue);
        Assert.Equal(raw, Fact(evidence, "sourceJson").TextValue);
        Assert.Contains(evidence.Issues, issue => issue.Code == IssueCode.PermissionDenied);
        Assert.DoesNotContain(evidence.Facts, fact => fact.Key.Contains("ProtectionStatus.name", StringComparison.Ordinal));
    }

    [Fact]
    public async Task KnownEmptyDiscoveryIsDifferentFromUnavailableProvider()
    {
        var evidence = await Collect(Document([]));
        Assert.Equal(EvidenceStatus.Complete, evidence.Status);
        Assert.Empty(evidence.Issues);
        Assert.Equal(0m, Fact(evidence, "retainedVolumes").NumberValue);
    }

    [Fact]
    public async Task FailedOrSkippedGetterKeepsUnknownStateWhileOtherGettersSurvive()
    {
        var volume = Volume(); Getters(volume)["GetProtectionStatus"] = Method(false, null, new() { ["ProtectionStatus"] = null },
            [new { code = "LimitReached", message = "cooperative deadline" }]);
        var evidence = await Collect(Document([volume], limited: true));
        Assert.Equal(EvidenceStatus.Partial, evidence.Status);
        Assert.Equal("false", Fact(evidence, "volumes.0.GetProtectionStatus.invoked").TextValue);
        Assert.Null(Fact(evidence, "volumes.0.GetProtectionStatus.ReturnValue").NumberValue);
        Assert.Null(Fact(evidence, "volumes.0.GetProtectionStatus.ProtectionStatus").NumberValue);
        Assert.Equal("Unlocked", Fact(evidence, "volumes.0.GetLockStatus.LockStatus.name").TextValue);
        Assert.True(evidence.Coverage.LimitReached);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("4294967296")]
    [InlineData("\"0\"")]
    [InlineData("false")]
    [InlineData("null")]
    public async Task MalformedReturnValueNeverMeansSuccessfulGetter(string json)
    {
        var volume = Volume(); var method = (Dictionary<string, object?>)Getters(volume)["GetProtectionStatus"]!;
        using var value = JsonDocument.Parse(json); method["ReturnValue"] = value.RootElement.Clone();
        var raw = Document([volume]); var evidence = await Collect(raw);
        Assert.Equal(EvidenceStatus.Partial, evidence.Status);
        Assert.Null(Fact(evidence, "volumes.0.GetProtectionStatus.ReturnValue").NumberValue);
        Assert.Equal("false", Fact(evidence, "volumes.0.GetProtectionStatus.outputAccepted").TextValue);
        Assert.Null(Fact(evidence, "volumes.0.GetProtectionStatus.ProtectionStatus").NumberValue);
        Assert.Equal(raw, Fact(evidence, "sourceJson").TextValue);
    }

    [Fact]
    public async Task GetterShapesCannotAddAnotherMethodOrChangeTheSelectedNamespace()
    {
        var node = JsonNode.Parse(Document([Volume()]))!;
        node["sourceNamespace"] = "root/another-provider";
        var evidence = await Collect(node.ToJsonString());
        Assert.Equal(EvidenceStatus.Unavailable, evidence.Status);
        var volume = Volume(); Getters(volume)["UnadvertisedGetter"] = Method(true, 0u, []);
        var invalid = await Collect(Document([volume]));
        Assert.Equal(EvidenceStatus.Unavailable, invalid.Status);
        Assert.DoesNotContain(invalid.Facts, fact => fact.Key.StartsWith("volumes.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MissingSelectedFieldsStayUnknownWithoutAZeroFallback()
    {
        var volume = Volume(); volume.Remove("DeviceID");
        var method = (Dictionary<string, object?>)Getters(volume)["GetLockStatus"]!;
        ((Dictionary<string, object?>)method["values"]!).Remove("LockStatus");
        var evidence = await Collect(Document([volume]));
        Assert.Equal(EvidenceStatus.Partial, evidence.Status);
        Assert.Null(Fact(evidence, "volumes.0.DeviceID").TextValue);
        Assert.Null(Fact(evidence, "volumes.0.GetLockStatus.LockStatus").NumberValue);
        Assert.Contains(evidence.Issues, issue => issue.Code == IssueCode.ReadFailed);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("{\"kind\":\"optimus.bitlocker-status\",\"kind\":\"ambiguous\"}")]
    public async Task MalformedEnvelopeKeepsRawDataWithoutTrustedVolumeStates(string raw)
    {
        var evidence = await Collect(raw);
        Assert.Equal(EvidenceStatus.Unavailable, evidence.Status);
        Assert.Equal(raw, Fact(evidence, "sourceJson").TextValue);
    }

    [Fact]
    public async Task DuplicateNestedReturnValueInvalidatesTheProjection()
    {
        var raw = Document([Volume()]).Replace("\"ReturnValue\":0", "\"ReturnValue\":0,\"ReturnValue\":0", StringComparison.Ordinal);
        var evidence = await Collect(raw);
        Assert.Equal(EvidenceStatus.Unavailable, evidence.Status);
        Assert.Equal(raw, Fact(evidence, "sourceJson").TextValue);
        Assert.DoesNotContain(evidence.Facts, fact => fact.Key.StartsWith("volumes.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ProbeAndProjectionCapsKeepOriginalRecordsAndExplicitCoverage()
    {
        var records = Enumerable.Range(0, 32).Select(_ => Volume()).ToArray();
        var raw = Document(records, examined: 33, limited: true); var evidence = await Collect(raw);
        Assert.Equal(EvidenceStatus.Partial, evidence.Status);
        Assert.Equal(33m, Fact(evidence, "examinedVolumes").NumberValue);
        Assert.Equal(32m, Fact(evidence, "retainedVolumes").NumberValue);
        Assert.InRange(Fact(evidence, "projectedVolumes").NumberValue!.Value, 1m, 31m);
        Assert.Equal(raw, Fact(evidence, "sourceJson").TextValue);
        Assert.True(evidence.Coverage.LimitReached);
    }

    [Fact]
    public async Task ImpossibleCoverageAndOversizedSourceAreRejectedBeforeInterpretation()
    {
        var inconsistent = await Collect(Document([Volume()], examined: 2));
        Assert.Equal(EvidenceStatus.Unavailable, inconsistent.Status);
        var oversized = await Collect(new string('Ω', 131073));
        Assert.Equal(EvidenceStatus.Unavailable, oversized.Status);
        Assert.Equal(262146m, Fact(oversized, "sourceJsonUtf8Bytes").NumberValue);
        Assert.DoesNotContain(oversized.Facts, fact => fact.Key == "sourceJson");
    }

    [Fact]
    public async Task TransportFailureRetainsPrimaryReasonCleanupCodepageAndRawBytes()
    {
        var execution = new ToolRunResult(ToolRunStatus.CleanupFailed, null, "original partial", "stderr Ω", new(TimeSpan.FromMilliseconds(1250), TimeSpan.FromMilliseconds(250), 12345),
            new(ErrorCode.PermissionDenied, "primary denial Ω", [@"D:\owned Ω"]), [1, 2], [3, 4], 65001);
        var evidence = Assert.Single(await new BitLockerStatusCollector(new Host(_ => execution)).CollectAsync(CancellationToken.None));
        Assert.Equal(EvidenceStatus.Unavailable, evidence.Status);
        Assert.Equal("primary denial Ω", Fact(evidence, "toolErrorMessage").TextValue);
        Assert.Equal("stderr Ω", Fact(evidence, "toolStderr").TextValue);
        Assert.Equal("AQI=", Fact(evidence, "toolStdoutBytesBase64").TextValue);
        Assert.Equal("AwQ=", Fact(evidence, "toolStderrBytesBase64").TextValue);
        Assert.Equal(65001m, Fact(evidence, "toolOutputCodePage").NumberValue);
        Assert.Equal(new[] { @"D:\owned Ω" }, JsonSerializer.Deserialize<string[]>(Fact(evidence, "toolPendingCleanupPaths").TextValue!));
        Assert.Equal(1.25m, Fact(evidence, "elapsedSeconds").NumberValue);
        Assert.Equal(0.25m, Fact(evidence, "cpuSeconds").NumberValue);
        Assert.Equal(12345m, Fact(evidence, "peakProcessFamilyMemoryBytes").NumberValue);
    }

    [Fact]
    public async Task MissingMetricsAndExcessiveMetadataRemainUnknownRatherThanTruncated()
    {
        var execution = Completed(Document([])) with { StandardError = new string('Ω', 9000), Error = new(ErrorCode.ToolFailed, new string('X', 9000), Enumerable.Repeat("pending", 129).ToArray()) };
        var evidence = Assert.Single(await new BitLockerStatusCollector(new Host(_ => execution)).CollectAsync(CancellationToken.None));
        Assert.Equal(EvidenceStatus.Unavailable, evidence.Status);
        Assert.Null(Fact(evidence, "cpuSeconds").NumberValue);
        Assert.Null(Fact(evidence, "peakProcessFamilyMemoryBytes").NumberValue);
        Assert.Null(Fact(evidence, "toolStderr").TextValue);
        Assert.Null(Fact(evidence, "toolErrorMessage").TextValue);
        Assert.Null(Fact(evidence, "toolPendingCleanupPaths").TextValue);
        Assert.Equal(18000m, Fact(evidence, "toolStderr.utf8Bytes").NumberValue);
        Assert.Contains(evidence.Issues, issue => issue.Code == IssueCode.LimitReached);
    }

    [Fact]
    public async Task CancellationBeforeOrAfterTheHostNeverPublishesABitLockerState()
    {
        using var before = new CancellationTokenSource(); before.Cancel();
        var host = new Host(_ => Completed(Document([])));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new BitLockerStatusCollector(host).CollectAsync(before.Token));
        Assert.Equal(0, host.Calls);
        using var after = new CancellationTokenSource();
        host = new Host(_ => { after.Cancel(); return Completed(Document([Volume()])); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new BitLockerStatusCollector(host).CollectAsync(after.Token));
        Assert.Equal(1, host.Calls);
    }

    private static Dictionary<string, object?> Volume() => new(StringComparer.Ordinal)
    {
        ["DeviceID"] = @"\\?\Volume{fixture-volume}\",
        ["DriveLetter"] = "C:",
        ["PersistentVolumeID"] = "fixture-persistent-id",
        ["getters"] = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["GetConversionStatus"] = Method(true, 0u, new() { ["ConversionStatus"] = 1u, ["EncryptionPercentage"] = 100u, ["EncryptionFlags"] = 0u }),
            ["GetProtectionStatus"] = Method(true, 0u, new() { ["ProtectionStatus"] = 1u }),
            ["GetEncryptionMethod"] = Method(true, 0u, new() { ["EncryptionMethod"] = 6u }),
            ["GetLockStatus"] = Method(true, 0u, new() { ["LockStatus"] = 0u })
        },
        ["issues"] = Array.Empty<object>()
    };
    private static Dictionary<string, object?> Getters(Dictionary<string, object?> volume) => (Dictionary<string, object?>)volume["getters"]!;
    private static Dictionary<string, object?> Method(bool invoked, object? code, Dictionary<string, object?> values, object[]? notes = null) =>
        new(StringComparer.Ordinal) { ["invoked"] = invoked, ["ReturnValue"] = code, ["values"] = values, ["issues"] = notes ?? [] };
    private static string Document(Dictionary<string, object?>[] volumes, bool succeeded = true, int? examined = null, bool limited = false, object[]? notes = null) => JsonSerializer.Serialize(new
    { kind = "optimus.bitlocker-status", version = 1, observedAt = Observed, sourceNamespace = BitLockerStatusCollector.SourceNamespace, query = new { succeeded, examined = examined ?? volumes.Length, limitReached = limited, records = volumes, issues = notes ?? [] } });
    private static DiagnosticFact Fact(DiagnosticEvidence evidence, string key) => evidence.Facts.Single(fact => fact.Key == key);
    private static ToolRunResult Completed(string json) => new(ToolRunStatus.Exited, 0, json, "", new(TimeSpan.Zero));
    private static async Task<DiagnosticEvidence> Collect(string json) => Assert.Single(await new BitLockerStatusCollector(new Host(_ => Completed(json))).CollectAsync(CancellationToken.None));
    private sealed class Host(Func<CancellationToken, ToolRunResult> run) : IWindowsSystemQueryRunner
    {
        public int Calls { get; private set; }
        public WindowsSystemQuery? Query { get; private set; }
        public Task<ToolRunResult> RunAsync(WindowsSystemQuery query, CancellationToken cancellationToken) { Calls++; Query = query; return Task.FromResult(run(cancellationToken)); }
    }
}
