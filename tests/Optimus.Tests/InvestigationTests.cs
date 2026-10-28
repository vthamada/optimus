using Optimus.Application;
using Optimus.Domain;
using Optimus.Infrastructure;
using Optimus.Infrastructure.AI;

namespace Optimus.Tests;

public sealed class InvestigationTests
{
    [Fact]
    public async Task DeterministicInvestigationExecutesEachNamedSourceOnceAndPreservesUnrelatedEvidence()
    {
        var sources = Sources();
        var priorItem = Evidence("unrelated", [new("original", "Original", "preserve")]);
        var prior = Session([priorItem]);
        var result = await new InstabilityInvestigationService(Bind(sources)).RunAsync(prior, new("Meu PC reinicia sozinho"), null, CancellationToken.None);
        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal("CatalogCompleted", result.Value!.StopReason);
        Assert.Equal(3, result.Value.Steps.Count);
        Assert.All(sources.Values, source => Assert.Equal(1, source.Calls));
        Assert.Same(priorItem, result.Value.Session.Evidence.Single(item => item.CollectorId == "unrelated"));
        var hypothesis = result.Value.Hypotheses.Single(item => item.Id == "unexpected-shutdown");
        Assert.Equal("windows.restart-history", result.Value.Session.Evidence.Single(item => item.Id == Assert.Single(hypothesis.EvidenceIds)).CollectorId);
        Assert.Equal("false", result.Value.Session.Evidence.Single(item => item.CollectorId == InstabilityInvestigationService.AssessmentCollectorId).Facts.Single(fact => fact.Key == "causalDiagnosisConfirmed").TextValue);
    }

    [Fact]
    public async Task SelectedToolCannotBeRepeatedByThePlanner()
    {
        var sources = Sources();
        var planner = new Planner(_ => Result<InvestigationDecision>.Success(new(InvestigationTool.RestartHistory, "fixture")));
        var result = await new InstabilityInvestigationService(Bind(sources), planner).RunAsync(null, new("Reinícios", UseLocalAi: true), null, CancellationToken.None);
        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCode.InvalidResponse, result.Error!.Code);
        Assert.Single(result.Value!.Steps);
        Assert.Equal(1, sources[InvestigationTool.RestartHistory].Calls);
        Assert.Equal(0, sources[InvestigationTool.MemoryResults].Calls);
        Assert.Equal(SessionStatus.Partial, result.Value.Session.Status);
    }

    [Fact]
    public async Task CancellationRetainsCompletedStepWithoutStartingTheNextTool()
    {
        using var cancel = new CancellationTokenSource();
        var sources = Sources();
        sources[InvestigationTool.MemoryResults].Before = token => { cancel.Cancel(); token.ThrowIfCancellationRequested(); };
        var result = await new InstabilityInvestigationService(Bind(sources)).RunAsync(null, new("Falhas"), null, cancel.Token);
        Assert.Equal(ErrorCode.Cancelled, result.Error!.Code);
        Assert.Equal("Cancelled", result.Value!.StopReason);
        Assert.Single(result.Value.Steps);
        Assert.Contains(result.Value.Session.Evidence, item => item.CollectorId == "windows.restart-history");
        Assert.Equal(0, sources[InvestigationTool.HardwareProfile].Calls);
    }

    [Fact]
    public async Task StepLimitIsExplicitRatherThanClaimingCatalogCompletion()
    {
        var sources = Sources();
        var result = await new InstabilityInvestigationService(Bind(sources)).RunAsync(null, new("Falhas", MaximumSteps: 1), null, CancellationToken.None);
        Assert.Equal("StepLimit", result.Value!.StopReason);
        Assert.Single(result.Value.Steps);
        Assert.Equal(SessionStatus.Partial, result.Value.Session.Status);
        Assert.Equal(0, sources[InvestigationTool.MemoryResults].Calls);
    }

    [Fact]
    public async Task CancellationAtTheFinalClockRetainsLastCompletedToolWithoutClaimingSuccess()
    {
        using var cancel = new CancellationTokenSource();
        var sources = Sources();
        var bindings = Bind(sources).Where(pair => pair.Key == InvestigationTool.RestartHistory).ToDictionary();
        var result = await new InstabilityInvestigationService(bindings, clock: new FinalClock(cancel)).RunAsync(null, new("Falhas"), null, cancel.Token);
        Assert.Equal(ErrorCode.Cancelled, result.Error!.Code);
        Assert.Equal("Cancelled", result.Value!.StopReason);
        Assert.Single(result.Value.Steps);
        Assert.Equal(SessionStatus.Partial, result.Value.Session.Status);
    }

    [Fact]
    public async Task TerminalModelDecisionPreservesItsReceiptWithoutInventingAnExecutedStep()
    {
        var decision = new InvestigationDecision(null, "IA local: sem próxima ferramenta", @"C:\Receipts\decision.json");
        var result = await new InstabilityInvestigationService(Bind(Sources()), new Planner(_ => Result<InvestigationDecision>.Success(decision)))
            .RunAsync(null, new("Falhas", UseLocalAi: true), null, CancellationToken.None);
        Assert.True(result.IsSuccess);
        Assert.Equal("PlannerStopped", result.Value!.StopReason);
        Assert.Empty(result.Value.Steps);
        Assert.Equal(decision, result.Value.TerminalDecision);
        var trace = result.Value.Session.Evidence.Single(item => item.CollectorId == InstabilityInvestigationService.AssessmentCollectorId).Facts.Single(fact => fact.Key == "traceJson").TextValue!;
        using var document = System.Text.Json.JsonDocument.Parse(trace);
        Assert.Equal(decision.ReceiptPath, document.RootElement.GetProperty("terminalDecision").GetProperty("receiptPath").GetString());
    }

    [Fact]
    public async Task BindingsAreCapturedAndForeignCoverageCannotEnterTheSession()
    {
        var sources = Sources();
        var bindings = Bind(sources);
        var service = new InstabilityInvestigationService(bindings);
        bindings.Clear();
        sources[InvestigationTool.MemoryResults].ReportedId = "foreign";
        var result = await service.RunAsync(null, new("Falhas"), null, CancellationToken.None);
        Assert.Equal(ErrorCode.InvalidData, result.Error!.Code);
        Assert.Single(result.Value!.Steps);
        Assert.DoesNotContain(result.Value.Session.Evidence, item => item.CollectorId == "foreign");
        Assert.Equal(0, sources[InvestigationTool.HardwareProfile].Calls);
    }

    [Fact]
    public async Task MissingPlannerOrWrongCompositionCannotFallBackToAnotherOperation()
    {
        var sources = Sources();
        var missing = await new InstabilityInvestigationService(Bind(sources)).RunAsync(null, new("Falhas", UseLocalAi: true), null, CancellationToken.None);
        Assert.Equal(ErrorCode.DependencyUnavailable, missing.Error!.Code);
        var bindings = Bind(sources);
        bindings[InvestigationTool.RestartHistory] = new("wrong", new([sources[InvestigationTool.RestartHistory]]));
        var wrong = await new InstabilityInvestigationService(bindings).RunAsync(null, new("Falhas"), null, CancellationToken.None);
        Assert.Equal(ErrorCode.InvalidConfiguration, wrong.Error!.Code);
        Assert.All(sources.Values, source => Assert.Equal(0, source.Calls));
    }

    [Fact]
    public async Task UnavailableResultsDoNotCreateHealthyOrMemoryFailureConclusions()
    {
        var sources = Sources();
        foreach (var source in sources.Values) source.Unavailable = true;
        var result = await new InstabilityInvestigationService(Bind(sources)).RunAsync(null, new("Falhas"), null, CancellationToken.None);
        Assert.True(result.IsSuccess);
        Assert.Equal(SessionStatus.Partial, result.Value!.Session.Status);
        Assert.Empty(result.Value.Hypotheses);
        Assert.All(result.Value.Steps, step => Assert.NotEmpty(step.EvidenceIds));
    }

    [Fact]
    public void HardwareReferencesRequireExactKnownManufacturerAndPreserveModelAndEvidenceIdentity()
    {
        var hardware = Evidence("windows.hardware-profile", [new("system.0.Manufacturer", "Manufacturer", "Dell Inc."), new("system.0.Model", "Model", "Selected model Ω")]);
        var reference = Assert.Single(FirmwareSupportCatalog.FromEvidence([hardware]));
        Assert.Equal("https://www.dell.com/support/home/", reference.Url);
        Assert.Equal("Selected model Ω", reference.Model);
        Assert.Equal(hardware.Id, reference.EvidenceId);
        Assert.Null(FirmwareSupportCatalog.OfficialUrl("untrusted Dell URL https://other.example"));
        Assert.Empty(FirmwareSupportCatalog.FromEvidence([hardware with { Status = EvidenceStatus.Unavailable }]));
    }

    [Fact]
    public void CompositionDoesNotCreateDirectoriesMerelyByPreparingOptionalDumpTools()
    {
        string root = Path.Combine(Path.GetTempPath(), "optimus-no-startup-io", Guid.NewGuid().ToString("N"));
        var services = Composition.CreateInstability(root);
        _ = services.CreateInvestigation(30, new(@"C:\Selected\cdb.exe", @"C:\Selected\dump.dmp", @"C:\Selected\symbols"));
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public async Task LocalPlannerSelectsOnlyAReferenceFromTheCurrentProposalCatalog()
    {
        var context = Context();
        var assistant = new Assistant(session => Answer(session));
        var result = await new LocalInvestigationPlanner(assistant, new("unused", "unused")).DecideAsync(context, CancellationToken.None);
        Assert.True(result.IsSuccess);
        Assert.Equal(InvestigationTool.RestartHistory, result.Value!.Tool);
        var received = Assert.IsType<DiagnosticSession>(assistant.Received);
        Assert.Single(received.Evidence);
        Assert.Equal("optimus.investigation-proposals", received.Evidence[0].CollectorId);
        Assert.Contains("Proposta permitida", received.Evidence[0].Facts[0].TextValue);
    }

    [Theory]
    [InlineData("tool.FlashBios")]
    [InlineData("tool.RestartHistory; shutdown")]
    public async Task FabricatedModelReferencesCannotInvokeCommands(string key)
    {
        var assistant = new Assistant(session => Answer(session, key));
        var result = await new LocalInvestigationPlanner(assistant, new("unused", "unused")).DecideAsync(Context(), CancellationToken.None);
        Assert.Equal(ErrorCode.InvalidResponse, result.Error!.Code);
    }

    [Fact]
    public async Task FreeTextAndStaleReferencesAreRejectedByThePlannerAdapter()
    {
        var freeText = new Assistant(session => Answer(session) with { Mode = AssistantMode.Unspecified });
        Assert.Equal(ErrorCode.InvalidResponse, (await new LocalInvestigationPlanner(freeText, new("unused", "unused")).DecideAsync(Context(), CancellationToken.None)).Error!.Code);
        var stale = new Assistant(session => Answer(session) with { SessionId = Guid.NewGuid() });
        Assert.Equal(ErrorCode.InvalidResponse, (await new LocalInvestigationPlanner(stale, new("unused", "unused")).DecideAsync(Context(), CancellationToken.None)).Error!.Code);
    }

    [Fact]
    public async Task ValidEmptyModelSelectionStopsWithoutInventingAnotherTool()
    {
        var empty = new Assistant(session => Answer(session) with { Insights = [], SummaryEvidenceIds = [] });
        var result = await new LocalInvestigationPlanner(empty, new("unused", "unused")).DecideAsync(Context(), CancellationToken.None);
        Assert.True(result.IsSuccess);
        Assert.Null(result.Value!.Tool);
    }

    [Fact]
    public async Task ActualTransportKeepsContextAndEveryAlternativeOrRejectsBeforeInference()
    {
        var assistant = new Assistant(session => Answer(session));
        var result = await new LocalInvestigationPlanner(assistant, new("unused", "unused")).DecideAsync(Context(), CancellationToken.None);
        Assert.True(result.IsSuccess);
        var request = assistant.Received!;
        var selected = AssistantProtocol.Select(request, "Selecione a próxima ferramenta de investigação", 6400);
        Assert.NotNull(selected);
        Assert.All(InstabilityInvestigationService.Catalog.Take(3), choice => Assert.Contains(selected.Facts.Values, fact => fact.FactKey == "tool." + choice.Tool));
        Assert.Contains(selected.Facts.Values, fact => fact.FactKey == "investigation.context");
        var tooSmall = new Assistant(session => Answer(session));
        var refused = await new LocalInvestigationPlanner(tooSmall, new("unused", "unused", ContextTokens: 1800)).DecideAsync(Context(), CancellationToken.None);
        Assert.Equal(ErrorCode.InvalidData, refused.Error!.Code);
        Assert.Null(tooSmall.Received);
    }

    [Fact]
    public void InvestigationSelectionLimitIsBoundInTheActualSchemaAndParserWhileDefaultRemainsSix()
    {
        var item = Evidence("catalog", [new("tool.A", "ferramenta", "A"), new("tool.B", "ferramenta", "B")]);
        var session = Session([item]);
        var single = AssistantProtocol.Select(session, "ferramenta", 6400, selectionLimit: 1)!;
        using var schema = System.Text.Json.JsonDocument.Parse(single.Schema);
        Assert.Equal(1, schema.RootElement.GetProperty("properties").GetProperty("selectedFacts").GetProperty("maxItems").GetInt32());
        var keys = single.Facts.Keys.ToArray();
        var raw = System.Text.Json.JsonSerializer.Serialize(new { selectedFacts = keys });
        var rejected = AssistantProtocol.Parse(raw, session, single, "fixture", new(TimeSpan.Zero), null);
        Assert.False(rejected.IsSuccess);
        var standard = AssistantProtocol.Select(session, "ferramenta", 6400)!;
        Assert.True(AssistantProtocol.Parse(raw, session, standard, "fixture", new(TimeSpan.Zero), null).IsSuccess);
    }

    private static InvestigationContext Context() => new(null, "Computador reinicia sozinho", InstabilityInvestigationService.Catalog.Take(3).ToArray(), []);
    private static AssistantAnswer Answer(DiagnosticSession session, string? key = null)
    {
        var item = Assert.Single(session.Evidence);
        return new(session.Id, DateTimeOffset.UtcNow, "fixture proposal", [item.Id],
            [new("proposta", "sem texto executável", [item.Id], new(item.Id, key ?? item.Facts[0].Key, item.CapturedAt))], [], "fixture", new(TimeSpan.Zero), Mode: AssistantMode.Extractive);
    }
    private static Dictionary<InvestigationTool, Collector> Sources() => new()
    {
        [InvestigationTool.RestartHistory] = new("windows.restart-history", "restart.kernelPower41Observed", 2),
        [InvestigationTool.MemoryResults] = new("windows.memory-diagnostic-results", "memory.reportedFailEvents", 0),
        [InvestigationTool.HardwareProfile] = new("windows.hardware-profile", "noPhysicalDiagnosis", 0)
    };
    private static Dictionary<InvestigationTool, MaintenanceQueryBinding> Bind(Dictionary<InvestigationTool, Collector> sources) => sources.ToDictionary(pair => pair.Key, pair => new MaintenanceQueryBinding(pair.Value.Id, new([pair.Value])));
    private static DiagnosticEvidence Evidence(string id, DiagnosticFact[] facts) => new(Guid.NewGuid(), id, id, "fixture", DateTimeOffset.UtcNow, EvidenceStatus.Complete, new("fixture, no native provider", 1), facts, []);
    private static DiagnosticSession Session(DiagnosticEvidence[] evidence) => new(Guid.NewGuid(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, SessionStatus.Complete, evidence, []);
    private sealed class Collector(string id, string key, decimal value) : IDiagnosticCollector
    {
        public string Id => ReportedId ?? id;
        public string DisplayName => id;
        public int Calls { get; private set; }
        public string? ReportedId { get; set; }
        public bool Unavailable { get; set; }
        public Action<CancellationToken>? Before { get; set; }
        public Task<IReadOnlyList<DiagnosticEvidence>> CollectAsync(CancellationToken cancellationToken)
        {
            Calls++; Before?.Invoke(cancellationToken); cancellationToken.ThrowIfCancellationRequested();
            var item = Evidence(ReportedId ?? id, Unavailable ? [] : [new(key, key, NumberValue: value)]);
            return Task.FromResult<IReadOnlyList<DiagnosticEvidence>>([item with { Status = Unavailable ? EvidenceStatus.Unavailable : EvidenceStatus.Complete }]);
        }
    }
    private sealed class Planner(Func<InvestigationContext, Result<InvestigationDecision>> select) : IInvestigationPlanner
    {
        public Task<Result<InvestigationDecision>> DecideAsync(InvestigationContext context, CancellationToken cancellationToken) => Task.FromResult(select(context));
    }
    private sealed class FinalClock(CancellationTokenSource cancel) : TimeProvider
    {
        private int _calls;
        public override DateTimeOffset GetUtcNow()
        { if (++_calls == 3) cancel.Cancel(); return DateTimeOffset.UtcNow; }
    }
    private sealed class Assistant(Func<DiagnosticSession, AssistantAnswer> respond) : IAssistantService
    {
        public DiagnosticSession? Received { get; private set; }
        public Task<Result<AssistantAnswer>> AnalyzeAsync(DiagnosticSession session, string question, LocalAiSettings settings, CancellationToken cancellationToken)
        { Received = session; return Task.FromResult(Result<AssistantAnswer>.Success(respond(session))); }
    }
}
