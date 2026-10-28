using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Text.Json;
using Optimus.Application;
using Optimus.Domain;

namespace Optimus.Infrastructure.Events;

/// <summary>A bounded history of selected provider/ID pairs; it makes no causal diagnosis.</summary>
public sealed class ApplicationFailuresCollector(
    Func<DateTimeOffset, CancellationToken, IEnumerable<SystemEventObservation>>? source = null,
    TimeProvider? clock = null) : IDiagnosticCollector
{
    public const int EventLimit = 20;
    public const int MaximumPayloadBytes = 256 * 1024;
    public string Id => MaintenanceCollectors.ApplicationFailures;
    public string DisplayName => "Falhas e travamentos registrados de aplicativos";

    public Task<IReadOnlyList<DiagnosticEvidence>> CollectAsync(CancellationToken cancellationToken) => Task.Run<IReadOnlyList<DiagnosticEvidence>>(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        var time = clock ?? TimeProvider.System;
        var now = time.GetUtcNow();
        var cutoff = now.AddDays(-7);
        var timer = Stopwatch.StartNew();
        var facts = new List<DiagnosticFact>();
        var issues = new List<CollectionIssue>();
        int examined = 0, retained = 0, rejected = 0, issueCount = 0, issueDetailsOmitted = 0;
        bool limited = false;
        long remaining = MaximumPayloadBytes - 24 * 1024, issueBudget = 16 * 1024;
        try
        {
            foreach (var observation in (source ?? WindowsEventReader.ReadApplication)(cutoff, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (timer.Elapsed >= TimeSpan.FromSeconds(30)) throw new TimeoutException("Prazo cooperativo da coleta atingido; os registros já observados foram preservados.");
                if (examined == EventLimit) { limited = true; AddIssue(new(IssueCode.LimitReached, "Uma sonda confirmou mais de vinte registros; os demais não foram examinados.")); break; }
                examined++;
                var record = WindowsEventReader.Interpret(observation);
                var parsed = record.Xml!;
                foreach (var issue in parsed.Issues) AddIssue(issue);
                string? kind = (record.Provider, record.EventId) switch
                {
                    ("Application Error", 1000) => "Application Error / 1000",
                    ("Windows Error Reporting", 1001) => "Windows Error Reporting / 1001",
                    ("Application Hang", 1002) => "Application Hang / 1002",
                    _ => null
                };
                bool outside = record.OccurredAt is { } stamp && (stamp < cutoff || stamp > now);
                bool otherChannel = parsed.Channel is { } channel && channel != "Application";
                if (kind is null || outside || otherChannel)
                {
                    rejected++;
                    AddIssue(new(IssueCode.ReadFailed, "A fonte retornou registro fora dos pares provider/ID, canal ou janela solicitados; não foi classificado como falha recente de aplicativo."));
                    DiagnosticFact[] rejectedFacts = [
                        new($"rejected.{rejected - 1}.sourceXmlUtf8Bytes", "Bytes UTF-8 do XML rejeitado pelo escopo", NumberValue: parsed.XmlUtf8Bytes, Unit: "bytes", Detailed: true),
                        new($"rejected.{rejected - 1}.xml", "XML original de registro fora do escopo", parsed.XmlUtf8Bytes <= WindowsEventReader.MaximumXmlBytes ? record.OriginalXml : null, Detailed: true)
                    ];
                    if (!Append(rejectedFacts)) break;
                    continue;
                }
                string prefix = "event." + retained;
                DiagnosticFact[] header = [
                    new(prefix + ".selection", "Par provider/ID selecionado (não é diagnóstico causal)", kind),
                    new(prefix + ".provider", "Provider original do registro", record.Provider),
                    new(prefix + ".id", "ID original do evento", NumberValue: record.EventId),
                    new(prefix + ".recordId", "EventRecordID original do canal", NumberValue: record.RecordId),
                    new(prefix + ".time", "SystemTime original do XML (fuso preservado quando informado)", parsed.SystemTime),
                    new(prefix + ".timeParsedUtc", "Instante reconhecido em UTC", record.OccurredAt?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture), Detailed: true),
                    new(prefix + ".sourceXmlUtf8Bytes", "Tamanho UTF-8 do XML observado", NumberValue: parsed.XmlUtf8Bytes, Unit: "bytes", Detailed: true),
                    new(prefix + ".xml", "XML original do evento", parsed.XmlUtf8Bytes <= WindowsEventReader.MaximumXmlBytes ? record.OriginalXml : null, Detailed: true),
                    new(prefix + ".eventDataFieldsRetained", "Campos Data interpretados dentro do orçamento por evento", NumberValue: parsed.Data.Count, Detailed: true)
                ];
                if (!Append(header)) break;
                foreach (var field in parsed.Data)
                {
                    DiagnosticFact[] selected = [
                        new($"{prefix}.data.{field.Index}.name", $"Campo Data #{field.Index}: nome original", field.Name, Detailed: true),
                        new($"{prefix}.data.{field.Index}.value", field.Name is null ? $"Data sem nome #{field.Index} (valor original)" : $"Data[{field.Name}] (valor original)", field.Value)
                    ];
                    if (!Append(selected)) break;
                }
                retained++;
                if (limited && remaining <= 0) break;
            }
        }
        catch (Exception error) when (error is EventLogException or IOException or UnauthorizedAccessException or System.Security.SecurityException or TimeoutException)
        {
            AddIssue(new(error is UnauthorizedAccessException or System.Security.SecurityException ? IssueCode.PermissionDenied : IssueCode.ReadFailed, error.GetType().Name + ": " + error.Message));
        }
        cancellationToken.ThrowIfCancellationRequested();
        facts.InsertRange(0, [
            new("examinedEvents", "Registros examinados antes da sonda", NumberValue: examined),
            new("retainedEvents", "Registros selecionados retidos", NumberValue: retained),
            new("rejectedEvents", "Registros rejeitados por divergência do escopo", NumberValue: rejected),
            new("windowStart", "Início da janela solicitada", cutoff.ToString("O", CultureInfo.InvariantCulture)),
            new("windowEnd", "Fim da janela solicitada", now.ToString("O", CultureInfo.InvariantCulture)),
            new("logChannel", "Canal solicitado", "Application"),
            new("causalDiagnosisMade", "Diagnóstico de causa inferido", "false"),
            new("localizedMessagesParsed", "Mensagens localizadas interpretadas", "false"),
            new("elapsedSeconds", "Tempo decorrido da coleta (processo atual)", NumberValue: timer.Elapsed.Ticks / (decimal)TimeSpan.TicksPerSecond, Unit: "seconds", Detailed: true),
            new("issueCount", "Ocorrências de limitação/falha", NumberValue: issueCount),
            new("issueDetailsOmitted", "Detalhes de ocorrências omitidos por orçamento", NumberValue: issueDetailsOmitted)
        ]);
        if (issueDetailsOmitted > 0) issues.Add(new(IssueCode.LimitReached, $"{issueDetailsOmitted} detalhes de ocorrências não foram retidos; contadores foram preservados, sem truncamento de mensagens."));
        return [new(Guid.NewGuid(), Id, DisplayName, "Log Application local", time.GetUtcNow(),
            issueCount == 0 ? EvidenceStatus.Complete : retained > 0 ? EvidenceStatus.Partial : EvidenceStatus.Unavailable,
            new($"Application desde {cutoff:O} até {now:O}, ordem da fonte nativa do mais recente para o mais antigo; somente Application Error/1000, Windows Error Reporting/1001 e Application Hang/1002. Até vinte registros mais sonda, XML retido de até 32 KiB UTF-8 por registro, até 32 Data por XML e payload aproximado limitado a 256 KiB. São registros, não incidentes únicos: WER/1001 pode descrever tipos de relato diferentes, sem inferir crash ou causa pelo ID. A consulta não é snapshot atômico; ausência de eventos não comprova ausência de falhas. O prazo/cancelamento são cooperativos entre chamadas. ReadEvent e ToXml podem alocar/trabalhar antes do limite de retenção, que não limita toda atividade do serviço Windows. Não interpreta mensagem localizada, não lê dumps nem muda logs/configurações WER.", examined, EventLimit, limited), facts, issues)];

        bool Append(IReadOnlyList<DiagnosticFact> selected)
        {
            int cost = JsonSerializer.SerializeToUtf8Bytes(selected, DiagnosticJson.Options).Length;
            if (cost > remaining) { limited = true; remaining = 0; AddIssue(new(IssueCode.LimitReached, "Orçamento total de retenção de 256 KiB atingido; valores não retidos não foram truncados ou tratados como ausentes.")); return false; }
            remaining -= cost;
            facts.AddRange(selected);
            return true;
        }
        void AddIssue(CollectionIssue issue)
        {
            issueCount++;
            if (issue.Code == IssueCode.LimitReached) limited = true;
            int cost = issue.Message.Length <= 2048 ? JsonSerializer.SerializeToUtf8Bytes(issue, DiagnosticJson.Options).Length : int.MaxValue;
            if (issues.Count < 32 && cost <= issueBudget) { issueBudget -= cost; issues.Add(issue); }
            else { issueDetailsOmitted++; limited = true; }
        }
    }, cancellationToken);
}
