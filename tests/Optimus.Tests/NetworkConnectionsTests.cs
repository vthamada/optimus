using System.Text.Json;
using Optimus.Application;
using Optimus.Domain;
using Optimus.Infrastructure.Network;

namespace Optimus.Tests;

public sealed class NetworkConnectionsTests
{
    private const string Stamp = "2026-10-28T10:11:12.1234567-03:00";

    [Fact]
    public async Task NamedLocalQueriesKeepIndependentCoverageAndOriginalIpText()
    {
        var tcp = Tcp();
        tcp["LocalAddress"] = "fe80::abc%0";
        tcp["RemoteAddress"] = "2001:0DB8:0000::1";
        var udp = Udp();
        udp["LocalAddress"] = "fe80::abc%12";
        var json = Document(Query([tcp]), Query([udp]));
        var host = new Host(Completed(json));

        var evidence = Assert.Single(await new NetworkConnectionsCollector(host).CollectAsync(CancellationToken.None));

        Assert.Equal(WindowsSystemQuery.NetworkConnections, host.Query);
        Assert.Equal(1, host.Calls);
        Assert.Equal(MaintenanceCollectors.Connections, evidence.CollectorId);
        Assert.Equal(EvidenceStatus.Complete, evidence.Status);
        Assert.Equal(DateTimeOffset.Parse(Stamp), evidence.CapturedAt);
        Assert.Equal(TimeSpan.FromHours(-3), evidence.CapturedAt.Offset);
        Assert.Equal(json, Fact(evidence, "sourceJson").TextValue);
        Assert.Equal("fe80::abc%0", Fact(evidence, "tcp.0.LocalAddress").TextValue);
        Assert.Equal("2001:0DB8:0000::1", Fact(evidence, "tcp.0.RemoteAddress").TextValue);
        Assert.Equal("fe80::abc%12", Fact(evidence, "udp.0.LocalAddress").TextValue);
        Assert.Equal("false", Fact(evidence, "networkTrafficRequested").TextValue);
        Assert.Equal("false", Fact(evidence, "processIdentityResolved").TextValue);
        Assert.False(Fact(evidence, "tcp.0.LocalAddress").Detailed);
        Assert.DoesNotContain(evidence.Facts, fact => fact.Key.StartsWith("udp.0.Remote", StringComparison.Ordinal));
        Assert.Contains("não comprova ownership", evidence.Coverage.Scope);
        Assert.True(SessionValidation.IsValid(new(Guid.NewGuid(), evidence.CapturedAt, evidence.CapturedAt, SessionStatus.Complete, [evidence], [])));
    }

    [Fact]
    public async Task PortsPidsAndZeroKeepTheirExactUnsignedValuesWithoutMeaningInference()
    {
        var tcp = Tcp();
        tcp["LocalPort"] = 0U;
        tcp["RemotePort"] = 65535U;
        tcp["OwningProcess"] = uint.MaxValue;
        tcp["State"] = uint.MaxValue;
        var udp = Udp();
        udp["OwningProcess"] = 0U;

        var evidence = await Collect(Document(Query([tcp]), Query([udp])));

        Assert.Equal(EvidenceStatus.Complete, evidence.Status);
        Assert.Equal(0m, Fact(evidence, "tcp.0.LocalPort").NumberValue);
        Assert.Equal(65535m, Fact(evidence, "tcp.0.RemotePort").NumberValue);
        Assert.Equal(4_294_967_295m, Fact(evidence, "tcp.0.OwningProcess").NumberValue);
        Assert.Equal(4_294_967_295m, Fact(evidence, "tcp.0.State").NumberValue);
        Assert.Equal(0m, Fact(evidence, "udp.0.OwningProcess").NumberValue);
        Assert.DoesNotContain(evidence.Facts, item => item.Key == "tcp.0.State.name");
    }

    [Theory]
    [InlineData(5U, "Established")]
    [InlineData(100U, "Bound")]
    [InlineData(42U, "42")]
    public async Task TypedStateKeepsOriginalEnumCodeNameAndTypeWithoutMapping(uint code, string name)
    {
        var tcp = Tcp();
        tcp["State"] = new { sourceType = "Microsoft.PowerShell.Cmdletization.GeneratedTypes.NetTCPIP.State", value = code, name };
        var evidence = await Collect(Document(Query([tcp]), Query([])));
        Assert.Equal(EvidenceStatus.Complete, evidence.Status);
        Assert.Equal((decimal)code, Fact(evidence, "tcp.0.State").NumberValue);
        Assert.Equal(name, Fact(evidence, "tcp.0.State.name").TextValue);
        Assert.Equal("Microsoft.PowerShell.Cmdletization.GeneratedTypes.NetTCPIP.State", Fact(evidence, "tcp.0.State.sourceType").TextValue);
        Assert.False(Fact(evidence, "tcp.0.State.name").Detailed);
    }

    [Theory]
    [InlineData("LocalPort", "-1")]
    [InlineData("RemotePort", "65536")]
    [InlineData("LocalPort", "\"80\"")]
    [InlineData("OwningProcess", "4294967296")]
    [InlineData("OwningProcess", "1.5")]
    [InlineData("State", "\"Established\"")]
    [InlineData("State", "-1")]
    [InlineData("State", "{\"sourceType\":\"Enum\",\"value\":-1,\"name\":\"Unknown\"}")]
    [InlineData("LocalAddress", "null")]
    [InlineData("RemoteAddress", "false")]
    public async Task UnsupportedValuesStayUnknownWhileOtherRowsAndProtocolRemainAvailable(string field, string raw)
    {
        using var value = JsonDocument.Parse(raw);
        var tcp = Tcp();
        tcp[field] = value.RootElement.Clone();
        var json = Document(Query([tcp]), Query([Udp()]));
        var evidence = await Collect(json);
        Assert.Equal(EvidenceStatus.Partial, evidence.Status);
        Assert.Null(Fact(evidence, "tcp.0." + field).TextValue);
        Assert.Null(Fact(evidence, "tcp.0." + field).NumberValue);
        Assert.Equal("::", Fact(evidence, "udp.0.LocalAddress").TextValue);
        Assert.Equal(json, Fact(evidence, "sourceJson").TextValue);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OneProtocolFailureDoesNotEraseTheOtherProtocolOrEarlierRows(bool earlierTcp)
    {
        var tcp = Query(earlierTcp ? [Tcp()] : [], succeeded: false, issues: [Note("PermissionDenied", "fixture TCP access denied")]);
        var evidence = await Collect(Document(tcp, Query([Udp()])));
        Assert.Equal(EvidenceStatus.Partial, evidence.Status);
        Assert.Equal("false", Fact(evidence, "tcp.querySucceeded").TextValue);
        Assert.Equal("true", Fact(evidence, "udp.querySucceeded").TextValue);
        Assert.Equal(1m, Fact(evidence, "udp.projected").NumberValue);
        Assert.Contains(evidence.Issues, item => item.Code == IssueCode.PermissionDenied);
        if (earlierTcp) Assert.Equal("127.0.0.1", Fact(evidence, "tcp.0.LocalAddress").TextValue);
    }

    [Fact]
    public async Task EmptyKnownCatalogAndTwoFailedCatalogsAreDistinct()
    {
        var empty = await Collect(Document(Query([]), Query([])));
        var failed = await Collect(Document(Query([], false), Query([], false)));
        Assert.Equal(EvidenceStatus.Complete, empty.Status);
        Assert.Equal(0m, Fact(empty, "tcp.retained").NumberValue);
        Assert.Equal(0m, Fact(empty, "udp.retained").NumberValue);
        Assert.Equal(EvidenceStatus.Unavailable, failed.Status);
        Assert.Equal(0m, Fact(failed, "udp.retained").NumberValue);
        Assert.Contains(failed.Issues, item => item.Message.Contains("não concluída", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ProtocolBudgetsAreIndependentAndPreserveSourceIndexesWhenRowsAreOmitted()
    {
        var tcpRows = Enumerable.Range(0, 256).Select(_ => Tcp()).ToArray();
        var udpRows = Enumerable.Range(0, 128).Select(_ => Udp()).ToArray();
        var json = Document(Query(tcpRows, examined: 257, limited: true), Query(udpRows, examined: 129, limited: true));
        var evidence = await Collect(json);
        Assert.Equal(EvidenceStatus.Partial, evidence.Status);
        Assert.Equal(386, evidence.Coverage.ExaminedItems);
        Assert.True(evidence.Coverage.LimitReached);
        Assert.Equal(256m, Fact(evidence, "tcp.retained").NumberValue);
        Assert.Equal(128m, Fact(evidence, "udp.retained").NumberValue);
        Assert.InRange(Fact(evidence, "tcp.projected").NumberValue!.Value, 1m, 255m);
        Assert.InRange(Fact(evidence, "udp.projected").NumberValue!.Value, 1m, 127m);
        Assert.Equal(0m, Fact(evidence, "udp.0.sourceIndex").NumberValue);
        Assert.Equal(json, Fact(evidence, "sourceJson").TextValue);
    }

    [Fact]
    public async Task SourceIssueProjectionCannotConsumeTheOtherProtocolsBudget()
    {
        var notes = Enumerable.Range(0, 50).Select(_ => Note("ReadFailed", new string('x', 2048))).ToArray();
        var evidence = await Collect(Document(Query([Tcp()], issues: notes), Query([Udp()])));
        Assert.Equal(EvidenceStatus.Partial, evidence.Status);
        Assert.Equal(1m, Fact(evidence, "udp.projected").NumberValue);
        Assert.Contains(evidence.Issues, item => item.Code == IssueCode.LimitReached && item.Message.Contains("limitações da fonte", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("{\"kind\":\"optimus.network-connections\",\"version\":1}")]
    [InlineData("{\"kind\":\"optimus.network-connections\",\"kind\":\"ambiguous\",\"version\":1,\"observedAt\":\"2026-10-28T10:00:00Z\",\"tcp\":{},\"udp\":{}}")]
    public async Task InvalidEnvelopeIsRetainedWithoutPublishingTrustedRows(string json)
    {
        var evidence = await Collect(json);
        Assert.Equal(EvidenceStatus.Unavailable, evidence.Status);
        Assert.Equal(json, Fact(evidence, "sourceJson").TextValue);
        Assert.DoesNotContain(evidence.Facts, item => item.Key.StartsWith("tcp.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DuplicateRowPropertyUnknownFieldAndImpossibleCountsAreRejected()
    {
        var tcp = Tcp();
        tcp["CreationTime"] = "not selected";
        Assert.Equal(EvidenceStatus.Unavailable, (await Collect(Document(Query([tcp]), Query([])))).Status);
        var duplicated = Document(Query([Tcp()]), Query([])).Replace("\"LocalPort\":80", "\"LocalPort\":80,\"LocalPort\":81", StringComparison.Ordinal);
        Assert.Equal(EvidenceStatus.Unavailable, (await Collect(duplicated)).Status);
        Assert.Equal(EvidenceStatus.Unavailable, (await Collect(Document(Query([Tcp()], examined: 0), Query([])))).Status);
        Assert.Equal(EvidenceStatus.Unavailable, (await Collect(Document(Query([Tcp()], examined: 257), Query([])))).Status);
    }

    [Fact]
    public async Task OversizedAddressIsUnknownWithoutNormalizingOrTruncatingTheOriginal()
    {
        var tcp = Tcp();
        tcp["LocalAddress"] = new string('x', 513);
        var json = Document(Query([tcp]), Query([Udp()]));
        var evidence = await Collect(json);
        Assert.Equal(EvidenceStatus.Partial, evidence.Status);
        Assert.Null(Fact(evidence, "tcp.0.LocalAddress").TextValue);
        Assert.Equal(json, Fact(evidence, "sourceJson").TextValue);
        Assert.Contains(evidence.Issues, item => item.Code == IssueCode.LimitReached);
    }

    [Fact]
    public async Task ResponseLimitIsUtf8BasedAndDoesNotParseAPrefix()
    {
        var evidence = await Collect(new string('Ω', NetworkConnectionsCollector.MaximumResponseBytes / 2 + 1));
        Assert.Equal(EvidenceStatus.Unavailable, evidence.Status);
        Assert.Equal(NetworkConnectionsCollector.MaximumResponseBytes + 2m, Fact(evidence, "sourceJsonUtf8Bytes").NumberValue);
        Assert.DoesNotContain(evidence.Facts, item => item.Key == "sourceJson");
    }

    [Fact]
    public async Task FailedTransportRetainsOriginalReasonBytesAndCleanupOwnership()
    {
        var result = new ToolRunResult(ToolRunStatus.CleanupFailed, null, "raw prefix", "original stderr Ω", new(TimeSpan.FromTicks(15), TimeSpan.FromTicks(9), 1234),
            new(ErrorCode.ToolFailed, "primary fixture error", [@"D:\fixture\owned-resource"]), [1, 2], [3, 4]);
        var evidence = Assert.Single(await new NetworkConnectionsCollector(new Host(result)).CollectAsync(CancellationToken.None));
        Assert.Equal(EvidenceStatus.Unavailable, evidence.Status);
        Assert.Equal("primary fixture error", Fact(evidence, "toolErrorMessage").TextValue);
        Assert.Equal("original stderr Ω", Fact(evidence, "toolStderr").TextValue);
        Assert.Equal("raw prefix", Fact(evidence, "sourceJson").TextValue);
        Assert.Equal("AQI=", Fact(evidence, "toolStdoutBytesBase64").TextValue);
        Assert.Equal("AwQ=", Fact(evidence, "toolStderrBytesBase64").TextValue);
        Assert.Contains("owned-resource", Fact(evidence, "toolPendingCleanupPaths").TextValue!);
        Assert.Equal(0.0000015m, Fact(evidence, "elapsedSeconds").NumberValue);
        Assert.Equal(0.0000009m, Fact(evidence, "cpuSeconds").NumberValue);
        Assert.Equal(1234m, Fact(evidence, "peakProcessFamilyMemoryBytes").NumberValue);
        Assert.True(Fact(evidence, "cpuSeconds").Detailed);
    }

    [Fact]
    public async Task MissingMetricsAreUnknownAndCancellationDoesNotPublishACollection()
    {
        var evidence = await Collect(Document(Query([]), Query([])));
        Assert.Null(Fact(evidence, "cpuSeconds").NumberValue);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var host = new Host(Completed(Document(Query([Tcp()]), Query([]))));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new NetworkConnectionsCollector(host).CollectAsync(cancelled.Token));
        Assert.Equal(0, host.Calls);
        using var late = new CancellationTokenSource();
        host.OnRun = late.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new NetworkConnectionsCollector(host).CollectAsync(late.Token));
    }

    private static Dictionary<string, object?> Tcp() => new(StringComparer.Ordinal)
    { ["LocalAddress"] = "127.0.0.1", ["LocalPort"] = 80U, ["RemoteAddress"] = "0.0.0.0", ["RemotePort"] = 0U, ["State"] = 2U, ["OwningProcess"] = 17U };
    private static Dictionary<string, object?> Udp() => new(StringComparer.Ordinal)
    { ["LocalAddress"] = "::", ["LocalPort"] = 53U, ["OwningProcess"] = 9U };
    private static object Note(string code, string message) => new { code, message };
    private static object Query(IReadOnlyList<Dictionary<string, object?>> records, bool succeeded = true, int? examined = null, bool limited = false, object[]? issues = null) =>
        new { succeeded, examined = examined ?? records.Count, limitReached = limited, records, issues = issues ?? [] };
    private static string Document(object tcp, object udp) => JsonSerializer.Serialize(new { kind = "optimus.network-connections", version = 1, observedAt = Stamp, tcp, udp });
    private static ToolRunResult Completed(string json) => new(ToolRunStatus.Exited, 0, json, "", new(TimeSpan.Zero));
    private static async Task<DiagnosticEvidence> Collect(string json) => Assert.Single(await new NetworkConnectionsCollector(new Host(Completed(json))).CollectAsync(CancellationToken.None));
    private static DiagnosticFact Fact(DiagnosticEvidence evidence, string key) => evidence.Facts.Single(item => item.Key == key);
    private sealed class Host(ToolRunResult result) : IWindowsSystemQueryRunner
    {
        public int Calls { get; private set; }
        public WindowsSystemQuery? Query { get; private set; }
        public Action? OnRun { get; set; }
        public Task<ToolRunResult> RunAsync(WindowsSystemQuery query, CancellationToken cancellationToken)
        { Calls++; Query = query; OnRun?.Invoke(); return Task.FromResult(result); }
    }
}
