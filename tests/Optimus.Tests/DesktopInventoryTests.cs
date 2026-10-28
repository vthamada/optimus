using Optimus.Application;
using Optimus.Desktop;
using Optimus.Domain;

namespace Optimus.Tests;

public sealed class DesktopInventoryTests
{
    private static readonly DateTimeOffset Earlier = DateTimeOffset.Parse("2026-10-20T10:00:00Z");
    private static readonly DateTimeOffset Later = DateTimeOffset.Parse("2026-10-27T10:00:00Z");

    [Fact]
    public async Task ConstructionHistoryAndDefaultDiagnosticDoNotCollectInventory()
    {
        var fixture = new Fixture();
        Assert.All(fixture.InventoryCollectors, item => Assert.Equal(0, item.Calls));

        await fixture.Model.InitializeAsync();
        await fixture.DiagnoseAsync();

        Assert.All(fixture.InventoryCollectors, item => Assert.Equal(0, item.Calls));
        Assert.Equal(1, fixture.Diagnostic.Calls);
        Assert.Same(fixture.Original, Assert.Single(fixture.Model.CurrentSession!.Evidence));
        Assert.Empty(fixture.Model.InventoryEvidence);
        Assert.True(fixture.Model.InventoryCommand.CanExecute(null));
    }

    [Fact]
    public async Task RefreshKeepsOtherEvidenceIdentityTimeAndUnknownAndReplacesOwnedInventory()
    {
        var fixture = new Fixture();
        await fixture.DiagnoseAsync();
        Assert.True(await fixture.Model.RunInventoryAsync());
        var first = fixture.Model.CurrentSession!;
        var oldOwnedIds = fixture.Model.InventoryEvidence.Select(item => item.Id).ToHashSet();

        Assert.True(await fixture.Model.RunInventoryAsync());
        var refreshed = fixture.Model.CurrentSession!;

        Assert.NotEqual(first.Id, refreshed.Id);
        Assert.Same(fixture.Original, refreshed.Evidence.Single(item => item.Id == fixture.Original.Id));
        Assert.Equal(Earlier, refreshed.Evidence.Single(item => item.Id == fixture.Original.Id).CapturedAt);
        var unknown = Assert.Single(fixture.Original.Facts);
        Assert.Null(unknown.TextValue);
        Assert.Null(unknown.NumberValue);
        Assert.Equal(4, fixture.Model.InventoryEvidence.Count);
        Assert.All(fixture.Model.InventoryEvidence, item => Assert.Equal(Later, item.CapturedAt));
        Assert.DoesNotContain(refreshed.Evidence, item => oldOwnedIds.Contains(item.Id));
        Assert.All(first.Evidence.Where(item => InventoryCollectors.Contains(item.CollectorId)), item => Assert.Contains(item.Id, oldOwnedIds));
        Assert.Equal(3, fixture.Store.Saved.Count);
        Assert.Same(refreshed, fixture.Store.Saved[^1]);
        Assert.True(SessionValidation.IsValid(refreshed));
    }

    [Fact]
    public async Task CancellationDuringInventoryKeepsPreviousSessionAndReturnsNotApplied()
    {
        var fixture = new Fixture();
        await fixture.DiagnoseAsync();
        var previous = fixture.Model.CurrentSession;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.InventoryCollectors[0].Handler = async token =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException("The fixture must stop on cancellation.");
        };

        var running = fixture.Model.RunInventoryAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(fixture.Model.IsBusy);
        Assert.False(fixture.Model.InventoryCommand.CanExecute(null));
        fixture.Model.RequestCancellation();
        var applied = await running;

        Assert.False(applied);
        Assert.Same(previous, fixture.Model.CurrentSession);
        Assert.Single(fixture.Store.Saved);
        Assert.Empty(fixture.Model.InventoryEvidence);
        Assert.Contains("cancelada", fixture.Model.StatusMessage);
        Assert.False(fixture.Model.IsBusy);
        Assert.All(fixture.InventoryCollectors.Skip(1), item => Assert.Equal(0, item.Calls));
    }

    [Fact]
    public async Task MissingCollectorCoverageReturnsNotAppliedAndDoesNotPublishAnotherSession()
    {
        var fixture = new Fixture(includeStartup: false);
        await fixture.DiagnoseAsync();
        var previous = fixture.Model.CurrentSession;

        var applied = await fixture.Model.RunInventoryAsync();

        Assert.False(applied);
        Assert.Same(previous, fixture.Model.CurrentSession);
        Assert.Single(fixture.Store.Saved);
        Assert.Empty(fixture.Model.InventoryEvidence);
        Assert.Contains("cobertura", fixture.Model.StatusMessage);
        Assert.False(fixture.Model.IsBusy);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationAfterConfirmedSaveKeepsTheCommittedSessionAndReportsLateCancellation(bool inventory)
    {
        var fixture = new Fixture();
        if (inventory) await fixture.DiagnoseAsync();
        var previous = fixture.Model.CurrentSession;
        fixture.Store.AfterSaving = token =>
        {
            Assert.False(token.IsCancellationRequested);
            fixture.Model.RequestCancellation();
            Assert.True(token.IsCancellationRequested);
        };

        if (inventory) Assert.True(await fixture.Model.RunInventoryAsync());
        else await fixture.DiagnoseAsync();

        Assert.NotNull(fixture.Model.CurrentSession);
        Assert.NotSame(previous, fixture.Model.CurrentSession);
        Assert.Same(fixture.Store.Saved[^1], fixture.Model.CurrentSession);
        Assert.Contains("Cancelamento recebido após a gravação confirmada", fixture.Model.StatusMessage);
        Assert.Contains("sessão salva foi preservada", fixture.Model.StatusMessage);
        Assert.False(fixture.Model.IsBusy);
        Assert.Equal(inventory ? 2 : 1, fixture.Store.Saved.Count);
        Assert.Equal(inventory ? 4 : 0, fixture.Model.InventoryEvidence.Count);
    }

    private sealed class Fixture
    {
        public DiagnosticEvidence Original { get; } = new(Guid.NewGuid(), "windows.system", "Existing system facts", "fixture computer",
            Earlier, EvidenceStatus.Complete, new("Selected fact is explicitly unknown", 1), [new("unknownValue", "Original unknown")], []);
        public ProbeCollector Diagnostic { get; }
        public List<ProbeCollector> InventoryCollectors { get; }
        public MemoryStore Store { get; } = new();
        public MainViewModel Model { get; }

        public Fixture(bool includeStartup = true)
        {
            Diagnostic = new("windows.system", _ => Task.FromResult<IReadOnlyList<DiagnosticEvidence>>([Original]));
            string[] ids = [Optimus.Application.InventoryCollectors.Network, Optimus.Application.InventoryCollectors.Power,
                Optimus.Application.InventoryCollectors.InstalledApplications, Optimus.Application.InventoryCollectors.Startup];
            InventoryCollectors = ids.Take(includeStartup ? 4 : 3).Select(id => new ProbeCollector(id,
                _ => Task.FromResult<IReadOnlyList<DiagnosticEvidence>>([new(Guid.NewGuid(), id, "Inventory fixture", id, Later,
                    EvidenceStatus.Complete, new("Only fixture data; not native verification", 1), [new("sourceValue", "Source value", id)], [])]))).ToList();
            var unused = new UnusedModules();
            Model = new(new DiagnosticService([Diagnostic]), Store, unused, new(unused, unused, unused), unused,
                new DiagnosticService(InventoryCollectors));
        }

        public Task DiagnoseAsync()
        {
            // Progress<T> otherwise posts to the thread pool in these headless tests. The UI uses its
            // dispatcher context; this inline context makes equivalent callbacks deterministic here.
            var previous = SynchronizationContext.Current;
            try
            {
                SynchronizationContext.SetSynchronizationContext(new ImmediateProgressContext());
                return Model.RunDiagnosticAsync();
            }
            finally { SynchronizationContext.SetSynchronizationContext(previous); }
        }
    }

    private sealed class ImmediateProgressContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback callback, object? state) => callback(state);
    }

    private sealed class ProbeCollector(string id, Func<CancellationToken, Task<IReadOnlyList<DiagnosticEvidence>>> handler) : IDiagnosticCollector
    {
        public string Id => id;
        public string DisplayName => id;
        public int Calls { get; private set; }
        public Func<CancellationToken, Task<IReadOnlyList<DiagnosticEvidence>>> Handler { get; set; } = handler;
        public Task<IReadOnlyList<DiagnosticEvidence>> CollectAsync(CancellationToken cancellationToken)
        {
            Calls++;
            return Handler(cancellationToken);
        }
    }

    private sealed class MemoryStore : ISessionStore
    {
        public List<DiagnosticSession> Saved { get; } = [];
        public Action<CancellationToken>? AfterSaving { get; set; }
        public Task<Result<bool>> SaveAsync(DiagnosticSession session, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Saved.Add(session);
            AfterSaving?.Invoke(cancellationToken);
            return Task.FromResult(Result<bool>.Success(true));
        }
        public Task<Result<IReadOnlyList<SessionSummary>>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Result<IReadOnlyList<SessionSummary>>.Success(Saved.Select(session =>
                new SessionSummary(session.Id, session.StartedAt, session.Status, session.Findings.Count)).ToArray()));
        public Task<Result<DiagnosticSession>> GetAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult(Result<DiagnosticSession>.Success(Saved.Single(session => session.Id == id)));
    }

    private sealed class UnusedModules : IAssistantService, IBackupService, ISmartService, IReportExporter, IUserConfirmation
    {
        public Task<Result<AssistantAnswer>> AnalyzeAsync(DiagnosticSession session, string question, LocalAiSettings settings, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Inventory must not invoke inference.");
        public Task<Result<BackupPlan>> PlanAsync(BackupSettings settings, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Inventory must not prepare backup.");
        public Task<Result<BackupReceipt>> ExecuteAsync(BackupPlan plan, Guid confirmationId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Inventory must not execute backup.");
        public Task<Result<RestorePlan>> PlanRestoreAsync(BackupSettings settings, string snapshotId, string destinationDirectory, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Inventory must not prepare restore.");
        public Task<Result<RestoreReceipt>> RestoreAsync(RestorePlan plan, Guid confirmationId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Inventory must not execute restore.");
        public Task<Result<DiagnosticEvidence>> ReadAsync(string executablePath, string target, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Inventory must not invoke SMART.");
        public Task<Result<string>> ExportAsync(DiagnosticSession session, string path, ReportFormat format, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Inventory must not export without an explicit action.");
        public bool Confirm(string title, string message) => throw new InvalidOperationException("Read-only inventory requires no effect confirmation.");
    }
}
