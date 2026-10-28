using Optimus.Domain;

namespace Optimus.Application;

public sealed record MaintenanceServices(
    DiagnosticService Defender, DiagnosticService Devices, IIntegrityService Integrity,
    DiagnosticService? Services = null, DiagnosticService? Updates = null,
    Func<string, DiagnosticService>? FolderSpace = null,
    DiagnosticService? Processes = null, DiagnosticService? ScheduledTasks = null, DiagnosticService? Storage = null,
    DiagnosticService? Connections = null, DiagnosticService? Firewall = null, DiagnosticService? ApplicationFailures = null,
    DiagnosticService? PendingRestart = null, DiagnosticService? RestorePoints = null, DiagnosticService? BitLocker = null,
    DiagnosticService? RestartHistory = null, DiagnosticService? MemoryResults = null, DiagnosticService? HardwareProfile = null)
{
    public Result<MaintenanceQueryBinding> ResolveQuery(MaintenanceQuery query)
    {
        (DiagnosticService? Service, string? CollectorId) selected = query switch
        {
            MaintenanceQuery.Defender => (Defender, MaintenanceCollectors.Defender),
            MaintenanceQuery.DeviceDrivers => (Devices, MaintenanceCollectors.DeviceDrivers),
            MaintenanceQuery.Services => (Services, MaintenanceCollectors.Services),
            MaintenanceQuery.Updates => (Updates, MaintenanceCollectors.Updates),
            MaintenanceQuery.Processes => (Processes, MaintenanceCollectors.Processes),
            MaintenanceQuery.ScheduledTasks => (ScheduledTasks, MaintenanceCollectors.ScheduledTasks),
            MaintenanceQuery.Storage => (Storage, MaintenanceCollectors.Storage),
            MaintenanceQuery.Connections => (Connections, MaintenanceCollectors.Connections),
            MaintenanceQuery.Firewall => (Firewall, MaintenanceCollectors.Firewall),
            MaintenanceQuery.ApplicationFailures => (ApplicationFailures, MaintenanceCollectors.ApplicationFailures),
            MaintenanceQuery.PendingRestart => (PendingRestart, MaintenanceCollectors.PendingRestart),
            MaintenanceQuery.RestorePoints => (RestorePoints, MaintenanceCollectors.RestorePoints),
            MaintenanceQuery.BitLocker => (BitLocker, MaintenanceCollectors.BitLocker),
            MaintenanceQuery.RestartHistory => (RestartHistory, MaintenanceCollectors.RestartHistory),
            MaintenanceQuery.MemoryResults => (MemoryResults, MaintenanceCollectors.MemoryResults),
            MaintenanceQuery.HardwareProfile => (HardwareProfile, MaintenanceCollectors.HardwareProfile),
            _ => (null, null)
        };
        if (selected.CollectorId is null) return Result<MaintenanceQueryBinding>.Failure(
            ErrorCode.InvalidConfiguration, "Ferramenta de consulta não reconhecida.");
        if (selected.Service is null) return Result<MaintenanceQueryBinding>.Failure(
            ErrorCode.DependencyUnavailable, "Esta consulta não está configurada nesta composição.");
        return Result<MaintenanceQueryBinding>.Success(new(selected.CollectorId, selected.Service));
    }
}
