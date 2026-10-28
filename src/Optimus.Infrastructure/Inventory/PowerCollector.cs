using System.ComponentModel;
using System.Runtime.InteropServices;
using Optimus.Application;
using Optimus.Domain;

namespace Optimus.Infrastructure.Inventory;

public sealed record PowerStatusObservation(byte AcLineStatus, byte BatteryFlag, byte BatteryLifePercent,
    byte SystemStatusFlag, uint BatteryLifeTime, uint BatteryFullLifeTime);

/// <summary>A snapshot of GetSystemPowerStatus; no battery wear, health or physical test is inferred.</summary>
public sealed class PowerCollector(Func<CancellationToken, PowerStatusObservation>? source = null) : IDiagnosticCollector
{
    public string Id => InventoryCollectors.Power;
    public string DisplayName => "Alimentação e bateria";

    public Task<IReadOnlyList<DiagnosticEvidence>> CollectAsync(CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<DiagnosticEvidence>>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var raw = (source ?? ReadWindowsStatus)(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                return [Describe(raw)];
            }
            catch (Exception error) when (error is Win32Exception or IOException or UnauthorizedAccessException
                or System.Security.SecurityException or NotSupportedException)
            {
                return [new(Guid.NewGuid(), Id, DisplayName, "Computador local", DateTimeOffset.UtcNow,
                    EvidenceStatus.Unavailable, new("GetSystemPowerStatus falhou; nenhum campo nativo foi presumido", 0), [],
                    [new(error is NotSupportedException ? IssueCode.NotSupported
                        : error is UnauthorizedAccessException or System.Security.SecurityException ? IssueCode.PermissionDenied : IssueCode.ReadFailed, error.Message)])];
            }
        }, cancellationToken);

    private DiagnosticEvidence Describe(PowerStatusObservation raw)
    {
        var issues = new List<CollectionIssue>();
        var facts = new List<DiagnosticFact>
        {
            new("acLineStatusRaw", "ACLineStatus original", NumberValue: raw.AcLineStatus, Detailed: true),
            new("batteryFlagRaw", "BatteryFlag original", NumberValue: raw.BatteryFlag, Detailed: true),
            new("batteryLifePercentRaw", "BatteryLifePercent original", NumberValue: raw.BatteryLifePercent, Detailed: true),
            new("systemStatusFlagRaw", "SystemStatusFlag original", NumberValue: raw.SystemStatusFlag, Detailed: true),
            new("batteryLifeTimeRaw", "BatteryLifeTime original", NumberValue: raw.BatteryLifeTime, Unit: "seconds", Detailed: true),
            new("batteryFullLifeTimeRaw", "BatteryFullLifeTime original", NumberValue: raw.BatteryFullLifeTime, Unit: "seconds", Detailed: true)
        };
        string? ac = raw.AcLineStatus switch { 0 => "Desconectada", 1 => "Conectada", _ => null };
        facts.Add(new("acPower", "Alimentação externa", ac));
        if (ac is null) Unknown("ACLineStatus", raw.AcLineStatus);

        bool flagUnknown = raw.BatteryFlag == 255;
        bool noBattery = raw.BatteryFlag == 128;
        bool validBatteryFlags = raw.BatteryFlag <= 15;
        string? presence = noBattery ? "Ausente" : validBatteryFlags ? "Presente" : null;
        facts.Add(new("batteryPresence", "Bateria do sistema", presence));
        if (presence is null) Unknown("BatteryFlag", raw.BatteryFlag);
        facts.Add(new("batteryChargingReported", "Indicador de carregamento reportado",
            noBattery ? "Não aplicável: sem bateria" : validBatteryFlags ? ((raw.BatteryFlag & 8) != 0 ? "true" : "false") : null));
        if (!flagUnknown && !noBattery && validBatteryFlags)
        {
            facts.Add(new("batteryChargeFlags", "Indicadores de carga reportados (não medem desgaste)", raw.BatteryFlag == 0
                ? "Sem indicador alto/baixo/crítico/carregando"
                : string.Join(", ", new[] { (1, "alto"), (2, "baixo"), (4, "crítico"), (8, "carregando") }
                    .Where(item => (raw.BatteryFlag & item.Item1) != 0).Select(item => item.Item2))));
        }

        decimal? percent = !noBattery && raw.BatteryLifePercent <= 100 ? raw.BatteryLifePercent : null;
        facts.Add(new("batteryChargePercent", noBattery ? "Carga da bateria (não aplicável: sem bateria)" : "Carga reportada da bateria", NumberValue: percent, Unit: "%"));
        if (!noBattery && percent is null) Unknown("BatteryLifePercent", raw.BatteryLifePercent);
        if (noBattery && raw.BatteryLifePercent <= 100)
            issues.Add(new(IssueCode.ReadFailed, "A API informou ausência de bateria e percentual simultaneamente. O valor original foi preservado sem tratar a carga como aplicável."));

        AddDuration("batteryRemainingSeconds", "Autonomia restante estimada pelo Windows", raw.BatteryLifeTime);
        AddDuration("batteryFullSeconds", "Autonomia com carga cheia estimada pelo Windows", raw.BatteryFullLifeTime);
        facts.Add(new("batterySaver", "Economia de bateria reportada", raw.SystemStatusFlag switch { 0 => "false", 1 => "true", _ => null }));
        if (raw.SystemStatusFlag > 1) Unknown("SystemStatusFlag", raw.SystemStatusFlag);
        facts.Add(new("batteryHealthTested", "Desgaste e saúde física da bateria avaliados", "false"));
        return new(Guid.NewGuid(), Id, DisplayName, "Computador local", DateTimeOffset.UtcNow,
            issues.Count == 0 ? EvidenceStatus.Complete : EvidenceStatus.Partial,
            new("Snapshot GetSystemPowerStatus; valores e sentinelas originais preservados. Autonomia é estimativa do Windows, sem medição de desgaste, capacidade física ou teste da bateria", 1), facts, issues);

        void Unknown(string field, uint value) => issues.Add(new(IssueCode.NotSupported,
            $"{field} retornou {value}: estado desconhecido ou não reconhecido; o valor original foi preservado e não se tornou zero ou indicação de saúde."));

        void AddDuration(string key, string label, uint value)
        {
            facts.Add(new(key, noBattery ? label + " (não aplicável: sem bateria)" : label,
                NumberValue: !noBattery && value != uint.MaxValue ? value : null, Unit: "seconds"));
            // -1 is documented for unknown duration or AC power; it is not a read failure.
        }
    }

    private static PowerStatusObservation ReadWindowsStatus(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("GetSystemPowerStatus requer Windows.");
        if (!GetSystemPowerStatus(out var value)) throw new Win32Exception(Marshal.GetLastWin32Error());
        token.ThrowIfCancellationRequested();
        return new(value.AcLineStatus, value.BatteryFlag, value.BatteryLifePercent, value.SystemStatusFlag, value.BatteryLifeTime, value.BatteryFullLifeTime);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte AcLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public uint BatteryLifeTime, BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);
}
