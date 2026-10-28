using System.Text.Json;
using Optimus.Domain;

namespace Optimus.Application;

public enum InvestigationTool { RestartHistory, MemoryResults, HardwareProfile, AnalyzeDump }
public sealed record InvestigationOptions(string Question, int MaximumSteps = 4, bool UseLocalAi = false);
public sealed record InvestigationToolChoice(InvestigationTool Tool, string CollectorId, string Label);
public sealed record InvestigationContext(DiagnosticSession? Session, string Question, IReadOnlyList<InvestigationToolChoice> Available,
    IReadOnlyList<InvestigationStep> Completed);
public sealed record InvestigationDecision(InvestigationTool? Tool, string Source, string? ReceiptPath = null);
public sealed record InvestigationStep(int Index, InvestigationTool Tool, string DecisionSource, string? DecisionReceipt,
    IReadOnlyList<Guid> EvidenceIds, DateTimeOffset CompletedAt);
public sealed record InvestigationHypothesis(string Id, string Title, string Explanation,
    IReadOnlyList<Guid> EvidenceIds, IReadOnlyList<string> NextChecks);
public sealed record FirmwareSupportReference(string Manufacturer, string Model, string Url, Guid EvidenceId);
public sealed record InvestigationRun(DiagnosticSession Session, string StopReason, IReadOnlyList<InvestigationStep> Steps,
    IReadOnlyList<InvestigationHypothesis> Hypotheses, IReadOnlyList<FirmwareSupportReference> FirmwareReferences,
    InvestigationDecision? TerminalDecision = null);
public interface IInvestigationPlanner
{
    Task<Result<InvestigationDecision>> DecideAsync(InvestigationContext context, CancellationToken cancellationToken);
}

/// <summary>Owns the finite read-only tool catalog. No model text can become a command, path or operation.</summary>
public sealed class InstabilityInvestigationService(
    IReadOnlyDictionary<InvestigationTool, MaintenanceQueryBinding> tools, IInvestigationPlanner? planner = null,
    TimeProvider? clock = null)
{
    public const string AssessmentCollectorId = "optimus.instability-investigation";
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly IReadOnlyDictionary<InvestigationTool, MaintenanceQueryBinding> _tools = new Dictionary<InvestigationTool, MaintenanceQueryBinding>(tools);
    public static readonly IReadOnlyList<InvestigationToolChoice> Catalog = Array.AsReadOnly(new[]
    {
        new InvestigationToolChoice(InvestigationTool.RestartHistory, "windows.restart-history", "Histórico de reinicializações e WHEA"),
        new InvestigationToolChoice(InvestigationTool.MemoryResults, "windows.memory-diagnostic-results", "Resultados do diagnóstico de memória do Windows"),
        new InvestigationToolChoice(InvestigationTool.HardwareProfile, "windows.hardware-profile", "Placa-mãe, BIOS, CPU e módulos de memória"),
        new InvestigationToolChoice(InvestigationTool.AnalyzeDump, "windows.crash-dump", "Dump e depurador locais previamente selecionados")
    });

    public async Task<Result<InvestigationRun>> RunAsync(DiagnosticSession? previous, InvestigationOptions options,
        IProgress<string>? progress, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.Question) || options.Question.Length > 400 || options.MaximumSteps is < 1 or > 4
            || previous is not null && !SessionValidation.IsValid(previous))
            return Result<InvestigationRun>.Failure(ErrorCode.InvalidData, "Informe uma pergunta de até 400 caracteres, sessão válida e limite de um a quatro passos.");
        if (options.UseLocalAi && planner is null)
            return Result<InvestigationRun>.Failure(ErrorCode.DependencyUnavailable, "Planejador local não configurado; escolha o modo sem IA ou selecione executável e modelo.");
        if (_tools.Count == 0)
            return Result<InvestigationRun>.Failure(ErrorCode.DependencyUnavailable, "Nenhuma ferramenta de investigação está configurada.");
        foreach (var pair in _tools)
        {
            var contract = Catalog.SingleOrDefault(entry => entry.Tool == pair.Key);
            if (contract is null || pair.Value.CollectorId != contract.CollectorId)
                return Result<InvestigationRun>.Failure(ErrorCode.InvalidConfiguration, "Composição de investigação fora do catálogo nomeado.");
        }
        var started = _clock.GetUtcNow();
        var current = previous;
        List<InvestigationStep> steps = [];
        var attempted = new HashSet<InvestigationTool>();
        string stop = "StepLimit";
        AppError? failure = null;
        InvestigationDecision? terminal = null;
        var completedAt = started;
        try
        {
            for (int index = 0; index < options.MaximumSteps; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var available = Catalog.Where(entry => _tools.ContainsKey(entry.Tool) && !attempted.Contains(entry.Tool)).ToArray();
                if (available.Length == 0) { stop = "CatalogCompleted"; break; }
                InvestigationDecision decision;
                if (options.UseLocalAi)
                {
                    progress?.Report("A IA está selecionando a próxima consulta permitida…");
                    var selected = await planner!.DecideAsync(new(current, options.Question, Array.AsReadOnly(available), steps.ToArray()), cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!selected.IsSuccess || selected.Value is null) { failure = selected.Error ?? new(ErrorCode.InvalidResponse, "Planejador não entregou uma decisão."); stop = "PlannerFailed"; break; }
                    decision = selected.Value!;
                    if (decision.Tool is null) { stop = "PlannerStopped"; terminal = decision; break; }
                }
                else decision = new(available[0].Tool, "Catálogo determinístico sem IA");
                if (decision.Tool is not { } tool || !available.Any(entry => entry.Tool == tool))
                { failure = new(ErrorCode.InvalidResponse, "O planejador selecionou ferramenta desconhecida, repetida ou não configurada."); stop = "InvalidDecision"; break; }
                attempted.Add(tool);
                var binding = _tools[tool];
                var collected = await binding.RunAsync(progress, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (!collected.IsSuccess) { failure = collected.Error; stop = "InvalidCoverage"; break; }
                var merged = EvidenceSessionUpdates.ReplaceOwned(current, collected.Value!, new HashSet<string>([binding.CollectorId], StringComparer.Ordinal));
                if (!merged.IsSuccess) { failure = merged.Error; stop = "MergeFailed"; break; }
                current = merged.Value!;
                steps.Add(new(index + 1, tool, decision.Source, decision.ReceiptPath,
                    collected.Value!.Evidence.Select(item => item.Id).ToArray(), _clock.GetUtcNow()));
                if (index + 1 == options.MaximumSteps && Catalog.All(entry => !_tools.ContainsKey(entry.Tool) || attempted.Contains(entry.Tool))) stop = "CatalogCompleted";
            }
            cancellationToken.ThrowIfCancellationRequested();
            completedAt = _clock.GetUtcNow();
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { stop = "Cancelled"; failure = new(ErrorCode.Cancelled, "Investigação cancelada; etapas concluídas permanecem no resultado parcial."); }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or NotSupportedException)
        { stop = "ExecutionFailed"; failure = new(error is UnauthorizedAccessException ? ErrorCode.PermissionDenied : error is InvalidDataException ? ErrorCode.InvalidData : ErrorCode.ToolFailed, "Investigação interrompida: " + error.Message); }
        if (completedAt == started) completedAt = _clock.GetUtcNow();
        var empty = new DiagnosticSession(Guid.NewGuid(), started, completedAt, SessionStatus.Partial, [], []);
        var report = InstabilityAssessment.Build(current ?? empty, options.Question, steps.ToArray(), stop, failure, completedAt, terminal);
        if (failure is null && cancellationToken.IsCancellationRequested)
        {
            stop = "Cancelled";
            failure = new(ErrorCode.Cancelled, "Cancelamento recebido na finalização; etapas concluídas permanecem no resultado parcial.");
            report = InstabilityAssessment.Build(current ?? empty, options.Question, steps.ToArray(), stop, failure, completedAt, terminal);
        }
        return failure is null ? Result<InvestigationRun>.Success(report)
            : Result<InvestigationRun>.FailureWithValue(report, failure.Code, failure.Message, failure.PendingCleanupPaths);
    }
}

/// <summary>Derives only catalogued hypotheses from normalized numeric facts, never from display text or raw debugger/XML output.</summary>
public static class InstabilityAssessment
{
    public static InvestigationRun Build(DiagnosticSession session, string question, IReadOnlyList<InvestigationStep> steps,
        string stop, AppError? failure, DateTimeOffset now, InvestigationDecision? terminalDecision = null)
    {
        List<InvestigationHypothesis> hypotheses = [];
        Add("unexpected-shutdown", "Desligamentos inesperados precisam de correlação",
            "Há registros Kernel-Power 41 no recorte observado. Eles não distinguem sozinhos energia, travamento e bugcheck; 6008 não foi somado como outro reinício.",
            "windows.restart-history", "restart.kernelPower41Observed",
            ["Correlacionar horários e campos de bugcheck com os dumps disponíveis.", "Verificar contexto de energia e carga no momento das ocorrências."]);
        Add("hardware-error-signals", "Eventos WHEA justificam investigação de hardware",
            "O Windows registrou eventos WHEA no recorte. A presença desses eventos não identifica por si só o componente defeituoso.",
            "windows.restart-history", "restart.wheaEventsObserved",
            ["Revisar os campos originais dos eventos WHEA e suas limitações.", "Comparar hardware, firmware e resultados de testes controlados."]);
        Add("kernel-failure", "Falha de kernel precisa de análise de dump",
            "Foram observados registros de bugcheck. Um módulo citado em um dump pode ser vítima da corrupção; não é prova automática de autoria.",
            "windows.restart-history", "restart.bugcheckEventsObserved",
            ["Selecionar um dump correspondente ao horário e analisar com símbolos locais adequados."]);
        Add("memory-test-signals", "Teste reportou falha no subsistema de memória",
            "Há resultado explícito de falha em diagnóstico de memória. RAM, configuração, controlador e outros componentes continuam como hipóteses a distinguir.",
            "windows.memory-diagnostic-results", "memory.reportedFailEvents",
            ["Executar novo teste controlado após salvar o trabalho; reinício depende de confirmação separada.", "Conferir parâmetros de memória no firmware e comparar com a especificação do fabricante."]);
        Add("imported-memory-errors", "O relatório importado contém linhas de erro",
            "O relatório contém divergências observadas. Bits repetidos são posições dentro dos valores comparados, não identificação automática de um módulo físico.",
            "memory.test-report", "tests.errorRowsObserved",
            ["Conferir quantas linhas foram retidas e a cobertura do relatório original.", "Comparar testes em condições controladas e consultar o suporte do fabricante."], "tests.reportedErrors");
        var references = FirmwareSupportCatalog.FromEvidence(session.Evidence);
        var assessment = new DiagnosticEvidence(Guid.NewGuid(), InstabilityInvestigationService.AssessmentCollectorId,
            "Investigação de instabilidade", "Hipóteses derivadas e roteiro; não é laudo físico", now,
            failure is null && stop == "CatalogCompleted" ? EvidenceStatus.Complete : EvidenceStatus.Partial,
            new("Até quatro ferramentas nomeadas, no máximo uma execução por ferramenta. Hipóteses vêm de regras declaradas sobre fatos normalizados; a IA opcional seleciona a próxima consulta, sem comandos livres ou diagnóstico causal gerado. Cobertura de cada fonte e datas originais continuam válidas. Firmware recente, perfil XMP/EXPO, módulo defeituoso e recuperação não foram confirmados.", steps.Count, 4, stop == "StepLimit"),
            [new("question", "Pergunta investigada", question), new("stopReason", "Motivo de parada", stop),
             new("steps", "Etapas concluídas", NumberValue: steps.Count), new("hypotheses", "Hipóteses sustentadas por sinais", NumberValue: hypotheses.Count),
             new("causalDiagnosisConfirmed", "Causa física confirmada", "false"),
             new("traceJson", "Rastro de decisões e referências (JSON)", JsonSerializer.Serialize(new { schemaVersion = 1, steps, terminalDecision, hypotheses, firmware = references }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } }), Detailed: true)],
            failure is null ? [] : [new(failure.Code == ErrorCode.PermissionDenied ? IssueCode.PermissionDenied : IssueCode.ReadFailed, failure.Message)]);
        var items = session.Evidence.Where(item => item.CollectorId != InstabilityInvestigationService.AssessmentCollectorId).Append(assessment).ToArray();
        var derived = new DiagnosticSession(Guid.NewGuid(), session.StartedAt, now,
            failure is null && session.Status == SessionStatus.Complete && items.All(item => item.Status == EvidenceStatus.Complete) ? SessionStatus.Complete : SessionStatus.Partial,
            items, DiagnosticRules.Evaluate(items));
        return new(derived, stop, steps, hypotheses.ToArray(), references, terminalDecision);

        void Add(string id, string title, string explanation, string collector, string key, string[] checks, string? alternateKey = null)
        {
            var sources = session.Evidence.Where(item => item.CollectorId == collector && item.Status != EvidenceStatus.Unavailable
                && item.Facts.Any(fact => (fact.Key == key || alternateKey is not null && fact.Key == alternateKey) && fact.NumberValue is > 0)).Select(item => item.Id).ToArray();
            if (sources.Length > 0) hypotheses.Add(new(id, title, explanation, sources, checks));
        }
    }
}
