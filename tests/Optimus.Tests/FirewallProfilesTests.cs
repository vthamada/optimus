using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Optimus.Application;
using Optimus.Domain;
using Optimus.Infrastructure.Security;

namespace Optimus.Tests;

public sealed class FirewallProfilesTests
{
    private const string TypePrefix = "Microsoft.PowerShell.Cmdletization.GeneratedTypes.NetSecurity.";
    private const string Observed = "2026-10-28T12:13:14.1234567-03:00";

    [Fact]
    public async Task ActiveStoreQueryPreservesEnumNamesCodesTypesAndOriginalDateWithoutHealthInference()
    {
        var raw = Document([Profile()]);
        var host = new Host(_ => Completed(raw));
        var evidence = Assert.Single(await new FirewallProfilesCollector(host).CollectAsync(CancellationToken.None));

        Assert.Equal(WindowsSystemQuery.FirewallProfiles, host.Query);
        Assert.Equal(1, host.Calls);
        Assert.Equal(MaintenanceCollectors.Firewall, evidence.CollectorId);
        Assert.Equal(EvidenceStatus.Complete, evidence.Status);
        Assert.Equal(DateTimeOffset.Parse(Observed), evidence.CapturedAt);
        Assert.Equal(TimeSpan.FromHours(-3), evidence.CapturedAt.Offset);
        Assert.Equal(raw, Fact(evidence, "sourceJson").TextValue);
        Assert.Equal(Observed, Fact(evidence, "sourceObservedAt").TextValue);
        Assert.Equal("ActiveStore", Fact(evidence, "policyStore").TextValue);
        Assert.Equal("Domain", Fact(evidence, "profiles.0.Name").TextValue);
        Assert.Equal("True", Fact(evidence, "profiles.0.Enabled").TextValue);
        Assert.Contains("nome de enum original", Fact(evidence, "profiles.0.Enabled").Label);
        Assert.Equal(1m, Fact(evidence, "profiles.0.Enabled.code").NumberValue);
        Assert.Equal("Block", Fact(evidence, "profiles.0.DefaultInboundAction").TextValue);
        Assert.Equal(4m, Fact(evidence, "profiles.0.DefaultInboundAction.code").NumberValue);
        Assert.Equal("Allow", Fact(evidence, "profiles.0.DefaultOutboundAction").TextValue);
        Assert.Equal("false", Fact(evidence, "interfaceProfileActivationResolved").TextValue);
        Assert.Equal("false", Fact(evidence, "trafficEffectivenessTested").TextValue);
        Assert.Equal("false", Fact(evidence, "rulesQueried").TextValue);
        Assert.False(Fact(evidence, "profiles.0.Enabled").Detailed);
        Assert.True(Fact(evidence, "profiles.0.Enabled.code").Detailed);
        Assert.DoesNotContain(evidence.Facts, fact => fact.Key is "protected" or "healthy" or "activeProfile");
        using var document = JsonDocument.Parse(Fact(evidence, "sourceJson").TextValue!);
        var enabled = document.RootElement.GetProperty("query").GetProperty("records")[0].GetProperty("Enabled");
        Assert.Equal(TypePrefix + "GpoBoolean", enabled.GetProperty("sourceType").GetString());
        Assert.Equal("System.UInt16", enabled.GetProperty("underlyingType").GetString());
        Assert.Equal(1, enabled.GetProperty("cimValue").GetInt32());
        Assert.True(SessionValidation.IsValid(new(Guid.NewGuid(), evidence.CapturedAt, evidence.CapturedAt, SessionStatus.Complete, [evidence], [])));
    }

    [Theory]
    [InlineData(0, "False")]
    [InlineData(1, "True")]
    [InlineData(2, "NotConfigured")]
    public async Task GpoBooleanIsRetainedAsEnumRatherThanInventedBoolean(int code, string name)
    {
        var profile = Profile(); profile["Enabled"] = EnumValue("GpoBoolean", code, name);
        var evidence = await Collect(Document([profile]));
        Assert.Equal(EvidenceStatus.Complete, evidence.Status);
        Assert.Equal(name, Fact(evidence, "profiles.0.Enabled").TextValue);
        Assert.Equal((decimal)code, Fact(evidence, "profiles.0.Enabled.code").NumberValue);
        Assert.DoesNotContain(evidence.Facts, fact => fact.Key == "profiles.0.Enabled.booleanValue");
    }

    [Fact]
    public async Task TrueBooleanAndTextThatLooksBooleanRemainDifferentSourceRepresentations()
    {
        var boolean = Profile(); boolean["Enabled"] = Value("boolean", "System.Boolean", true, "Boolean");
        var evidence = await Collect(Document([boolean]));
        Assert.Equal(EvidenceStatus.Complete, evidence.Status);
        Assert.Equal("true", Fact(evidence, "profiles.0.Enabled").TextValue);
        Assert.Contains("booleano original", Fact(evidence, "profiles.0.Enabled").Label);
        Assert.DoesNotContain(evidence.Facts, fact => fact.Key == "profiles.0.Enabled.code");
        var text = Profile(); text["Enabled"] = Value("text", "System.String", "False", "String");
        var untyped = await Collect(Document([text]));
        Assert.Equal(EvidenceStatus.Partial, untyped.Status);
        Assert.Equal("False", Fact(untyped, "profiles.0.Enabled").TextValue);
        Assert.Contains("texto original", Fact(untyped, "profiles.0.Enabled").Label);
        Assert.Contains(untyped.Issues, issue => issue.Code == IssueCode.NotSupported);
    }

    [Fact]
    public async Task CimNullNeverBecomesAdaptedFalseOrNumericZero()
    {
        var profile = Profile(); profile["Enabled"] = Value("null", null, null, "UInt16");
        var raw = Document([profile]); var evidence = await Collect(raw);
        Assert.Equal(EvidenceStatus.Partial, evidence.Status);
        Assert.Null(Fact(evidence, "profiles.0.Enabled").TextValue);
        Assert.Null(Fact(evidence, "profiles.0.Enabled").NumberValue);
        Assert.DoesNotContain(evidence.Facts, fact => fact.Key == "profiles.0.Enabled.code");
        Assert.Equal(raw, Fact(evidence, "sourceJson").TextValue);
        Assert.Contains(evidence.Issues, issue => issue.Message.Contains("CIM nulo/omitido"));
    }

    [Fact]
    public async Task FailedAdaptedGetterKeepsKnownCimScalarWithoutInventingAnAdaptedType()
    {
        var profile = Profile(); var field = Value("unavailable", null, null, "UInt16"); field["cimValue"] = 2; profile["Enabled"] = field;
        var raw = Document([profile]); var evidence = await Collect(raw);
        Assert.Equal(EvidenceStatus.Partial, evidence.Status);
        Assert.Null(Fact(evidence, "profiles.0.Enabled").TextValue);
        Assert.Equal(raw, Fact(evidence, "sourceJson").TextValue);
        Assert.Contains(evidence.Issues, issue => issue.Message.Contains("tipo adaptado permanece desconhecido"));
    }

    [Theory]
    [InlineData(1, "False", "GpoBoolean", "ReadFailed")]
    [InlineData(7, "7", "GpoBoolean", "NotSupported")]
    [InlineData(1, "True", "FutureBoolean", "NotSupported")]
    public async Task UnknownOrContradictoryEnumsKeepOriginalCodeAndNameWithoutNormalization(int code, string name, string type, string issue)
    {
        var profile = Profile(); profile["Enabled"] = EnumValue(type, code, name);
        var raw = Document([profile]); var evidence = await Collect(raw);
        Assert.Equal(EvidenceStatus.Partial, evidence.Status);
        Assert.Equal((decimal)code, Fact(evidence, "profiles.0.Enabled.code").NumberValue);
        Assert.Equal(name, Fact(evidence, "profiles.0.Enabled.reportedName").TextValue);
        Assert.Equal(raw, Fact(evidence, "sourceJson").TextValue);
        Assert.Contains(evidence.Issues, item => item.Code.ToString() == issue);
        if (issue == "ReadFailed") Assert.Null(Fact(evidence, "profiles.0.Enabled").TextValue);
    }

    [Fact]
    public async Task NameMayBeTypedProfileEnumWithoutInferringAnActiveInterface()
    {
        var profile = Profile(); var name = EnumValue("Profile", 2, "Private"); name["cimType"] = "String"; name["cimValue"] = "Private"; profile["Name"] = name;
        var evidence = await Collect(Document([profile]));
        Assert.Equal(EvidenceStatus.Complete, evidence.Status);
        Assert.Equal("Private", Fact(evidence, "profiles.0.Name").TextValue);
        Assert.Equal(2m, Fact(evidence, "profiles.0.Name.code").NumberValue);
        Assert.Equal("false", Fact(evidence, "interfaceProfileActivationResolved").TextValue);
    }

    [Fact]
    public async Task AdaptedBooleanCannotHideTheUnderlyingNotConfiguredCode()
    {
        var profile = Profile(); var value = Value("boolean", "System.Boolean", true, "UInt16"); value["cimValue"] = 2; profile["Enabled"] = value;
        var raw = Document([profile]); var evidence = await Collect(raw);
        Assert.Equal(EvidenceStatus.Partial, evidence.Status);
        Assert.Null(Fact(evidence, "profiles.0.Enabled").TextValue);
        Assert.Equal(raw, Fact(evidence, "sourceJson").TextValue);
        Assert.Contains(evidence.Issues, issue => issue.Message.Contains("NotConfigured não foi convertido"));
    }

    [Fact]
    public async Task NumericValueWithoutEnumMetadataIsNotCoercedToTrue()
    {
        var profile = Profile(); profile["Enabled"] = Value("integer", "System.UInt16", 1, "UInt16");
        var evidence = await Collect(Document([profile]));
        Assert.Equal(EvidenceStatus.Partial, evidence.Status);
        Assert.Equal(1m, Fact(evidence, "profiles.0.Enabled").NumberValue);
        Assert.Null(Fact(evidence, "profiles.0.Enabled").TextValue);
        Assert.Contains(evidence.Issues, issue => issue.Code == IssueCode.NotSupported);
    }

    [Fact]
    public async Task ThreeOrdinaryProfilesFitTheProjectionBudgetAndRemainDistinct()
    {
        var records = new[] { Profile("Domain"), Profile("Private"), Profile("Public") };
        var evidence = await Collect(Document(records));
        Assert.Equal(EvidenceStatus.Complete, evidence.Status);
        Assert.Equal(3m, Fact(evidence, "retainedProfiles").NumberValue);
        Assert.Equal(3m, Fact(evidence, "projectedProfiles").NumberValue);
        Assert.Equal("Domain", Fact(evidence, "profiles.0.Name").TextValue);
        Assert.Equal("Private", Fact(evidence, "profiles.1.Name").TextValue);
        Assert.Equal("Public", Fact(evidence, "profiles.2.Name").TextValue);
    }

    [Fact]
    public async Task KnownEmptySelectionAndFailedDiscoveryAreNotTheSameOutcome()
    {
        var empty = await Collect(Document([]));
        Assert.Equal(EvidenceStatus.Complete, empty.Status);
        Assert.Equal(0m, Fact(empty, "retainedProfiles").NumberValue);
        Assert.Empty(empty.Issues);
        var failed = await Collect(Document([], succeeded: false));
        Assert.Equal(EvidenceStatus.Unavailable, failed.Status);
        Assert.Equal("false", Fact(failed, "querySucceeded").TextValue);
    }

    [Fact]
    public async Task LateDiscoveryFailureKeepsEarlierProfilesAndExplicitPartialCoverage()
    {
        var raw = Document([Profile()], succeeded: false);
        var evidence = await Collect(raw);
        Assert.Equal(EvidenceStatus.Partial, evidence.Status);
        Assert.Equal("Domain", Fact(evidence, "profiles.0.Name").TextValue);
        Assert.Equal(raw, Fact(evidence, "sourceJson").TextValue);
    }

    [Fact]
    public async Task MissingFieldsAndOversizedScalarsStayUnknownWithoutTruncation()
    {
        var record = Profile(); record.Remove("NotifyOnListen"); record["Name"] = Value("text", "System.String", new string('X', 513), "String");
        var raw = Document([record]); var evidence = await Collect(raw);
        Assert.Equal(EvidenceStatus.Partial, evidence.Status);
        Assert.Null(Fact(evidence, "profiles.0.Name").TextValue);
        Assert.Null(Fact(evidence, "profiles.0.NotifyOnListen").TextValue);
        Assert.Equal(raw, Fact(evidence, "sourceJson").TextValue);
    }

    [Theory]
    [InlineData("PersistentStore")]
    [InlineData("host.example.test\\GPO")]
    public async Task DifferentPolicyStoresCannotBeReportedAsTheSelectedActiveStore(string store)
    {
        var raw = JsonNode.Parse(Document([Profile()]))!; raw["policyStore"] = store;
        var evidence = await Collect(raw.ToJsonString());
        Assert.Equal(EvidenceStatus.Unavailable, evidence.Status);
        Assert.DoesNotContain(evidence.Facts, fact => fact.Key.StartsWith("profiles.", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("{\"kind\":\"optimus.firewall-profiles\",\"kind\":\"ambiguous\"}")]
    public async Task MalformedRootKeepsOriginalDataWithoutPartiallyTrustedProfiles(string raw)
    {
        var evidence = await Collect(raw);
        Assert.Equal(EvidenceStatus.Unavailable, evidence.Status);
        Assert.Equal(raw, Fact(evidence, "sourceJson").TextValue);
    }

    [Fact]
    public async Task DuplicateNestedFieldMetadataInvalidatesTheWholeProjection()
    {
        var raw = Document([Profile()]).Replace("\"kind\":\"enum\"", "\"kind\":\"enum\",\"kind\":\"boolean\"", StringComparison.Ordinal);
        var evidence = await Collect(raw);
        Assert.Equal(EvidenceStatus.Unavailable, evidence.Status);
        Assert.DoesNotContain(evidence.Facts, fact => fact.Key.StartsWith("profiles.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RawCimCodeAndAdaptedEnumDisagreementRemainsUnknown()
    {
        var profile = Profile(); var value = EnumValue("GpoBoolean", 1, "True"); value["cimValue"] = 2; profile["Enabled"] = value;
        var raw = Document([profile]); var evidence = await Collect(raw);
        Assert.Equal(EvidenceStatus.Partial, evidence.Status);
        Assert.Null(Fact(evidence, "profiles.0.Enabled").TextValue);
        Assert.Equal(raw, Fact(evidence, "sourceJson").TextValue);
        Assert.Contains(evidence.Issues, issue => issue.Message.Contains("diverge do valor CIM"));
    }

    [Fact]
    public async Task ProbeAndProjectionLimitsKeepAllReceivedProfilesInOriginalJson()
    {
        var records = Enumerable.Range(0, 8).Select(index => Profile("Profile-" + index)).ToArray();
        var raw = Document(records, examined: 9, limited: true); var evidence = await Collect(raw);
        Assert.Equal(EvidenceStatus.Partial, evidence.Status);
        Assert.Equal(9m, Fact(evidence, "examinedProfiles").NumberValue);
        Assert.Equal(8m, Fact(evidence, "retainedProfiles").NumberValue);
        Assert.InRange(Fact(evidence, "projectedProfiles").NumberValue!.Value, 1m, 7m);
        Assert.Equal(raw, Fact(evidence, "sourceJson").TextValue);
        Assert.True(evidence.Coverage.LimitReached);
    }

    [Fact]
    public async Task ImpossibleCountsDoNotBecomeACompleteSelection()
    {
        var evidence = await Collect(Document([Profile()], examined: 2));
        Assert.Equal(EvidenceStatus.Unavailable, evidence.Status);
        Assert.DoesNotContain(evidence.Facts, fact => fact.Key.StartsWith("profiles.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SourceLimitUsesUtf8BytesRatherThanCharacterCount()
    {
        var evidence = await Collect(new string('Ω', 32769));
        Assert.Equal(EvidenceStatus.Unavailable, evidence.Status);
        Assert.Equal(65538m, Fact(evidence, "sourceJsonUtf8Bytes").NumberValue);
        Assert.DoesNotContain(evidence.Facts, fact => fact.Key == "sourceJson");
    }

    [Fact]
    public async Task FailedTransportRetainsOriginalReasonPendingResourcesAndBytes()
    {
        var execution = new ToolRunResult(ToolRunStatus.CleanupFailed, null, "raw partial", "stderr Ω", new(TimeSpan.FromMilliseconds(1250), TimeSpan.FromMilliseconds(300), 12345),
            new(ErrorCode.PermissionDenied, "primary denied Ω", [@"D:\owned Ω"]), [1, 2], [3, 4], 65001);
        var evidence = Assert.Single(await new FirewallProfilesCollector(new Host(_ => execution)).CollectAsync(CancellationToken.None));
        Assert.Equal(EvidenceStatus.Unavailable, evidence.Status);
        Assert.Equal("raw partial", Fact(evidence, "sourceJson").TextValue);
        Assert.Equal("stderr Ω", Fact(evidence, "toolStderr").TextValue);
        Assert.Equal("primary denied Ω", Fact(evidence, "toolErrorMessage").TextValue);
        Assert.Equal(new[] { @"D:\owned Ω" }, JsonSerializer.Deserialize<string[]>(Fact(evidence, "toolPendingCleanupPaths").TextValue!));
        Assert.Equal("AQI=", Fact(evidence, "toolStdoutBytesBase64").TextValue);
        Assert.Equal(1.25m, Fact(evidence, "elapsedSeconds").NumberValue);
        Assert.Equal(0.3m, Fact(evidence, "cpuSeconds").NumberValue);
        Assert.Equal(12345m, Fact(evidence, "peakProcessFamilyMemoryBytes").NumberValue);
    }

    [Fact]
    public async Task MissingMetricsRemainUnknownAndLargeMetadataIsOmittedExplicitly()
    {
        var execution = Completed(Document([])) with { StandardError = new string('Ω', 5000), Error = new(ErrorCode.ToolFailed, new string('X', 9000), Enumerable.Repeat("pending", 65).ToArray()) };
        var evidence = Assert.Single(await new FirewallProfilesCollector(new Host(_ => execution)).CollectAsync(CancellationToken.None));
        Assert.Equal(EvidenceStatus.Unavailable, evidence.Status);
        Assert.Null(Fact(evidence, "cpuSeconds").NumberValue);
        Assert.Null(Fact(evidence, "peakProcessFamilyMemoryBytes").NumberValue);
        Assert.Null(Fact(evidence, "toolStderr").TextValue);
        Assert.Null(Fact(evidence, "toolErrorMessage").TextValue);
        Assert.Null(Fact(evidence, "toolPendingCleanupPaths").TextValue);
        Assert.Equal(10000m, Fact(evidence, "toolStderr.utf8Bytes").NumberValue);
        Assert.Contains(evidence.Issues, issue => issue.Code == IssueCode.LimitReached);
    }

    [Fact]
    public async Task CancellationBeforeOrAfterHostDoesNotPublishProfiles()
    {
        using var before = new CancellationTokenSource(); before.Cancel();
        var host = new Host(_ => Completed(Document([])));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new FirewallProfilesCollector(host).CollectAsync(before.Token));
        Assert.Equal(0, host.Calls);
        using var after = new CancellationTokenSource();
        host = new Host(_ => { after.Cancel(); return Completed(Document([Profile()])); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new FirewallProfilesCollector(host).CollectAsync(after.Token));
        Assert.Equal(1, host.Calls);
    }

    private static Dictionary<string, object?> Profile(string name = "Domain") => new(StringComparer.Ordinal)
    {
        ["Name"] = Value("text", "System.String", name, "String"),
        ["Enabled"] = EnumValue("GpoBoolean", 1, "True"),
        ["DefaultInboundAction"] = EnumValue("Action", 4, "Block"),
        ["DefaultOutboundAction"] = EnumValue("Action", 2, "Allow"),
        ["AllowInboundRules"] = EnumValue("GpoBoolean", 1, "True"),
        ["AllowLocalFirewallRules"] = EnumValue("GpoBoolean", 2, "NotConfigured"),
        ["AllowLocalIPsecRules"] = EnumValue("GpoBoolean", 2, "NotConfigured"),
        ["NotifyOnListen"] = EnumValue("GpoBoolean", 1, "True")
    };
    private static Dictionary<string, object?> EnumValue(string type, int code, string name) => new(StringComparer.Ordinal)
    { ["kind"] = "enum", ["sourceType"] = TypePrefix + type, ["underlyingType"] = "System.UInt16", ["value"] = code, ["name"] = name, ["cimType"] = "UInt16", ["cimValue"] = code };
    private static Dictionary<string, object?> Value(string kind, string? sourceType, object? value, string cimType) => new(StringComparer.Ordinal)
    { ["kind"] = kind, ["sourceType"] = sourceType, ["underlyingType"] = null, ["value"] = value, ["name"] = null, ["cimType"] = cimType, ["cimValue"] = value };
    private static string Document(Dictionary<string, object?>[] profiles, bool succeeded = true, int? examined = null, bool limited = false) => JsonSerializer.Serialize(new
    { kind = "optimus.firewall-profiles", version = 1, observedAt = Observed, policyStore = "ActiveStore", query = new { succeeded, examined = examined ?? profiles.Length, limitReached = limited, records = profiles, issues = Array.Empty<object>() } });
    private static DiagnosticFact Fact(DiagnosticEvidence evidence, string key) => evidence.Facts.Single(fact => fact.Key == key);
    private static ToolRunResult Completed(string json) => new(ToolRunStatus.Exited, 0, json, "", new(TimeSpan.Zero));
    private static async Task<DiagnosticEvidence> Collect(string json) => Assert.Single(await new FirewallProfilesCollector(new Host(_ => Completed(json))).CollectAsync(CancellationToken.None));
    private sealed class Host(Func<CancellationToken, ToolRunResult> run) : IWindowsSystemQueryRunner
    {
        public int Calls { get; private set; }
        public WindowsSystemQuery? Query { get; private set; }
        public Task<ToolRunResult> RunAsync(WindowsSystemQuery query, CancellationToken cancellationToken) { Calls++; Query = query; return Task.FromResult(run(cancellationToken)); }
    }
}
