using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Microsoft.Win32;
using Optimus.Application;
using Optimus.Domain;

namespace Optimus.Desktop;

public sealed partial class MainViewModel : INotifyPropertyChanged
{
    private readonly DiagnosticService _diagnostics;
    private readonly DiagnosticService? _inventory;
    private readonly ISessionStore _store;
    private readonly IReportExporter _exporter;
    private CancellationTokenSource? _cancellation;
    private bool _isBusy;
    private string _statusMessage = "Pronto para iniciar. A coleta é consultiva e não exige elevação.";
    private DiagnosticSession? _currentSession;
    private SessionSummary? _selectedHistory;

    public MainViewModel(DiagnosticService diagnostics, ISessionStore store, IReportExporter exporter, ModuleServices modules,
        IUserConfirmation? confirmation = null, DiagnosticService? inventory = null, MaintenanceServices? maintenance = null,
        InstabilityServices? instability = null)
    {
        _diagnostics = diagnostics; _store = store; _exporter = exporter;
        _inventory = inventory;
        _confirmation = confirmation ?? new WindowConfirmation();
        InitializeModules(modules);
        InitializeMaintenance(maintenance);
        InitializeInstability(instability);
        DiagnoseCommand = new AsyncCommand(RunDiagnosticAsync, () => !IsBusy, SetError);
        InventoryCommand = new AsyncCommand(async () => { await RunInventoryAsync(); }, () => !IsBusy && _inventory is not null, SetError);
        CancelCommand = new ActionCommand(RequestCancellation, () => _cancellation is { IsCancellationRequested: false });
        ExportCommand = new AsyncCommand(ExportAsync, () => !IsBusy && CurrentSession is not null, SetError);
        RefreshHistoryCommand = new AsyncCommand(InitializeAsync, () => !IsBusy, SetError);
        OpenHistoryCommand = new AsyncCommand(OpenHistoryAsync, () => !IsBusy && SelectedHistory is not null, SetError);
        InitializeWorkspace();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<DiagnosticEvidence> Evidence { get; } = [];
    public ObservableCollection<DiagnosticEvidence> InventoryEvidence { get; } = [];
    public ObservableCollection<DiagnosticFinding> Findings { get; } = [];
    public ObservableCollection<SessionSummary> History { get; } = [];
    public AsyncCommand DiagnoseCommand { get; }
    public AsyncCommand InventoryCommand { get; }
    public ActionCommand CancelCommand { get; }
    public AsyncCommand ExportCommand { get; }
    public AsyncCommand RefreshHistoryCommand { get; }
    public AsyncCommand OpenHistoryCommand { get; }
    public DiagnosticSession? CurrentSession => _currentSession;
    public bool IsBusy { get => _isBusy; private set { _isBusy = value; Changed(); RefreshCommands(); } }
    public string StatusMessage { get => _statusMessage; private set { _statusMessage = value; Changed(); } }
    public string SessionDate => CurrentSession?.StartedAt.ToLocalTime().ToString("dd/MM · HH:mm") ?? "Ainda não realizada";
    public string SessionState => CurrentSession?.StatusText ?? "Escolha Iniciar diagnóstico";
    public string EvidenceCount => CurrentSession?.Evidence.Count.ToString() ?? "—";
    public string FindingCount => CurrentSession?.Findings.Count.ToString() ?? "—";
    public string Guidance => CurrentSession is null
        ? "Inicie uma coleta para observar os dados do computador. Não são executados reparos, exclusões, antivírus ou testes físicos."
        : "As recomendações abaixo seguem regras de triagem e os dados desta coleta. Ausência de alerta não comprova a saúde completa do equipamento. Exporte o relatório para consultar as evidências originais.";
    public SessionSummary? SelectedHistory { get => _selectedHistory; set { _selectedHistory = value; Changed(); RefreshCommands(); } }

    public async Task InitializeAsync()
    {
        var result = await _store.ListAsync(CancellationToken.None);
        if (!result.IsSuccess) { StatusMessage = $"Histórico indisponível: {result.Error!.Message}"; return; }
        History.Clear();
        foreach (var item in result.Value!) History.Add(item);
    }

    public async Task RunDiagnosticAsync()
    {
        if (IsBusy) return;
        _cancellation = new();
        IsBusy = true;
        try
        {
            var progress = new Progress<string>(message => StatusMessage = message);
            var session = await _diagnostics.RunAsync(progress, _cancellation.Token);
            await PersistCollectionAsync(session, _cancellation.Token,
                "Coleta salva no histórico local. Nenhuma operação de manutenção foi executada.");
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
            StatusMessage = "Coleta cancelada. A sessão anterior, se houver, permanece disponível.";
        }
        finally
        {
            _cancellation.Dispose();
            _cancellation = null;
            IsBusy = false;
        }
    }

    public async Task<bool> RunInventoryAsync()
    {
        if (_inventory is null) { StatusMessage = "O inventário não está configurado nesta composição."; return false; }
        bool applied = false;
        await RunModuleAsync(async token =>
        {
            StatusMessage = "Consultando rede, energia, programas e entradas de inicialização…";
            var previous = CurrentSession;
            var collected = await _inventory.RunAsync(null, token);
            var merged = EvidenceSessionUpdates.ReplaceOwned(previous, collected, InventoryCollectors.All);
            if (!merged.IsSuccess) { StatusMessage = merged.Error!.Message; return; }
            var session = merged.Value!;
            await PersistCollectionAsync(session, token,
                "Inventário consultado e salvo. As demais evidências conservam suas datas de coleta.");
            applied = true;
        });
        return applied;
    }

    private async Task<Result<bool>> PersistCollectionAsync(DiagnosticSession session, CancellationToken token, string savedMessage)
    {
        var saved = await _store.SaveAsync(session, token);
        Display(session);
        await InitializeAsync();
        StatusMessage = saved.IsSuccess ? savedMessage : $"Dados disponíveis para exportação; gravação não confirmada: {saved.Error!.Message}";
        if (token.IsCancellationRequested)
            StatusMessage += saved.IsSuccess
                ? " Cancelamento recebido após a gravação confirmada; a sessão salva foi preservada."
                : " Cancelamento recebido na finalização; os dados observados foram preservados na interface.";
        return saved;
    }

    public void RequestCancellation()
    {
        var cancellation = _cancellation;
        if (cancellation is null || cancellation.IsCancellationRequested) return;
        StatusMessage = "Cancelamento solicitado. Aguardando a consulta em andamento finalizar com segurança…";
        // Cancellation can synchronously complete the operation and publish its terminal status.
        // Publish the request first so it cannot overwrite that completed result afterwards.
        cancellation.Cancel();
        RefreshCommands();
    }

    private async Task OpenHistoryAsync()
    {
        if (SelectedHistory is null || IsBusy) return;
        IsBusy = true;
        try
        {
            var result = await _store.GetAsync(SelectedHistory.Id, CancellationToken.None);
            if (!result.IsSuccess) { StatusMessage = result.Error!.Message; return; }
            Display(result.Value!);
            SelectedPage = WorkspacePage.Diagnostics;
            StatusMessage = "Sessão histórica aberta. Confira a data: os dados não foram coletados novamente.";
        }
        finally { IsBusy = false; }
    }

    private async Task ExportAsync()
    {
        if (CurrentSession is null || IsBusy) return;
        var dialog = new SaveFileDialog
        {
            Title = "Salvar um novo relatório do opTImus",
            FileName = $"optimus-{CurrentSession.StartedAt.ToLocalTime():yyyyMMdd-HHmmss}",
            Filter = "Relatório HTML (*.html)|*.html|Evidências JSON (*.json)|*.json",
            AddExtension = true,
            OverwritePrompt = false
        };
        if (dialog.ShowDialog() != true) return;
        IsBusy = true;
        try
        {
            var result = await _exporter.ExportAsync(CurrentSession, dialog.FileName,
                dialog.FilterIndex == 2 ? ReportFormat.Json : ReportFormat.Html, CancellationToken.None);
            StatusMessage = result.IsSuccess ? $"Relatório salvo em {result.Value}" : result.Error!.Message;
        }
        finally { IsBusy = false; }
    }

    private void Display(DiagnosticSession session)
    {
        _currentSession = session;
        ResetAssistantForSession();
        Evidence.Clear(); Findings.Clear(); InventoryEvidence.Clear(); MaintenanceEvidence.Clear();
        foreach (var item in session.Evidence)
        {
            Evidence.Add(item);
            if (InventoryCollectors.Contains(item.CollectorId)) InventoryEvidence.Add(item);
            if (MaintenanceCollectors.Contains(item.CollectorId)) MaintenanceEvidence.Add(item);
        }
        foreach (var item in session.Findings) Findings.Add(item);
        DisplayInstabilityEvidence(session);
        foreach (var property in new[] { nameof(CurrentSession), nameof(SessionDate), nameof(SessionState), nameof(EvidenceCount), nameof(FindingCount), nameof(Guidance) }) Changed(property);
        RefreshCommands();
    }

    private void SetError(Exception error) => StatusMessage = $"Não foi possível concluir: {error.Message}";
    private void Changed([CallerMemberName] string? property = null) => PropertyChanged?.Invoke(this, new(property));
    private void RefreshCommands()
    {
        DiagnoseCommand.Notify(); InventoryCommand.Notify(); CancelCommand.Notify(); ExportCommand.Notify(); RefreshHistoryCommand.Notify(); OpenHistoryCommand.Notify();
        RefreshModuleCommands();
        RefreshMaintenanceCommands();
        RefreshInstabilityCommands();
    }
}

public sealed class AsyncCommand(Func<Task> action, Func<bool> canExecute, Action<Exception> onError) : ICommand
{
    private bool _running;
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => !_running && canExecute();
    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;
        _running = true; Notify();
        try { await action(); }
        catch (Exception error) { onError(error); }
        finally { _running = false; Notify(); }
    }
    public void Notify() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public sealed class ActionCommand(Action action, Func<bool> canExecute) : ICommand
{
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => canExecute();
    public void Execute(object? parameter) { if (CanExecute(parameter)) action(); }
    public void Notify() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
