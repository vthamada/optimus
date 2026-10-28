using System.ComponentModel;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Optimus.Application;
using Optimus.Domain;
using Optimus.Infrastructure.Events;

namespace Optimus.Infrastructure;

public sealed class SystemCollector : IDiagnosticCollector
{
    public string Id => "windows.system";
    public string DisplayName => "Sistema e memória";

    public Task<IReadOnlyList<DiagnosticEvidence>> CollectAsync(CancellationToken cancellationToken) => Task.Run<IReadOnlyList<DiagnosticEvidence>>(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        var facts = new List<DiagnosticFact>
        {
            new("osDescription", "Sistema", RuntimeInformation.OSDescription),
            new("osArchitecture", "Arquitetura", RuntimeInformation.OSArchitecture.ToString()),
            new("logicalProcessors", "Processadores lógicos", NumberValue: Environment.ProcessorCount),
            new("machineName", "Nome do computador", Environment.MachineName)
        };
        var issues = new List<CollectionIssue>();
        try
        {
            using var cpu = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0", writable: false);
            facts.Add(new("cpuName", "Processador", cpu?.GetValue("ProcessorNameString") as string));
            using var version = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", writable: false);
            facts.Add(new("windowsRelease", "Versão do Windows", version?.GetValue("DisplayVersion") as string));
            facts.Add(new("windowsBuild", "Build", version?.GetValue("CurrentBuildNumber") as string));
        }
        catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            issues.Add(new(IssueCode.ReadFailed, error.Message));
        }

        var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (GlobalMemoryStatusEx(ref memory))
        {
            facts.Add(new("physicalMemoryBytes", "Memória física total", NumberValue: memory.TotalPhysical, Unit: "bytes"));
            facts.Add(new("availableMemoryBytes", "Memória física disponível", NumberValue: memory.AvailablePhysical, Unit: "bytes"));
        }
        else
        {
            issues.Add(new(IssueCode.ReadFailed, new Win32Exception(Marshal.GetLastWin32Error()).Message));
        }

        cancellationToken.ThrowIfCancellationRequested();
        return [new(Guid.NewGuid(), Id, DisplayName, "Computador local", DateTimeOffset.UtcNow,
            issues.Count == 0 ? EvidenceStatus.Complete : EvidenceStatus.Partial,
            new("APIs do Windows e valores selecionados do Registro; amostra de memória física", facts.Count), facts, issues)];
    }, cancellationToken);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysical;
        public ulong AvailablePhysical;
        public ulong TotalPageFile;
        public ulong AvailablePageFile;
        public ulong TotalVirtual;
        public ulong AvailableVirtual;
        public ulong AvailableExtendedVirtual;
    }
}

public sealed class VolumeCollector : IDiagnosticCollector
{
    public string Id => "windows.volumes";
    public string DisplayName => "Volumes locais";

    public Task<IReadOnlyList<DiagnosticEvidence>> CollectAsync(CancellationToken cancellationToken) => Task.Run<IReadOnlyList<DiagnosticEvidence>>(() =>
    {
        var result = new List<DiagnosticEvidence>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var facts = new List<DiagnosticFact>();
            var issues = new List<CollectionIssue>();
            try
            {
                if (drive.DriveType != DriveType.Fixed) continue;
                facts.Add(new("rootPath", "Raiz do volume", drive.Name));
                if (!drive.IsReady)
                {
                    issues.Add(new(IssueCode.ReadFailed, "O volume local não está pronto para consulta."));
                }
                else
                {
                    facts.Add(new("fileSystem", "Sistema de arquivos", drive.DriveFormat));
                    facts.Add(new("totalBytes", "Capacidade do volume", NumberValue: drive.TotalSize, Unit: "bytes"));
                    facts.Add(new("availableBytes", "Disponível ao usuário", NumberValue: drive.AvailableFreeSpace, Unit: "bytes"));
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                issues.Add(new(IssueCode.ReadFailed, error.Message));
            }

            result.Add(new(Guid.NewGuid(), Id, $"Volume {drive.Name}", drive.Name, DateTimeOffset.UtcNow,
                issues.Count == 0 ? EvidenceStatus.Complete : EvidenceStatus.Partial,
                new("Volumes locais de tipo Fixed; não examina arquivos nem consulta SMART/discos físicos", 1), facts, issues));
        }

        if (result.Count == 0)
        {
            result.Add(new(Guid.NewGuid(), Id, DisplayName, "Computador local", DateTimeOffset.UtcNow,
                EvidenceStatus.Unavailable, new("Volumes locais de tipo Fixed", 0), [],
                [new(IssueCode.NotSupported, "Nenhum volume local Fixed pôde ser selecionado para consulta.")]));
        }

        return result;
    }, cancellationToken);
}

public sealed record SystemEventObservation(string? Provider, int EventId, DateTimeOffset? OccurredAt, string OriginalXml,
    ulong? RecordId = null, WindowsEventXml? Xml = null);

public sealed class SystemEventsCollector(
    Func<DateTimeOffset, CancellationToken, IEnumerable<SystemEventObservation>>? source = null) : IDiagnosticCollector
{
    public const int EventLimit = 10;
    public string Id => "windows.system-events";
    public string DisplayName => "Eventos recentes do Windows";

    public Task<IReadOnlyList<DiagnosticEvidence>> CollectAsync(CancellationToken cancellationToken) => Task.Run<IReadOnlyList<DiagnosticEvidence>>(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        var cutoff = DateTimeOffset.UtcNow.AddDays(-3);
        var facts = new List<DiagnosticFact>();
        var count = 0;
        bool limitReached = false;
        var issues = new List<CollectionIssue>();
        try
        {
            foreach (var record in (source ?? WindowsEventReader.ReadSystem)(cutoff, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (count == EventLimit) { limitReached = true; break; }
                var prefix = $"event.{count}";
                int xmlBytes = record.Xml?.XmlUtf8Bytes ?? Encoding.UTF8.GetByteCount(record.OriginalXml);
                bool oversized = xmlBytes > WindowsEventReader.MaximumXmlBytes;
                // Stage the record so a failing property read cannot publish half of an event.
                DiagnosticFact[] recordFacts = [
                    new($"{prefix}.provider", "Origem do evento", record.Provider),
                    new($"{prefix}.id", "ID do evento", NumberValue: record.EventId),
                    new($"{prefix}.time", "Data do evento (UTC)", record.OccurredAt?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)),
                    new($"{prefix}.xml", "XML original do evento", oversized ? null : record.OriginalXml, Detailed: true)
                ];
                facts.AddRange(recordFacts);
                if (record.Xml is { } parsed) issues.AddRange(parsed.Issues);
                else if (oversized) issues.Add(new(IssueCode.LimitReached, "XML original excedeu 32 KiB UTF-8; conteúdo não retido, sem aceitar um prefixo truncado."));
                if (record.Xml is not null || oversized) facts.Add(new($"{prefix}.sourceXmlUtf8Bytes", "Tamanho UTF-8 original do XML", NumberValue: xmlBytes, Unit: "bytes", Detailed: true));
                count++;
            }
        }
        catch (Exception error) when (error is EventLogException or IOException or UnauthorizedAccessException or System.Security.SecurityException or TimeoutException)
        {
            issues.Add(new(IssueCode.ReadFailed, error.Message));
        }
        if (limitReached) issues.Add(new(IssueCode.LimitReached, "Existem mais eventos no intervalo. Os dez selecionados não representam toda a atividade do sistema."));

        facts.Insert(0, new("examinedEvents", "Eventos examinados", NumberValue: count));
        return [new(Guid.NewGuid(), Id, DisplayName, "Log System", DateTimeOffset.UtcNow,
            issues.Count > 0 ? (count > 0 ? EvidenceStatus.Partial : EvidenceStatus.Unavailable) : EvidenceStatus.Complete,
            new($"Eventos de nível crítico, erro e aviso desde {cutoff:O}, do mais recente para o mais antigo; XML retido de até 32 KiB UTF-8 por registro. ToXml pode alocar antes do limite. Prazo nativo cooperativo de 30 segundos entre chamadas; cada ReadEvent solicita timeout de dois segundos, sem tornar a leitura/XML preemptíveis", count, EventLimit, limitReached || issues.Any(issue => issue.Code == IssueCode.LimitReached)),
            facts, issues)];
    }, cancellationToken);

}
