namespace Optimus.Application;

public static class MaintenanceCollectors
{
    public const string Defender = "windows.defender";
    public const string DeviceDrivers = "windows.devices-drivers";
    public const string Integrity = "windows.integrity";
    public const string Services = "windows.services";
    public const string Updates = "windows.update-history";
    public const string FolderSpace = "storage.folder-space";
    public const string Processes = "windows.processes";
    public const string ScheduledTasks = "windows.scheduled-tasks";
    public const string Storage = "windows.storage";
    public const string Connections = "windows.network-connections";
    public const string Firewall = "windows.firewall-profiles";
    public const string ApplicationFailures = "windows.application-failures";
    public const string PendingRestart = "windows.pending-restart";
    public const string RestorePoints = "windows.restore-points";
    public const string BitLocker = "windows.bitlocker-status";
    public const string RestartHistory = "windows.restart-history";
    public const string MemoryResults = "windows.memory-diagnostic-results";
    public const string HardwareProfile = "windows.hardware-profile";
    public const string CrashDump = "windows.crash-dump";
    public const string MemoryReport = "memory.test-report";
    public const string FirmwareNotes = "firmware.release-notes";
    public static bool Contains(string collectorId) => collectorId is Defender or DeviceDrivers or Integrity or Services or Updates or FolderSpace or Processes or ScheduledTasks or Storage or Connections or Firewall or ApplicationFailures or PendingRestart or RestorePoints or BitLocker or RestartHistory or MemoryResults or HardwareProfile or CrashDump or MemoryReport or FirmwareNotes or InstabilityInvestigationService.AssessmentCollectorId;
}
