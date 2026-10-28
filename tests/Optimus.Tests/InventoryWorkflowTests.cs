using Optimus.Application;
using Optimus.Domain;

namespace Optimus.Tests;

public sealed class InventoryWorkflowTests
{
    private static readonly DateTimeOffset Earlier = DateTimeOffset.Parse("2026-10-20T10:00:00Z");
    private static readonly DateTimeOffset Later = DateTimeOffset.Parse("2026-10-27T10:00:00Z");

    [Fact]
    public void RefreshReplacesOnlyOwnedCollectorsAndPreservesOriginalEvidenceIdentityAndTime()
    {
        var system = Evidence("windows.system", Earlier);
        var smart = Evidence("smart.fixture", Earlier);
        var old = Evidence(InventoryCollectors.Network, Earlier);
        var previous = Session(Earlier, [system, smart, old]);
        var inventory = Inventory();

        var result = EvidenceSessionUpdates.ReplaceOwned(previous, inventory, InventoryCollectors.All);

        Assert.True(result.IsSuccess, result.Error?.Message);
        var merged = result.Value!;
        Assert.NotEqual(previous.Id, merged.Id);
        Assert.NotEqual(inventory.Id, merged.Id);
        Assert.Equal(Later, merged.StartedAt);
        Assert.Same(system, merged.Evidence.Single(item => item.Id == system.Id));
        Assert.Same(smart, merged.Evidence.Single(item => item.Id == smart.Id));
        Assert.Equal(Earlier, merged.Evidence[0].CapturedAt);
        Assert.DoesNotContain(merged.Evidence, item => item.Id == old.Id);
        Assert.Equal(6, merged.Evidence.Count);
        Assert.Equal(3, previous.Evidence.Count);
        Assert.Equal(4, inventory.Evidence.Count);
    }

    [Fact]
    public void UnavailableRefreshRemovesStaleOwnedFactsAndRetainsPartialCoverage()
    {
        var old = Evidence(InventoryCollectors.Power, Earlier);
        var fresh = Inventory();
        var unavailable = Evidence(InventoryCollectors.Power, Later, EvidenceStatus.Unavailable);
        fresh = Session(Later, fresh.Evidence.Where(item => item.CollectorId != InventoryCollectors.Power).Append(unavailable).ToArray());

        var result = EvidenceSessionUpdates.ReplaceOwned(Session(Earlier, [old]), fresh, InventoryCollectors.All);

        Assert.True(result.IsSuccess);
        Assert.Equal(SessionStatus.Partial, result.Value!.Status);
        var power = Assert.Single(result.Value.Evidence, item => item.CollectorId == InventoryCollectors.Power);
        Assert.Equal(unavailable.Id, power.Id);
        Assert.Empty(power.Facts);
        Assert.NotEmpty(power.Issues);
    }

    [Fact]
    public void InventoryCanStartANewSessionWithoutAPreviousDiagnostic()
    {
        var result = EvidenceSessionUpdates.ReplaceOwned(null, Inventory(), InventoryCollectors.All);

        Assert.True(result.IsSuccess);
        Assert.Equal(SessionStatus.Complete, result.Value!.Status);
        Assert.Equal(4, result.Value.Evidence.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SessionPartialityWithNoItemLevelExplanationRemainsUnknown(bool incoming)
    {
        var previous = Session(Earlier, [Evidence("windows.system", Earlier)]);
        var inventory = Inventory();
        if (incoming) inventory = inventory with { Status = SessionStatus.Partial };
        else previous = previous with { Status = SessionStatus.Partial };

        var result = EvidenceSessionUpdates.ReplaceOwned(previous, inventory, InventoryCollectors.All);

        Assert.True(result.IsSuccess);
        Assert.All(result.Value!.Evidence, item => Assert.Equal(EvidenceStatus.Complete, item.Status));
        Assert.Equal(SessionStatus.Partial, result.Value.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingOrForeignCollectorsCannotSilentlyReplaceInventoryCoverage(bool foreign)
    {
        var evidence = Inventory().Evidence.Take(3).ToList();
        if (foreign) evidence.Add(Evidence("foreign.collector", Later));
        var result = EvidenceSessionUpdates.ReplaceOwned(null, Session(Later, evidence), InventoryCollectors.All);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCode.InvalidData, result.Error!.Code);
        Assert.Null(result.Value);
    }

    [Fact]
    public void CrossSessionIdentityCollisionIsRejectedWithoutChangingEitherInput()
    {
        var inventory = Inventory();
        var previousEvidence = Evidence("windows.system", Earlier) with { Id = inventory.Evidence[0].Id };
        var previous = Session(Earlier, [previousEvidence]);

        var result = EvidenceSessionUpdates.ReplaceOwned(previous, inventory, InventoryCollectors.All);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCode.InvalidData, result.Error!.Code);
        Assert.Same(previousEvidence, Assert.Single(previous.Evidence));
        Assert.Equal(4, inventory.Evidence.Count);
    }

    private static DiagnosticSession Inventory() => Session(Later,
        [Evidence(InventoryCollectors.Network, Later), Evidence(InventoryCollectors.Power, Later),
         Evidence(InventoryCollectors.InstalledApplications, Later), Evidence(InventoryCollectors.Startup, Later)]);

    private static DiagnosticEvidence Evidence(string collector, DateTimeOffset captured, EvidenceStatus status = EvidenceStatus.Complete) =>
        new(Guid.NewGuid(), collector, collector, "fixture scope", captured, status,
            new("Fixture only; not native Windows verification", status == EvidenceStatus.Complete ? 1 : 0),
            status == EvidenceStatus.Complete ? [new("observed", "Original value", "kept")] : [],
            status == EvidenceStatus.Complete ? [] : [new(IssueCode.ReadFailed, "fixture unavailable")]);

    private static DiagnosticSession Session(DateTimeOffset started, IReadOnlyList<DiagnosticEvidence> evidence) =>
        new(Guid.NewGuid(), started, started.AddSeconds(1),
            evidence.All(item => item.Status == EvidenceStatus.Complete) ? SessionStatus.Complete : SessionStatus.Partial,
            evidence, DiagnosticRules.Evaluate(evidence));
}
