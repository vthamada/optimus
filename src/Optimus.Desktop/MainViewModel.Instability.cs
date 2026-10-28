using System.Collections.ObjectModel;
using Optimus.Application;
using Optimus.Domain;

namespace Optimus.Desktop;

public sealed partial class MainViewModel
{
    private InstabilityServices? _instability;
    private string _investigationQuestion = "Quais evidências podem ajudar a investigar os reinícios e a instabilidade deste computador?";
    private int _investigationDays = 30;
    private bool _useInvestigationAi, _includeDump;
    private string _cdbPath = "", _symbolDirectory = "", _dumpPath = "";
    private string? _cdbHash;
    private string _firmwareNotesUrl = "";
    private FirmwareSupportReference? _selectedFirmwareReference;
    private string _investigationSummary = "Escolha uma pergunta e investigue até quatro fontes consultivas. Nenhuma consulta é iniciada ao abrir esta aba.";
    private Guid? _investigationSessionId;
    private static readonly IReadOnlySet<string> InstabilityCollectorIds = new HashSet<string>([
        "windows.restart-history", "windows.memory-diagnostic-results", "windows.hardware-profile", "windows.crash-dump",
        "memory.test-report", "firmware.release-notes", InstabilityInvestigationService.AssessmentCollectorId
    ], StringComparer.Ordinal);

    public ObservableCollection<DiagnosticEvidence> InstabilityEvidence { get; } = [];
    public ObservableCollection<InvestigationHypothesis> InvestigationHypotheses { get; } = [];
    public ObservableCollection<InvestigationStep> InvestigationSteps { get; } = [];
    public ObservableCollection<FirmwareSupportReference> FirmwareReferences { get; } = [];
    public IReadOnlyList<int> InvestigationDayChoices { get; } = Enumerable.Range(1, 365).ToArray();
    public AsyncCommand InvestigateCommand { get; private set; } = null!;
    public AsyncCommand ImportMemoryReportCommand { get; private set; } = null!;
    public AsyncCommand AnalyzeDumpCommand { get; private set; } = null!;
    public AsyncCommand ReadFirmwareNotesCommand { get; private set; } = null!;
    public ActionCommand BrowseCdbCommand { get; private set; } = null!;
    public ActionCommand BrowseSymbolDirectoryCommand { get; private set; } = null!;
    public ActionCommand BrowseDumpCommand { get; private set; } = null!;
    public string InvestigationQuestion
    {
        get => _investigationQuestion;
        set { if (Field(ref _investigationQuestion, value, false)) RefreshInstabilityCommands(); }
    }
    public int InvestigationDays
    {
        get => _investigationDays;
        set { if (value == _investigationDays) return; _investigationDays = value; Changed(); RefreshInstabilityCommands(); }
    }
    public bool UseInvestigationAi
    {
        get => _useInvestigationAi;
        set { if (value == _useInvestigationAi) return; _useInvestigationAi = value; Changed(); RefreshInstabilityCommands(); }
    }
    public bool IncludeDump
    {
        get => _includeDump;
        set { if (value == _includeDump) return; _includeDump = value; Changed(); RefreshInstabilityCommands(); }
    }
    public string CdbPath
    {
        get => _cdbPath;
        set { if (Field(ref _cdbPath, value, false)) { _cdbHash = null; RefreshInstabilityCommands(); } }
    }
    public string SymbolDirectory
    {
        get => _symbolDirectory;
        set { if (Field(ref _symbolDirectory, value, false)) RefreshInstabilityCommands(); }
    }
    public string DumpPath
    {
        get => _dumpPath;
        set { if (Field(ref _dumpPath, value, false)) RefreshInstabilityCommands(); }
    }
    public string InvestigationSummary { get => _investigationSummary; private set { _investigationSummary = value; Changed(); } }
    public FirmwareSupportReference? SelectedFirmwareReference
    {
        get => _selectedFirmwareReference;
        set
        {
            if (value is not null && !FirmwareReferences.Contains(value) || value == _selectedFirmwareReference) return;
            _selectedFirmwareReference = value; Changed(); RefreshInstabilityCommands();
        }
    }
    public string FirmwareNotesUrl
    {
        get => _firmwareNotesUrl;
        set { if (Field(ref _firmwareNotesUrl, value, false)) RefreshInstabilityCommands(); }
    }
    private bool DumpInputsSelected => !string.IsNullOrWhiteSpace(CdbPath) && !string.IsNullOrWhiteSpace(SymbolDirectory) && !string.IsNullOrWhiteSpace(DumpPath);
    private bool InvestigationInputsSelected => !string.IsNullOrWhiteSpace(InvestigationQuestion) && InvestigationQuestion.Length <= 400
        && InvestigationDays is >= 1 and <= 365 && (!IncludeDump || DumpInputsSelected)
        && (!UseInvestigationAi || !string.IsNullOrWhiteSpace(LlamaPath) && !string.IsNullOrWhiteSpace(ModelPath));

    private void InitializeInstability(InstabilityServices? services)
    {
        _instability = services;
        InvestigateCommand = new(async () => { await RunInvestigationAsync(); }, () => !IsBusy && _instability is not null && InvestigationInputsSelected, SetError);
        ImportMemoryReportCommand = new(ImportMemoryReportAsync, () => !IsBusy && _instability is not null, SetError);
        AnalyzeDumpCommand = new(async () => { await RunDumpAnalysisAsync(); }, () => !IsBusy && _instability is not null && DumpInputsSelected, SetError);
        ReadFirmwareNotesCommand = new(async () => { await ReadFirmwareNotesAsync(); },
            () => !IsBusy && _instability?.FirmwareNotes is not null && SelectedFirmwareReference is not null
                && FirmwareReferences.Contains(SelectedFirmwareReference) && !string.IsNullOrWhiteSpace(FirmwareNotesUrl), SetError);
        BrowseCdbCommand = new(() => PickFile("Selecionar CDB local (cdb.exe)", "CDB (cdb.exe)|cdb.exe", value => CdbPath = value), () => !IsBusy);
        BrowseSymbolDirectoryCommand = new(() => PickFolder("Selecionar uma pasta local de símbolos", value => SymbolDirectory = value), () => !IsBusy);
        BrowseDumpCommand = new(() => PickFile("Selecionar dump existente", "Dump (*.dmp)|*.dmp", value => DumpPath = value), () => !IsBusy);
        PropertyChanged += (_, change) =>
        {
            if (change.PropertyName is nameof(LlamaPath) or nameof(ModelPath)) RefreshInstabilityCommands();
        };
    }

    private void RefreshInstabilityCommands()
    {
        InvestigateCommand?.Notify(); ImportMemoryReportCommand?.Notify(); AnalyzeDumpCommand?.Notify();
        ReadFirmwareNotesCommand?.Notify();
        BrowseCdbCommand?.Notify(); BrowseSymbolDirectoryCommand?.Notify(); BrowseDumpCommand?.Notify();
    }

    public async Task<bool> RunInvestigationAsync()
    {
        if (IsBusy) return false;
        var services = _instability;
        if (services is null) { StatusMessage = "As ferramentas de instabilidade não estão configuradas nesta composição."; return false; }
        if (!InvestigationInputsSelected)
        { StatusMessage = "Informe uma pergunta de até 400 caracteres, um a 365 dias e os caminhos locais das opções selecionadas."; return false; }
        // Capture every selection before the first await. Edits cannot relabel the in-flight investigation.
        var previous = CurrentSession;
        var options = new InvestigationOptions(InvestigationQuestion, MaximumSteps: 4, UseLocalAi: UseInvestigationAi);
        int days = InvestigationDays;
        var dump = IncludeDump ? CaptureDumpSettings() : null;
        var ai = UseInvestigationAi ? new LocalAiSettings(LlamaPath, ModelPath, _llamaHash, _modelHash) : null;
        bool applied = false;
        await RunModuleAsync(async token =>
        {
            StatusMessage = "Investigando fontes locais consultivas…";
            var progress = new Progress<string>(message => StatusMessage = message);
            var result = await services.CreateInvestigation(days, dump, ai).RunAsync(previous, options, progress, token);
            if (result.Value is not { } report)
            { StatusMessage = result.Error?.Message ?? "A investigação não entregou resultado."; return; }
            if (!SessionValidation.IsValid(report.Session))
            { StatusMessage = "A investigação devolveu uma sessão inválida; o histórico e a sessão anterior foram preservados."; return; }
            if (result.Error?.Code == ErrorCode.Cancelled || token.IsCancellationRequested)
            {
                Display(report.Session);
                DisplayInvestigationReport(report);
                StatusMessage = "Investigação cancelada. Etapas concluídas disponíveis na interface e para exportação; esta execução cancelada não foi salva no histórico.";
                return;
            }
            await PersistCollectionAsync(report.Session, token, "Investigação salva com a cobertura e datas de cada fonte. Hipóteses não confirmam a causa física.");
            DisplayInvestigationReport(report);
            applied = true;
            if (!result.IsSuccess) StatusMessage += " Resultado parcial: " + result.Error!.Message;
        });
        return applied;
    }

    public async Task<bool> RunDumpAnalysisAsync()
    {
        if (IsBusy) return false;
        var services = _instability;
        if (services is null || !DumpInputsSelected)
        { StatusMessage = "Selecione cdb.exe, dump e pasta local de símbolos para analisar."; return false; }
        var selected = CaptureDumpSettings();
        var previous = CurrentSession;
        bool applied = false;
        await RunModuleAsync(async token =>
        {
            StatusMessage = "Analisando o dump selecionado com CDB local; a saída não confirma a causa da falha…";
            var binding = new MaintenanceQueryBinding("windows.crash-dump", services.AnalyzeDump(selected));
            var result = await binding.RunAsync(new Progress<string>(message => StatusMessage = message), token);
            token.ThrowIfCancellationRequested();
            if (!result.IsSuccess) { StatusMessage = result.Error!.Message; return; }
            applied = await ApplyInstabilityOwnedAsync(previous, result.Value!, "windows.crash-dump", token,
                "Análise do dump salva. Confira o CDB, símbolos, saída original e limites; um módulo citado pode ser vítima da falha.");
        });
        return applied;
    }

    private CrashDumpSettings CaptureDumpSettings() => new(CdbPath, DumpPath, SymbolDirectory, _cdbHash);

    public async Task<bool> ReadFirmwareNotesAsync()
    {
        if (IsBusy) return false;
        var reader = _instability?.FirmwareNotes;
        var reference = SelectedFirmwareReference;
        if (reader is null || reference is null || !FirmwareReferences.Contains(reference) || string.IsNullOrWhiteSpace(FirmwareNotesUrl))
        { StatusMessage = "Selecione um fabricante observado e informe a URL da página oficial; o leitor precisa estar configurado."; return false; }
        string selectedUrl = FirmwareNotesUrl;
        string manufacturer = reference.Manufacturer;
        var previous = CurrentSession;
        bool applied = false;
        await RunModuleAsync(async token =>
        {
            StatusMessage = "Consultando somente a página oficial selecionada pela internet; nenhum log ou dump será enviado…";
            var result = await reader.ReadAsync(selectedUrl, manufacturer, token);
            if (result.Value is not { } evidence)
            {
                token.ThrowIfCancellationRequested();
                StatusMessage = result.Error?.Message ?? "O leitor não entregou evidência da página.";
                return;
            }
            var now = DateTimeOffset.UtcNow;
            var collected = new DiagnosticSession(Guid.NewGuid(), now, now,
                evidence.Status == EvidenceStatus.Complete ? SessionStatus.Complete : SessionStatus.Partial, [evidence], DiagnosticRules.Evaluate([evidence]));
            if (result.Error?.Code == ErrorCode.Cancelled || token.IsCancellationRequested)
            {
                // A bounded prefix already belongs to this response; cancellation must not discard its provenance.
                var merged = EvidenceSessionUpdates.ReplaceOwned(previous, collected, new HashSet<string>(["firmware.release-notes"], StringComparer.Ordinal));
                if (!merged.IsSuccess)
                { StatusMessage = "Consulta de firmware cancelada; evidência parcial inválida, sessão anterior preservada. " + merged.Error!.Message; return; }
                Display(merged.Value! with { Status = SessionStatus.Partial });
                StatusMessage = "Consulta de firmware cancelada. Prefixo recebido disponível na interface e para exportação; conteúdo incompleto, esta execução não foi salva no histórico. Nenhuma requisição será repetida automaticamente.";
                return;
            }
            token.ThrowIfCancellationRequested();
            applied = await ApplyInstabilityOwnedAsync(previous, collected, "firmware.release-notes", token,
                "Página oficial consultada e evidência salva com seus limites. Confira modelo e revisão; isto não confirma firmware atualizado nem aplica BIOS.");
            if (applied && !result.IsSuccess) StatusMessage += " Resultado parcial: " + result.Error!.Message;
        });
        return applied;
    }

    private async Task ImportMemoryReportAsync()
    {
        string? path = null;
        PickFile("Importar relatório HTML existente do MemTest86", "Relatório HTML (*.html;*.htm)|*.html;*.htm", selected => path = selected);
        if (path is not null) await ImportMemoryReportFromPathAsync(path);
    }

    public async Task<bool> ImportMemoryReportFromPathAsync(string path)
    {
        if (IsBusy) return false;
        var services = _instability;
        if (services is null) { StatusMessage = "O importador de relatórios não está configurado."; return false; }
        string selectedPath = path;
        var previous = CurrentSession;
        bool applied = false;
        await RunModuleAsync(async token =>
        {
            StatusMessage = "Importando o relatório selecionado como evidência inerte; nenhum teste físico será iniciado…";
            var imported = await services.MemoryReports.ImportAsync(selectedPath, token);
            token.ThrowIfCancellationRequested();
            if (imported.Value is not { } evidence) { StatusMessage = imported.Error?.Message ?? "O importador não entregou evidência."; return; }
            var now = DateTimeOffset.UtcNow;
            var collected = new DiagnosticSession(Guid.NewGuid(), now, now,
                evidence.Status == EvidenceStatus.Complete ? SessionStatus.Complete : SessionStatus.Partial, [evidence], DiagnosticRules.Evaluate([evidence]));
            applied = await ApplyInstabilityOwnedAsync(previous, collected, "memory.test-report", token,
                "Relatório importado e salvo com sua cobertura. Linhas de erro não identificam automaticamente um módulo físico.");
            if (applied && !imported.IsSuccess) StatusMessage += " Resultado parcial: " + imported.Error!.Message;
        });
        return applied;
    }

    private async Task<bool> ApplyInstabilityOwnedAsync(DiagnosticSession? previous, DiagnosticSession collected, string collectorId,
        CancellationToken token, string savedMessage)
    {
        var merged = EvidenceSessionUpdates.ReplaceOwned(previous, collected, new HashSet<string>([collectorId], StringComparer.Ordinal));
        if (!merged.IsSuccess) { StatusMessage = merged.Error!.Message; return false; }
        token.ThrowIfCancellationRequested();
        await PersistCollectionAsync(merged.Value!, token, savedMessage);
        return true; // Data applied and persisted/failed persistence are distinct; the status preserves that result.
    }

    private void DisplayInvestigationReport(InvestigationRun report)
    {
        _investigationSessionId = report.Session.Id;
        InvestigationHypotheses.Clear(); InvestigationSteps.Clear();
        foreach (var item in report.Hypotheses) InvestigationHypotheses.Add(item);
        foreach (var item in report.Steps) InvestigationSteps.Add(item);
        ReplaceFirmwareReferences(report.FirmwareReferences);
        InvestigationSummary = $"Roteiro encerrado: {report.StopReason}. {report.Steps.Count} etapa(s), {report.Hypotheses.Count} hipótese(s). Data do resultado: {report.Session.CompletedAt:dd/MM/yyyy HH:mm zzz}. Ausência de hipótese não comprova ausência de problema.";
    }

    private void DisplayInstabilityEvidence(DiagnosticSession session)
    {
        InstabilityEvidence.Clear();
        foreach (var item in session.Evidence.Where(item => InstabilityCollectorIds.Contains(item.CollectorId))) InstabilityEvidence.Add(item);
        if (_investigationSessionId == session.Id) return;
        _investigationSessionId = null;
        InvestigationHypotheses.Clear(); InvestigationSteps.Clear();
        ReplaceFirmwareReferences(FirmwareSupportCatalog.FromEvidence(session.Evidence));
        // Historical traces remain owned by the application producer; do not reinterpret its raw JSON in the view model.
        InvestigationSummary = session.Evidence.Any(item => item.CollectorId == InstabilityInvestigationService.AssessmentCollectorId)
            ? "Sessão com investigação anterior. Confira a data de cada evidência; o rastro original permanece nos detalhes e no relatório exportado. Nenhuma consulta foi repetida."
            : "Nenhum roteiro de investigação foi apresentado para esta sessão. As evidências importadas ou consultadas permanecem disponíveis abaixo.";
    }

    private void ReplaceFirmwareReferences(IReadOnlyList<FirmwareSupportReference> references)
    {
        var previous = SelectedFirmwareReference;
        FirmwareReferences.Clear();
        foreach (var reference in references) FirmwareReferences.Add(reference);
        SelectedFirmwareReference = previous is null ? null : FirmwareReferences.FirstOrDefault(reference => reference == previous);
        RefreshInstabilityCommands();
    }
}
