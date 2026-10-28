using Optimus.Application;
using Optimus.Infrastructure.AI;
using Optimus.Infrastructure.Backup;
using Optimus.Infrastructure.Smart;
using Optimus.Infrastructure.Tools;
using Optimus.Infrastructure.Inventory;
using Optimus.Infrastructure.Security;
using Optimus.Infrastructure.Devices;
using Optimus.Infrastructure.Integrity;
using Optimus.Infrastructure.SystemTools;
using Optimus.Infrastructure.Services;
using Optimus.Infrastructure.Updates;
using Optimus.Infrastructure.Storage;
using Optimus.Infrastructure.Processes;
using Optimus.Infrastructure.Scheduling;
using Optimus.Infrastructure.Network;
using Optimus.Infrastructure.Events;
using Optimus.Infrastructure.Recovery;
using Optimus.Infrastructure.Hardware;
using Optimus.Infrastructure.Memory;
using Optimus.Infrastructure.Crashes;
using Optimus.Infrastructure.Files;
using Microsoft.Win32.SafeHandles;

namespace Optimus.Infrastructure;

public static class Composition
{
    public static DiagnosticService CreateDiagnostics() => new([
        new SystemCollector(), new VolumeCollector(), new SystemEventsCollector()
    ]);

    public static DiagnosticService CreateInventory() => new([
        new NetworkCollector(), new PowerCollector(), new InstalledApplicationsCollector(), new StartupCollector()
    ]);

    public static MaintenanceServices CreateMaintenance(string? dataDirectory = null, int instabilityDays = 30)
    {
        var directory = Path.GetFullPath(dataDirectory ?? DefaultDataDirectory);
        var runner = new WindowsToolRunner();
        var queries = new WindowsSystemQueryRunner(runner);
        return new(new DiagnosticService([new DefenderCollector(queries)]),
            new DiagnosticService([new DeviceDriverCollector(queries)]),
            new WindowsIntegrityService(runner, Path.Combine(directory, "integrity-operations")),
            new DiagnosticService([new WindowsServicesCollector(queries)]),
            new DiagnosticService([new WindowsUpdateHistoryCollector(queries)]),
            CreateFolderSpace,
            new DiagnosticService([new WindowsProcessesCollector(queries)]),
            new DiagnosticService([new ScheduledTasksCollector(queries)]),
            new DiagnosticService([new StorageDevicesCollector(queries)]),
            new DiagnosticService([new NetworkConnectionsCollector(queries)]),
            new DiagnosticService([new FirewallProfilesCollector(queries)]),
            new DiagnosticService([new ApplicationFailuresCollector()]),
            new DiagnosticService([new PendingRestartCollector()]),
            new DiagnosticService([new RestorePointsCollector(queries)]),
            new DiagnosticService([new BitLockerStatusCollector(queries)]),
            new DiagnosticService([new RestartHistoryCollector(instabilityDays)]),
            new DiagnosticService([new MemoryDiagnosticResultsCollector(instabilityDays)]),
            new DiagnosticService([new HardwareProfileCollector(queries)]));
    }

    public static InstabilityServices CreateInstability(string? dataDirectory = null)
    {
        var directory = Path.GetFullPath(dataDirectory ?? DefaultDataDirectory);
        var runner = new WindowsToolRunner();
        var queries = new WindowsSystemQueryRunner(runner);
        return new(days => new([new RestartHistoryCollector(days)]),
            days => new([new MemoryDiagnosticResultsCollector(days)]),
            new([new HardwareProfileCollector(queries)]),
            settings => new([new CrashDumpCollector(runner, settings, Path.Combine(directory, "crash-dumps"), () => PrepareInstabilityDirectory(directory))]),
            new MemoryTestReportImporter(),
            settings => new LocalInvestigationPlanner(new LlamaCppAssistantService(runner, Path.Combine(directory, "assistant")), settings),
            new FirmwareNotesReader());
    }

    public static string PrepareInstabilityDirectory(string? dataDirectory = null)
    {
        var target = LocalPathGuard.LocalPath(dataDirectory ?? DefaultDataDirectory);
        var missing = new Stack<string>();
        var existing = target;
        while (!Directory.Exists(existing))
        {
            if (missing.Count == 64) throw new InvalidDataException("Mais de 64 níveis de pasta ausentes; selecione uma raiz local existente mais próxima.");
            missing.Push(existing);
            existing = Path.GetDirectoryName(existing) ?? throw new InvalidDataException("Não há pasta ancestral local disponível.");
        }
        List<SafeFileHandle> leases = [];
        try
        {
            leases.Add(LocalPathGuard.HoldDirectory(existing));
            while (missing.TryPop(out var path))
            {
                _ = LocalPathGuard.LocalPath(path);
                LocalPathGuard.CreateNewDirectory(path);
                leases.Add(LocalPathGuard.HoldDirectory(path));
            }
            return LocalPathGuard.LocalPath(target);
        }
        finally { foreach (var lease in leases.AsEnumerable().Reverse()) lease.Dispose(); }
    }

    public static DiagnosticService CreateFolderSpace(string selectedPath) => new([new FolderSpaceCollector(selectedPath)]);

    public static ModuleServices CreateModules(string? dataDirectory = null)
    {
        var directory = Path.GetFullPath(dataDirectory ?? DefaultDataDirectory);
        var runner = new WindowsToolRunner();
        return new(new LlamaCppAssistantService(runner, Path.Combine(directory, "assistant")),
            new ResticBackupService(runner, Path.Combine(directory, "operations")), new SmartService(runner));
    }
    public static string DefaultDataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "opTImus");

    public static SqliteSessionStore CreateStore(string? directory = null) =>
        new(Path.Combine(directory ?? DefaultDataDirectory, "diagnostics.db"));
}
