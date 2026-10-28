using Optimus.Application;
using Optimus.Domain;

namespace Optimus.Infrastructure.Inventory;

public sealed class InstalledApplicationsCollector(
    Func<SoftwareRegistryLocation, CancellationToken, IEnumerable<SoftwareRegistryObservation>>? source = null) : IDiagnosticCollector
{
    public string Id => InventoryCollectors.InstalledApplications;
    public string DisplayName => "Programas registrados no Windows";
    public Task<IReadOnlyList<DiagnosticEvidence>> CollectAsync(CancellationToken cancellationToken) =>
        Task.Run(() => SoftwareRegistryEvidence.Collect(Id, DisplayName, false, source ?? SoftwareRegistrySource.Read, cancellationToken), CancellationToken.None);
}
