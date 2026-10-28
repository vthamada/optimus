using Optimus.Application;
using Optimus.Desktop;
using Optimus.Domain;

namespace Optimus.Tests;

public sealed class DesktopMaintenanceTests
{
    private static readonly DateTimeOffset Earlier = DateTimeOffset.Parse("2026-10-20T01:02:03Z");
    private static readonly DateTimeOffset Later = DateTimeOffset.Parse("2026-10-27T01:02:03Z");

    [Fact]
    public async Task StartupHistoryAndBasicDiagnosticDoNotInvokeMaintenance()
    {
        var fixture = new Fixture();
        await fixture.Model.InitializeAsync();
        await fixture.DiagnoseAsync();

        Assert.Equal(0, fixture.Defender.Calls + fixture.Devices.Calls);
        Assert.Equal(0, fixture.Integrity.PlanCalls + fixture.Integrity.ExecuteCalls);
        Assert.Equal(0, fixture.Confirmation.Calls);
        Assert.Same(fixture.Original, Assert.Single(fixture.Model.CurrentSession!.Evidence));
        Assert.Empty(fixture.Model.MaintenanceEvidence);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RefreshReplacesOnlyTheSelectedMaintenanceSourceAndPreservesOtherIdentityAndTime(bool devices)
    {
        var fixture = new Fixture();
        await fixture.DiagnoseAsync();
        Assert.True(await fixture.Model.RunDefenderStatusAsync());
        Assert.True(await fixture.Model.RunDeviceInventoryAsync());
        var previous = fixture.Model.CurrentSession!;
        var selected = devices ? MaintenanceCollectors.DeviceDrivers : MaintenanceCollectors.Defender;
        var previousOwned = previous.Evidence.Single(item => item.CollectorId == selected);
        var preserved = previous.Evidence.Single(item => MaintenanceCollectors.Contains(item.CollectorId) && item.CollectorId != selected);

        Assert.True(await QueryAsync(fixture.Model, devices));

        var refreshed = fixture.Model.CurrentSession!;
        Assert.NotEqual(previous.Id, refreshed.Id);
        Assert.Same(fixture.Original, refreshed.Evidence.Single(item => item.Id == fixture.Original.Id));
        Assert.Equal(Earlier, fixture.Original.CapturedAt);
        Assert.Null(Assert.Single(fixture.Original.Facts).TextValue);
        Assert.Null(Assert.Single(fixture.Original.Facts).NumberValue);
        Assert.Same(preserved, refreshed.Evidence.Single(item => item.Id == preserved.Id));
        Assert.DoesNotContain(refreshed.Evidence, item => item.Id == previousOwned.Id);
        Assert.Equal(Later, refreshed.Evidence.Single(item => item.CollectorId == selected).CapturedAt);
        Assert.Equal(2, fixture.Model.MaintenanceEvidence.Count);
        Assert.Equal(4, fixture.Store.Saved.Count);
        Assert.Equal(0, fixture.Confirmation.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ACollectorOutsideTheSelectedScopeCannotBePublishedAsTheRequestedQuery(bool devices)
    {
        var fixture = new Fixture(wrongScope: true);
        await fixture.DiagnoseAsync();
        var previous = fixture.Model.CurrentSession;

        var applied = await QueryAsync(fixture.Model, devices);

        Assert.False(applied);
        Assert.Same(previous, fixture.Model.CurrentSession);
        Assert.Single(fixture.Store.Saved);
        Assert.Empty(fixture.Model.MaintenanceEvidence);
        Assert.Contains("cobertura", fixture.Model.StatusMessage);
    }

    [Fact]
    public async Task APlanWithoutAnElevatedContextNeverRequestsConfirmationOrExecution()
    {
        var fixture = new Fixture();
        fixture.Integrity.Elevated = false;

        await fixture.Model.PlanIntegrityAsync();
        await fixture.Model.ExecuteIntegrityAsync();

        Assert.True(fixture.Model.IntegrityHasPreparedPlan);
        Assert.False(fixture.Model.ExecuteIntegrityCommand.CanExecute(null));
        Assert.Equal(1, fixture.Integrity.PlanCalls);
        Assert.Equal(0, fixture.Integrity.ExecuteCalls);
        Assert.Equal(0, fixture.Confirmation.Calls);
        Assert.Contains("elevação automática", fixture.Model.IntegrityResult);
    }

    [Fact]
    public async Task ChangingSelectionWhilePreparingCannotInstallAnObsoletePlan()
    {
        var fixture = new Fixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Integrity.BeforePlanCompletion = async () => { entered.SetResult(); await release.Task; };

        var preparing = fixture.Model.PlanIntegrityAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Model.SelectedIntegrityChoice = fixture.Model.IntegrityChoices.Single(item => item.Operation == IntegrityOperation.DismCheckHealth);
        release.SetResult();
        await preparing;
        await fixture.Model.ExecuteIntegrityAsync();

        Assert.False(fixture.Model.IntegrityHasPreparedPlan);
        Assert.Equal(0, fixture.Integrity.ExecuteCalls);
        Assert.Equal(0, fixture.Confirmation.Calls);
        Assert.Contains("mudou durante a prévia", fixture.Model.IntegrityPreview);
    }

    [Fact]
    public async Task ChangingSelectionDuringConfirmationCannotExecuteTheCapturedOldPlan()
    {
        var fixture = new Fixture();
        await fixture.Model.PlanIntegrityAsync();
        fixture.Confirmation.OnConfirm = () => fixture.Model.SelectedIntegrityChoice = fixture.Model.IntegrityChoices.Single(item => item.Operation == IntegrityOperation.DismScanHealth);

        await fixture.Model.ExecuteIntegrityAsync();

        Assert.Equal(1, fixture.Confirmation.Calls);
        Assert.Equal(0, fixture.Integrity.ExecuteCalls);
        Assert.False(fixture.Model.IntegrityHasPreparedPlan);
        Assert.Contains("mudou durante a confirmação", fixture.Model.IntegrityPreview);
    }

    [Fact]
    public async Task DeclinedConfirmationDoesNotConsumeOrExecuteThePreparedPlan()
    {
        var fixture = new Fixture();
        await fixture.Model.PlanIntegrityAsync();
        fixture.Confirmation.Accept = false;

        await fixture.Model.ExecuteIntegrityAsync();

        Assert.Equal(1, fixture.Confirmation.Calls);
        Assert.Equal(0, fixture.Integrity.ExecuteCalls);
        Assert.True(fixture.Model.IntegrityHasPreparedPlan);
    }

    [Fact]
    public async Task FailureWithoutAReceiptDoesNotKeepThePreviousCompletionText()
    {
        var fixture = new Fixture();
        await fixture.Model.PlanIntegrityAsync();
        await fixture.Model.ExecuteIntegrityAsync();
        Assert.Contains("fixture-completed-journal", fixture.Model.IntegrityResult);
        Assert.Contains("CommandCompleted", fixture.Model.IntegrityResult);
        fixture.Integrity.FailWithoutReceipt = true;
        await fixture.Model.PlanIntegrityAsync();

        await fixture.Model.ExecuteIntegrityAsync();

        Assert.Contains("new attempt failed without receipt", fixture.Model.IntegrityResult);
        Assert.DoesNotContain("fixture-completed-journal", fixture.Model.IntegrityResult);
        Assert.DoesNotContain("CommandCompleted", fixture.Model.IntegrityResult);
        Assert.Equal(2, fixture.Integrity.ExecuteCalls);
        Assert.Single(fixture.Store.Saved);
    }

    [Fact]
    public async Task CompletedOperationAndItsReceiptRemainVisibleWhenDiagnosticHistoryCannotBeSaved()
    {
        var fixture = new Fixture();
        await fixture.DiagnoseAsync();
        var previous = fixture.Model.CurrentSession;
        DiagnosticSession? attempted = null;
        fixture.Store.SaveOutcome = session =>
        {
            attempted = session;
            return Result<bool>.Failure(ErrorCode.StorageFailure, "memory-store-failure");
        };
        await fixture.Model.PlanIntegrityAsync();

        await fixture.Model.ExecuteIntegrityAsync();

        Assert.Contains("CommandCompleted", fixture.Model.IntegrityResult);
        Assert.Contains("Reparo executado: não", fixture.Model.IntegrityResult);
        Assert.Contains("fixture-completed-journal", fixture.Model.IntegrityResult);
        Assert.Contains("Comando encerrado", fixture.Model.StatusMessage);
        Assert.Contains("gravação no histórico de diagnósticos não foi confirmada", fixture.Model.StatusMessage);
        Assert.Contains("memory-store-failure", fixture.Model.StatusMessage);
        Assert.DoesNotContain("Evidências da manutenção salvas", fixture.Model.StatusMessage);
        var attemptedSession = Assert.IsType<DiagnosticSession>(attempted);
        Assert.NotSame(previous, fixture.Model.CurrentSession);
        Assert.Same(attemptedSession, fixture.Model.CurrentSession);
        Assert.Same(fixture.Integrity.LastEvidence, Assert.Single(fixture.Model.MaintenanceEvidence));
        Assert.DoesNotContain(fixture.Store.Saved, session => session.Id == attemptedSession.Id);
        Assert.Single(fixture.Store.Saved);
        Assert.Equal(1, fixture.Integrity.ExecuteCalls);
        Assert.False(fixture.Model.IsBusy);
    }

    [Fact]
    public async Task BusyOperationIsSerializedAndLateCancellationPreservesTheDurableReceiptAndFacts()
    {
        var fixture = new Fixture();
        await fixture.Model.PlanIntegrityAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Integrity.BeforeExecutionCompletion = async () => { entered.SetResult(); await release.Task; };
        fixture.Integrity.CancelWithReceipt = true;
        fixture.Integrity.OnReturningReceipt = fixture.Model.RequestCancellation;

        var running = fixture.Model.ExecuteIntegrityAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(fixture.Model.IsBusy);
        Assert.False(await fixture.Model.RunDefenderStatusAsync());
        Assert.False(fixture.Model.PlanIntegrityCommand.CanExecute(null));
        release.SetResult();
        await running;

        Assert.Equal(0, fixture.Defender.Calls);
        Assert.False(fixture.Model.IsBusy);
        Assert.False(fixture.Model.IntegrityHasPreparedPlan);
        Assert.Equal(1, fixture.Integrity.ExecuteCalls);
        Assert.Contains("fixture-completed-journal", fixture.Model.IntegrityResult);
        Assert.Contains("cancelled after receipt", fixture.Model.IntegrityResult);
        Assert.Contains("Cancelamento recebido", fixture.Model.StatusMessage);
        Assert.Same(fixture.Integrity.LastEvidence, Assert.Single(fixture.Model.MaintenanceEvidence));
        Assert.Same(fixture.Integrity.LastEvidence, Assert.Single(Assert.Single(fixture.Store.Saved).Evidence));
    }

    private static Task<bool> QueryAsync(MainViewModel model, bool devices) => devices ? model.RunDeviceInventoryAsync() : model.RunDefenderStatusAsync();
    private static DiagnosticEvidence Evidence(string collector, DateTimeOffset captured, string? value = "fixture") =>
        new(Guid.NewGuid(), collector, collector, "fixture-only source", captured, EvidenceStatus.Complete,
            new("Contract fixture, not native Windows verification", 1), [new("sourceValue", "Original field", value)], []);

    private sealed class Fixture
    {
        public DiagnosticEvidence Original { get; } = Evidence("windows.system", Earlier, null);
        public ProbeCollector Defender { get; }
        public ProbeCollector Devices { get; }
        public ProbeIntegrity Integrity { get; } = new();
        public MemoryStore Store { get; } = new();
        public Confirmation Confirmation { get; } = new();
        public MainViewModel Model { get; }
        public Fixture(bool wrongScope = false)
        {
            Defender = new(wrongScope ? "foreign.defender" : MaintenanceCollectors.Defender);
            Devices = new(wrongScope ? "foreign.devices" : MaintenanceCollectors.DeviceDrivers);
            var unused = new UnusedModules();
            var diagnostics = new DiagnosticService([new ProbeCollector("windows.system", () => Original)]);
            Model = new(diagnostics, Store, unused, new(unused, unused, unused), Confirmation,
                maintenance: new(new DiagnosticService([Defender]), new DiagnosticService([Devices]), Integrity));
        }
        public Task DiagnoseAsync()
        {
            var previous = SynchronizationContext.Current;
            try { SynchronizationContext.SetSynchronizationContext(new ImmediateProgressContext()); return Model.RunDiagnosticAsync(); }
            finally { SynchronizationContext.SetSynchronizationContext(previous); }
        }
    }
    private sealed class ImmediateProgressContext : SynchronizationContext
    { public override void Post(SendOrPostCallback callback, object? state) => callback(state); }
    private sealed class ProbeCollector(string id, Func<DiagnosticEvidence>? source = null) : IDiagnosticCollector
    {
        public string Id => id;
        public string DisplayName => id;
        public int Calls { get; private set; }
        public Task<IReadOnlyList<DiagnosticEvidence>> CollectAsync(CancellationToken cancellationToken)
        { Calls++; return Task.FromResult<IReadOnlyList<DiagnosticEvidence>>([source?.Invoke() ?? Evidence(id, Later, "snapshot " + Calls)]); }
    }
    private sealed class Confirmation : IUserConfirmation
    {
        public int Calls { get; private set; }
        public bool Accept { get; set; } = true;
        public Action? OnConfirm { get; set; }
        public bool Confirm(string title, string message) { Calls++; OnConfirm?.Invoke(); return Accept; }
    }
    private sealed class ProbeIntegrity : IIntegrityService
    {
        public bool Elevated { get; set; } = true;
        public int PlanCalls { get; private set; }
        public int ExecuteCalls { get; private set; }
        public bool FailWithoutReceipt { get; set; }
        public bool CancelWithReceipt { get; set; }
        public Func<Task>? BeforePlanCompletion { get; set; }
        public Func<Task>? BeforeExecutionCompletion { get; set; }
        public Action? OnReturningReceipt { get; set; }
        public DiagnosticEvidence? LastEvidence { get; private set; }
        public async Task<Result<IntegrityPlan>> PlanAsync(IntegrityOperation operation, CancellationToken cancellationToken)
        {
            PlanCalls++;
            var plan = new IntegrityPlan(Guid.NewGuid(), DateTimeOffset.UtcNow, operation, "Fixture verify-only",
                "selected-fixture.exe", "fixture-digest", ["fixed-verify-only"], "fixture-working-directory", Elevated,
                TimeSpan.FromSeconds(1), ["fixture only"], ToolTextEncoding.Utf8Strict);
            if (BeforePlanCompletion is { } wait) await wait();
            return Result<IntegrityPlan>.Success(plan);
        }
        public async Task<Result<IntegrityReceipt>> ExecuteAsync(IntegrityPlan plan, Guid confirmationId, CancellationToken cancellationToken)
        {
            ExecuteCalls++;
            Assert.Equal(plan.Id, confirmationId);
            if (BeforeExecutionCompletion is { } wait) await wait();
            if (FailWithoutReceipt) return Result<IntegrityReceipt>.Failure(ErrorCode.InvalidConfiguration, "new attempt failed without receipt");
            LastEvidence = Evidence(MaintenanceCollectors.Integrity, Later, "command completed; not a health claim");
            var receipt = new IntegrityReceipt(plan.Id, plan.Operation, IntegrityState.CommandCompleted, false,
                "fixture-completed-journal", LastEvidence, null, ["fixture only"], "Preserve this receipt.");
            OnReturningReceipt?.Invoke();
            return CancelWithReceipt ? Result<IntegrityReceipt>.FailureWithValue(receipt, ErrorCode.Cancelled, "cancelled after receipt")
                : Result<IntegrityReceipt>.Success(receipt);
        }
    }
    private sealed class MemoryStore : ISessionStore
    {
        public List<DiagnosticSession> Saved { get; } = [];
        public Func<DiagnosticSession, Result<bool>>? SaveOutcome { get; set; }
        public Task<Result<bool>> SaveAsync(DiagnosticSession session, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (SaveOutcome?.Invoke(session) is { IsSuccess: false } failed) return Task.FromResult(failed);
            Saved.Add(session);
            return Task.FromResult(Result<bool>.Success(true));
        }
        public Task<Result<IReadOnlyList<SessionSummary>>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Result<IReadOnlyList<SessionSummary>>.Success(Saved.Select(session => new SessionSummary(session.Id, session.StartedAt, session.Status, session.Findings.Count)).ToArray()));
        public Task<Result<DiagnosticSession>> GetAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(Result<DiagnosticSession>.Success(Saved.Single(session => session.Id == id)));
    }
    private sealed class UnusedModules : IAssistantService, IBackupService, ISmartService, IReportExporter
    {
        public Task<Result<AssistantAnswer>> AnalyzeAsync(DiagnosticSession session, string question, LocalAiSettings settings, CancellationToken cancellationToken) => throw new InvalidOperationException("Inference is not implicit maintenance.");
        public Task<Result<BackupPlan>> PlanAsync(BackupSettings settings, CancellationToken cancellationToken) => throw new InvalidOperationException("Backup was not requested.");
        public Task<Result<BackupReceipt>> ExecuteAsync(BackupPlan plan, Guid confirmationId, CancellationToken cancellationToken) => throw new InvalidOperationException("Backup was not requested.");
        public Task<Result<RestorePlan>> PlanRestoreAsync(BackupSettings settings, string snapshotId, string destinationDirectory, CancellationToken cancellationToken) => throw new InvalidOperationException("Restore was not requested.");
        public Task<Result<RestoreReceipt>> RestoreAsync(RestorePlan plan, Guid confirmationId, CancellationToken cancellationToken) => throw new InvalidOperationException("Restore was not requested.");
        public Task<Result<DiagnosticEvidence>> ReadAsync(string executablePath, string target, CancellationToken cancellationToken) => throw new InvalidOperationException("SMART was not requested.");
        public Task<Result<string>> ExportAsync(DiagnosticSession session, string path, ReportFormat format, CancellationToken cancellationToken) => throw new InvalidOperationException("Export was not requested.");
    }
}
