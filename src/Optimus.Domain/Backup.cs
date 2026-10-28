namespace Optimus.Domain;

public enum BackupState { Prepared, RepositoryInitialized, SnapshotCreated, IntegrityVerified, Partial, Failed, OutcomeUnknown, Cancelled }
public enum RestoreState { Prepared, RestoredAndVerified, Failed, OutcomeUnknown, Cancelled }

public sealed record BackupCommandReceipt(
    string Stage, string Status, int? ExitCode, string StandardOutput, string StandardError,
    double ElapsedSeconds, double? CpuSeconds, long? PeakProcessFamilyMemoryBytes,
    string? FailureReason, IReadOnlyList<string>? PendingCleanupPaths = null);

public sealed record BackupReceipt(
    Guid OperationId, BackupState State, string SourceDirectory, string RepositoryDirectory,
    string? SnapshotId, bool IntegrityVerified, bool RestorationVerified, string JournalDirectory,
    IReadOnlyList<BackupCommandReceipt> Commands, IReadOnlyList<string> Limitations,
    string? RecoveryInstruction = null);

public sealed record RestoreReceipt(
    Guid OperationId, RestoreState State, string SnapshotId, string DestinationDirectory,
    string JournalDirectory, IReadOnlyList<BackupCommandReceipt> Commands,
    IReadOnlyList<string> Limitations, string? RecoveryInstruction = null,
    bool RestorationVerified = false);
