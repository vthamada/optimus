using Optimus.Application;
using Optimus.Desktop;
using Optimus.Domain;

namespace Optimus.Tests;

public sealed class DesktopModuleStateTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedReanalysisClearsPreviousValidatedAnswer(bool cancelled)
    {
        var fixture = new Fixture();
        await fixture.Model.RunDiagnosticAsync();
        await fixture.Model.AnalyzeCurrentSessionAsync();
        Assert.True(fixture.Model.AssistantHasValidatedAnswer);
        Assert.Contains("previous-answer-receipt", fixture.Model.AssistantDetails);

        fixture.Assistant.Handler = (_, _) => Task.FromResult(Result<AssistantAnswer>.Failure(
            cancelled ? ErrorCode.Cancelled : ErrorCode.InvalidResponse, "new analysis failed"));
        var notifications = new List<string?>();
        fixture.Model.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);
        await fixture.Model.AnalyzeCurrentSessionAsync();

        AssertAssistantCleared(fixture.Model);
        Assert.Equal("new analysis failed", fixture.Model.AssistantSummary);
        Assert.Contains(nameof(MainViewModel.AssistantHasValidatedAnswer), notifications);
        Assert.False(fixture.Model.IsBusy);
    }

    [Fact]
    public async Task CancellationWhileReanalyzingClearsPreviousAnswerBeforeCompletion()
    {
        var fixture = new Fixture();
        await fixture.Model.RunDiagnosticAsync();
        await fixture.Model.AnalyzeCurrentSessionAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Assistant.Handler = async (_, token) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException("Cancellation must stop the fake analysis.");
        };

        var analysis = fixture.Model.AnalyzeCurrentSessionAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(fixture.Model.IsBusy);
        AssertAssistantCleared(fixture.Model);
        fixture.Model.RequestCancellation();
        await analysis;

        AssertAssistantCleared(fixture.Model);
        Assert.False(fixture.Model.IsBusy);
        Assert.Contains("cancelada", fixture.Model.StatusMessage);
        Assert.DoesNotContain("analisando", fixture.Model.AssistantSummary);
    }

    [Fact]
    public async Task NewDiagnosticSessionClearsAnswerAndItsEvidenceDetails()
    {
        var fixture = new Fixture();
        await fixture.Model.RunDiagnosticAsync();
        await fixture.Model.AnalyzeCurrentSessionAsync();
        var previousSession = fixture.Model.CurrentSession!.Id;
        Assert.True(fixture.Model.AssistantHasValidatedAnswer);

        await fixture.Model.RunDiagnosticAsync();

        Assert.NotEqual(previousSession, fixture.Model.CurrentSession!.Id);
        AssertAssistantCleared(fixture.Model);
        Assert.Contains("sessão mudou", fixture.Model.AssistantSummary);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailureWithoutReceiptDoesNotReusePreviousVerifiedOperation(bool restore)
    {
        var fixture = new Fixture();
        await PlanAsync(fixture.Model, restore);
        await ExecuteAsync(fixture.Model, restore);
        Assert.Contains(restore ? "Conteúdo: verificado" : "Integridade: verificada",
            OperationResult(fixture.Model, restore));
        Assert.Contains("previous-operation-journal", OperationResult(fixture.Model, restore));

        fixture.Backup.FailExecution = true;
        await PlanAsync(fixture.Model, restore);
        await ExecuteAsync(fixture.Model, restore);

        var displayed = OperationResult(fixture.Model, restore);
        Assert.Contains("new operation failed without receipt", displayed);
        Assert.DoesNotContain("previous-operation-journal", displayed);
        Assert.DoesNotContain("Integridade: verificada", displayed);
        Assert.DoesNotContain("Conteúdo: verificado", displayed);
        Assert.Equal(2, restore ? fixture.Backup.RestoreCalls : fixture.Backup.BackupCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfigurationChangedWhilePreparingCannotReinstallObsoletePlan(bool restore)
    {
        var fixture = new Fixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Backup.BeforePlanCompletion = async () => { entered.SetResult(); await release.Task; };

        var preparing = PlanAsync(fixture.Model, restore);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(fixture.Model.IsBusy);
        fixture.Model.BackupRepository = "changed-repository";
        release.SetResult();
        await preparing;
        await ExecuteAsync(fixture.Model, restore);

        Assert.False(restore ? fixture.Model.ExecuteRestoreCommand.CanExecute(null) : fixture.Model.ExecuteBackupCommand.CanExecute(null));
        Assert.Contains("mudaram durante a prévia", restore ? fixture.Model.RestorePreview : fixture.Model.BackupPreview);
        Assert.Equal(0, fixture.Backup.BackupCalls + fixture.Backup.RestoreCalls);
        Assert.Equal(0, fixture.Confirmation.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfigurationChangedDuringConfirmationCannotExecuteCapturedPlan(bool restore)
    {
        var fixture = new Fixture();
        await PlanAsync(fixture.Model, restore);
        fixture.Confirmation.OnConfirm = () => fixture.Model.BackupRepository = "changed-during-confirmation";

        await ExecuteAsync(fixture.Model, restore);

        Assert.Equal(1, fixture.Confirmation.Calls);
        Assert.Equal(0, fixture.Backup.BackupCalls + fixture.Backup.RestoreCalls);
        Assert.Contains("mudou durante a confirmação", restore ? fixture.Model.RestorePreview : fixture.Model.BackupPreview);
        Assert.False(restore ? fixture.Model.ExecuteRestoreCommand.CanExecute(null) : fixture.Model.ExecuteBackupCommand.CanExecute(null));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DecliningConfirmationDoesNotExecuteOperation(bool restore)
    {
        var fixture = new Fixture();
        await PlanAsync(fixture.Model, restore);
        fixture.Confirmation.Accept = false;

        await ExecuteAsync(fixture.Model, restore);

        Assert.Equal(1, fixture.Confirmation.Calls);
        Assert.Equal(0, fixture.Backup.BackupCalls + fixture.Backup.RestoreCalls);
        Assert.True(restore ? fixture.Model.ExecuteRestoreCommand.CanExecute(null) : fixture.Model.ExecuteBackupCommand.CanExecute(null));
    }

    private static void AssertAssistantCleared(MainViewModel model)
    {
        Assert.False(model.AssistantHasValidatedAnswer);
        Assert.Empty(model.AssistantInsights);
        Assert.DoesNotContain("previous-answer-receipt", model.AssistantDetails);
        Assert.DoesNotContain("previous-model", model.AssistantDetails);
        Assert.DoesNotContain("previous-summary", model.AssistantSummary);
    }

    private static Task PlanAsync(MainViewModel model, bool restore) => restore ? model.PlanRestoreAsync() : model.PlanBackupAsync();
    private static Task ExecuteAsync(MainViewModel model, bool restore) => restore ? model.ExecuteRestoreAsync() : model.ExecuteBackupAsync();
    private static string OperationResult(MainViewModel model, bool restore) => restore ? model.RestoreResult : model.BackupResult;

    private sealed class Fixture
    {
        public FakeAssistant Assistant { get; } = new();
        public FakeBackup Backup { get; } = new();
        public FakeConfirmation Confirmation { get; } = new();
        public MainViewModel Model { get; }

        public Fixture()
        {
            Model = new(new DiagnosticService([new FakeCollector()]), new MemoryStore(), new UnusedExporter(),
                new(Assistant, Backup, new UnusedSmart()), Confirmation)
            {
                LlamaPath = "selected-llama.exe",
                ModelPath = "selected-model.gguf",
                ResticPath = "selected-restic.exe",
                BackupSource = "selected-source",
                BackupRepository = "selected-repository",
                PasswordFile = "selected-password-file",
                Snapshot = new string('b', 64),
                RestoreDestination = "selected-new-restore"
            };
        }
    }

    private sealed class FakeConfirmation : IUserConfirmation
    {
        public int Calls { get; private set; }
        public bool Accept { get; set; } = true;
        public Action? OnConfirm { get; set; }
        public bool Confirm(string title, string message) { Calls++; OnConfirm?.Invoke(); return Accept; }
    }

    private sealed class FakeAssistant : IAssistantService
    {
        public Func<DiagnosticSession, CancellationToken, Task<Result<AssistantAnswer>>> Handler { get; set; } = (session, _) =>
            Task.FromResult(Result<AssistantAnswer>.Success(new(session.Id, DateTimeOffset.UtcNow, "previous-summary",
                [session.Evidence[0].Id], [new("previous-insight", "fixture interpretation", [session.Evidence[0].Id])],
                ["fixture-only coverage"], "previous-model", new(TimeSpan.Zero), "previous-answer-receipt")));

        public Task<Result<AssistantAnswer>> AnalyzeAsync(DiagnosticSession session, string question,
            LocalAiSettings settings, CancellationToken cancellationToken) => Handler(session, cancellationToken);
    }

    private sealed class FakeBackup : IBackupService
    {
        public bool FailExecution { get; set; }
        public int BackupCalls { get; private set; }
        public int RestoreCalls { get; private set; }
        public Func<Task>? BeforePlanCompletion { get; set; }

        public async Task<Result<BackupPlan>> PlanAsync(BackupSettings settings, CancellationToken cancellationToken)
        {
            var plan = new BackupPlan(Guid.NewGuid(), DateTimeOffset.UtcNow, settings, settings.SourceDirectory,
                settings.RepositoryDirectory, settings.ExecutablePath, settings.PasswordFilePath, "fixture-sha", []);
            if (BeforePlanCompletion is { } wait) await wait();
            return Result<BackupPlan>.Success(plan);
        }

        public async Task<Result<RestorePlan>> PlanRestoreAsync(BackupSettings settings, string snapshotId,
            string destinationDirectory, CancellationToken cancellationToken)
        {
            var plan = new RestorePlan(Guid.NewGuid(), DateTimeOffset.UtcNow, settings, snapshotId, settings.RepositoryDirectory,
                destinationDirectory, settings.ExecutablePath, settings.PasswordFilePath, "fixture-sha", []);
            if (BeforePlanCompletion is { } wait) await wait();
            return Result<RestorePlan>.Success(plan);
        }

        public Task<Result<BackupReceipt>> ExecuteAsync(BackupPlan plan, Guid confirmationId, CancellationToken cancellationToken)
        {
            BackupCalls++;
            Assert.Equal(plan.Id, confirmationId);
            return Task.FromResult(FailExecution
                ? Result<BackupReceipt>.Failure(ErrorCode.InvalidConfiguration, "new operation failed without receipt")
                : Result<BackupReceipt>.Success(new(plan.Id, BackupState.IntegrityVerified, plan.SourceDirectory,
                    plan.RepositoryDirectory, new string('b', 64), true, false, "previous-operation-journal", [], [])));
        }

        public Task<Result<RestoreReceipt>> RestoreAsync(RestorePlan plan, Guid confirmationId, CancellationToken cancellationToken)
        {
            RestoreCalls++;
            Assert.Equal(plan.Id, confirmationId);
            return Task.FromResult(FailExecution
                ? Result<RestoreReceipt>.Failure(ErrorCode.InvalidConfiguration, "new operation failed without receipt")
                : Result<RestoreReceipt>.Success(new(plan.Id, RestoreState.RestoredAndVerified, plan.SnapshotId,
                    plan.DestinationDirectory, "previous-operation-journal", [], [], RestorationVerified: true)));
        }
    }

    private sealed class FakeCollector : IDiagnosticCollector
    {
        public string Id => "desktop-state-fixture";
        public string DisplayName => "Selected fixture";
        public Task<IReadOnlyList<DiagnosticEvidence>> CollectAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DiagnosticEvidence>>([new(Guid.NewGuid(), Id, DisplayName, "fixture",
                DateTimeOffset.UtcNow, EvidenceStatus.Complete, new("fixture-only", 1), [], [])]);
    }

    private sealed class MemoryStore : ISessionStore
    {
        private readonly Dictionary<Guid, DiagnosticSession> _sessions = [];
        public Task<Result<bool>> SaveAsync(DiagnosticSession session, CancellationToken cancellationToken)
        {
            _sessions[session.Id] = session;
            return Task.FromResult(Result<bool>.Success(true));
        }
        public Task<Result<IReadOnlyList<SessionSummary>>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Result<IReadOnlyList<SessionSummary>>.Success(_sessions.Values.Select(session =>
                new SessionSummary(session.Id, session.StartedAt, session.Status, session.Findings.Count)).ToArray()));
        public Task<Result<DiagnosticSession>> GetAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult(_sessions.TryGetValue(id, out var session) ? Result<DiagnosticSession>.Success(session)
                : Result<DiagnosticSession>.Failure(ErrorCode.InvalidData, "No fixture session with that identity."));
    }

    private sealed class UnusedExporter : IReportExporter
    {
        public Task<Result<string>> ExportAsync(DiagnosticSession session, string path, ReportFormat format,
            CancellationToken cancellationToken) => throw new InvalidOperationException("Export is outside these tests.");
    }

    private sealed class UnusedSmart : ISmartService
    {
        public Task<Result<DiagnosticEvidence>> ReadAsync(string executablePath, string target,
            CancellationToken cancellationToken) => throw new InvalidOperationException("SMART is outside these tests.");
    }
}
