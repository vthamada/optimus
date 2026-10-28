using Optimus.Domain;

namespace Optimus.Application;

public sealed record BackupSettings(
    string ExecutablePath, string SourceDirectory, string RepositoryDirectory,
    string PasswordFilePath, string? ExpectedExecutableSha256 = null);

public sealed record BackupPlan(
    Guid Id, DateTimeOffset CreatedAt, BackupSettings Settings,
    string SourceDirectory, string RepositoryDirectory, string ExecutablePath,
    string PasswordFilePath, string ExecutableSha256, IReadOnlyList<string> Limitations);

public sealed record RestorePlan(
    Guid Id, DateTimeOffset CreatedAt, BackupSettings Settings, string SnapshotId,
    string RepositoryDirectory, string DestinationDirectory, string ExecutablePath,
    string PasswordFilePath, string ExecutableSha256, IReadOnlyList<string> Limitations);

public interface IBackupService
{
    Task<Result<BackupPlan>> PlanAsync(BackupSettings settings, CancellationToken cancellationToken);
    Task<Result<BackupReceipt>> ExecuteAsync(BackupPlan plan, Guid confirmationId, CancellationToken cancellationToken);
    Task<Result<RestorePlan>> PlanRestoreAsync(BackupSettings settings, string snapshotId, string destinationDirectory, CancellationToken cancellationToken);
    Task<Result<RestoreReceipt>> RestoreAsync(RestorePlan plan, Guid confirmationId, CancellationToken cancellationToken);
}
