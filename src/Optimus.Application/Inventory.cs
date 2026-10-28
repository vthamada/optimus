using System.Collections.Frozen;

namespace Optimus.Application;

public static class InventoryCollectors
{
    public const string Network = "windows.network";
    public const string Power = "windows.power";
    public const string InstalledApplications = "windows.installed-applications";
    public const string Startup = "windows.startup";

    public static IReadOnlySet<string> All { get; } = new[] { Network, Power, InstalledApplications, Startup }.ToFrozenSet(StringComparer.Ordinal);
    public static bool Contains(string collectorId) => All.Contains(collectorId);
}
