using System.Net.NetworkInformation;
using System.Net.Sockets;
using Optimus.Application;
using Optimus.Domain;

namespace Optimus.Infrastructure.Inventory;

public sealed record NetworkAddressObservation(string Address, int AddressFamily);

public sealed record NetworkAdapterObservation(
    string? SourceId, string? Name, string? Description, int? InterfaceType, int? OperationalStatus,
    long? LinkSpeedBitsPerSecond, IEnumerable<NetworkAddressObservation>? UnicastAddresses,
    IEnumerable<NetworkAddressObservation>? GatewayAddresses, IEnumerable<NetworkAddressObservation>? DnsAddresses,
    IReadOnlyList<CollectionIssue>? Issues = null);

/// <summary>Reads local adapter metadata. It never resolves a hostname, sends a packet or changes configuration.</summary>
public sealed class NetworkCollector(
    Func<CancellationToken, IEnumerable<NetworkAdapterObservation>>? source = null) : IDiagnosticCollector
{
    // Bounds retained metadata in a user-requested snapshot, not the number supported by Windows.
    public const int MaximumAdapters = 64;
    public const int MaximumAddressesPerKind = 32;
    public string Id => InventoryCollectors.Network;
    public string DisplayName => "Interfaces e configuração de rede";

    public Task<IReadOnlyList<DiagnosticEvidence>> CollectAsync(CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<DiagnosticEvidence>>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var adapters = new List<DiagnosticEvidence>();
            var issues = new List<CollectionIssue>();
            bool limited = false;
            bool discoveryFailed = false;
            try
            {
                foreach (var adapter in (source ?? ReadLocalAdapters)(cancellationToken))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (adapters.Count == MaximumAdapters)
                    {
                        limited = true;
                        issues.Add(new(IssueCode.LimitReached, "Mais de 64 interfaces foram retornadas; somente as primeiras 64 foram retidas, na ordem da API."));
                        break;
                    }
                    adapters.Add(Describe(adapter, adapters.Count, cancellationToken));
                }
            }
            catch (Exception error) when (Expected(error))
            {
                discoveryFailed = true;
                issues.Add(Issue(error, "Enumeração local de interfaces"));
            }
            cancellationToken.ThrowIfCancellationRequested();
            adapters.Insert(0, new(Guid.NewGuid(), Id, DisplayName, "Computador local", DateTimeOffset.UtcNow,
                discoveryFailed && adapters.Count == 0 ? EvidenceStatus.Unavailable
                    : issues.Count > 0 ? EvidenceStatus.Partial : EvidenceStatus.Complete,
                new("NetworkInterface.GetAllNetworkInterfaces; inventário local, sem teste de conectividade, consultas DNS ou tráfego externo", adapters.Count, MaximumAdapters, limited),
                [new("examinedAdapters", "Interfaces retidas", NumberValue: adapters.Count),
                 new("connectivityTested", "Conectividade externa testada", "false")], issues));
            return adapters;
        }, cancellationToken);

    private DiagnosticEvidence Describe(NetworkAdapterObservation adapter, int index, CancellationToken token)
    {
        var issues = new List<CollectionIssue>(adapter.Issues ?? []);
        var facts = new List<DiagnosticFact>
        {
            new("sourceId", "Identificador de origem da interface", adapter.SourceId),
            new("name", "Nome da interface", adapter.Name),
            new("description", "Descrição da interface", adapter.Description),
            new("interfaceTypeRaw", "Tipo da interface: código original", NumberValue: adapter.InterfaceType),
            new("operationalStatusRaw", "Estado operacional: código original", NumberValue: adapter.OperationalStatus),
            new("linkSpeedReportedBitsPerSecond", "Velocidade de enlace: valor original reportado", NumberValue: adapter.LinkSpeedBitsPerSecond, Unit: "bits/s", Detailed: true)
        };
        AddEnum<NetworkInterfaceType>("interfaceType", "Tipo da interface", adapter.InterfaceType);
        AddEnum<OperationalStatus>("operationalStatus", "Estado operacional da interface", adapter.OperationalStatus);
        bool speedKnown = adapter.LinkSpeedBitsPerSecond is >= 0;
        facts.Add(new("linkSpeedBitsPerSecond", "Velocidade reportada do enlace (não mede transferência)",
            NumberValue: speedKnown ? adapter.LinkSpeedBitsPerSecond : null, Unit: "bits/s"));
        if (!speedKnown) issues.Add(new(IssueCode.NotSupported, "Velocidade de enlace não informada ou retornada com representação negativa; nenhum valor zero foi inferido."));
        bool limited = false;
        int examinedAddresses = 0;
        AddAddresses("unicast", "Endereço local", adapter.UnicastAddresses);
        AddAddresses("gateway", "Gateway configurado", adapter.GatewayAddresses);
        AddAddresses("dns", "Servidor DNS configurado", adapter.DnsAddresses);
        token.ThrowIfCancellationRequested();
        return new(Guid.NewGuid(), Id, $"Rede · {adapter.Name ?? "interface " + (index + 1)}", adapter.SourceId ?? $"Interface na posição {index}",
            DateTimeOffset.UtcNow, issues.Count == 0 ? EvidenceStatus.Complete : EvidenceStatus.Partial,
            new("Metadados da interface e endereços examinados; orçamento de 96 endereços dividido em três classes de até 32 (local/gateway/DNS), preservando ordem e família; limite atingido indica classe truncada. Velocidade informada pelo driver, sem medir throughput ou acesso à Internet", examinedAddresses,
                MaximumAddressesPerKind * 3, limited), facts, issues);

        void AddEnum<T>(string key, string label, int? code) where T : struct, Enum
        {
            string? name = code is { } value && Enum.IsDefined(typeof(T), value) ? Enum.GetName(typeof(T), value) : null;
            facts.Add(new(key, label, name));
            if (name is null) issues.Add(new(IssueCode.NotSupported, $"{label}: código ausente ou não reconhecido; o valor original foi preservado."));
        }

        void AddAddresses(string kind, string label, IEnumerable<NetworkAddressObservation>? addresses)
        {
            int count = 0;
            if (addresses is null)
            {
                facts.Add(new($"{kind}.examined", $"{label}: endereços retidos", NumberValue: null));
                issues.Add(new(IssueCode.ReadFailed, $"{label}: a coleção não pôde ser consultada; não equivale a lista vazia."));
                return;
            }
            try
            {
                foreach (var address in addresses)
                {
                    token.ThrowIfCancellationRequested();
                    if (count == MaximumAddressesPerKind)
                    {
                        limited = true;
                        issues.Add(new(IssueCode.LimitReached, $"{label}: existem mais de 32 endereços; os primeiros 32 foram retidos na ordem da API."));
                        break;
                    }
                    var prefix = $"{kind}.{count}";
                    facts.Add(new(prefix + ".address", label, address.Address));
                    facts.Add(new(prefix + ".familyRaw", "Família do endereço: código original", NumberValue: address.AddressFamily, Detailed: true));
                    string? family = address.AddressFamily switch
                    {
                        (int)AddressFamily.InterNetwork => "IPv4",
                        (int)AddressFamily.InterNetworkV6 => "IPv6",
                        _ => null
                    };
                    facts.Add(new(prefix + ".family", "Família do endereço", family));
                    if (family is null) issues.Add(new(IssueCode.NotSupported, $"{label}: família não reconhecida; endereço e código originais preservados."));
                    count++;
                    examinedAddresses++;
                }
            }
            catch (Exception error) when (Expected(error)) { issues.Add(Issue(error, label)); }
            facts.Add(new($"{kind}.examined", $"{label}: endereços retidos", NumberValue: count));
        }
    }

    private static IEnumerable<NetworkAdapterObservation> ReadLocalAdapters(CancellationToken token)
    {
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            token.ThrowIfCancellationRequested();
            var issues = new List<CollectionIssue>();
            string? id = Text(() => adapter.Id, "Identificador");
            string? name = Text(() => adapter.Name, "Nome");
            string? description = Text(() => adapter.Description, "Descrição");
            int? type = Value(() => (int)adapter.NetworkInterfaceType, "Tipo");
            int? state = Value(() => (int)adapter.OperationalStatus, "Estado operacional");
            long? speed = Value(() => adapter.Speed, "Velocidade");
            IEnumerable<NetworkAddressObservation>? unicast = null, gateways = null, dns = null;
            try
            {
                var properties = adapter.GetIPProperties();
                unicast = properties.UnicastAddresses.Select(item => new NetworkAddressObservation(item.Address.ToString(), (int)item.Address.AddressFamily));
                gateways = properties.GatewayAddresses.Select(item => new NetworkAddressObservation(item.Address.ToString(), (int)item.Address.AddressFamily));
                dns = properties.DnsAddresses.Select(item => new NetworkAddressObservation(item.ToString(), (int)item.AddressFamily));
            }
            catch (Exception error) when (Expected(error)) { issues.Add(Issue(error, "Propriedades IP")); }
            yield return new(id, name, description, type, state, speed, unicast, gateways, dns, issues);

            string? Text(Func<string> read, string field)
            {
                try { return read(); }
                catch (Exception error) when (Expected(error)) { issues.Add(Issue(error, field)); return null; }
            }
            T? Value<T>(Func<T> read, string field) where T : struct
            {
                try { return read(); }
                catch (Exception error) when (Expected(error)) { issues.Add(Issue(error, field)); return null; }
            }
        }
    }

    private static bool Expected(Exception error) => error is IOException or UnauthorizedAccessException
        or System.Security.SecurityException or NotSupportedException or System.ComponentModel.Win32Exception;
    private static CollectionIssue Issue(Exception error, string field) => new(
        error is UnauthorizedAccessException or System.Security.SecurityException ? IssueCode.PermissionDenied
            : error is NotSupportedException ? IssueCode.NotSupported : IssueCode.ReadFailed,
        $"{field}: {error.Message}");
}
