namespace Optimus.Application;

public sealed record InstabilityServices(
    Func<int, DiagnosticService> RestartHistory,
    Func<int, DiagnosticService> MemoryResults,
    DiagnosticService HardwareProfile,
    Func<CrashDumpSettings, DiagnosticService> AnalyzeDump,
    IMemoryTestReportImporter MemoryReports,
    Func<LocalAiSettings, IInvestigationPlanner> LocalPlanner,
    IFirmwareNotesReader? FirmwareNotes = null)
{
    public InstabilityInvestigationService CreateInvestigation(int days, CrashDumpSettings? dump = null, LocalAiSettings? ai = null)
    {
        if (days is < 1 or > 365) throw new ArgumentOutOfRangeException(nameof(days), "Escolha entre um e 365 dias.");
        var tools = new Dictionary<InvestigationTool, MaintenanceQueryBinding>
        {
            [InvestigationTool.RestartHistory] = new("windows.restart-history", RestartHistory(days)),
            [InvestigationTool.MemoryResults] = new("windows.memory-diagnostic-results", MemoryResults(days)),
            [InvestigationTool.HardwareProfile] = new("windows.hardware-profile", HardwareProfile)
        };
        if (dump is not null) tools[InvestigationTool.AnalyzeDump] = new("windows.crash-dump", AnalyzeDump(dump));
        return new(tools, ai is null ? null : LocalPlanner(ai));
    }
}
