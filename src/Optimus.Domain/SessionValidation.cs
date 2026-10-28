namespace Optimus.Domain;

public static class SessionValidation
{
    public static bool IsValid(DiagnosticSession? session)
    {
        if (session is null || session.Id == Guid.Empty || session.Evidence is null || session.Findings is null
            || session.CompletedAt < session.StartedAt || !Enum.IsDefined(session.Status)) return false;
        var ids = new HashSet<Guid>();
        foreach (var item in session.Evidence)
        {
            if (item is null || item.Id == Guid.Empty || !ids.Add(item.Id)
                || string.IsNullOrWhiteSpace(item.CollectorId) || item.Title is null || item.Subject is null
                || !Enum.IsDefined(item.Status) || item.Facts is null || item.Issues is null || item.Coverage is null
                || item.Coverage.Scope is null || item.Coverage.ExaminedItems < 0 || item.Coverage.ItemLimit < 0
                || (item.Coverage.LimitReached && item.Coverage.ItemLimit is null)) return false;
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var fact in item.Facts)
            {
                if (fact is null || string.IsNullOrWhiteSpace(fact.Key) || fact.Label is null || !keys.Add(fact.Key)
                    || (fact.TextValue is not null && fact.NumberValue is not null)) return false;
            }
            if (item.Issues.Any(issue => issue is null || issue.Message is null || !Enum.IsDefined(issue.Code))) return false;
        }

        if (session.Status == SessionStatus.Complete && (session.Evidence.Count == 0
            || session.Evidence.Any(item => item.Status != EvidenceStatus.Complete))) return false;
        return session.Findings.All(finding => finding is not null && !string.IsNullOrWhiteSpace(finding.Id)
            && Enum.IsDefined(finding.Severity) && finding.Title is not null && finding.Explanation is not null
            && finding.Recommendation is not null && finding.EvidenceIds is { Count: > 0 }
            && finding.EvidenceIds.All(ids.Contains));
    }
}
