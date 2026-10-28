using System.ComponentModel;
using Optimus.Application;
using Optimus.Domain;
using Optimus.Infrastructure.Inventory;

namespace Optimus.Tests;

public sealed class NetworkPowerTests
{
    [Fact]
    public async Task LocalNetworkFactsKeepSourceIdentityNumericUnitsAndAddressFamilies()
    {
        var observation = Adapter() with
        {
            UnicastAddresses = [new("192.0.2.10", 2), new("fe80::1%12", 23)],
            GatewayAddresses = [new("192.0.2.1", 2)],
            DnsAddresses = [new("2001:db8::53", 23)]
        };
        var result = await new NetworkCollector(_ => [observation]).CollectAsync(CancellationToken.None);
        var adapter = Assert.Single(result, item => item.Subject == "fixture-source-id");

        Assert.Equal(InventoryCollectors.Network, adapter.CollectorId);
        Assert.Equal(EvidenceStatus.Complete, adapter.Status);
        Assert.Equal("fixture-source-id", Fact(adapter, "sourceId").TextValue);
        Assert.Equal(1_000_000_000m, Fact(adapter, "linkSpeedBitsPerSecond").NumberValue);
        Assert.Equal("bits/s", Fact(adapter, "linkSpeedBitsPerSecond").Unit);
        Assert.Equal("192.0.2.10", Fact(adapter, "unicast.0.address").TextValue);
        Assert.Equal("fe80::1%12", Fact(adapter, "unicast.1.address").TextValue);
        Assert.Equal("IPv6", Fact(adapter, "unicast.1.family").TextValue);
        Assert.Equal(23m, Fact(adapter, "unicast.1.familyRaw").NumberValue);
        Assert.Equal("2001:db8::53", Fact(adapter, "dns.0.address").TextValue);
        Assert.Equal(4, adapter.Coverage.ExaminedItems);
        Assert.Equal("false", Fact(result[0], "connectivityTested").TextValue);
    }

    [Fact]
    public async Task NegativeSpeedAndUnknownEnumKeepRawValuesWithoutInventingZeros()
    {
        var result = await new NetworkCollector(_ => [Adapter() with
        {
            LinkSpeedBitsPerSecond = -1, InterfaceType = 9999, OperationalStatus = null
        }]).CollectAsync(CancellationToken.None);
        var adapter = result[1];

        Assert.Equal(EvidenceStatus.Partial, adapter.Status);
        Assert.Equal(-1m, Fact(adapter, "linkSpeedReportedBitsPerSecond").NumberValue);
        Assert.Null(Fact(adapter, "linkSpeedBitsPerSecond").NumberValue);
        Assert.Equal(9999m, Fact(adapter, "interfaceTypeRaw").NumberValue);
        Assert.Null(Fact(adapter, "interfaceType").TextValue);
        Assert.Null(Fact(adapter, "operationalStatus").TextValue);
        Assert.Contains(adapter.Issues, item => item.Message.Contains("zero", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AdapterBudgetReportsOmissionInsteadOfClaimingCompleteEnumeration()
    {
        var result = await new NetworkCollector(_ => Enumerable.Range(0, 65)
            .Select(index => Adapter() with { SourceId = "adapter-" + index })).CollectAsync(CancellationToken.None);

        Assert.Equal(65, result.Count); // One discovery record and 64 adapter records.
        Assert.Equal(EvidenceStatus.Partial, result[0].Status);
        Assert.Equal(64, result[0].Coverage.ExaminedItems);
        Assert.True(result[0].Coverage.LimitReached);
        Assert.DoesNotContain(result, item => item.Subject == "adapter-64");
        Assert.Single(result[0].Issues, item => item.Code == IssueCode.LimitReached);
    }

    [Fact]
    public async Task AddressBudgetAndLateReadFailureRetainPartialFactsAndDifferentReasons()
    {
        static IEnumerable<NetworkAddressObservation> Interrupted()
        {
            yield return new("192.0.2.53", 2);
            throw new IOException("DNS enumeration interrupted");
        }
        var result = await new NetworkCollector(_ => [Adapter() with
        {
            UnicastAddresses = Enumerable.Range(1, 33).Select(index => new NetworkAddressObservation("192.0.2." + index, 2)),
            DnsAddresses = Interrupted()
        }]).CollectAsync(CancellationToken.None);
        var adapter = result[1];

        Assert.Equal(EvidenceStatus.Partial, adapter.Status);
        Assert.True(adapter.Coverage.LimitReached);
        Assert.Equal(33, adapter.Coverage.ExaminedItems);
        Assert.Equal(32m, Fact(adapter, "unicast.examined").NumberValue);
        Assert.Equal("192.0.2.32", Fact(adapter, "unicast.31.address").TextValue);
        Assert.DoesNotContain(adapter.Facts, fact => fact.Key == "unicast.32.address");
        Assert.Equal("192.0.2.53", Fact(adapter, "dns.0.address").TextValue);
        Assert.Contains(adapter.Issues, item => item.Code == IssueCode.LimitReached);
        Assert.Contains(adapter.Issues, item => item.Message.Contains("interrupted", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FailedAdapterFieldDoesNotPreventOtherAdapterFacts()
    {
        var result = await new NetworkCollector(_ => [Adapter() with
        {
            DnsAddresses = null,
            Issues = [new(IssueCode.PermissionDenied, "fixture property denied")]
        }, Adapter() with { SourceId = "second-adapter" }]).CollectAsync(CancellationToken.None);

        Assert.Equal(EvidenceStatus.Partial, result[1].Status);
        Assert.Null(Fact(result[1], "dns.examined").NumberValue);
        Assert.Equal(EvidenceStatus.Complete, result[2].Status);
        Assert.Equal("second-adapter", Fact(result[2], "sourceId").TextValue);
    }

    [Fact]
    public async Task LateDiscoveryFailurePreservesAlreadyReadAdapter()
    {
        static IEnumerable<NetworkAdapterObservation> Interrupted(CancellationToken _)
        {
            yield return Adapter();
            throw new UnauthorizedAccessException("late discovery denied");
        }
        var result = await new NetworkCollector(Interrupted).CollectAsync(CancellationToken.None);

        Assert.Equal(2, result.Count);
        Assert.Equal(EvidenceStatus.Partial, result[0].Status);
        Assert.Equal(1, result[0].Coverage.ExaminedItems);
        Assert.Equal("fixture-source-id", Fact(result[1], "sourceId").TextValue);
        Assert.Equal(IssueCode.PermissionDenied, Assert.Single(result[0].Issues).Code);
    }

    [Fact]
    public async Task DiscoveryFailureAndEmptyInventoryRemainDistinct()
    {
        var failed = Assert.Single(await new NetworkCollector(_ => throw new IOException("fixture failure")).CollectAsync(CancellationToken.None));
        var empty = Assert.Single(await new NetworkCollector(_ => []).CollectAsync(CancellationToken.None));

        Assert.Equal(EvidenceStatus.Unavailable, failed.Status);
        Assert.Equal(EvidenceStatus.Complete, empty.Status);
        Assert.Equal(0m, Fact(empty, "examinedAdapters").NumberValue);
        Assert.Equal("false", Fact(empty, "connectivityTested").TextValue);
        Assert.Empty(empty.Issues);
    }

    [Fact]
    public async Task CancelledEnumerationDoesNotPublishAPartialCompletedSnapshot()
    {
        using var cancellation = new CancellationTokenSource();
        IEnumerable<NetworkAdapterObservation> CancelAfterFirst(CancellationToken _)
        {
            yield return Adapter();
            cancellation.Cancel();
            yield return Adapter();
        }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new NetworkCollector(CancelAfterFirst).CollectAsync(cancellation.Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PrecancelledCollectorsNeverCallTheirNativeBoundary(bool power)
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        bool called = false;
        IDiagnosticCollector collector = power
            ? new PowerCollector(_ => { called = true; return Power(); })
            : new NetworkCollector(_ => { called = true; return [Adapter()]; });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => collector.CollectAsync(cancellation.Token));
        Assert.False(called);
    }

    [Fact]
    public async Task NoBatteryIsAnExplicitValidObservationAndNotAHealthyBattery()
    {
        var evidence = Assert.Single(await new PowerCollector(_ => new(1, 128, 255, 0, uint.MaxValue, uint.MaxValue)).CollectAsync(CancellationToken.None));

        Assert.Equal(InventoryCollectors.Power, evidence.CollectorId);
        Assert.Equal(EvidenceStatus.Complete, evidence.Status);
        Assert.Equal("Ausente", Fact(evidence, "batteryPresence").TextValue);
        Assert.Null(Fact(evidence, "batteryChargePercent").NumberValue);
        Assert.Null(Fact(evidence, "batteryRemainingSeconds").NumberValue);
        Assert.Equal(255m, Fact(evidence, "batteryLifePercentRaw").NumberValue);
        Assert.Equal((decimal)uint.MaxValue, Fact(evidence, "batteryLifeTimeRaw").NumberValue);
        Assert.Equal("false", Fact(evidence, "batteryHealthTested").TextValue);
        Assert.Empty(evidence.Issues);
    }

    [Fact]
    public async Task AllUnknownBatteryFlagDoesNotMeanEveryChargeFlagOrNoBattery()
    {
        var evidence = Assert.Single(await new PowerCollector(_ => new(255, 255, 255, 255, uint.MaxValue, uint.MaxValue)).CollectAsync(CancellationToken.None));

        Assert.Equal(EvidenceStatus.Partial, evidence.Status);
        Assert.Null(Fact(evidence, "acPower").TextValue);
        Assert.Null(Fact(evidence, "batteryPresence").TextValue);
        Assert.Null(Fact(evidence, "batteryChargingReported").TextValue);
        Assert.Null(Fact(evidence, "batteryChargePercent").NumberValue);
        Assert.Null(Fact(evidence, "batterySaver").TextValue);
        Assert.DoesNotContain(evidence.Facts, fact => fact.Key == "batteryChargeFlags");
        Assert.Equal(255m, Fact(evidence, "batteryFlagRaw").NumberValue);
        Assert.Equal(4, evidence.Issues.Count);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(100, 100)]
    [InlineData(101, null)]
    [InlineData(255, null)]
    public async Task ChargePercentagePreservesZeroAndRejectsSentinelsOrOutOfRange(int rawPercent, int? expected)
    {
        var evidence = Assert.Single(await new PowerCollector(_ => Power() with { BatteryLifePercent = (byte)rawPercent }).CollectAsync(CancellationToken.None));
        Assert.Equal(expected is { } number ? (decimal?)number : null, Fact(evidence, "batteryChargePercent").NumberValue);
        Assert.Equal((decimal)rawPercent, Fact(evidence, "batteryLifePercentRaw").NumberValue);
        Assert.Equal("%", Fact(evidence, "batteryChargePercent").Unit);
    }

    [Fact]
    public async Task EstimatesKeepSecondsAndRawDataWithoutHealthOrWearClaims()
    {
        var evidence = Assert.Single(await new PowerCollector(_ => Power()).CollectAsync(CancellationToken.None));
        Assert.Equal(EvidenceStatus.Complete, evidence.Status);
        Assert.Equal(3600m, Fact(evidence, "batteryRemainingSeconds").NumberValue);
        Assert.Equal("seconds", Fact(evidence, "batteryRemainingSeconds").Unit);
        Assert.Equal("true", Fact(evidence, "batteryChargingReported").TextValue);
        Assert.Equal("false", Fact(evidence, "batteryHealthTested").TextValue);
        Assert.Contains("estimativa", evidence.Coverage.Scope);
    }

    [Fact]
    public async Task FailedNativePowerReadPublishesNoZeroInitializedFields()
    {
        var evidence = Assert.Single(await new PowerCollector(_ => throw new Win32Exception(5)).CollectAsync(CancellationToken.None));
        Assert.Equal(EvidenceStatus.Unavailable, evidence.Status);
        Assert.Equal(0, evidence.Coverage.ExaminedItems);
        Assert.Empty(evidence.Facts);
        Assert.Single(evidence.Issues);
    }

    [Fact]
    public async Task CollectorsUseTheSharedDiagnosticWorkflowAndKeepCoverageFailuresPartial()
    {
        var service = new DiagnosticService([new NetworkCollector(_ => [Adapter()]),
            new PowerCollector(_ => new(255, 255, 255, 0, uint.MaxValue, uint.MaxValue))]);
        var session = await service.RunAsync(null, CancellationToken.None);
        Assert.Equal(SessionStatus.Partial, session.Status);
        Assert.True(SessionValidation.IsValid(session));
        Assert.Contains(session.Evidence, evidence => evidence.CollectorId == InventoryCollectors.Network);
        Assert.Contains(session.Evidence, evidence => evidence.CollectorId == InventoryCollectors.Power);
    }

    private static DiagnosticFact Fact(DiagnosticEvidence evidence, string key) => Assert.Single(evidence.Facts, fact => fact.Key == key);
    private static NetworkAdapterObservation Adapter() => new("fixture-source-id", "Fixture adapter", "Local fixture", 6, 1, 1_000_000_000, [], [], []);
    private static PowerStatusObservation Power() => new(1, 8, 50, 0, 3600, 7200);
}
