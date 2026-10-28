using Optimus.Application;
using Optimus.Desktop;
using Optimus.Domain;
using Optimus.Infrastructure;

namespace Optimus.Tests;

public sealed class DesktopMaintenanceToolsTests
{
    [Fact]
    public async Task StartupDoesNotQueryServicesUpdatesOrFolders()
    {
        var fixture = new Fixture();
        await fixture.Model.InitializeAsync();
        Assert.Equal(0, fixture.Services.Calls);
        Assert.Equal(0, fixture.Updates.Calls);
        Assert.Equal(0, fixture.Processes.Calls);
        Assert.Equal(0, fixture.Tasks.Calls);
        Assert.Equal(0, fixture.Storage.Calls);
        Assert.Empty(fixture.SelectedFolders);
        Assert.Null(fixture.Model.CurrentSession);
        Assert.False(fixture.Model.FolderSpaceCommand.CanExecute(null));
    }

    [Fact]
    public async Task ExplicitQueriesPreserveOtherSourcesAndRefreshOnlyTheirOwner()
    {
        var fixture = new Fixture();
        Assert.True(await fixture.Model.RunServiceInventoryAsync());
        var original = Assert.Single(fixture.Model.CurrentSession!.Evidence);
        Assert.True(await fixture.Model.RunUpdateHistoryAsync());
        Assert.Same(original, fixture.Model.CurrentSession!.Evidence.Single(item => item.Id == original.Id));
        fixture.Model.FolderSpacePath = @"D:\selected folder Ω";
        Assert.True(await fixture.Model.RunFolderSpaceAsync());
        var folder = fixture.Model.CurrentSession!.Evidence.Single(item => item.CollectorId == MaintenanceCollectors.FolderSpace);
        Assert.True(await fixture.Model.RunServiceInventoryAsync());
        Assert.DoesNotContain(fixture.Model.CurrentSession!.Evidence, item => item.Id == original.Id);
        Assert.Same(folder, fixture.Model.CurrentSession.Evidence.Single(item => item.Id == folder.Id));
        Assert.Equal(3, fixture.Model.MaintenanceEvidence.Count);
        Assert.Equal(4, fixture.Store.Saved.Count);
        Assert.All(fixture.Store.Saved, session => Assert.True(SessionValidation.IsValid(session)));
    }

    [Fact]
    public async Task FolderSelectionIsCapturedBeforeAwaitAndBusyActionsDoNotInvokeAnotherFactory()
    {
        var fixture = new Fixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.FolderWait = async token => { entered.SetResult(); await release.Task.WaitAsync(token); };
        fixture.Model.FolderSpacePath = @"D:\first Ω";
        var running = fixture.Model.RunFolderSpaceAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Model.FolderSpacePath = @"D:\second";
        Assert.False(await fixture.Model.RunFolderSpaceAsync());
        Assert.False(await fixture.Model.RunUpdateHistoryAsync());
        Assert.False(fixture.Model.RunMaintenanceQueryCommand.CanExecute(null));
        release.SetResult();
        Assert.True(await running);
        Assert.Equal(@"D:\first Ω", Assert.Single(fixture.SelectedFolders));
        Assert.Equal(@"D:\first Ω", Assert.Single(fixture.Model.CurrentSession!.Evidence).Subject);
        Assert.Equal(0, fixture.Updates.Calls);
    }

    [Fact]
    public async Task CancelledFolderAnalysisPreservesThePreviousSession()
    {
        var fixture = new Fixture();
        Assert.True(await fixture.Model.RunServiceInventoryAsync());
        var previous = fixture.Model.CurrentSession;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.FolderWait = async token => { entered.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); };
        fixture.Model.FolderSpacePath = @"D:\selected";
        var running = fixture.Model.RunFolderSpaceAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Model.RequestCancellation();
        Assert.False(await running);
        Assert.Same(previous, fixture.Model.CurrentSession);
        Assert.Single(fixture.Store.Saved);
        Assert.False(fixture.Model.IsBusy);
    }

    [Fact]
    public async Task ForeignCollectorCannotBePublishedAsFolderAnalysis()
    {
        var fixture = new Fixture { FolderCollectorId = "foreign.folder" };
        fixture.Model.FolderSpacePath = @"D:\selected";
        Assert.False(await fixture.Model.RunFolderSpaceAsync());
        Assert.Null(fixture.Model.CurrentSession);
        Assert.Empty(fixture.Store.Saved);
        Assert.Contains("cobertura", fixture.Model.StatusMessage);
    }

    [Theory]
    [InlineData(0, MaintenanceCollectors.Processes)]
    [InlineData(1, MaintenanceCollectors.ScheduledTasks)]
    [InlineData(2, MaintenanceCollectors.Storage)]
    public async Task SystemQueryPreservesOtherSourcesAndReplacesOnlyItsOwnScope(int selection, string collectorId)
    {
        var fixture = new Fixture();
        Assert.True(await fixture.Model.RunServiceInventoryAsync());
        var original = Assert.Single(fixture.Model.CurrentSession!.Evidence);
        Assert.True(await QuerySystemAsync(fixture.Model, selection));
        var previousOwned = fixture.Model.CurrentSession!.Evidence.Single(item => item.CollectorId == collectorId);
        Assert.True(await QuerySystemAsync(fixture.Model, selection));
        Assert.Same(original, fixture.Model.CurrentSession!.Evidence.Single(item => item.Id == original.Id));
        Assert.DoesNotContain(fixture.Model.CurrentSession.Evidence, item => item.Id == previousOwned.Id);
        Assert.Equal(SessionStatus.Partial, fixture.Model.CurrentSession.Status);
        Assert.Equal(2, fixture.Model.MaintenanceEvidence.Count);
        Assert.All(fixture.Store.Saved, session => Assert.True(SessionValidation.IsValid(session)));
        Assert.Equal(3, fixture.Store.Saved.Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task SystemQueryCannotPublishForeignCoverage(int selection)
    {
        var fixture = new Fixture("foreign.collector");
        Assert.False(await QuerySystemAsync(fixture.Model, selection));
        Assert.Null(fixture.Model.CurrentSession);
        Assert.Empty(fixture.Store.Saved);
        Assert.Contains("cobertura", fixture.Model.StatusMessage);
    }

    [Fact]
    public async Task CancelledSystemQueryKeepsThePreviousSessionAndSerializesOtherActions()
    {
        var fixture = new Fixture();
        Assert.True(await fixture.Model.RunServiceInventoryAsync());
        var previous = fixture.Model.CurrentSession;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.SystemWait = async token => { entered.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); };
        var running = fixture.Model.RunProcessInventoryAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(fixture.Model.RunMaintenanceQueryCommand.CanExecute(null));
        Assert.False(await fixture.Model.RunScheduledTasksAsync());
        Assert.False(await fixture.Model.RunStorageInventoryAsync());
        fixture.Model.RequestCancellation();
        Assert.False(await running);
        Assert.Same(previous, fixture.Model.CurrentSession);
        Assert.Equal(0, fixture.Tasks.Calls);
        Assert.Equal(0, fixture.Storage.Calls);
        Assert.Single(fixture.Store.Saved);
    }

    private static Task<bool> QuerySystemAsync(MainViewModel model, int selection) => selection switch
    {
        0 => model.RunProcessInventoryAsync(),
        1 => model.RunScheduledTasksAsync(),
        _ => model.RunStorageInventoryAsync()
    };

    private sealed class Fixture
    {
        public Collector Services { get; } = new(MaintenanceCollectors.Services);
        public Collector Updates { get; } = new(MaintenanceCollectors.Updates);
        public Collector Processes { get; }
        public Collector Tasks { get; }
        public Collector Storage { get; }
        public Func<CancellationToken, Task>? SystemWait { get; set; }
        public Store Store { get; } = new();
        public List<string> SelectedFolders { get; } = [];
        public Func<CancellationToken, Task>? FolderWait { get; set; }
        public string FolderCollectorId { get; set; } = MaintenanceCollectors.FolderSpace;
        public MainViewModel Model { get; }
        public Fixture(string? overriddenSystemId = null)
        {
            Processes = new(overriddenSystemId ?? MaintenanceCollectors.Processes, wait: token => SystemWait?.Invoke(token) ?? Task.CompletedTask);
            Tasks = new(overriddenSystemId ?? MaintenanceCollectors.ScheduledTasks, wait: token => SystemWait?.Invoke(token) ?? Task.CompletedTask);
            Storage = new(overriddenSystemId ?? MaintenanceCollectors.Storage, wait: token => SystemWait?.Invoke(token) ?? Task.CompletedTask);
            var data = Path.Combine(Path.GetTempPath(), "optimus-unused-modules", Guid.NewGuid().ToString("N"));
            var unused = new DiagnosticService([new Collector("unused")]);
            Model = new(unused, Store, new ReportExporter(), Composition.CreateModules(data),
                maintenance: new(unused, unused, new UnusedIntegrity(), new([Services]), new([Updates]), path =>
                {
                    SelectedFolders.Add(path);
                    return new([new Collector(FolderCollectorId, path, token => FolderWait?.Invoke(token) ?? Task.CompletedTask)]);
                }, new([Processes]), new([Tasks]), new([Storage])));
        }
    }

    private sealed class Collector(string id, string subject = "fixture-only source", Func<CancellationToken, Task>? wait = null) : IDiagnosticCollector
    {
        public string Id => id;
        public string DisplayName => id;
        public int Calls { get; private set; }
        public async Task<IReadOnlyList<DiagnosticEvidence>> CollectAsync(CancellationToken cancellationToken)
        {
            Calls++;
            if (wait is not null) await wait(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return [new(Guid.NewGuid(), id, id, subject, DateTimeOffset.UtcNow, EvidenceStatus.Partial,
                new("Fixture with explicit unknown; not a native-provider proof", 1), [new("unknown", "Unavailable source field")],
                [new(IssueCode.NotSupported, "Fixture unknown")])];
        }
    }

    private sealed class Store : ISessionStore
    {
        public List<DiagnosticSession> Saved { get; } = [];
        public Task<Result<bool>> SaveAsync(DiagnosticSession session, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); Saved.Add(session); return Task.FromResult(Result<bool>.Success(true)); }
        public Task<Result<IReadOnlyList<SessionSummary>>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Result<IReadOnlyList<SessionSummary>>.Success([]));
        public Task<Result<DiagnosticSession>> GetAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult(Result<DiagnosticSession>.Success(Saved.Single(item => item.Id == id)));
    }

    private sealed class UnusedIntegrity : IIntegrityService
    {
        public Task<Result<IntegrityPlan>> PlanAsync(IntegrityOperation operation, CancellationToken cancellationToken) => throw new InvalidOperationException("Integrity was not selected.");
        public Task<Result<IntegrityReceipt>> ExecuteAsync(IntegrityPlan plan, Guid confirmationId, CancellationToken cancellationToken) => throw new InvalidOperationException("Integrity was not selected.");
    }
}
