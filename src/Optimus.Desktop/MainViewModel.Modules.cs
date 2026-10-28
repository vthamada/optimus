using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using System.Windows;
using Microsoft.Win32;
using Optimus.Application;
using Optimus.Domain;
using Optimus.Infrastructure;
using Optimus.Infrastructure.Smart;

namespace Optimus.Desktop;

public sealed partial class MainViewModel
{
    private ModuleServices _modules = null!;
    private readonly IUserConfirmation _confirmation;
    private long _configurationRevision;
    private Guid? _assistantSessionId;
    private BackupPlan? _backupPlan;
    private RestorePlan? _restorePlan;
    private AssistantAnswer? _assistantAnswer;
    private string _llamaPath = "", _modelPath = "", _resticPath = "", _smartPath = "";
    private string? _llamaHash, _modelHash, _resticHash;
    private string _question = "Selecione os fatos mais relevantes para entender os sinais observados nesta coleta.";
    private string _assistantSummary = "Escolha um modelo local nas configurações e execute um diagnóstico para iniciar a análise.";
    private string _assistantDetails = "A relevância da seleção é experimental. Valores e fontes permanecem vinculados às evidências originais.";
    private string _backupSource = "", _backupRepository = "", _passwordFile = "", _snapshot = "", _restoreDestination = "", _smartTarget = "";
    private string _backupPreview = "Selecione origem, repositório novo e arquivo de senha. A prévia não inicia o backup.";
    private string _backupResult = "Nenhum backup executado nesta sessão.", _restorePreview = "Escolha um snapshot e um destino novo para preparar a restauração.";
    private string _restoreResult = "Nenhuma restauração executada nesta sessão.";

    public ObservableCollection<AssistantInsight> AssistantInsights { get; } = [];
    public AsyncCommand AnalyzeCommand { get; private set; } = null!;
    public AsyncCommand PlanBackupCommand { get; private set; } = null!;
    public AsyncCommand ExecuteBackupCommand { get; private set; } = null!;
    public AsyncCommand PlanRestoreCommand { get; private set; } = null!;
    public AsyncCommand ExecuteRestoreCommand { get; private set; } = null!;
    public AsyncCommand ReadSmartCommand { get; private set; } = null!;
    public AsyncCommand ImportSmartCommand { get; private set; } = null!;
    public AsyncCommand ImportProfileCommand { get; private set; } = null!;
    public AsyncCommand ExportProfileCommand { get; private set; } = null!;
    public ActionCommand BrowseLlamaCommand { get; private set; } = null!;
    public ActionCommand BrowseModelCommand { get; private set; } = null!;
    public ActionCommand BrowseResticCommand { get; private set; } = null!;
    public ActionCommand BrowseSmartCommand { get; private set; } = null!;
    public ActionCommand BrowseBackupSourceCommand { get; private set; } = null!;
    public ActionCommand BrowseBackupRepositoryCommand { get; private set; } = null!;
    public ActionCommand BrowsePasswordCommand { get; private set; } = null!;
    public ActionCommand BrowseRestoreDestinationCommand { get; private set; } = null!;
    public bool CanEditModules => !IsBusy;
    public bool AssistantHasValidatedAnswer => _assistantAnswer is not null && _assistantAnswer.SessionId == CurrentSession?.Id;

    public string LlamaPath { get => _llamaPath; set { if (Field(ref _llamaPath, value)) _llamaHash = null; } }
    public string ModelPath { get => _modelPath; set { if (Field(ref _modelPath, value)) _modelHash = null; } }
    public string ResticPath { get => _resticPath; set { if (Field(ref _resticPath, value)) _resticHash = null; } }
    public string SmartPath { get => _smartPath; set => Field(ref _smartPath, value); }
    public string Question { get => _question; set => Field(ref _question, value, false); }
    public string AssistantSummary { get => _assistantSummary; private set { _assistantSummary = value; Changed(); } }
    public string AssistantDetails { get => _assistantDetails; private set { _assistantDetails = value; Changed(); } }
    public string BackupSource { get => _backupSource; set => Field(ref _backupSource, value); }
    public string BackupRepository { get => _backupRepository; set => Field(ref _backupRepository, value); }
    public string PasswordFile { get => _passwordFile; set => Field(ref _passwordFile, value); }
    public string Snapshot { get => _snapshot; set => Field(ref _snapshot, value); }
    public string RestoreDestination { get => _restoreDestination; set => Field(ref _restoreDestination, value); }
    public string SmartTarget { get => _smartTarget; set => Field(ref _smartTarget, value, false); }
    public string BackupPreview { get => _backupPreview; private set { _backupPreview = value; Changed(); } }
    public string BackupResult { get => _backupResult; private set { _backupResult = value; Changed(); } }
    public string RestorePreview { get => _restorePreview; private set { _restorePreview = value; Changed(); } }
    public string RestoreResult { get => _restoreResult; private set { _restoreResult = value; Changed(); } }

    private void InitializeModules(ModuleServices modules)
    {
        _modules = modules;
        AnalyzeCommand = new(AnalyzeCurrentSessionAsync, () => !IsBusy && CurrentSession is not null && HasAiPaths, SetError);
        PlanBackupCommand = new(PlanBackupAsync, () => !IsBusy, SetError);
        ExecuteBackupCommand = new(ExecuteBackupAsync, () => !IsBusy && _backupPlan is not null, SetError);
        PlanRestoreCommand = new(PlanRestoreAsync, () => !IsBusy, SetError);
        ExecuteRestoreCommand = new(ExecuteRestoreAsync, () => !IsBusy && _restorePlan is not null, SetError);
        ReadSmartCommand = new(ReadSmartAsync, () => !IsBusy && SmartPath.Length > 0 && SmartTarget.Length > 0, SetError);
        ImportSmartCommand = new(ImportSmartAsync, () => !IsBusy, SetError);
        ImportProfileCommand = new(ImportProfileAsync, () => !IsBusy, SetError);
        ExportProfileCommand = new(ExportProfileAsync, () => !IsBusy, SetError);
        BrowseLlamaCommand = new(() => PickFile("Selecionar llama-completion local", "Executável (*.exe)|*.exe", value => LlamaPath = value), () => !IsBusy);
        BrowseModelCommand = new(() => PickFile("Selecionar modelo local GGUF", "Modelo GGUF (*.gguf)|*.gguf", value => ModelPath = value), () => !IsBusy);
        BrowseResticCommand = new(() => PickFile("Selecionar restic local", "Executável (*.exe)|*.exe", value => ResticPath = value), () => !IsBusy);
        BrowseSmartCommand = new(() => PickFile("Selecionar smartctl local", "Executável (*.exe)|*.exe", value => SmartPath = value), () => !IsBusy);
        BrowsePasswordCommand = new(() => PickFile("Selecionar arquivo de senha do backup", "Arquivo de senha|*.*", value => PasswordFile = value), () => !IsBusy);
        BrowseBackupSourceCommand = new(() => PickFolder("Selecionar pasta de origem", value => BackupSource = value), () => !IsBusy);
        BrowseBackupRepositoryCommand = new(() => PickFolder("Selecionar repositório novo e vazio", value => BackupRepository = value), () => !IsBusy);
        BrowseRestoreDestinationCommand = new(() => PickFolder("Selecionar pasta pai; informe depois o nome de uma subpasta nova para restaurar", value => RestoreDestination = System.IO.Path.Combine(value, "restauracao-optimus-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"))), () => !IsBusy);
    }

    public async Task LoadProfileAsync(string path, CancellationToken cancellationToken = default)
    {
        var loaded = await RuntimeProfileFiles.ReadAsync(path, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        if (!loaded.IsSuccess) { StatusMessage = loaded.Error!.Message; return; }
        var profile = loaded.Value!;
        LlamaPath = profile.LlamaExecutablePath ?? ""; ModelPath = profile.ModelPath ?? "";
        ResticPath = profile.ResticExecutablePath ?? ""; SmartPath = profile.SmartctlExecutablePath ?? "";
        _llamaHash = profile.LlamaSha256; _modelHash = profile.ModelSha256; _resticHash = profile.ResticSha256;
        CdbPath = profile.CdbExecutablePath ?? ""; SymbolDirectory = profile.SymbolDirectory ?? ""; _cdbHash = profile.CdbSha256;
        _configurationRevision++; _backupPlan = null; _restorePlan = null; RefreshModuleCommands(); RefreshInstabilityCommands();
        StatusMessage = "Configuração local carregada. As ferramentas só serão executadas pelas ações selecionadas.";
    }

    public async Task AnalyzeCurrentSessionAsync() => await RunModuleAsync(async token =>
    {
        var session = CurrentSession ?? throw new InvalidOperationException("Execute um diagnóstico antes de analisar.");
        ResetAssistantState("O modelo local está analisando as evidências selecionadas…");
        _assistantSessionId = session.Id;
        AssistantSummary = "O modelo local está analisando as evidências selecionadas…";
        AssistantInsights.Clear();
        StatusMessage = "IA local em execução. O primeiro carregamento pode levar mais tempo.";
        Result<AssistantAnswer> result;
        try
        {
            result = await _modules.Assistant.AnalyzeAsync(session, Question,
                new(LlamaPath, ModelPath, _llamaHash, _modelHash), token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            ResetAssistantState("Análise cancelada. Execute uma nova análise quando desejar.");
            throw;
        }
        if (!result.IsSuccess) { AssistantSummary = result.Error!.Message; StatusMessage = "A análise não foi validada; nenhum texto simulado foi usado."; return; }
        _assistantAnswer = result.Value!;
        Changed(nameof(AssistantHasValidatedAnswer));
        AssistantSummary = _assistantAnswer.Summary;
        foreach (var insight in _assistantAnswer.Insights) AssistantInsights.Add(insight);
        AssistantDetails = $"Modelo: {_assistantAnswer.ModelName}\nEvidências dos fatos selecionados: {string.Join(", ", _assistantAnswer.SummaryEvidenceIds)}\n{string.Join("\n", _assistantAnswer.Limitations)}\nRecibo local: {_assistantAnswer.EvidencePath}";
        StatusMessage = "Seleção recebida. Os valores apresentados vêm das evidências; confira se os fatos respondem à pergunta.";
    });

    private BackupSettings BackupSettings() => new(ResticPath, BackupSource, BackupRepository, PasswordFile, _resticHash);

    public async Task PlanBackupAsync() => await RunModuleAsync(async token =>
    {
        var revision = _configurationRevision;
        var result = await _modules.Backup.PlanAsync(BackupSettings(), token);
        if (revision != _configurationRevision) { BackupPreview = "Os parâmetros mudaram durante a prévia. Prepare outra prévia."; return; }
        _backupPlan = result.Value;
        if (!result.IsSuccess) { BackupPreview = result.Error!.Message; return; }
        var plan = result.Value!;
        BackupPreview = $"Origem: {plan.SourceDirectory}\nRepositório novo: {plan.RepositoryDirectory}\nOperações: inicializar repositório, criar snapshot e ler os dados para conferir integridade.\nA restauração será uma operação separada.\n{string.Join("\n", plan.Limitations)}\nPlano: {plan.Id}";
        StatusMessage = "Prévia preparada. Nenhum backup foi executado.";
    });

    public async Task ExecuteBackupAsync()
    {
        var plan = _backupPlan;
        if (plan is null || IsBusy) return;
        if (!_confirmation.Confirm("opTImus · Confirmar backup", BackupPreview + "\n\nConfirma criar e verificar este backup? Preserve o arquivo de senha para recuperação.")) return;
        if (!ReferenceEquals(plan, _backupPlan)) { BackupPreview = "O plano mudou durante a confirmação. Prepare outra prévia."; return; }
        _backupPlan = null;
        await RunModuleAsync(async token =>
        {
            BackupResult = "Resultado desta operação ainda não confirmado.";
            StatusMessage = "Criando e verificando backup local…";
            var result = await _modules.Backup.ExecuteAsync(plan, plan.Id, token);
            if (result.Value is { } receipt)
            {
                Snapshot = receipt.SnapshotId ?? "";
                BackupResult = $"Snapshot: {receipt.SnapshotId ?? "não identificado"}\nIntegridade: {(receipt.IntegrityVerified ? "verificada" : "não confirmada")}\nRestauração: {(receipt.RestorationVerified ? "verificada" : "pendente")}\nRecibo: {receipt.JournalDirectory}\n{receipt.RecoveryInstruction}\n{string.Join("\n", receipt.Limitations)}";
            }
            if (!result.IsSuccess) { BackupResult += "\n" + result.Error!.Message; StatusMessage = "Backup não confirmado como completo. Consulte o recibo antes de repetir."; }
            else StatusMessage = "Snapshot criado e integridade verificada. Prepare uma restauração de teste.";
        });
    }

    public async Task PlanRestoreAsync() => await RunModuleAsync(async token =>
    {
        var revision = _configurationRevision;
        var result = await _modules.Backup.PlanRestoreAsync(BackupSettings(), Snapshot, RestoreDestination, token);
        if (revision != _configurationRevision) { RestorePreview = "Os parâmetros mudaram durante a prévia. Prepare outra prévia."; return; }
        _restorePlan = result.Value;
        if (!result.IsSuccess) { RestorePreview = result.Error!.Message; return; }
        var plan = result.Value!;
        RestorePreview = $"Snapshot: {plan.SnapshotId}\nDestino novo: {plan.DestinationDirectory}\nOperação: restaurar e verificar o conteúdo. Nenhum arquivo existente será substituído.\n{string.Join("\n", plan.Limitations)}\nPlano: {plan.Id}";
        StatusMessage = "Prévia de restauração preparada; nenhum arquivo restaurado.";
    });

    public async Task ExecuteRestoreAsync()
    {
        var plan = _restorePlan;
        if (plan is null || IsBusy) return;
        if (!_confirmation.Confirm("opTImus · Confirmar restauração", RestorePreview + "\n\nConfirma restaurar para este destino novo?")) return;
        if (!ReferenceEquals(plan, _restorePlan)) { RestorePreview = "O plano mudou durante a confirmação. Prepare outra prévia."; return; }
        _restorePlan = null;
        await RunModuleAsync(async token =>
        {
            RestoreResult = "Resultado desta operação ainda não confirmado.";
            StatusMessage = "Restaurando e verificando arquivos…";
            var result = await _modules.Backup.RestoreAsync(plan, plan.Id, token);
            if (result.Value is { } receipt)
                RestoreResult = $"Destino: {receipt.DestinationDirectory}\nConteúdo: {(receipt.RestorationVerified ? "verificado" : "verificação não confirmada")}\nRecibo: {receipt.JournalDirectory}\n{receipt.RecoveryInstruction}\n{string.Join("\n", receipt.Limitations)}";
            StatusMessage = result.IsSuccess ? "Restauração concluída e conteúdo verificado." : "Restauração não confirmada. Consulte o recibo antes de repetir.";
            if (!result.IsSuccess) RestoreResult += "\n" + result.Error!.Message;
        });
    }

    private async Task ReadSmartAsync() => await RunModuleAsync(async token =>
    {
        var result = await _modules.Smart.ReadAsync(SmartPath, SmartTarget, token);
        await AcceptSmartAsync(result, token);
    });

    private async Task ImportSmartAsync()
    {
        var dialog = new OpenFileDialog { Title = "Importar uma saída JSON do smartctl", Filter = "JSON (*.json)|*.json" };
        if (dialog.ShowDialog() != true) return;
        await RunModuleAsync(async token =>
        {
            var file = await RuntimeProfileFiles.ReadUtf8Async(dialog.FileName, 1_048_576, token);
            if (!file.IsSuccess) { StatusMessage = file.Error!.Message; return; }
            await AcceptSmartAsync(SmartJsonParser.Parse(file.Value!), token);
        });
    }

    private async Task AcceptSmartAsync(Result<DiagnosticEvidence> result, CancellationToken token)
    {
        if (result.Value is { } evidence)
        {
            DiagnosticEvidence[] all = [.. CurrentSession?.Evidence ?? [], evidence];
            var now = DateTimeOffset.UtcNow;
            var session = new DiagnosticSession(Guid.NewGuid(), CurrentSession?.StartedAt ?? now, now,
                all.All(item => item.Status == EvidenceStatus.Complete) ? SessionStatus.Complete : SessionStatus.Partial,
                all, DiagnosticRules.Evaluate(all));
            var saved = await _store.SaveAsync(session, token);
            Display(session);
            await InitializeAsync();
            StatusMessage = !saved.IsSuccess ? saved.Error!.Message : "Evidência SMART adicionada com sua cobertura e limitações.";
        }
        if (!result.IsSuccess) StatusMessage = result.Error!.Message;
    }

    private async Task ImportProfileAsync()
    {
        var dialog = new OpenFileDialog { Title = "Importar perfil local do opTImus", Filter = "Perfil JSON (*.json)|*.json" };
        if (dialog.ShowDialog() == true) await RunModuleAsync(token => LoadProfileAsync(dialog.FileName, token));
    }

    private async Task ExportProfileAsync()
    {
        var dialog = new SaveFileDialog { Title = "Exportar configuração para um arquivo novo", Filter = "Perfil JSON (*.json)|*.json", FileName = $"optimus-perfil-{DateTime.Now:yyyyMMdd-HHmmss}", OverwritePrompt = false, AddExtension = true };
        if (dialog.ShowDialog() != true) return;
        var profile = new RuntimeProfile(Optional(LlamaPath), Optional(ModelPath), Optional(ResticPath), Optional(SmartPath), _llamaHash, _modelHash, _resticHash, Optional(CdbPath), Optional(SymbolDirectory), _cdbHash);
        var result = await RuntimeProfileFiles.WriteNewAsync(profile, dialog.FileName, CancellationToken.None);
        StatusMessage = result.IsSuccess ? $"Perfil salvo em {result.Value}. O arquivo de senha não é incluído." : result.Error!.Message;
    }

    private async Task RunModuleAsync(Func<CancellationToken, Task> action)
    {
        if (IsBusy) return;
        _cancellation = new(); IsBusy = true;
        try { await action(_cancellation.Token); }
        catch (OperationCanceledException error) when (_cancellation.IsCancellationRequested) { StatusMessage = $"Operação cancelada. {error.Message}"; }
        finally { _cancellation.Dispose(); _cancellation = null; IsBusy = false; }
    }

    private void ResetAssistantForSession()
    {
        if (_assistantSessionId is null || _assistantSessionId == CurrentSession?.Id) return;
        ResetAssistantState("A sessão mudou. Execute uma nova análise para usar as evidências atuais.");
    }

    private void ResetAssistantState(string message)
    {
        _assistantAnswer = null;
        _assistantSessionId = null;
        AssistantInsights.Clear();
        AssistantSummary = message;
        AssistantDetails = "A relevância da seleção é experimental. Valores e fontes permanecem vinculados às evidências originais.";
        Changed(nameof(AssistantHasValidatedAnswer));
    }
    private bool Field(ref string field, string value, bool invalidatePlans = true, [CallerMemberName] string? property = null)
    {
        if (field == value) return false;
        field = value ?? ""; Changed(property);
        if (invalidatePlans)
        {
            _configurationRevision++;
            if (_backupPlan is not null) BackupPreview = "Os parâmetros mudaram. Prepare outra prévia antes de executar.";
            if (_restorePlan is not null) RestorePreview = "Os parâmetros mudaram. Prepare outra prévia antes de executar.";
            _backupPlan = null; _restorePlan = null;
        }
        RefreshModuleCommands();
        return true;
    }

    private void RefreshModuleCommands()
    {
        Changed(nameof(CanEditModules));
        RefreshWorkspaceState();
        foreach (var command in new[] { AnalyzeCommand, PlanBackupCommand, ExecuteBackupCommand, PlanRestoreCommand, ExecuteRestoreCommand, ReadSmartCommand, ImportSmartCommand, ImportProfileCommand, ExportProfileCommand }) command?.Notify();
        foreach (var command in new[] { BrowseLlamaCommand, BrowseModelCommand, BrowseResticCommand, BrowseSmartCommand, BrowseBackupSourceCommand, BrowseBackupRepositoryCommand, BrowsePasswordCommand, BrowseRestoreDestinationCommand }) command?.Notify();
    }

    private static string? Optional(string value) => string.IsNullOrWhiteSpace(value) ? null : value;
    private static void PickFile(string title, string filter, Action<string> selected)
    {
        var dialog = new OpenFileDialog { Title = title, Filter = filter };
        if (dialog.ShowDialog() == true) selected(dialog.FileName);
    }
    private static void PickFolder(string title, Action<string> selected)
    {
        var dialog = new OpenFolderDialog { Title = title };
        if (dialog.ShowDialog() == true) selected(dialog.FolderName);
    }
}
