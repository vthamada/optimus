using Optimus.Application;
using Optimus.Domain;

namespace Optimus.Tests;

public sealed class DiagnosticWorkflowTests
{
    [Fact]
    public async Task FailedCollectorPreservesAvailableFactsAndMarksPartialCoverage()
    {
        var observed = Samples.Evidence();
        var workflow = new DiagnosticService([
            new Collector("windows.volumes", _ => Task.FromResult<IReadOnlyList<DiagnosticEvidence>>([observed])),
            new Collector("broken", _ => throw new IOException("Access to the selected log failed"))
        ]);

        var result = await workflow.RunAsync(null, CancellationToken.None);

        Assert.Equal(SessionStatus.Partial, result.Status);
        Assert.Contains(result.Evidence, evidence => evidence.Id == observed.Id);
        var failure = Assert.Single(result.Evidence, evidence => evidence.CollectorId == "broken");
        Assert.Equal(EvidenceStatus.Unavailable, failure.Status);
        Assert.Equal("Access to the selected log failed", Assert.Single(failure.Issues).Message);
        Assert.All(result.Findings, finding => Assert.All(finding.EvidenceIds,
            id => Assert.Contains(result.Evidence, evidence => evidence.Id == id)));
    }

    [Fact]
    public async Task CancellationDuringCollectionDoesNotPublishCompletedSession()
    {
        using var cancellation = new CancellationTokenSource();
        var workflow = new DiagnosticService([new Collector("cancelled", token =>
        {
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<DiagnosticEvidence>>([]);
        })]);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => workflow.RunAsync(null, cancellation.Token));
    }

    [Fact]
    public async Task EmptyOrMisattributedCollectorOutputIsUnavailable()
    {
        var workflow = new DiagnosticService([
            new Collector("empty", _ => Task.FromResult<IReadOnlyList<DiagnosticEvidence>>([])),
            new Collector("wrong-owner", _ => Task.FromResult<IReadOnlyList<DiagnosticEvidence>>([Samples.Evidence()]))
        ]);

        var result = await workflow.RunAsync(null, CancellationToken.None);

        Assert.Equal(SessionStatus.Partial, result.Status);
        Assert.Equal(2, result.Evidence.Count);
        Assert.All(result.Evidence, evidence => Assert.Equal(EvidenceStatus.Unavailable, evidence.Status));
    }

    [Fact]
    public void DiskRuleUsesSourceUnitsAndSelectedCollectorInsteadOfDisplayText()
    {
        var low = Samples.Evidence(total: 1000, available: 99);
        var boundary = Samples.Evidence(total: 1000, available: 100);
        var foreign = low with { Id = Guid.NewGuid(), CollectorId = "foreign" };
        var unknown = low with { Id = Guid.NewGuid(), Facts = [new("availableBytes", "Livre", TextValue: "0 bytes")] };

        Assert.Single(DiagnosticRules.Evaluate([low]), finding => finding.Severity == FindingSeverity.Warning);
        Assert.Empty(DiagnosticRules.Evaluate([boundary, foreign, unknown]));
    }

    [Fact]
    public void PartialEvidenceNeverBecomesAHealthConclusion()
    {
        var evidence = Samples.Evidence() with { Status = EvidenceStatus.Partial };
        var finding = Assert.Single(DiagnosticRules.Evaluate([evidence]));
        Assert.Equal(FindingSeverity.Information, finding.Severity);
        Assert.Equal(evidence.Id, Assert.Single(finding.EvidenceIds));
        Assert.Contains("não estabelece", finding.Explanation);
    }

    private sealed class Collector(string id, Func<CancellationToken, Task<IReadOnlyList<DiagnosticEvidence>>> collect) : IDiagnosticCollector
    {
        public string Id => id;
        public string DisplayName => id;
        public Task<IReadOnlyList<DiagnosticEvidence>> CollectAsync(CancellationToken cancellationToken) => collect(cancellationToken);
    }
}

internal static class Samples
{
    public static DiagnosticEvidence Evidence(decimal total = 1000, decimal available = 500) =>
        new(Guid.NewGuid(), "windows.volumes", "Volume C:\\", "C:\\", DateTimeOffset.UtcNow,
            EvidenceStatus.Complete, new("Test volume fixture; not physical SMART coverage", 1),
            [new("totalBytes", "Total", NumberValue: total, Unit: "bytes"),
             new("availableBytes", "Livre", NumberValue: available, Unit: "bytes")], []);

    public static DiagnosticSession Session(DiagnosticEvidence? evidence = null)
    {
        var item = evidence ?? Evidence();
        var now = DateTimeOffset.UtcNow;
        return new(Guid.NewGuid(), now, now, SessionStatus.Complete, [item], DiagnosticRules.Evaluate([item]));
    }
}
