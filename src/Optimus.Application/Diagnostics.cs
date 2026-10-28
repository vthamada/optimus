using Optimus.Domain;

namespace Optimus.Application;

public interface IDiagnosticCollector
{
    string Id { get; }
    string DisplayName { get; }
    Task<IReadOnlyList<DiagnosticEvidence>> CollectAsync(CancellationToken cancellationToken);
}

public interface ISessionStore
{
    Task<Result<bool>> SaveAsync(DiagnosticSession session, CancellationToken cancellationToken);
    Task<Result<IReadOnlyList<SessionSummary>>> ListAsync(CancellationToken cancellationToken);
    Task<Result<DiagnosticSession>> GetAsync(Guid id, CancellationToken cancellationToken);
}

public enum ReportFormat { Html, Json }
public interface IReportExporter
{
    Task<Result<string>> ExportAsync(DiagnosticSession session, string path, ReportFormat format, CancellationToken cancellationToken);
}

public sealed class DiagnosticService(IReadOnlyList<IDiagnosticCollector> collectors, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public async Task<DiagnosticSession> RunAsync(IProgress<string>? progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var started = _clock.GetUtcNow();
        var evidence = new List<DiagnosticEvidence>();
        foreach (var collector in collectors)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report($"Coletando: {collector.DisplayName}");
            try
            {
                var collected = await collector.CollectAsync(cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (collected.Count == 0 || collected.Any(item => item.CollectorId != collector.Id || item.Id == Guid.Empty))
                    throw new InvalidDataException("O coletor não entregou evidências válidas para seu contrato.");
                evidence.AddRange(collected);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error)
            {
                // A collector boundary must preserve unavailable coverage instead of declaring health.
                evidence.Add(new(Guid.NewGuid(), collector.Id, collector.DisplayName, "Coletor",
                    _clock.GetUtcNow(), EvidenceStatus.Unavailable, new("A coleta não pôde ser concluída", 0), [],
                    [new(IssueCode.ReadFailed, error.Message)]));
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (evidence.Select(item => item.Id).Distinct().Count() != evidence.Count)
            throw new InvalidDataException("Identidades de evidência duplicadas no diagnóstico.");
        var status = evidence.Count > 0 && evidence.All(item => item.Status == EvidenceStatus.Complete)
            ? SessionStatus.Complete : SessionStatus.Partial;
        return new(Guid.NewGuid(), started, _clock.GetUtcNow(), status, evidence.ToArray(), DiagnosticRules.Evaluate(evidence));
    }
}
