using Optimus.Domain;

namespace Optimus.Application;

public sealed record IntegrityPlan(
    Guid Id, DateTimeOffset CreatedAt, IntegrityOperation Operation, string DisplayName,
    string ExecutablePath, string ExecutableSha256, IReadOnlyList<string> Arguments,
    string WorkingDirectory, bool IsElevated, TimeSpan Timeout, IReadOnlyList<string> Limitations,
    ToolTextEncoding OutputEncoding);

public interface IIntegrityService
{
    Task<Result<IntegrityPlan>> PlanAsync(IntegrityOperation operation, CancellationToken cancellationToken);
    Task<Result<IntegrityReceipt>> ExecuteAsync(IntegrityPlan plan, Guid confirmationId, CancellationToken cancellationToken);
}
