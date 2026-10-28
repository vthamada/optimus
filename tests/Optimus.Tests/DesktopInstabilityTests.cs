using Optimus.Application;
using Optimus.Desktop;
using Optimus.Domain;
using Optimus.Infrastructure;

namespace Optimus.Tests;

// Public view-model workflows with fake source boundaries. No WPF render, debugger, physical test, registry or event-log provider runs here.
public sealed class DesktopInstabilityTests
{
    [Fact]
    public async Task InitializationAndChangingOptionsDoNotExecuteAnySource()
    {
        var fixture = new Fixture();
        await fixture.Model.InitializeAsync();
        fixture.Model.InvestigationDays = 7;
        fixture.Model.IncludeDump = true;
        fixture.Model.CdbPath = @"D:\chosen\cdb.exe";
        fixture.Model.SymbolDirectory = @"D:\chosen\symbols";
        fixture.Model.DumpPath = @"D:\chosen\one.dmp";
        Assert.False(fixture.Model.UseInvestigationAi);
        Assert.All(fixture.Collectors.Values, collector => Assert.Equal(0, collector.Calls));
        Assert.Empty(fixture.DumpSelections);
        Assert.Equal(0, fixture.PlannerFactories);
        Assert.Equal(0, fixture.Importer.Calls);
        Assert.Empty(fixture.Store.Saved);
        Assert.Null(fixture.Model.CurrentSession);
    }

    [Fact]
    public async Task DeterministicInvestigationCapturesQuestionDaysAndCatalogBeforeAwait()
    {
        var fixture = new Fixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Collectors[InvestigationTool.RestartHistory].Wait = async token => { entered.SetResult(); await release.Task.WaitAsync(token); };
        string question = fixture.Model.InvestigationQuestion;
        var pending = fixture.Model.RunInvestigationAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(fixture.Model.IsBusy);
        Assert.True(fixture.Model.CancelCommand.CanExecute(null));
        fixture.Model.InvestigationQuestion = "Outra pergunta";
        fixture.Model.InvestigationDays = 7;
        fixture.Model.IncludeDump = true;
        fixture.Model.UseInvestigationAi = true;
        fixture.Model.DumpPath = @"D:\another\dump.dmp";
        Assert.False(await fixture.Model.RunInvestigationAsync());
        Assert.False(await fixture.Model.RunDumpAnalysisAsync());
        Assert.False(await fixture.Model.ImportMemoryReportFromPathAsync("another.html"));
        release.SetResult();
        Assert.True(await pending);
        Assert.Equal(new[] { 30 }, fixture.RestartWindows);
        Assert.Equal(new[] { 30 }, fixture.MemoryWindows);
        Assert.Equal(0, fixture.PlannerFactories);
        Assert.Empty(fixture.DumpSelections);
        Assert.Equal(3, fixture.Model.InvestigationSteps.Count);
        Assert.Equal(0, fixture.Collectors[InvestigationTool.AnalyzeDump].Calls);
        var assessment = Assert.Single(fixture.Model.CurrentSession!.Evidence, evidence => evidence.CollectorId == InstabilityInvestigationService.AssessmentCollectorId);
        Assert.Equal(question, Assert.Single(assessment.Facts, fact => fact.Key == "question").TextValue);
        Assert.Single(fixture.Store.Saved);
    }

    [Fact]
    public async Task UnavailableSourceIsSavedWithItsIdentityAndDoesNotFailTheInvestigationFlow()
    {
        var fixture = new Fixture();
        fixture.Collectors[InvestigationTool.MemoryResults].Status = EvidenceStatus.Unavailable;
        Assert.True(await fixture.Model.RunInvestigationAsync());
        var session = fixture.Model.CurrentSession!;
        Assert.Equal(SessionStatus.Partial, session.Status);
        var unavailable = Assert.Single(session.Evidence, item => item.CollectorId == "windows.memory-diagnostic-results");
        Assert.Equal(EvidenceStatus.Unavailable, unavailable.Status);
        Assert.Contains(unavailable.Issues, issue => issue.Code == IssueCode.PermissionDenied);
        Assert.Equal(3, fixture.Model.InvestigationSteps.Count);
        Assert.Single(fixture.Store.Saved);
        Assert.Equal(session.Id, fixture.Store.Saved[0].Id);
    }

    [Fact]
    public async Task HypothesesAndFirmwareKeepSourceCitationsAndDoNotAssertAnUpdate()
    {
        var fixture = new Fixture();
        Assert.True(await fixture.Model.RunInvestigationAsync());
        var ids = fixture.Model.CurrentSession!.Evidence.Select(item => item.Id).ToHashSet();
        var hypothesis = Assert.Single(fixture.Model.InvestigationHypotheses);
        Assert.NotEmpty(hypothesis.NextChecks);
        Assert.All(hypothesis.EvidenceIds, id => Assert.Contains(id, ids));
        var reference = Assert.Single(fixture.Model.FirmwareReferences);
        Assert.Equal("https://www.dell.com/support/home/", reference.Url);
        Assert.Equal("Fixture model", reference.Model);
        Assert.Contains(reference.EvidenceId, ids);
        Assert.DoesNotContain(fixture.Model.CurrentSession!.Evidence.SelectMany(item => item.Facts), fact => fact.Key is "firmwareIsCurrent" or "ramIsFaulty");
    }

    [Fact]
    public async Task IncludeDumpUsesTheCapturedSettingsOnceAndAChangeCannotRelabelIt()
    {
        var fixture = new Fixture();
        fixture.SelectDump(); fixture.Model.IncludeDump = true;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Collectors[InvestigationTool.RestartHistory].Wait = async token => { entered.SetResult(); await release.Task.WaitAsync(token); };
        var task = fixture.Model.RunInvestigationAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Model.CdbPath = @"D:\changed\cdb.exe";
        fixture.Model.SymbolDirectory = @"D:\changed\symbols";
        fixture.Model.DumpPath = @"D:\changed\two.dmp";
        release.SetResult();
        Assert.True(await task);
        var selected = Assert.Single(fixture.DumpSelections);
        Assert.Equal(@"D:\chosen\cdb.exe", selected.DebuggerExecutablePath);
        Assert.Equal(@"D:\chosen\symbols", selected.SymbolDirectory);
        Assert.Equal(@"D:\chosen\one.dmp", selected.DumpPath);
        Assert.Equal(4, fixture.Model.InvestigationSteps.Count);
        Assert.Equal(1, fixture.Collectors[InvestigationTool.AnalyzeDump].Calls);
    }

    [Fact]
    public async Task LocalAiRequiresExplicitSelectionAndKeepsItsCapturedConfiguration()
    {
        var fixture = new Fixture();
        fixture.Model.UseInvestigationAi = true;
        Assert.False(await fixture.Model.RunInvestigationAsync());
        Assert.Equal(0, fixture.PlannerFactories);
        fixture.Model.LlamaPath = @"D:\models\llama-completion.exe";
        fixture.Model.ModelPath = @"D:\models\chosen.gguf";
        Assert.True(fixture.Model.InvestigateCommand.CanExecute(null));
        Assert.True(await fixture.Model.RunInvestigationAsync());
        Assert.Equal(1, fixture.PlannerFactories);
        Assert.Equal(@"D:\models\llama-completion.exe", fixture.PlannerSettings!.ExecutablePath);
        Assert.Equal(@"D:\models\chosen.gguf", fixture.PlannerSettings.ModelPath);
        Assert.Empty(fixture.Model.InvestigationSteps); // fixture planner explicitly stops; no implicit fallback.
        Assert.All(fixture.Collectors.Values, collector => Assert.Equal(0, collector.Calls));
        Assert.Contains("PlannerStopped", fixture.Model.InvestigationSummary);
    }

    [Fact]
    public async Task CancelledInvestigationDisplaysCompletedStepsWithoutSavingACancelledSession()
    {
        var fixture = new Fixture();
        await fixture.Model.RunDiagnosticAsync();
        var previous = fixture.Model.CurrentSession!;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Collectors[InvestigationTool.MemoryResults].Wait = async token => { entered.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); };
        var task = fixture.Model.RunInvestigationAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Model.RequestCancellation();
        Assert.False(await task);
        Assert.False(fixture.Model.IsBusy);
        Assert.Single(fixture.Store.Saved);
        Assert.NotEqual(previous.Id, fixture.Model.CurrentSession!.Id);
        Assert.Contains(fixture.Model.CurrentSession!.Evidence, item => item.Id == previous.Evidence[0].Id);
        Assert.Single(fixture.Model.InvestigationSteps);
        Assert.Equal(InvestigationTool.RestartHistory, fixture.Model.InvestigationSteps[0].Tool);
        Assert.Equal(0, fixture.Collectors[InvestigationTool.HardwareProfile].Calls);
        Assert.Contains("cancelada", fixture.Model.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("não foi salva", fixture.Model.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ImportReplacesOnlyItsOwnedSourceAndPreservesTheOriginalSessionEvidence()
    {
        var fixture = new Fixture();
        await fixture.Model.RunDiagnosticAsync();
        var baseline = Assert.Single(fixture.Model.CurrentSession!.Evidence);
        Assert.True(await fixture.Model.ImportMemoryReportFromPathAsync(@"D:\reports\chosen Ω.html"));
        var first = Assert.Single(fixture.Model.CurrentSession!.Evidence, item => item.CollectorId == "memory.test-report");
        Assert.True(await fixture.Model.ImportMemoryReportFromPathAsync(@"D:\reports\next.html"));
        var next = Assert.Single(fixture.Model.CurrentSession!.Evidence, item => item.CollectorId == "memory.test-report");
        Assert.NotEqual(first.Id, next.Id);
        Assert.Contains(fixture.Model.CurrentSession!.Evidence, item => item.Id == baseline.Id && item.CapturedAt == baseline.CapturedAt);
        Assert.Equal(new[] { @"D:\reports\chosen Ω.html", @"D:\reports\next.html" }, fixture.Importer.Paths);
        Assert.Equal(3, fixture.Store.Saved.Count);
        Assert.Equal(0, fixture.Collectors[InvestigationTool.AnalyzeDump].Calls);
        Assert.All(fixture.Collectors.Values, collector => Assert.Equal(0, collector.Calls));
    }

    [Fact]
    public async Task ForeignImportedSourceCannotBeStoredOrReplaceTheCurrentSession()
    {
        var fixture = new Fixture();
        await fixture.Model.RunDiagnosticAsync();
        var original = fixture.Model.CurrentSession;
        fixture.Importer.CollectorId = "foreign.report";
        Assert.False(await fixture.Model.ImportMemoryReportFromPathAsync("selected.html"));
        Assert.Same(original, fixture.Model.CurrentSession);
        Assert.Single(fixture.Store.Saved);
    }

    [Fact]
    public async Task StandaloneDumpReplacesOnlyDumpCoverageAndPreservesUnavailableEvidence()
    {
        var fixture = new Fixture(); fixture.SelectDump();
        await fixture.Model.RunDiagnosticAsync();
        var initial = fixture.Model.CurrentSession!.Evidence[0];
        fixture.Collectors[InvestigationTool.AnalyzeDump].Status = EvidenceStatus.Unavailable;
        Assert.True(await fixture.Model.RunDumpAnalysisAsync());
        var dump = Assert.Single(fixture.Model.CurrentSession!.Evidence, item => item.CollectorId == "windows.crash-dump");
        Assert.Equal(EvidenceStatus.Unavailable, dump.Status);
        Assert.Contains(fixture.Model.CurrentSession!.Evidence, item => item.Id == initial.Id);
        Assert.Equal(2, fixture.Store.Saved.Count);
        fixture.Collectors[InvestigationTool.AnalyzeDump].ReportedId = "foreign.dump";
        var current = fixture.Model.CurrentSession;
        Assert.False(await fixture.Model.RunDumpAnalysisAsync());
        Assert.Same(current, fixture.Model.CurrentSession);
        Assert.Equal(2, fixture.Store.Saved.Count);
    }

    [Fact]
    public async Task InvalidQuestionDaysOrUnconfiguredServicesCannotStartAQuery()
    {
        var fixture = new Fixture();
        fixture.Model.InvestigationQuestion = new string('X', 401);
        Assert.False(await fixture.Model.RunInvestigationAsync());
        fixture.Model.InvestigationQuestion = "Pergunta válida";
        fixture.Model.InvestigationDays = 0;
        Assert.False(await fixture.Model.RunInvestigationAsync());
        fixture.Model.InvestigationDays = 366;
        Assert.False(await fixture.Model.RunInvestigationAsync());
        Assert.Empty(fixture.RestartWindows);
        Assert.Empty(fixture.Store.Saved);
        fixture.Model.InvestigationDays = 365;
        Assert.True(await fixture.Model.RunInvestigationAsync());
        Assert.Equal(new[] { 365 }, fixture.RestartWindows);
        var absent = new Fixture(configured: false);
        Assert.False(absent.Model.InvestigateCommand.CanExecute(null));
        Assert.False(await absent.Model.RunInvestigationAsync());
        Assert.False(await absent.Model.ImportMemoryReportFromPathAsync("selected.html"));
        Assert.Empty(absent.Store.Saved);
    }

    [Fact]
    public async Task OpeningHistoryDoesNotShowHypothesesFromADifferentSessionOrReexecuteSources()
    {
        var fixture = new Fixture();
        await fixture.Model.RunDiagnosticAsync();
        var previous = fixture.Model.CurrentSession!;
        Assert.True(await fixture.Model.RunInvestigationAsync());
        Assert.NotEmpty(fixture.Model.InvestigationHypotheses);
        int calls = fixture.Collectors.Values.Sum(collector => collector.Calls);
        fixture.Model.SelectedHistory = new(previous.Id, previous.StartedAt, previous.Status, previous.Findings.Count);
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Model.PropertyChanged += (_, change) =>
        {
            if (change.PropertyName == nameof(MainViewModel.IsBusy) && !fixture.Model.IsBusy && fixture.Model.CurrentSession?.Id == previous.Id) opened.TrySetResult();
        };
        fixture.Model.OpenHistoryCommand.Execute(null);
        await opened.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(previous.Id, fixture.Model.CurrentSession!.Id);
        Assert.Empty(fixture.Model.InvestigationHypotheses);
        Assert.Empty(fixture.Model.InvestigationSteps);
        Assert.Empty(fixture.Model.FirmwareReferences);
        Assert.Equal(calls, fixture.Collectors.Values.Sum(collector => collector.Calls));
    }

    [Fact]
    public async Task NonCancelledPlannerFailurePersistsItsCompletedStepsAndDecisionReceipt()
    {
        var fixture = new Fixture { Planner = new FailAfterFirstPlanner() };
        fixture.Model.UseInvestigationAi = true;
        fixture.Model.LlamaPath = @"D:\selected\llama-completion.exe";
        fixture.Model.ModelPath = @"D:\selected\model.gguf";
        Assert.True(await fixture.Model.RunInvestigationAsync());
        Assert.Single(fixture.Store.Saved);
        var step = Assert.Single(fixture.Model.InvestigationSteps);
        Assert.Equal("fixture-first-decision.json", step.DecisionReceipt);
        Assert.Equal("Fixture model selection", step.DecisionSource);
        Assert.Contains("PlannerFailed", fixture.Model.InvestigationSummary);
        Assert.Contains("Resultado parcial", fixture.Model.StatusMessage);
        Assert.Contains("fixture planner unavailable", fixture.Model.StatusMessage);
        Assert.Equal(0, fixture.Collectors[InvestigationTool.MemoryResults].Calls);
    }

    [Fact]
    public async Task PersistenceFailureKeepsTheAppliedSessionAndNeverClaimsTheHistoryWasSaved()
    {
        var fixture = new Fixture();
        fixture.Store.FailSave = true;
        Assert.True(await fixture.Model.RunInvestigationAsync());
        Assert.NotNull(fixture.Model.CurrentSession);
        Assert.NotEmpty(fixture.Model.InvestigationSteps);
        Assert.Empty(fixture.Store.Saved);
        Assert.Contains("gravação não confirmada", fixture.Model.StatusMessage);
        Assert.Contains("fixture storage failure", fixture.Model.StatusMessage);
    }

    [Fact]
    public async Task StandaloneDumpSelectionIsCapturedBeforeItsAsyncSourceReturns()
    {
        var fixture = new Fixture(); fixture.SelectDump();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Collectors[InvestigationTool.AnalyzeDump].Wait = async token => { entered.SetResult(); await release.Task.WaitAsync(token); };
        var task = fixture.Model.RunDumpAnalysisAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Model.DumpPath = @"D:\changed\other.dmp";
        fixture.Model.CdbPath = @"D:\changed\cdb.exe";
        release.SetResult();
        Assert.True(await task);
        var selected = Assert.Single(fixture.DumpSelections);
        Assert.Equal(@"D:\chosen\one.dmp", selected.DumpPath);
        Assert.Equal(@"D:\chosen\cdb.exe", selected.DebuggerExecutablePath);
        Assert.Single(fixture.Store.Saved);
    }

    [Fact]
    public async Task FirmwareNotesRequireExplicitActionAndCaptureTheSelectedUrlAndObservedManufacturer()
    {
        var reader = new FirmwareReader();
        var fixture = new Fixture(firmwareNotes: reader);
        await fixture.Model.InitializeAsync();
        Assert.Equal(0, reader.Calls);
        Assert.True(await fixture.Model.RunInvestigationAsync());
        Assert.Equal(0, reader.Calls); // Internet reading never enters the automatic/AI tool catalog.
        Assert.Equal("", fixture.Model.FirmwareNotesUrl);
        Assert.False(fixture.Model.ReadFirmwareNotesCommand.CanExecute(null));
        var reference = Assert.Single(fixture.Model.FirmwareReferences);
        fixture.Model.SelectedFirmwareReference = reference;
        fixture.Model.FirmwareNotesUrl = "https://www.dell.com/support/selected-release-notes";
        Assert.True(fixture.Model.ReadFirmwareNotesCommand.CanExecute(null));
        var originalIds = fixture.Model.CurrentSession!.Evidence.Select(item => item.Id).ToArray();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        reader.Wait = async token => { entered.SetResult(); await release.Task.WaitAsync(token); };
        var read = fixture.Model.ReadFirmwareNotesAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Model.FirmwareNotesUrl = "https://www.dell.com/changed";
        fixture.Model.SelectedFirmwareReference = null;
        Assert.False(await fixture.Model.ReadFirmwareNotesAsync());
        Assert.False(fixture.Model.ReadFirmwareNotesCommand.CanExecute(null));
        release.SetResult();
        Assert.True(await read);
        Assert.Equal("https://www.dell.com/support/selected-release-notes", reader.SelectedUrl);
        Assert.Equal("Dell", reader.SelectedManufacturer);
        Assert.Equal(1, reader.Calls);
        Assert.All(originalIds, id => Assert.Contains(fixture.Model.CurrentSession!.Evidence, item => item.Id == id));
        Assert.Single(fixture.Model.InstabilityEvidence, item => item.CollectorId == "firmware.release-notes");
    }

    [Fact]
    public async Task FirmwareNotesKeepUnavailableUnknownFactsReplaceOnlyOwnedCoverageAndRejectForeignEvidence()
    {
        var reader = new FirmwareReader { Status = EvidenceStatus.Unavailable, FailWithValue = true };
        var fixture = new Fixture(firmwareNotes: reader);
        Assert.True(await fixture.Model.RunInvestigationAsync());
        var hardware = Assert.Single(fixture.Model.CurrentSession!.Evidence, item => item.CollectorId == "windows.hardware-profile");
        fixture.Model.SelectedFirmwareReference = Assert.Single(fixture.Model.FirmwareReferences);
        fixture.Model.FirmwareNotesUrl = "https://www.dell.com/support/selected-release-notes";
        Assert.True(await fixture.Model.ReadFirmwareNotesAsync());
        var first = Assert.Single(fixture.Model.CurrentSession!.Evidence, item => item.CollectorId == "firmware.release-notes");
        Assert.Equal(EvidenceStatus.Unavailable, first.Status);
        Assert.Null(Assert.Single(first.Facts, fact => fact.Key == "notes.observedVersion").TextValue);
        Assert.Contains("Resultado parcial", fixture.Model.StatusMessage);
        Assert.Equal(2, fixture.Store.Saved.Count);
        Assert.True(await fixture.Model.ReadFirmwareNotesAsync());
        var replacement = Assert.Single(fixture.Model.CurrentSession!.Evidence, item => item.CollectorId == "firmware.release-notes");
        Assert.NotEqual(first.Id, replacement.Id);
        Assert.Contains(fixture.Model.CurrentSession!.Evidence, item => item.Id == hardware.Id);
        reader.CollectorId = "foreign.internet-source";
        var preserved = fixture.Model.CurrentSession;
        Assert.False(await fixture.Model.ReadFirmwareNotesAsync());
        Assert.Same(preserved, fixture.Model.CurrentSession);
        Assert.Equal(3, fixture.Store.Saved.Count);
    }

    [Fact]
    public async Task UnconfiguredReaderAndUnobservedManufacturerCannotStartInternetReading()
    {
        var absent = new Fixture();
        Assert.True(await absent.Model.RunInvestigationAsync());
        absent.Model.SelectedFirmwareReference = Assert.Single(absent.Model.FirmwareReferences);
        absent.Model.FirmwareNotesUrl = "https://www.dell.com/support/selected-release-notes";
        Assert.False(absent.Model.ReadFirmwareNotesCommand.CanExecute(null));
        Assert.False(await absent.Model.ReadFirmwareNotesAsync());
        var reader = new FirmwareReader();
        var unobserved = new Fixture(firmwareNotes: reader);
        unobserved.Model.SelectedFirmwareReference = new("Dell", "Unobserved model", "https://www.dell.com/support/home/", Guid.NewGuid());
        unobserved.Model.FirmwareNotesUrl = "https://www.dell.com/support/selected-release-notes";
        Assert.Null(unobserved.Model.SelectedFirmwareReference);
        Assert.False(await unobserved.Model.ReadFirmwareNotesAsync());
        Assert.Equal(0, reader.Calls);
    }

    [Fact]
    public async Task CancelledFirmwareResponseKeepsItsCapturedPrefixAndOtherSourcesWithoutSavingOrRetrying()
    {
        var reader = new CancelledFirmwareReader();
        var fixture = new Fixture(firmwareNotes: reader);
        Assert.True(await fixture.Model.RunInvestigationAsync());
        var previousIds = fixture.Model.CurrentSession!.Evidence.Select(item => item.Id).ToArray();
        int persisted = fixture.Store.Saved.Count;
        fixture.Model.SelectedFirmwareReference = Assert.Single(fixture.Model.FirmwareReferences);
        fixture.Model.FirmwareNotesUrl = "https://www.dell.com/support/selected-release-notes";
        var reading = fixture.Model.ReadFirmwareNotesAsync();
        await reader.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Model.RequestCancellation();
        Assert.False(await reading);
        Assert.False(fixture.Model.IsBusy);
        Assert.Equal(1, reader.Calls);
        Assert.Equal(persisted, fixture.Store.Saved.Count);
        var partial = fixture.Model.CurrentSession!;
        Assert.Equal(SessionStatus.Partial, partial.Status);
        Assert.All(previousIds, id => Assert.Contains(partial.Evidence, item => item.Id == id));
        var response = Assert.Single(partial.Evidence, item => item.CollectorId == "firmware.release-notes");
        Assert.Equal(EvidenceStatus.Partial, response.Status);
        Assert.Equal("AQID", Assert.Single(response.Facts, fact => fact.Key == "sourceBytesBase64").TextValue);
        Assert.Equal("false", Assert.Single(response.Facts, fact => fact.Key == "bodyComplete").TextValue);
        Assert.Contains("Prefixo", fixture.Model.StatusMessage);
        Assert.Contains("incompleto", fixture.Model.StatusMessage);
        Assert.Contains("não foi salva", fixture.Model.StatusMessage);
    }

    [Theory]
    [InlineData("https://www.dell.com/support/home/", true)]
    [InlineData("http://www.dell.com/support/home/", false)]
    [InlineData("https://www.dell.com/support/home/#fragment", false)]
    [InlineData("https://www.dell.com.evil.example/support/home/", false)]
    [InlineData("file:///D:/report.html", false)]
    public void FirmwareNavigationRequiresExactOfficialHttpsUrl(string selected, bool expected)
    {
        var reference = new FirmwareSupportReference("Dell", "Fixture", "https://www.dell.com/support/home/", Guid.NewGuid());
        Assert.Equal(expected, MainWindow.IsOfficialFirmwareNavigation(reference, new Uri(selected)));
        Assert.False(MainWindow.IsOfficialFirmwareNavigation(reference with { Url = "https://example.org/" }, new Uri(selected)));
        Assert.False(MainWindow.IsOfficialFirmwareNavigation(reference with { Manufacturer = "Unknown vendor" }, new Uri(selected)));
    }

    private sealed class Fixture
    {
        public readonly Dictionary<InvestigationTool, Collector> Collectors = new()
        {
            [InvestigationTool.RestartHistory] = new("windows.restart-history", [new("restart.kernelPower41Observed", "Fixture signal", NumberValue: 1)]),
            [InvestigationTool.MemoryResults] = new("windows.memory-diagnostic-results", []),
            [InvestigationTool.HardwareProfile] = new("windows.hardware-profile", [new("system.0.Manufacturer", "Fixture manufacturer", "Dell"), new("system.0.Model", "Fixture model", "Fixture model")]),
            [InvestigationTool.AnalyzeDump] = new("windows.crash-dump", [])
        };
        public Store Store { get; } = new();
        public Importer Importer { get; } = new();
        public List<int> RestartWindows { get; } = [];
        public List<int> MemoryWindows { get; } = [];
        public List<CrashDumpSettings> DumpSelections { get; } = [];
        public int PlannerFactories { get; private set; }
        public LocalAiSettings? PlannerSettings { get; private set; }
        public IInvestigationPlanner? Planner { get; set; }
        public MainViewModel Model { get; }
        public Fixture(bool configured = true, IFirmwareNotesReader? firmwareNotes = null)
        {
            DiagnosticService Source(InvestigationTool tool) => new([Collectors[tool]]);
            var services = new InstabilityServices(
                days => { RestartWindows.Add(days); return Source(InvestigationTool.RestartHistory); },
                days => { MemoryWindows.Add(days); return Source(InvestigationTool.MemoryResults); },
                Source(InvestigationTool.HardwareProfile),
                settings => { DumpSelections.Add(settings); return Source(InvestigationTool.AnalyzeDump); },
                Importer, settings => { PlannerFactories++; PlannerSettings = settings; return Planner ?? new StopPlanner(); }, firmwareNotes);
            Model = new(new([new Collector("fixture.base", [])]), Store, new ReportExporter(), Composition.CreateModules(Path.GetTempPath()), instability: configured ? services : null);
        }
        public void SelectDump()
        { Model.CdbPath = @"D:\chosen\cdb.exe"; Model.SymbolDirectory = @"D:\chosen\symbols"; Model.DumpPath = @"D:\chosen\one.dmp"; }
    }
    private sealed class Collector(string id, IReadOnlyList<DiagnosticFact> facts) : IDiagnosticCollector
    {
        public string Id => ReportedId ?? id;
        public string DisplayName => id;
        public string? ReportedId { get; set; }
        public EvidenceStatus Status { get; set; } = EvidenceStatus.Complete;
        public Func<CancellationToken, Task>? Wait { get; set; }
        public int Calls { get; private set; }
        public async Task<IReadOnlyList<DiagnosticEvidence>> CollectAsync(CancellationToken cancellationToken)
        {
            Calls++;
            if (Wait is not null) await Wait(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return [new(Guid.NewGuid(), Id, id, "Fixture, not native provider validation", DateTimeOffset.UtcNow, Status,
                new("Desktop fixture", 1), facts, Status == EvidenceStatus.Unavailable ? [new(IssueCode.PermissionDenied, "Fixture unavailable source")] : [])];
        }
    }
    private sealed class Importer : IMemoryTestReportImporter
    {
        public string CollectorId { get; set; } = "memory.test-report";
        public int Calls { get; private set; }
        public List<string> Paths { get; } = [];
        public Task<Result<DiagnosticEvidence>> ImportAsync(string reportPath, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); Calls++; Paths.Add(reportPath);
            return Task.FromResult(Result<DiagnosticEvidence>.Success(new(Guid.NewGuid(), CollectorId, "Fixture imported report", reportPath,
                DateTimeOffset.UtcNow, EvidenceStatus.Partial, new("Fixture inert HTML report", 1), [new("selectedSourcePath", "Selected original", reportPath)], [new(IssueCode.NotSupported, "Fixture report")])));
        }
    }
    private sealed class StopPlanner : IInvestigationPlanner
    {
        public Task<Result<InvestigationDecision>> DecideAsync(InvestigationContext context, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(Result<InvestigationDecision>.Success(new(null, "Fixture explicit stop", "fixture-receipt.json"))); }
    }
    private sealed class FailAfterFirstPlanner : IInvestigationPlanner
    {
        private int _calls;
        public Task<Result<InvestigationDecision>> DecideAsync(InvestigationContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(++_calls == 1
                ? Result<InvestigationDecision>.Success(new(InvestigationTool.RestartHistory, "Fixture model selection", "fixture-first-decision.json"))
                : Result<InvestigationDecision>.Failure(ErrorCode.DependencyUnavailable, "fixture planner unavailable"));
        }
    }
    private sealed class FirmwareReader : IFirmwareNotesReader
    {
        public int Calls { get; private set; }
        public string? SelectedUrl { get; private set; }
        public string? SelectedManufacturer { get; private set; }
        public EvidenceStatus Status { get; set; } = EvidenceStatus.Partial;
        public bool FailWithValue { get; init; }
        public string CollectorId { get; set; } = "firmware.release-notes";
        public Func<CancellationToken, Task>? Wait { get; set; }
        public async Task<Result<DiagnosticEvidence>> ReadAsync(string url, string manufacturer, CancellationToken cancellationToken)
        {
            Calls++; SelectedUrl = url; SelectedManufacturer = manufacturer;
            if (Wait is not null) await Wait(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var evidence = new DiagnosticEvidence(Guid.NewGuid(), CollectorId, "Fixture official page", url, DateTimeOffset.UtcNow,
                Status, new("Fixture selected HTTP page, not native validation", 1),
                [new("selectedUrl", "Original selected URL", url), new("notes.observedVersion", "Observed version")],
                [new(IssueCode.NotSupported, "Fixture page cannot prove version or update availability")]);
            return FailWithValue ? Result<DiagnosticEvidence>.FailureWithValue(evidence, ErrorCode.DependencyUnavailable, "fixture page unavailable")
                : Result<DiagnosticEvidence>.Success(evidence);
        }
    }
    private sealed class CancelledFirmwareReader : IFirmwareNotesReader
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls { get; private set; }
        public async Task<Result<DiagnosticEvidence>> ReadAsync(string url, string manufacturer, CancellationToken cancellationToken)
        {
            Calls++;
            Entered.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            var evidence = new DiagnosticEvidence(Guid.NewGuid(), "firmware.release-notes", "Fixture cancelled response", url,
                DateTimeOffset.UtcNow, EvidenceStatus.Partial, new("Fixture prefix, not a complete HTTP response", 1),
                [new("sourceBytesBase64", "Captured original prefix bytes", "AQID"), new("bodyComplete", "Complete response received", "false")],
                [new(IssueCode.ReadFailed, "Fixture request cancelled after capturing a prefix")]);
            return Result<DiagnosticEvidence>.FailureWithValue(evidence, ErrorCode.Cancelled, "Fixture HTTP read cancelled; captured prefix retained");
        }
    }
    private sealed class Store : ISessionStore
    {
        public List<DiagnosticSession> Saved { get; } = [];
        public bool FailSave { get; set; }
        public Task<Result<bool>> SaveAsync(DiagnosticSession session, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FailSave) return Task.FromResult(Result<bool>.Failure(ErrorCode.StorageFailure, "fixture storage failure"));
            Saved.Add(session); return Task.FromResult(Result<bool>.Success(true));
        }
        public Task<Result<IReadOnlyList<SessionSummary>>> ListAsync(CancellationToken cancellationToken) => Task.FromResult(Result<IReadOnlyList<SessionSummary>>.Success(
            Saved.Select(session => new SessionSummary(session.Id, session.StartedAt, session.Status, session.Findings.Count)).ToArray()));
        public Task<Result<DiagnosticSession>> GetAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(Result<DiagnosticSession>.Success(Saved.Single(session => session.Id == id)));
    }
}
