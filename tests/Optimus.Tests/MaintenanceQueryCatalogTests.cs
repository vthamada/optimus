using Optimus.Application;
using Optimus.Desktop;
using Optimus.Domain;
using Optimus.Infrastructure;

namespace Optimus.Tests;

public sealed class MaintenanceQueryCatalogTests
{
    [Theory]
    [InlineData("defender-status", MaintenanceQuery.Defender, "windows.defender")]
    [InlineData("device-inventory", MaintenanceQuery.DeviceDrivers, "windows.devices-drivers")]
    [InlineData("services", MaintenanceQuery.Services, "windows.services")]
    [InlineData("update-history", MaintenanceQuery.Updates, "windows.update-history")]
    [InlineData("processes", MaintenanceQuery.Processes, "windows.processes")]
    [InlineData("scheduled-tasks", MaintenanceQuery.ScheduledTasks, "windows.scheduled-tasks")]
    [InlineData("storage", MaintenanceQuery.Storage, "windows.storage")]
    [InlineData("connections", MaintenanceQuery.Connections, "windows.network-connections")]
    [InlineData("firewall", MaintenanceQuery.Firewall, "windows.firewall-profiles")]
    [InlineData("application-failures", MaintenanceQuery.ApplicationFailures, "windows.application-failures")]
    [InlineData("pending-restart", MaintenanceQuery.PendingRestart, "windows.pending-restart")]
    [InlineData("restore-points", MaintenanceQuery.RestorePoints, "windows.restore-points")]
    [InlineData("bitlocker-status", MaintenanceQuery.BitLocker, "windows.bitlocker-status")]
    [InlineData("restart-history", MaintenanceQuery.RestartHistory, "windows.restart-history")]
    [InlineData("memory-results", MaintenanceQuery.MemoryResults, "windows.memory-diagnostic-results")]
    [InlineData("hardware-profile", MaintenanceQuery.HardwareProfile, "windows.hardware-profile")]
    public async Task CommandAndSelectorBindTheSameNamedSourceWithoutUsingCatalogPosition(string command, MaintenanceQuery query, string collectorId)
    {
        var fixture = new Fixture();
        Assert.True(MaintenanceQueries.TryParseCommand(command, out var parsed));
        Assert.Equal(query, parsed);
        var binding = fixture.Services.ResolveQuery(parsed);
        Assert.True(binding.IsSuccess);
        Assert.Equal(collectorId, binding.Value!.CollectorId);
        await fixture.Model.InitializeAsync();
        Assert.All(fixture.Collectors.Values, collector => Assert.Equal(0, collector.Calls));
        fixture.Model.SelectedMaintenanceQuery = fixture.Model.MaintenanceQueryChoices.OrderByDescending(choice => choice.Label)
            .Single(choice => choice.Query == query);
        Assert.True(fixture.Model.RunMaintenanceQueryCommand.CanExecute(null));
        Assert.True(await fixture.Model.RunSelectedMaintenanceQueryAsync());
        var evidence = Assert.Single(fixture.Model.CurrentSession!.Evidence);
        Assert.Equal(collectorId, evidence.CollectorId);
        Assert.Equal(1, fixture.Collectors[query].Calls);
        Assert.All(fixture.Collectors.Where(pair => pair.Key != query), pair => Assert.Equal(0, pair.Value.Calls));
        Assert.Single(fixture.Store.Saved);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("FirewallProfiles")]
    [InlineData("firewall --disable")]
    [InlineData("CONNECTIONS")]
    [InlineData("application-failures; cmd")]
    public void UnknownTokensCannotFallBackToADefaultOperation(string command)
    {
        Assert.False(MaintenanceQueries.TryParseCommand(command, out var selected));
        Assert.False(Enum.IsDefined(selected));
        var result = new Fixture().Services.ResolveQuery(selected);
        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCode.InvalidConfiguration, result.Error!.Code);
    }

    [Fact]
    public async Task ChangingTheSelectorDuringAwaitDoesNotRelabelTheSourceBeingCollected()
    {
        var fixture = new Fixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Collectors[MaintenanceQuery.Connections].Wait = async token => { entered.SetResult(); await release.Task.WaitAsync(token); };
        fixture.Select(MaintenanceQuery.Connections);
        var running = fixture.Model.RunSelectedMaintenanceQueryAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Select(MaintenanceQuery.Firewall);
        Assert.False(fixture.Model.RunMaintenanceQueryCommand.CanExecute(null));
        Assert.False(await fixture.Model.RunSelectedMaintenanceQueryAsync());
        release.SetResult();
        Assert.True(await running);
        Assert.Equal(MaintenanceCollectors.Connections, Assert.Single(fixture.Model.CurrentSession!.Evidence).CollectorId);
        Assert.Equal(0, fixture.Collectors[MaintenanceQuery.Firewall].Calls);
        Assert.True(fixture.Model.RunMaintenanceQueryCommand.CanExecute(null));
    }

    [Theory]
    [InlineData(MaintenanceQuery.Firewall)]
    [InlineData(MaintenanceQuery.PendingRestart)]
    [InlineData(MaintenanceQuery.RestorePoints)]
    [InlineData(MaintenanceQuery.BitLocker)]
    public async Task MissingOptionalServiceCannotBeRequestedFromTheSelector(MaintenanceQuery query)
    {
        var fixture = new Fixture(optionalConfigured: false);
        fixture.Select(query);
        Assert.False(fixture.Model.RunMaintenanceQueryCommand.CanExecute(null));
        Assert.False(await fixture.Model.RunSelectedMaintenanceQueryAsync());
        Assert.Empty(fixture.Store.Saved);
        Assert.Null(fixture.Model.CurrentSession);
        Assert.Equal(ErrorCode.DependencyUnavailable, fixture.Services.ResolveQuery(query).Error!.Code);
    }

    [Fact]
    public async Task SelectedQueryCannotPublishAForeignCollector()
    {
        var fixture = new Fixture();
        fixture.Collectors[MaintenanceQuery.Firewall].ReportedId = "foreign.firewall";
        fixture.Select(MaintenanceQuery.Firewall);
        Assert.False(await fixture.Model.RunSelectedMaintenanceQueryAsync());
        Assert.Empty(fixture.Store.Saved);
        Assert.Null(fixture.Model.CurrentSession);
        Assert.Contains("cobertura", fixture.Model.StatusMessage);
    }

    [Fact]
    public async Task SharedBindingRejectsForeignCoverageWhileRetainingTheActualCollectedSession()
    {
        var fixture = new Fixture();
        fixture.Collectors[MaintenanceQuery.Firewall].ReportedId = "foreign.firewall";
        var binding = fixture.Services.ResolveQuery(MaintenanceQuery.Firewall).Value!;
        var result = await binding.RunAsync(null, CancellationToken.None);
        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCode.InvalidData, result.Error!.Code);
        Assert.Equal("foreign.firewall", Assert.Single(result.Value!.Evidence).CollectorId);
        Assert.Empty(fixture.Store.Saved);
    }

    private sealed class Fixture
    {
        public Dictionary<MaintenanceQuery, Collector> Collectors { get; } = new()
        {
            [MaintenanceQuery.Defender] = new(MaintenanceCollectors.Defender),
            [MaintenanceQuery.DeviceDrivers] = new(MaintenanceCollectors.DeviceDrivers),
            [MaintenanceQuery.Services] = new(MaintenanceCollectors.Services),
            [MaintenanceQuery.Updates] = new(MaintenanceCollectors.Updates),
            [MaintenanceQuery.Processes] = new(MaintenanceCollectors.Processes),
            [MaintenanceQuery.ScheduledTasks] = new(MaintenanceCollectors.ScheduledTasks),
            [MaintenanceQuery.Storage] = new(MaintenanceCollectors.Storage),
            [MaintenanceQuery.Connections] = new(MaintenanceCollectors.Connections),
            [MaintenanceQuery.Firewall] = new(MaintenanceCollectors.Firewall),
            [MaintenanceQuery.ApplicationFailures] = new(MaintenanceCollectors.ApplicationFailures),
            [MaintenanceQuery.PendingRestart] = new(MaintenanceCollectors.PendingRestart),
            [MaintenanceQuery.RestorePoints] = new(MaintenanceCollectors.RestorePoints),
            [MaintenanceQuery.BitLocker] = new(MaintenanceCollectors.BitLocker),
            [MaintenanceQuery.RestartHistory] = new(MaintenanceCollectors.RestartHistory),
            [MaintenanceQuery.MemoryResults] = new(MaintenanceCollectors.MemoryResults),
            [MaintenanceQuery.HardwareProfile] = new(MaintenanceCollectors.HardwareProfile)
        };
        public Store Store { get; } = new();
        public MaintenanceServices Services { get; }
        public MainViewModel Model { get; }
        public Fixture(bool optionalConfigured = true)
        {
            DiagnosticService Query(MaintenanceQuery key) => new([Collectors[key]]);
            Services = new(Query(MaintenanceQuery.Defender), Query(MaintenanceQuery.DeviceDrivers), new UnusedIntegrity(),
                Query(MaintenanceQuery.Services), Query(MaintenanceQuery.Updates), null,
                Query(MaintenanceQuery.Processes), Query(MaintenanceQuery.ScheduledTasks), Query(MaintenanceQuery.Storage),
                Query(MaintenanceQuery.Connections), optionalConfigured ? Query(MaintenanceQuery.Firewall) : null, Query(MaintenanceQuery.ApplicationFailures),
                optionalConfigured ? Query(MaintenanceQuery.PendingRestart) : null,
                optionalConfigured ? Query(MaintenanceQuery.RestorePoints) : null,
                optionalConfigured ? Query(MaintenanceQuery.BitLocker) : null,
                Query(MaintenanceQuery.RestartHistory), Query(MaintenanceQuery.MemoryResults), Query(MaintenanceQuery.HardwareProfile));
            Model = new(new([]), Store, new ReportExporter(), Composition.CreateModules(Path.GetTempPath()), maintenance: Services);
        }
        public void Select(MaintenanceQuery query) => Model.SelectedMaintenanceQuery = Model.MaintenanceQueryChoices.Single(choice => choice.Query == query);
    }
    private sealed class Collector(string id) : IDiagnosticCollector
    {
        public string Id => ReportedId ?? id;
        public string DisplayName => id;
        public string? ReportedId { get; set; }
        public Func<CancellationToken, Task>? Wait { get; set; }
        public int Calls { get; private set; }
        public async Task<IReadOnlyList<DiagnosticEvidence>> CollectAsync(CancellationToken cancellationToken)
        {
            Calls++;
            if (Wait is not null) await Wait(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return [new(Guid.NewGuid(), Id, id, "Fixture, not native provider validation", DateTimeOffset.UtcNow,
                EvidenceStatus.Complete, new("Test fixture", 1), [new("source", "Original value", "fixture")], [])];
        }
    }
    private sealed class Store : ISessionStore
    {
        public List<DiagnosticSession> Saved { get; } = [];
        public Task<Result<bool>> SaveAsync(DiagnosticSession session, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); Saved.Add(session); return Task.FromResult(Result<bool>.Success(true)); }
        public Task<Result<IReadOnlyList<SessionSummary>>> ListAsync(CancellationToken cancellationToken) => Task.FromResult(Result<IReadOnlyList<SessionSummary>>.Success([]));
        public Task<Result<DiagnosticSession>> GetAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(Result<DiagnosticSession>.Success(Saved.Single(item => item.Id == id)));
    }
    private sealed class UnusedIntegrity : IIntegrityService
    {
        public Task<Result<IntegrityPlan>> PlanAsync(IntegrityOperation operation, CancellationToken cancellationToken) => throw new InvalidOperationException("Not a query.");
        public Task<Result<IntegrityReceipt>> ExecuteAsync(IntegrityPlan plan, Guid confirmationId, CancellationToken cancellationToken) => throw new InvalidOperationException("Not a query.");
    }
}
