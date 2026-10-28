using Optimus.Application;
using Optimus.Desktop;
using Optimus.Domain;
using Optimus.Infrastructure;

namespace Optimus.Tests;

public sealed class DesktopWorkspaceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationRequestPrecedesSynchronousCompletion(bool inventory)
    {
        var collector = new ProbeCollector();
        var store = new MemoryStore();
        var unused = new UnusedTools();
        var model = new MainViewModel(new([collector]), store, unused, new(unused, unused, unused), unused,
            inventory: new([collector]));
        string? statusDuringCancellation = null;
        collector.Handler = token =>
        {
            var pending = new TaskCompletionSource<IReadOnlyList<DiagnosticEvidence>>();
            token.Register(() =>
            {
                statusDuringCancellation = model.StatusMessage;
                pending.TrySetCanceled(token);
            });
            return pending.Task;
        };
        var previous = SynchronizationContext.Current;
        Task running;
        try
        {
            SynchronizationContext.SetSynchronizationContext(new ImmediateContext());
            running = inventory ? model.RunInventoryAsync() : model.RunDiagnosticAsync();
            model.RequestCancellation();
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
        await running;
        Assert.Contains("Cancelamento solicitado", statusDuringCancellation);
        Assert.Contains("cancelada", model.StatusMessage);
        Assert.False(model.IsBusy);
        Assert.Empty(store.Saved);
        var completedMessage = model.StatusMessage;
        model.RequestCancellation();
        Assert.Equal(completedMessage, model.StatusMessage);
    }

    private sealed class ImmediateContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback callback, object? state) => callback(state);
    }

    [Fact]
    public async Task NavigationAndConfigurationNeverCollectOrExecuteTools()
    {
        var collector = new ProbeCollector();
        var store = new MemoryStore();
        var model = Create(collector, store);
        Assert.Equal(WorkspacePage.Home, model.SelectedPage);
        Assert.False(model.HasSession);
        Assert.True(model.StartDiagnosticCommand.CanExecute(null));
        await model.InitializeAsync();
        foreach (var page in Enum.GetValues<WorkspacePage>())
        {
            Assert.True(model.NavigateCommand.CanExecute(page));
            model.NavigateCommand.Execute(page);
            Assert.Equal(page, model.SelectedPage);
            Assert.False(string.IsNullOrWhiteSpace(model.PageTitle));
        }
        model.NavigateCommand.Execute("Diagnostics");
        model.SelectedPage = (WorkspacePage)999;
        Assert.Equal(WorkspacePage.Settings, model.SelectedPage);
        model.LlamaPath = "not-a-real-executable.exe";
        model.ModelPath = "not-a-real-model.gguf";
        model.ResticPath = "not-a-real-restic.exe";
        Assert.Equal(0, collector.Calls);
        Assert.Empty(store.Saved);
        Assert.Equal("Caminhos informados", model.AiConfigurationStatus);
        Assert.Contains("Execute um diagnóstico", model.AssistantRequirement);
        Assert.False(model.AnalyzeCommand.CanExecute(null));
    }

    [Fact]
    public async Task AssistantExplainsMissingInputsAndHistoryOpensResultsWithoutRecollection()
    {
        var collector = new ProbeCollector();
        var store = new MemoryStore();
        var model = Create(collector, store);
        Assert.Contains("Configuração", model.AssistantRequirement);
        model.LlamaPath = " "; model.ModelPath = "model.gguf";
        Assert.False(model.AnalyzeCommand.CanExecute(null));
        model.LlamaPath = "llama-completion.exe";
        Assert.Contains("Execute um diagnóstico", model.AssistantRequirement);
        await model.RunDiagnosticAsync();
        Assert.True(model.HasSession);
        Assert.True(model.AnalyzeCommand.CanExecute(null));
        Assert.Contains("validados", model.AssistantRequirement);
        Assert.Equal(1, collector.Calls);
        model.SelectedPage = WorkspacePage.History;
        model.SelectedHistory = Assert.Single(model.History);
        model.OpenHistoryCommand.Execute(null);
        Assert.Equal(WorkspacePage.Diagnostics, model.SelectedPage);
        Assert.Equal(1, collector.Calls);
        Assert.Single(store.Saved);
    }

    [Fact]
    public void QuerySearchAcceptsAccentsAndCannotExecuteHiddenSelection()
    {
        var model = Create(new(), new(), Composition.CreateMaintenance(Path.GetTempPath()));
        model.MaintenanceSearch = "MEMORIA";
        Assert.NotEmpty(model.FilteredMaintenanceQueries);
        Assert.All(model.FilteredMaintenanceQueries, choice => Assert.Contains("memória", choice.Label.ToLowerInvariant()));
        model.MaintenanceSearch = "bitlocker";
        Assert.Equal(MaintenanceQuery.BitLocker, Assert.Single(model.FilteredMaintenanceQueries).Query);
        Assert.Equal(MaintenanceQuery.BitLocker, model.SelectedMaintenanceQuery.Query);
        Assert.True(model.RunMaintenanceQueryCommand.CanExecute(null));
        model.MaintenanceSearch = "no-query-matches-this";
        Assert.False(model.HasMaintenanceMatches);
        Assert.False(model.HasVisibleMaintenanceSelection);
        Assert.False(model.RunMaintenanceQueryCommand.CanExecute(null));
        model.MaintenanceSearch = "";
        Assert.True(model.HasVisibleMaintenanceSelection);
        Assert.True(model.RunMaintenanceQueryCommand.CanExecute(null));
    }

    [Fact]
    public void MissingOptionalInputsExplainHowToContinueWithoutAiOrDump()
    {
        var model = Create(new(), new());
        model.UseInvestigationAi = true;
        Assert.Contains("Desmarque a IA", model.InvestigationRequirement);
        model.UseInvestigationAi = false;
        model.IncludeDump = true;
        Assert.Contains("desmarque", model.InvestigationRequirement);
        model.IncludeDump = false;
        Assert.Contains("Pronto", model.InvestigationRequirement);
        Assert.Contains("cdb.exe", model.DumpRequirement);
        Assert.Contains("restic", model.BackupRequirement);
        Assert.Contains("prévia", model.BackupConfirmationHint);
    }

    private static MainViewModel Create(ProbeCollector collector, MemoryStore store, MaintenanceServices? maintenance = null)
    {
        var unused = new UnusedTools();
        return new(new([collector]), store, unused, new(unused, unused, unused), unused, maintenance: maintenance);
    }

    private sealed class ProbeCollector : IDiagnosticCollector
    {
        public string Id => "fixture.system";
        public string DisplayName => "Fixture source";
        public int Calls { get; private set; }
        public Func<CancellationToken, Task<IReadOnlyList<DiagnosticEvidence>>>? Handler { get; set; }
        public Task<IReadOnlyList<DiagnosticEvidence>> CollectAsync(CancellationToken cancellationToken)
        {
            Calls++;
            if (Handler is not null) return Handler(cancellationToken);
            return Task.FromResult<IReadOnlyList<DiagnosticEvidence>>([new(Guid.NewGuid(), Id, DisplayName, "Synthetic fixture",
                DateTimeOffset.UtcNow, EvidenceStatus.Partial, new("Not physical-provider proof", 0), [], [])]);
        }
    }
    private sealed class MemoryStore : ISessionStore
    {
        public List<DiagnosticSession> Saved { get; } = [];
        public Task<Result<bool>> SaveAsync(DiagnosticSession session, CancellationToken cancellationToken)
        { Saved.Add(session); return Task.FromResult(Result<bool>.Success(true)); }
        public Task<Result<IReadOnlyList<SessionSummary>>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Result<IReadOnlyList<SessionSummary>>.Success(Saved.Select(session =>
                new SessionSummary(session.Id, session.StartedAt, session.Status, session.Findings.Count)).ToArray()));
        public Task<Result<DiagnosticSession>> GetAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult(Result<DiagnosticSession>.Success(Saved.Single(session => session.Id == id)));
    }
    private sealed class UnusedTools : IAssistantService, IBackupService, ISmartService, IReportExporter, IUserConfirmation
    {
        public Task<Result<AssistantAnswer>> AnalyzeAsync(DiagnosticSession session, string question, LocalAiSettings settings, CancellationToken cancellationToken) => throw new InvalidOperationException("Must not infer.");
        public Task<Result<BackupPlan>> PlanAsync(BackupSettings settings, CancellationToken cancellationToken) => throw new InvalidOperationException("Must not prepare backup.");
        public Task<Result<BackupReceipt>> ExecuteAsync(BackupPlan plan, Guid confirmationId, CancellationToken cancellationToken) => throw new InvalidOperationException("Must not back up.");
        public Task<Result<RestorePlan>> PlanRestoreAsync(BackupSettings settings, string snapshotId, string destinationDirectory, CancellationToken cancellationToken) => throw new InvalidOperationException("Must not prepare restoration.");
        public Task<Result<RestoreReceipt>> RestoreAsync(RestorePlan plan, Guid confirmationId, CancellationToken cancellationToken) => throw new InvalidOperationException("Must not restore.");
        public Task<Result<DiagnosticEvidence>> ReadAsync(string executablePath, string target, CancellationToken cancellationToken) => throw new InvalidOperationException("Must not query SMART.");
        public Task<Result<string>> ExportAsync(DiagnosticSession session, string path, ReportFormat format, CancellationToken cancellationToken) => throw new InvalidOperationException("Must not export.");
        public bool Confirm(string title, string message) => throw new InvalidOperationException("Must not confirm any action.");
    }
}
