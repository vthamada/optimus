using Optimus.Application;
using Optimus.Domain;

namespace Optimus.Infrastructure.Inventory;

public sealed class StartupCollector(
    Func<SoftwareRegistryLocation, CancellationToken, IEnumerable<SoftwareRegistryObservation>>? source = null) : IDiagnosticCollector
{
    public string Id => InventoryCollectors.Startup;
    public string DisplayName => "Registros de inicialização do Windows";
    public Task<IReadOnlyList<DiagnosticEvidence>> CollectAsync(CancellationToken cancellationToken) =>
        Task.Run(() => SoftwareRegistryEvidence.Collect(Id, DisplayName, true, source ?? SoftwareRegistrySource.Read, cancellationToken), CancellationToken.None);
}
