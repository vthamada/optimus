using Optimus.Domain;

namespace Optimus.Application;

public enum MaintenanceQuery { Defender, DeviceDrivers, Services, Updates, Processes, ScheduledTasks, Storage, Connections, Firewall, ApplicationFailures, PendingRestart, RestorePoints, BitLocker, RestartHistory, MemoryResults, HardwareProfile }

public sealed record MaintenanceQueryBinding(string CollectorId, DiagnosticService Service)
{
    public async Task<Result<DiagnosticSession>> RunAsync(IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var collected = await Service.RunAsync(progress, cancellationToken).ConfigureAwait(false);
        var coverage = EvidenceSessionUpdates.ValidateCoverage(collected, new HashSet<string>([CollectorId], StringComparer.Ordinal));
        return coverage.IsSuccess ? Result<DiagnosticSession>.Success(collected)
            : Result<DiagnosticSession>.FailureWithValue(collected, coverage.Error!.Code, coverage.Error.Message);
    }
}

public static class MaintenanceQueries
{
    public static bool TryParseCommand(string command, out MaintenanceQuery query)
    {
        query = command switch
        {
            "defender-status" => MaintenanceQuery.Defender,
            "device-inventory" => MaintenanceQuery.DeviceDrivers,
            "services" => MaintenanceQuery.Services,
            "update-history" => MaintenanceQuery.Updates,
            "processes" => MaintenanceQuery.Processes,
            "scheduled-tasks" => MaintenanceQuery.ScheduledTasks,
            "storage" => MaintenanceQuery.Storage,
            "connections" => MaintenanceQuery.Connections,
            "firewall" => MaintenanceQuery.Firewall,
            "application-failures" => MaintenanceQuery.ApplicationFailures,
            "pending-restart" => MaintenanceQuery.PendingRestart,
            "restore-points" => MaintenanceQuery.RestorePoints,
            "bitlocker-status" => MaintenanceQuery.BitLocker,
            "restart-history" => MaintenanceQuery.RestartHistory,
            "memory-results" => MaintenanceQuery.MemoryResults,
            "hardware-profile" => MaintenanceQuery.HardwareProfile,
            _ => (MaintenanceQuery)(-1)
        };
        return Enum.IsDefined(query);
    }
}
