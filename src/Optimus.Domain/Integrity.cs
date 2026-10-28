namespace Optimus.Domain;

public enum IntegrityOperation { SfcVerifyOnly, DismCheckHealth, DismScanHealth }
public enum IntegrityState { Prepared, CommandCompleted, CommandReportedFailure, OutcomeUnknown, Cancelled }

public sealed record IntegrityCommandReceipt(
    string Status, int? ExitCode, string StandardOutput, string StandardError,
    string RequestedEncoding, int? OutputCodePage, string? StandardOutputBase64, string? StandardErrorBase64,
    double ElapsedSeconds, double? CpuSeconds, long? PeakProcessFamilyMemoryBytes,
    string? FailureReason, IReadOnlyList<string>? PendingCleanupPaths = null);

public sealed record IntegrityReceipt(
    Guid OperationId, IntegrityOperation Operation, IntegrityState State, bool RepairPerformed,
    string JournalDirectory, DiagnosticEvidence Evidence, IntegrityCommandReceipt? Command,
    IReadOnlyList<string> Limitations, string? RecoveryInstruction = null);
