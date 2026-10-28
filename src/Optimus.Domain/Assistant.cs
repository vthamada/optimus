namespace Optimus.Domain;

public enum AssistantMode { Unspecified, Extractive }

public sealed record AssistantFactReference(Guid EvidenceId, string FactKey, DateTimeOffset CapturedAt);

public sealed record AssistantInsight(string Title, string Explanation, IReadOnlyList<Guid> EvidenceIds,
    AssistantFactReference? Source = null);

public sealed record AssistantUsage(
    TimeSpan Elapsed,
    TimeSpan? CpuTime = null,
    long? PeakProcessFamilyMemoryBytes = null,
    int? InputTokens = null,
    int? OutputTokens = null);

public sealed record AssistantAnswer(
    Guid SessionId,
    DateTimeOffset GeneratedAt,
    string Summary,
    IReadOnlyList<Guid> SummaryEvidenceIds,
    IReadOnlyList<AssistantInsight> Insights,
    IReadOnlyList<string> Limitations,
    string ModelName,
    AssistantUsage Usage,
    string? EvidencePath = null,
    AssistantMode Mode = AssistantMode.Unspecified);
