using Optimus.Domain;

namespace Optimus.Application;

public static class EvidenceSessionUpdates
{
    public static Result<DiagnosticSession> ReplaceOwned(DiagnosticSession? previous, DiagnosticSession collected,
        IReadOnlySet<string> selectedCollectors)
    {
        var selection = ReadOwnedCoverage(collected, selectedCollectors);
        if (!selection.IsSuccess) return Invalid(selection.Error!.Message);
        if (previous is not null && !SessionValidation.IsValid(previous)) return Invalid("A sessão anterior não atende ao contrato de evidências.");
        var owned = selection.Value!;

        DiagnosticEvidence[] combined = [.. previous?.Evidence.Where(item => !owned.Contains(item.CollectorId)) ?? [], .. collected.Evidence];
        bool unexplainedPreviousPartial = previous is { Status: SessionStatus.Partial }
            && previous.Evidence.All(item => item.Status == EvidenceStatus.Complete);
        if (combined.Select(item => item.Id).Distinct().Count() != combined.Length)
            return Invalid("As identidades das evidências não permitem combinar as sessões sem ambiguidade.");
        var session = new DiagnosticSession(Guid.NewGuid(), collected.StartedAt, collected.CompletedAt,
            !unexplainedPreviousPartial && collected.Status == SessionStatus.Complete
                && combined.All(item => item.Status == EvidenceStatus.Complete) ? SessionStatus.Complete : SessionStatus.Partial,
            combined, DiagnosticRules.Evaluate(combined));
        return Result<DiagnosticSession>.Success(session);
    }

    public static Result<bool> ValidateCoverage(DiagnosticSession collected, IReadOnlySet<string> selectedCollectors)
    {
        var selected = ReadOwnedCoverage(collected, selectedCollectors);
        return selected.IsSuccess ? Result<bool>.Success(true) : Result<bool>.Failure(selected.Error!.Code, selected.Error.Message);
    }

    private static Result<HashSet<string>> ReadOwnedCoverage(DiagnosticSession collected, IReadOnlySet<string> selectedCollectors)
    {
        if (selectedCollectors is null || selectedCollectors.Count == 0 || selectedCollectors.Any(string.IsNullOrWhiteSpace)
            || !SessionValidation.IsValid(collected))
            return Result<HashSet<string>>.Failure(ErrorCode.InvalidData, "Uma sessão ou seleção de coletores não atende ao contrato de evidências.");
        var owned = selectedCollectors.ToHashSet(StringComparer.Ordinal);
        var incoming = collected.Evidence.Select(item => item.CollectorId).ToHashSet(StringComparer.Ordinal);
        return incoming.SetEquals(owned) ? Result<HashSet<string>>.Success(owned)
            : Result<HashSet<string>>.Failure(ErrorCode.InvalidData, "A coleta deve informar a cobertura de todos os coletores selecionados, inclusive quando indisponível.");
    }

    private static Result<DiagnosticSession> Invalid(string reason) => Result<DiagnosticSession>.Failure(ErrorCode.InvalidData, reason);
}
