using Optimus.Application;
using Optimus.Domain;

namespace Optimus.Tests;

public sealed class EvidenceSessionUpdateTests
{
    [Fact]
    public void RefreshingDefenderPreservesDriversInventoryAndTheirOriginalDates()
    {
        var oldDefender = Evidence(MaintenanceCollectors.Defender);
        var drivers = Evidence(MaintenanceCollectors.DeviceDrivers);
        var inventory = Evidence(InventoryCollectors.Network);
        var previous = Session([oldDefender, drivers, inventory]);
        var fresh = Evidence(MaintenanceCollectors.Defender) with { Status = EvidenceStatus.Unavailable, Facts = [], Issues = [new(IssueCode.PermissionDenied, "Selected query denied")] };

        var result = EvidenceSessionUpdates.ReplaceOwned(previous, Session([fresh]), new HashSet<string>([MaintenanceCollectors.Defender], StringComparer.Ordinal));

        Assert.True(result.IsSuccess);
        Assert.Equal(SessionStatus.Partial, result.Value!.Status);
        Assert.Same(drivers, result.Value.Evidence.Single(item => item.Id == drivers.Id));
        Assert.Same(inventory, result.Value.Evidence.Single(item => item.Id == inventory.Id));
        Assert.DoesNotContain(result.Value.Evidence, item => item.Id == oldDefender.Id);
        Assert.Same(fresh, result.Value.Evidence.Single(item => item.CollectorId == MaintenanceCollectors.Defender));
        Assert.Equal(3, previous.Evidence.Count);
    }

    [Fact]
    public void ACallerSetComparerCannotBroadenCollectorIdentity()
    {
        var differentlyCased = Evidence("WINDOWS.DEFENDER");
        var current = Evidence(MaintenanceCollectors.Defender);
        var result = EvidenceSessionUpdates.ReplaceOwned(Session([differentlyCased]), Session([current]),
            new HashSet<string>([MaintenanceCollectors.Defender], StringComparer.OrdinalIgnoreCase));

        Assert.True(result.IsSuccess);
        Assert.Equal(2, result.Value!.Evidence.Count);
        Assert.Same(differentlyCased, result.Value.Evidence[0]);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("sfc-verify /scannow")]
    [InlineData("dism-repair")]
    [InlineData("SfcVerifyOnly")]
    public void IntegrityTokensCannotSelectUnadvertisedOperations(string input) =>
        Assert.False(MaintenanceOperations.TryParseIntegrity(input, out _));

    private static DiagnosticEvidence Evidence(string collector) => new(Guid.NewGuid(), collector, collector, "Selected scope",
        DateTimeOffset.Parse("2026-10-20T10:00:00Z"), EvidenceStatus.Complete, new("Fixture coverage", 1), [new("unknown", "Original unknown")], []);
    private static DiagnosticSession Session(IReadOnlyList<DiagnosticEvidence> evidence) => new(Guid.NewGuid(),
        DateTimeOffset.Parse("2026-10-27T10:00:00Z"), DateTimeOffset.Parse("2026-10-27T10:01:00Z"),
        evidence.All(item => item.Status == EvidenceStatus.Complete) ? SessionStatus.Complete : SessionStatus.Partial,
        evidence, DiagnosticRules.Evaluate(evidence));
}
