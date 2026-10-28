using System.Text.Json;
using Optimus.Application;
using Optimus.Domain;

namespace Optimus.Infrastructure.AI;

/// <summary>Reuses the validated extractive transport to select a proposal, never to synthesize executable tool calls.</summary>
public sealed class LocalInvestigationPlanner(IAssistantService assistant, LocalAiSettings settings) : IInvestigationPlanner
{
    public async Task<Result<InvestigationDecision>> DecideAsync(InvestigationContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (context.Available.Count is < 1 or > 4 || context.Available.Select(choice => choice.Tool).Distinct().Count() != context.Available.Count
            || context.Available.Any(choice => !InstabilityInvestigationService.Catalog.Contains(choice)))
            return Result<InvestigationDecision>.Failure(ErrorCode.InvalidConfiguration, "Catálogo de propostas inválido.");
        var signals = context.Session?.Evidence.Where(item => item.CollectorId is "windows.restart-history" or "windows.memory-diagnostic-results" or "memory.test-report" or "windows.crash-dump")
            .Take(8).Select(item => new
            {
                item.Id,
                item.CollectorId,
                item.CapturedAt,
                item.Status,
                signals = item.Facts.Where(fact => fact.Key is "restart.kernelPower41Observed" or "restart.wheaEventsObserved" or "restart.bugcheckEventsObserved"
                    or "memory.reportedFailEvents" or "memory.reportedPassEvents" or "tests.errorRowsObserved" or "tests.singleBitRowsObserved")
                    .Take(8).Select(fact => new { fact.Key, fact.NumberValue }).ToArray(),
                issues = item.Issues.Count,
                item.Coverage.LimitReached
            }).ToArray();
        var summary = JsonSerializer.Serialize(new { signals, completed = context.Completed.Select(step => step.Tool.ToString()).ToArray() });
        if (summary.Length > 4096) return Result<InvestigationDecision>.Failure(ErrorCode.InvalidData, "Contexto de seleção acima do orçamento.");
        var now = DateTimeOffset.UtcNow;
        var catalogId = Guid.NewGuid();
        var choices = context.Available.ToDictionary(choice => "tool." + choice.Tool, StringComparer.Ordinal);
        const string contextKey = "investigation.context";
        DiagnosticFact[] facts = [.. choices.Select(pair => new DiagnosticFact(pair.Key, "Ferramenta de investigação: " + pair.Value.Label,
            "Proposta permitida, não uma observação do computador. " + pair.Value.Tool + ": " + pair.Value.Label)),
            new(contextKey, "Contexto das ferramentas de investigação", summary)];
        var catalog = new DiagnosticEvidence(catalogId, "optimus.investigation-proposals", "Propostas permitidas para o próximo passo",
            "Catálogo de consultas; não contém medições inventadas", now, EvidenceStatus.Complete,
            new("Uma proposta por ferramenta ainda não executada e um contexto compartilhado. Valores de sinais preservam IDs e datas das fontes; ausência de sinal não é prova de saúde.", facts.Length, 5), facts, []);
        var request = new DiagnosticSession(Guid.NewGuid(), now, now, SessionStatus.Complete, [catalog], []);
        var question = "Selecione a próxima ferramenta de investigação adequada à pergunta. Escolha uma proposta permitida, ou nenhuma para encerrar. Pergunta: " + context.Question;
        var promptBudget = Math.Min(16_384, settings.ContextTokens - settings.MaximumOutputTokens - 1024);
        var prepared = AssistantProtocol.Select(request, question, promptBudget, selectionLimit: 1);
        if (prepared is null || facts.Any(fact => !prepared.Facts.Values.Any(bound => bound.EvidenceId == catalogId && bound.FactKey == fact.Key)))
            return Result<InvestigationDecision>.Failure(ErrorCode.InvalidData, "O contexto e todas as alternativas precisam caber juntos no transporte da IA. Aumente o contexto ou reduza a pergunta; nenhuma alternativa foi omitida silenciosamente.");
        var answer = await assistant.AnalyzeAsync(request, question,
            settings with { MaximumSelectedFacts = 1 }, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (!answer.IsSuccess) return Result<InvestigationDecision>.Failure(answer.Error!.Code, answer.Error.Message, answer.Error.PendingCleanupPaths);
        var selected = answer.Value!;
        if (selected.SessionId != request.Id || selected.Mode != AssistantMode.Extractive || selected.Insights.Count > 1)
            return Result<InvestigationDecision>.Failure(ErrorCode.InvalidResponse, "A seleção local não corresponde ao catálogo proposto.");
        foreach (var insight in selected.Insights)
            if (insight.Source is not { } reference || reference.EvidenceId != catalogId || reference.CapturedAt != now || (!choices.ContainsKey(reference.FactKey) && reference.FactKey != contextKey))
                return Result<InvestigationDecision>.Failure(ErrorCode.InvalidResponse, "O modelo selecionou referência fora das propostas permitidas.");
        var chosen = selected.Insights.FirstOrDefault(insight => choices.ContainsKey(insight.Source!.FactKey));
        if (chosen is null) return Result<InvestigationDecision>.Success(new(null, "IA local: nenhuma próxima consulta selecionada", selected.EvidencePath));
        var first = choices[chosen.Source!.FactKey];
        return Result<InvestigationDecision>.Success(new(first.Tool, "IA local: seleção extrativa de proposta", selected.EvidencePath));
    }
}
