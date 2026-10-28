using System.Globalization;
using System.Text.Json.Serialization;

namespace Optimus.Domain;

public enum EvidenceStatus { Complete, Partial, Unavailable }
public enum SessionStatus { Complete, Partial }
public enum FindingSeverity { Information, Warning }
public enum IssueCode { PermissionDenied, ReadFailed, NotSupported, LimitReached }
public enum ErrorCode { StorageFailure, UnrecognizedDatabase, UnsupportedSchema, InvalidData, OutputExists, PermissionDenied, DependencyUnavailable, InvalidConfiguration, ToolFailed, Timeout, OutputLimitExceeded, InvalidResponse, VerificationFailed, Cancelled }

public sealed record AppError(ErrorCode Code, string Message, IReadOnlyList<string>? PendingCleanupPaths = null);

public sealed class Result<T>
{
    private Result(T? value, AppError? error) { Value = value; Error = error; }
    public T? Value { get; }
    public AppError? Error { get; }
    public bool IsSuccess => Error is null;
    public static Result<T> Success(T value) => new(value, null);
    public static Result<T> Failure(ErrorCode code, string message, IReadOnlyList<string>? pendingCleanupPaths = null) => new(default, new(code, message, pendingCleanupPaths));
    public static Result<T> FailureWithValue(T partialValue, ErrorCode code, string message, IReadOnlyList<string>? pendingCleanupPaths = null) => new(partialValue, new(code, message, pendingCleanupPaths));
}

// Numeric facts retain their unit and source value; consumers never parse display text.
public sealed record DiagnosticFact(
    string Key, string Label, string? TextValue = null, decimal? NumberValue = null,
    string? Unit = null, bool Detailed = false)
{
    [JsonIgnore]
    public string DisplayValue => TextValue ?? (NumberValue is { } number
        ? Unit == "bytes"
            ? $"{(number / 1_073_741_824m).ToString("N2", CultureInfo.GetCultureInfo("pt-BR"))} GiB ({number.ToString("N0", CultureInfo.GetCultureInfo("pt-BR"))} bytes)"
            : $"{number.ToString("N0", CultureInfo.GetCultureInfo("pt-BR"))}{(Unit is null ? "" : " " + Unit)}"
        : "Não disponível");
}

public sealed record CollectionIssue(IssueCode Code, string Message);
public sealed record EvidenceCoverage(string Scope, int ExaminedItems, int? ItemLimit = null, bool LimitReached = false);
public sealed record DiagnosticEvidence(
    Guid Id, string CollectorId, string Title, string Subject, DateTimeOffset CapturedAt,
    EvidenceStatus Status, EvidenceCoverage Coverage,
    IReadOnlyList<DiagnosticFact> Facts, IReadOnlyList<CollectionIssue> Issues)
{
    [JsonIgnore]
    public IEnumerable<DiagnosticFact> SummaryFacts => Facts.Where(fact => !fact.Detailed);
    [JsonIgnore]
    public string StatusText => Status switch
    {
        EvidenceStatus.Complete => "Coleta concluída no escopo informado",
        EvidenceStatus.Partial => "Coleta parcial",
        _ => "Coleta indisponível"
    };
}

public sealed record DiagnosticFinding(
    string Id, FindingSeverity Severity, string Title, string Explanation,
    string Recommendation, IReadOnlyList<Guid> EvidenceIds);

public sealed record DiagnosticSession(
    Guid Id, DateTimeOffset StartedAt, DateTimeOffset CompletedAt, SessionStatus Status,
    IReadOnlyList<DiagnosticEvidence> Evidence, IReadOnlyList<DiagnosticFinding> Findings)
{
    [JsonIgnore]
    public string StatusText => Status == SessionStatus.Complete ? "Concluído no escopo selecionado" : "Diagnóstico parcial";
}

public sealed record SessionSummary(Guid Id, DateTimeOffset StartedAt, SessionStatus Status, int FindingCount)
{
    public string DisplayText => $"{StartedAt.ToLocalTime():dd/MM/yyyy HH:mm} · {(Status == SessionStatus.Complete ? "concluído" : "parcial")} · {FindingCount} achado(s)";
}
