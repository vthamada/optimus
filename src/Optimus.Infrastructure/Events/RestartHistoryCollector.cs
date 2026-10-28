using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Text.Json;
using Optimus.Application;
using Optimus.Domain;

namespace Optimus.Infrastructure.Events;

/// <summary>Selected log records, never a count of unique physical restarts or a causal diagnosis.</summary>
public sealed class RestartHistoryCollector : IDiagnosticCollector
{
    public const int EventLimit = 512;
    public const int MaximumRetainedXmlBytes = 1024 * 1024;
    public const int MaximumPayloadBytes = 4 * 1024 * 1024;
    public const int TemporalCorrelationWindowSeconds = 120;
    public const int MaximumTemporalPairs = 32;
    public const int MaximumTemporalCorrelationBytes = 8 * 1024;
    public const string TemporalCorrelationPrefix = "inference.restartTemporal";
    public const string TemporalCorrelationPolicy = "correlação temporal sugerida, não confirmada";
    public const string KernelPower41Observed = "restart.kernelPower41Observed";
    public const string WheaEventsObserved = "restart.wheaEventsObserved";
    public const string BugcheckEventsObserved = "restart.bugcheckEventsObserved";
    public const string UnexpectedShutdown6008Observed = "restart.unexpectedShutdown6008Observed";
    public const string ShutdownRequests1074Observed = "restart.shutdownRequests1074Observed";
    private readonly int _days;
    private readonly Func<DateTimeOffset, DateTimeOffset, CancellationToken, IEnumerable<SystemEventObservation>> _source;
    private readonly TimeProvider _clock;

    public RestartHistoryCollector(int days = 30,
        Func<DateTimeOffset, DateTimeOffset, CancellationToken, IEnumerable<SystemEventObservation>>? source = null,
        TimeProvider? clock = null)
    {
        if (days is < 1 or > 365) throw new ArgumentOutOfRangeException(nameof(days), "A janela deve conter de 1 a 365 dias.");
        _days = days; _source = source ?? WindowsEventReader.ReadRestartHistory; _clock = clock ?? TimeProvider.System;
    }

    public string Id => "windows.restart-history";
    public string DisplayName => "Registros de reinicialização e instabilidade";
    public Task<IReadOnlyList<DiagnosticEvidence>> CollectAsync(CancellationToken cancellationToken) =>
        SelectedSystemEventCollection.CollectAsync(Id, DisplayName, _days, EventLimit, MaximumRetainedXmlBytes, MaximumPayloadBytes,
            _source, _clock, Select, Counters,
            "Kernel-Power/41 é registro de boot sem encerramento limpo; EventLog/6008 é registro separado de encerramento inesperado; User32/1074 registra um pedido de encerramento/reinício. WER-SystemErrorReporting ou BugCheck/1001 são registros de bugcheck; WHEA-Logger inclui somente Level 2 ou 3. As categorias podem descrever o mesmo episódio: não se somam 41 e 6008, não se contam reinicializações físicas únicas nem se atribui causa ao hardware. Correlações são sugestões temporais mútuas e únicas somente entre registros válidos retidos, em até 120 segundos entre TimeCreated 41/6008, com até 32 pares e reserva de 8 KiB; ambiguidades não são pareadas e referências não são identidades físicas.", cancellationToken,
            addInferences: AddTemporalCorrelations, reservedInferenceBytes: MaximumTemporalCorrelationBytes);

    private static SelectedEventClassification? Select(SystemEventObservation record, WindowsEventXml parsed) => (record.Provider, record.EventId) switch
    {
        ("Microsoft-Windows-Kernel-Power", 41) => new(KernelPower41Observed),
        ("EventLog", 6008) => new(UnexpectedShutdown6008Observed),
        ("User32", 1074) => new(ShutdownRequests1074Observed),
        ("Microsoft-Windows-WER-SystemErrorReporting" or "BugCheck", 1001) => new(BugcheckEventsObserved),
        ("Microsoft-Windows-WHEA-Logger", _) when parsed.Level is 2 or 3 => new(WheaEventsObserved),
        _ => null
    };

    private static IEnumerable<DiagnosticFact> Counters(IReadOnlyDictionary<string, int> counts, bool known)
    {
        yield return Counter(KernelPower41Observed, "Registros Kernel-Power/41 observados (não reinícios únicos)");
        yield return Counter(UnexpectedShutdown6008Observed, "Registros EventLog/6008 observados");
        yield return Counter(ShutdownRequests1074Observed, "Registros User32/1074 de pedidos observados");
        yield return Counter(BugcheckEventsObserved, "Registros selecionados de bugcheck/1001 observados");
        yield return Counter(WheaEventsObserved, "Registros WHEA Level 2/3 observados");
        DiagnosticFact Counter(string key, string label) => new(key, label, NumberValue: known ? counts.GetValueOrDefault(key) : null, Unit: "records");
    }

    private static void AddTemporalCorrelations(List<DiagnosticFact> facts, IReadOnlyList<SelectedSystemEventRecord> records,
        Action<CollectionIssue> addIssue, CancellationToken token)
    {
        var power = records.Where(record => record.Category == KernelPower41Observed).ToArray();
        var shutdown = records.Where(record => record.Category == UnexpectedShutdown6008Observed).ToArray();
        var candidates = power.Select(record => shutdown.Where(other => Close(record, other)).Take(2).ToArray()).ToArray();
        var reverseCounts = shutdown.ToDictionary(record => record.SourceIndex, record => power.Count(other => Close(other, record)));
        int pairs = 0;
        long remaining = MaximumTemporalCorrelationBytes - JsonSerializer.SerializeToUtf8Bytes(Policy(MaximumTemporalPairs), DiagnosticJson.Options).Length;
        for (int index = 0; index < power.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            if (candidates[index].Length != 1 || reverseCounts[candidates[index][0].SourceIndex] != 1) continue;
            var left = power[index];
            var right = candidates[index][0];
            string prefix = TemporalCorrelationPrefix + ".pair." + pairs;
            DiagnosticFact[] selected =
            [
                new(prefix + ".kernelPowerSourceIndex", TemporalCorrelationPolicy + ": índice de origem Kernel-Power/41", NumberValue: left.SourceIndex),
                new(prefix + ".unexpectedShutdownSourceIndex", "Índice de origem EventLog/6008 da sugestão", NumberValue: right.SourceIndex),
                new(prefix + ".kernelPowerRecordId", "RecordID Kernel-Power/41 referenciado pela sugestão", NumberValue: left.Source.RecordId, Detailed: true),
                new(prefix + ".unexpectedShutdownRecordId", "RecordID EventLog/6008 referenciado pela sugestão", NumberValue: right.Source.RecordId, Detailed: true)
            ];
            int cost = JsonSerializer.SerializeToUtf8Bytes(selected, DiagnosticJson.Options).Length;
            if (pairs == MaximumTemporalPairs || cost > remaining)
            { addIssue(new(IssueCode.LimitReached, "Sugestões temporais adicionais não apresentadas: limite de 32 pares ou reserva de 8 KiB atingido; eventos originais preservados, sem contador de reinícios físicos.")); break; }
            remaining -= cost;
            facts.AddRange(selected);
            pairs++;
        }
        facts.AddRange(Policy(pairs));

        static bool Close(SelectedSystemEventRecord left, SelectedSystemEventRecord right) =>
            (left.Source.OccurredAt!.Value - right.Source.OccurredAt!.Value).Duration().Ticks <= TemporalCorrelationWindowSeconds * TimeSpan.TicksPerSecond;
        static DiagnosticFact[] Policy(int count) =>
        [
            new(TemporalCorrelationPrefix + ".policy", TemporalCorrelationPolicy,
                "Somente observações normalizadas, válidas e retidas; par 41/6008 um a um quando ambos têm uma única candidata a até 120 segundos entre TimeCreated. Mesmos horários não estabelecem identidade; ambiguidades não são pareadas. Até 32 sugestões e 8 KiB reservados. Os índices referenciam event.{sourceIndex}, cujos horários/XML originais permanecem preservados. Cobertura de eventos não retidos é desconhecida."),
            new(TemporalCorrelationPrefix + ".pairsPresented", "Sugestões temporais apresentadas (não reinicializações físicas)", NumberValue: count)
        ];
    }
}

internal sealed record SelectedEventClassification(string Category, string? Completion = null);
internal sealed record SelectedSystemEventRecord(int SourceIndex, SystemEventObservation Source, string Category);

/// <summary>Owns the bounded lifecycle and projection used by the two selected System log queries.</summary>
internal static class SelectedSystemEventCollection
{
    public static Task<IReadOnlyList<DiagnosticEvidence>> CollectAsync(string id, string title, int days, int eventLimit,
        int xmlLimit, int payloadLimit,
        Func<DateTimeOffset, DateTimeOffset, CancellationToken, IEnumerable<SystemEventObservation>> source,
        TimeProvider clock, Func<SystemEventObservation, WindowsEventXml, SelectedEventClassification?> select,
        Func<IReadOnlyDictionary<string, int>, bool, IEnumerable<DiagnosticFact>> counters, string interpretationScope,
        CancellationToken cancellationToken,
        Action<List<DiagnosticFact>, IReadOnlyList<SelectedSystemEventRecord>, Action<CollectionIssue>, CancellationToken>? addInferences = null,
        int reservedInferenceBytes = 0) => Task.Run<IReadOnlyList<DiagnosticEvidence>>(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        var to = clock.GetUtcNow();
        var from = to.AddDays(-days);
        var timer = Stopwatch.StartNew();
        var facts = new List<DiagnosticFact>();
        var issues = new List<CollectionIssue>();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var identities = new HashSet<(ulong RecordId, string? Provider, int EventId)>();
        var selectedRecords = new List<SelectedSystemEventRecord>();
        int examined = 0, retained = 0, classified = 0, rejected = 0, issueCount = 0, issueOmitted = 0, xmlBytes = 0;
        bool limited = false, reachedEnd = false;
        long payloadRemaining = payloadLimit - 32 * 1024 - reservedInferenceBytes, issueRemaining = 16 * 1024;
        try
        {
            using var iterator = source(from, to, cancellationToken).GetEnumerator();
            while (true)
            {
                Check();
                bool next = iterator.MoveNext();
                Check();
                if (!next) { reachedEnd = true; break; }
                if (examined == eventLimit)
                { AddIssue(new(IssueCode.LimitReached, $"Sonda de presença confirmou registros além do limite de {eventLimit}; a sonda não foi interpretada nem contada.")); break; }
                int sourceIndex = examined++;
                var record = WindowsEventReader.Interpret(iterator.Current);
                var parsed = record.Xml!;
                foreach (var issue in parsed.Issues) AddIssue(issue);
                bool valid = parsed.MetadataValid && parsed.Channel == "System" && record.OccurredAt is { } stamp && stamp >= from && stamp <= to;
                var selection = valid ? select(record, parsed) : null;
                if (selection is not null && record.RecordId is { } identity && !identities.Add((identity, record.Provider, record.EventId)))
                { selection = null; AddIssue(new(IssueCode.ReadFailed, "EventRecordID/provider/ID repetidos no canal e na mesma consulta; original retido sem duplicar o contador de registros selecionados. Horário não foi usado como identidade.")); }
                if (selection is null)
                { rejected++; AddIssue(new(IssueCode.ReadFailed, "Registro sem metadados válidos ou fora de provider/ID/nível/canal/janela; XML preservado dentro dos limites, sem classificação.")); }
                bool retainXml = parsed.XmlUtf8Bytes <= WindowsEventReader.MaximumXmlBytes;
                int xmlCost = retainXml ? parsed.XmlUtf8Bytes : 0;
                if (xmlCost > xmlLimit - xmlBytes)
                { AddIssue(new(IssueCode.LimitReached, $"Orçamento de XML original retido de {xmlLimit} bytes atingido; nenhum prefixo truncado foi aceito e a cobertura restante é desconhecida.")); break; }
                string prefix = "event." + sourceIndex;
                var selected = new List<DiagnosticFact>
                {
                    new(prefix + ".sourceIndex", "Posição original da fonte nesta consulta", NumberValue: sourceIndex, Detailed: true),
                    new(prefix + ".category", "Categoria validada do registro (sem diagnóstico causal)", selection?.Category),
                    new(prefix + ".provider", "Provider original observado", record.Provider is { Length: <= 2048 } ? record.Provider : null),
                    new(prefix + ".id", "ID original observado", NumberValue: record.EventId),
                    new(prefix + ".recordId", "EventRecordID original do canal", NumberValue: record.RecordId),
                    new(prefix + ".level", "Código Level original do XML", NumberValue: parsed.Level),
                    new(prefix + ".time", "SystemTime original (fuso preservado)", parsed.SystemTime),
                    new(prefix + ".timeParsedUtc", "Instante reconhecido em UTC", record.OccurredAt?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)),
                    new(prefix + ".metadataValid", "Metadados XML consistentes com a fonte e o escopo", valid ? "true" : "false", Detailed: true),
                    new(prefix + ".xml", "XML original completo", retainXml ? record.OriginalXml : null, Detailed: true),
                    new(prefix + ".sourceXmlUtf8Bytes", "Tamanho UTF-8 do XML observado", NumberValue: parsed.XmlUtf8Bytes, Unit: "bytes", Detailed: true),
                    new(prefix + ".dataComplete", "Projeção dos campos estruturados completa no formato suportado", parsed.DataComplete ? "true" : "false", Detailed: true)
                };
                if (selection?.Completion is { } completion) selected.Add(new(prefix + ".reportedCompletion", "CompletionType estruturado reconhecido (não atesta memória atual)", completion));
                foreach (var field in parsed.Data)
                {
                    selected.Add(new($"{prefix}.data.{field.Index}.name", "Nome original do campo estruturado", field.Name, Detailed: true));
                    selected.Add(new($"{prefix}.data.{field.Index}.sourcePath", "Caminho e namespace interpretados pelo leitor XML", field.SourcePath, Detailed: true));
                    selected.Add(new($"{prefix}.data.{field.Index}.value", field.Name is null ? "Valor original sem nome" : "Valor original de " + field.Name, field.Value));
                }
                int cost = JsonSerializer.SerializeToUtf8Bytes(selected, DiagnosticJson.Options).Length;
                if (cost > payloadRemaining)
                { AddIssue(new(IssueCode.LimitReached, $"Orçamento aproximado de projeção serializada de {payloadLimit} bytes atingido; o próximo registro não foi retido nem contado.")); break; }
                payloadRemaining -= cost;
                xmlBytes += xmlCost;
                facts.AddRange(selected);
                retained++;
                if (selection is not null)
                {
                    classified++; counts[selection.Category] = counts.GetValueOrDefault(selection.Category) + 1;
                    if (addInferences is not null) selectedRecords.Add(new(sourceIndex, record, selection.Category));
                }
            }
        }
        catch (Exception error) when (error is EventLogException or IOException or UnauthorizedAccessException or System.Security.SecurityException or TimeoutException)
        {
            string description = error.Message.Length <= 2048 ? error.GetType().Name + ": " + error.Message
                : error.GetType().Name + ": mensagem original excedeu 2048 caracteres e não foi truncada nem retida.";
            facts.Add(new("sourceFailure.messageOriginalCharacters", "Caracteres da mensagem original da falha de leitura", NumberValue: error.Message.Length, Detailed: true));
            AddIssue(new(error is UnauthorizedAccessException or System.Security.SecurityException ? IssueCode.PermissionDenied : IssueCode.ReadFailed, description), primary: true);
        }
        cancellationToken.ThrowIfCancellationRequested();
        addInferences?.Invoke(facts, selectedRecords, issue => AddIssue(issue), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        facts.InsertRange(0, counters(counts, reachedEnd || classified > 0).Concat(new DiagnosticFact[]
        {
            new("examinedEvents", "Registros examinados antes da sonda", NumberValue: examined),
            new("retainedEvents", "Registros originais retidos (inclui rejeitados)", NumberValue: retained),
            new("classifiedEvents", "Registros retidos com classificação validada", NumberValue: classified),
            new("rejectedEvents", "Registros rejeitados para contagem/classificação", NumberValue: rejected),
            new("windowStart", "Início da janela solicitada", from.ToString("O", CultureInfo.InvariantCulture)),
            new("windowEnd", "Fim da janela solicitada", to.ToString("O", CultureInfo.InvariantCulture)),
            new("logChannel", "Canal local solicitado", "System"),
            new("sourceReachedEnd", "Fim da enumeração selecionada observado", reachedEnd ? "true" : "false"),
            new("retainedXmlBytes", "XML original retido", NumberValue: xmlBytes, Unit: "bytes", Detailed: true),
            new("logRolloverCoverage", "Cobertura de rotação/limpeza anterior do log", "unknown"),
            new("physicalRestartCountKnown", "História completa de reinícios físicos conhecida", "false"),
            new("elapsedSeconds", "Tempo decorrido no coletor", NumberValue: timer.Elapsed.Ticks / (decimal)TimeSpan.TicksPerSecond, Unit: "seconds", Detailed: true),
            new("issueCount", "Ocorrências de falha/limitação", NumberValue: issueCount),
            new("issueDetailsOmitted", "Detalhes de ocorrências não retidos", NumberValue: issueOmitted, Detailed: true)
        }));
        if (issueOmitted > 0) issues.Add(new(IssueCode.LimitReached, "Detalhes adicionais de ocorrências omitidos por orçamento; contador preservado."));
        return [new(Guid.NewGuid(), id, title, "Log System local", clock.GetUtcNow(),
            issueCount == 0 ? EvidenceStatus.Complete : retained > 0 ? EvidenceStatus.Partial : EvidenceStatus.Unavailable,
            new($"System local de {from:O} até {to:O}; ordem original da fonte, nativa em direção reversa do log, sem presumir ordenação global dos timestamps entre threads. Até {eventLimit} registros mais uma sonda de presença; XML de até 32 KiB por registro e {xmlLimit} bytes totais, projeção serializada aproximada de até {payloadLimit} bytes, 32 campos e 2048 caracteres por escalar. Contadores abrangem somente registros válidos retidos, não o total histórico quando há limites/falhas. Rotação, limpeza e disponibilidade anteriores do log são desconhecidas; ausência de registros não comprova ausência de falhas ou de testes. {interpretationScope} A consulta não é snapshot atômico. Cancelamento e prazo de 30 segundos são cooperativos entre chamadas; ReadEvent pode esperar até dois segundos e ToXml aloca antes do limite de retenção, sem limitar toda atividade do serviço Windows. Não executa testes, reinicia o computador, interpreta mensagens localizadas ou altera logs.", examined, eventLimit, limited), facts, issues)];

        void Check()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (timer.Elapsed >= TimeSpan.FromSeconds(30)) throw new TimeoutException("Prazo cooperativo da consulta atingido; registros anteriores preservados, cobertura restante desconhecida.");
        }
        void AddIssue(CollectionIssue issue, bool primary = false)
        {
            issueCount++;
            if (issue.Code == IssueCode.LimitReached) limited = true;
            int cost = issue.Message.Length <= 2048 ? JsonSerializer.SerializeToUtf8Bytes(issue, DiagnosticJson.Options).Length : int.MaxValue;
            // One final source failure has a reserved slot independent of prior malformed-record details.
            if (primary) { issues.Add(issue); return; }
            if (issues.Count < 32 && cost <= issueRemaining) { issueRemaining -= cost; issues.Add(issue); }
            else { issueOmitted++; limited = true; }
        }
    }, cancellationToken);
}
