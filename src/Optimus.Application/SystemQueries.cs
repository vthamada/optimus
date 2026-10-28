namespace Optimus.Application;

public enum WindowsSystemQuery { DefenderStatus, DeviceInventory, ServiceInventory, UpdateHistory, ProcessInventory, TaskInventory, StorageInventory, NetworkConnections, FirewallProfiles, RestorePoints, BitLockerStatus, HardwareProfile }

public interface IWindowsSystemQueryRunner
{
    Task<ToolRunResult> RunAsync(WindowsSystemQuery query, CancellationToken cancellationToken);
}
