using System.Globalization;
using System.Text;
using System.Text.Json;
using Optimus.Application;
using Optimus.Domain;

namespace Optimus.Infrastructure.Scheduling;

/// <summary>Owns selected Task Scheduler metadata; never fetches definitions or executes registered actions.</summary>
public sealed class ScheduledTasksCollector(IWindowsSystemQueryRunner runner) : IDiagnosticCollector
{
    public const int MaximumTasks = 128;
    public const int MaximumResponseBytes = 262_144;
    private const long MaximumProjectionBytes = 128_000;
    private static readonly string[] Fields = ["Name", "Path", "Enabled", "State", "LastRunTime", "NextRunTime", "LastTaskResult", "NumberOfMissedRuns"];
    public string Id => MaintenanceCollectors.ScheduledTasks;
    public string DisplayName => "Tarefas agendadas locais";

    public async Task<IReadOnlyList<DiagnosticEvidence>> CollectAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var execution = await runner.RunAsync(WindowsSystemQuery.TaskInventory, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var captured = DateTimeOffset.UtcNow;
        List<DiagnosticFact> facts = [
            new("toolStatus", "Estado do executor da consulta", execution.Status.ToString(), Detailed: true),
            new("toolExitCode", "Código de saída do executor", NumberValue: execution.ExitCode, Detailed: true),
            new("queryJobElapsedSeconds", "Job da consulta: duração", NumberValue: execution.Metrics.Elapsed.Ticks / (decimal)TimeSpan.TicksPerSecond, Unit: "seconds", Detailed: true),
            new("queryJobCpuSeconds", "Job da consulta: CPU da família", NumberValue: execution.Metrics.CpuTime is { } cpu ? cpu.Ticks / (decimal)TimeSpan.TicksPerSecond : null, Unit: "seconds", Detailed: true),
            new("queryJobPeakCommitBytes", "Job da consulta: pico de memória comprometida da família", NumberValue: execution.Metrics.PeakProcessFamilyMemoryBytes, Unit: "bytes", Detailed: true),
            new("hiddenTasksRequested", "Enumeração solicitou tarefas ocultas (GetTasks com flag 1)", "true"),
            new("taskDefinitionsRequested", "Definições, ações, argumentos ou credenciais solicitados", "false"),
            new("taskChangesRequested", "Execução, alteração ou exclusão de tarefa solicitada", "false")
        ];
        List<CollectionIssue> issues = [];
        RetainTransport(execution, facts, issues);
        int bytes = Encoding.UTF8.GetByteCount(execution.StandardOutput);
        facts.Add(new("sourceJsonUtf8Bytes", "Tamanho UTF-8 da resposta original", NumberValue: bytes, Unit: "bytes", Detailed: true));
        if (bytes > MaximumResponseBytes)
        {
            issues.Add(new(IssueCode.LimitReached, "A resposta excedeu 262144 bytes UTF-8; não foi interpretada nem retida como documento completo."));
            return [Evidence(EvidenceStatus.Unavailable, 0)];
        }
        facts.Add(new("sourceJson", "JSON original da consulta local do agendador", execution.StandardOutput, Detailed: true));
        if (!execution.ExitedSuccessfully)
        {
            issues.Add(new(execution.Error?.Code == ErrorCode.PermissionDenied ? IssueCode.PermissionDenied : IssueCode.ReadFailed,
                "O transporte não confirmou conclusão da consulta; os dados e o erro primário foram preservados sem inferir saúde."));
            return [Evidence(EvidenceStatus.Unavailable, 0)];
        }
        if (execution.StandardError.Length > 0) issues.Add(new(IssueCode.ReadFailed, "O executor retornou stderr; os dados permanecem disponíveis com cobertura parcial."));
        int transportFacts = facts.Count, transportIssues = issues.Count;
        try
        {
            using var document = JsonDocument.Parse(execution.StandardOutput, new JsonDocumentOptions { MaxDepth = 12 });
            var root = document.RootElement;
            if (Duplicates(root)) throw new JsonException("Propriedade JSON duplicada.");
            Exact(root, ["kind", "version", "observedAt", "query", "cleanup"]);
            if (root.GetProperty("kind").GetString() != "optimus.task-inventory" || !root.GetProperty("version").TryGetInt32(out int version) || version != 1)
                throw new JsonException("Identidade ou versão da consulta não reconhecida.");
            var observed = root.GetProperty("observedAt").GetString();
            if (ExplicitDate(observed, out var sourceTime)) captured = sourceTime;
            else issues.Add(new(IssueCode.ReadFailed, "Data da consulta sem fuso explícito reconhecido; CapturedAt identifica o recebimento."));
            facts.Add(new("sourceObservedAt", "Data original da consulta", observed, Detailed: true));
            var query = root.GetProperty("query");
            Exact(query, ["connected", "traversalComplete", "foldersVisited", "foldersFailed", "tasksExamined", "limitReached", "issuesOmitted", "records", "issues"]);
            bool connected = query.GetProperty("connected").GetBoolean(), complete = query.GetProperty("traversalComplete").GetBoolean(), limited = query.GetProperty("limitReached").GetBoolean();
            int folders = Count(query.GetProperty("foldersVisited"), 32), failed = Count(query.GetProperty("foldersFailed"), 96), examined = Count(query.GetProperty("tasksExamined"), MaximumTasks);
            int omitted = Count(query.GetProperty("issuesOmitted"), 4096);
            var records = query.GetProperty("records");
            if (records.ValueKind != JsonValueKind.Array || records.GetArrayLength() > examined
                || !connected && (folders > 0 || records.GetArrayLength() > 0)
                || connected && folders == 0 || examined > 0 && folders == 0
                || complete && (!connected || failed > 0 || limited || omitted > 0)
                || records.GetArrayLength() < examined && !limited || omitted > 0 && !limited)
                throw new JsonException("Cobertura de tarefas/pastas inconsistente.");
            ReadIssues(query.GetProperty("issues"), "scheduler", issues, 128);
            if (!connected) issues.Add(new(IssueCode.ReadFailed, "A conexão e a raiz local do agendador não foram confirmadas; nenhum catálogo vazio foi presumido."));
            if (!complete || failed > 0) issues.Add(new(IssueCode.ReadFailed, "A travessia não confirmou completude; pastas/descendentes não examinados permanecem desconhecidos."));
            if (limited || omitted > 0) issues.Add(new(IssueCode.LimitReached, "A fonte atingiu limite de tarefas, pastas, profundidade, campo ou retenção; omissões são explícitas."));
            facts.AddRange([
                new("query.connected", "Conexão e raiz local confirmadas pela fonte", connected ? "true" : "false"),
                new("query.traversalComplete", "Travessia concluída no escopo selecionado", complete ? "true" : "false"),
                new("foldersVisited", "Pastas visitadas (até 32 tentativas de aquisição)", NumberValue: folders),
                new("foldersFailed", "Ocorrências de falha em pastas ou sua enumeração", NumberValue: failed),
                new("tasksExamined", "Entradas de tarefas examinadas", NumberValue: examined),
                new("tasksRetained", "Registros retidos no JSON original", NumberValue: records.GetArrayLength()),
                new("sourceIssuesOmitted", "Detalhes de limitações omitidos pela fonte", NumberValue: omitted)
            ]);
            int projected = 0, index = 0;
            long budget = MaximumProjectionBytes;
            foreach (var record in records.EnumerateArray())
            {
                Exact(record, Fields);
                List<DiagnosticFact> selected = [];
                List<CollectionIssue> notes = [];
                string prefix = "tasks." + index;
                AddText(record.GetProperty("Name"), prefix + ".Name", $"Tarefa {index + 1}: nome original", selected, notes);
                AddText(record.GetProperty("Path"), prefix + ".Path", $"Tarefa {index + 1}: caminho registrado", selected, notes);
                var enabled = record.GetProperty("Enabled");
                bool enabledKnown = enabled.ValueKind is JsonValueKind.True or JsonValueKind.False;
                selected.Add(new(prefix + ".Enabled", $"Tarefa {index + 1}: habilitada segundo a fonte", enabledKnown ? enabled.GetRawText() : null));
                if (!enabledKnown) notes.Add(new(IssueCode.ReadFailed, prefix + ".Enabled: valor desconhecido; não foi tratado como false."));
                long? state = AddNumber(record.GetProperty("State"), prefix + ".State", $"Tarefa {index + 1}: estado numérico original", selected, notes, 0, uint.MaxValue, true);
                string? stateName = state switch { 0 => "TASK_STATE_UNKNOWN", 1 => "TASK_STATE_DISABLED", 2 => "TASK_STATE_QUEUED", 3 => "TASK_STATE_READY", 4 => "TASK_STATE_RUNNING", _ => null };
                selected.Add(new(prefix + ".stateName", $"Tarefa {index + 1}: estado informado (não é saúde)", stateName));
                if (stateName is null) notes.Add(new(IssueCode.NotSupported, prefix + ": estado fora da enumeração conhecida; código original preservado."));
                AddDate(record.GetProperty("LastRunTime"), prefix + ".LastRunTime", $"Tarefa {index + 1}: data da última execução informada", selected, notes);
                AddDate(record.GetProperty("NextRunTime"), prefix + ".NextRunTime", $"Tarefa {index + 1}: próximo horário informado (não garante execução)", selected, notes);
                long? result = AddNumber(record.GetProperty("LastTaskResult"), prefix + ".LastTaskResult", $"Tarefa {index + 1}: resultado original (não é laudo)", selected, notes, int.MinValue, uint.MaxValue, false);
                selected.Add(new(prefix + ".lastTaskResultHex", $"Tarefa {index + 1}: resultado em hexadecimal", result is { } code ? "0x" + unchecked((uint)code).ToString("X8", CultureInfo.InvariantCulture) : null));
                if (result == 0x41303) selected.Add(new(prefix + ".lastTaskResultName", "Código reportado pelo agendador", "SCHED_S_TASK_HAS_NOT_RUN"));
                AddNumber(record.GetProperty("NumberOfMissedRuns"), prefix + ".NumberOfMissedRuns", $"Tarefa {index + 1}: execuções perdidas reportadas", selected, notes, 0, uint.MaxValue, false);
                long cost = selected.Sum(fact => 128L + 6L * (fact.Key.Length + fact.Label.Length + (fact.TextValue?.Length ?? 0)))
                    + notes.Sum(issue => 128L + 6L * issue.Message.Length);
                if (cost <= budget) { budget -= cost; facts.AddRange(selected); issues.AddRange(notes); projected++; }
                index++;
            }
            facts.Add(new("tasksProjected", "Registros apresentados como fatos", NumberValue: projected));
            if (projected < records.GetArrayLength()) issues.Add(new(IssueCode.LimitReached, "Orçamento conservador de projeção de 128000 bytes atingido; registros não projetados permanecem no JSON original."));
            ParseCleanup(root.GetProperty("cleanup"), facts, issues);
            return [Evidence(!connected ? EvidenceStatus.Unavailable : issues.Count > 0 ? EvidenceStatus.Partial : EvidenceStatus.Complete, examined)];
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException or OverflowException)
        {
            facts.RemoveRange(transportFacts, facts.Count - transportFacts);
            issues.RemoveRange(transportIssues, issues.Count - transportIssues);
            issues.Add(new(IssueCode.ReadFailed, "Contrato de tarefas inválido: " + error.Message));
            return [Evidence(EvidenceStatus.Unavailable, 0)];
        }

        DiagnosticEvidence Evidence(EvidenceStatus status, int examined) => new(Guid.NewGuid(), Id, DisplayName,
            "Agendador local: metadados via Schedule.Service", captured, status,
            new("GetTasks(1) solicita tarefas ocultas, mas permissões podem limitar a descoberta. Até 128 tarefas, 32 tentativas de pastas e 16 níveis em ordem da fonte; resposta de 256 KiB e projeção conservadora de 128000 bytes. Não consulta definições, ações, argumentos ou credenciais; não executa, para, altera ou exclui tarefas. Snapshot não atômico. Estado e resultado não comprovam saúde; NextRunTime pode refletir triggers individualmente desabilitados. Datas mantêm tipo/fuso original, e DATE zero não prova uma execução.",
                examined, MaximumTasks, issues.Any(issue => issue.Code == IssueCode.LimitReached)), facts, issues);
    }

    private static void AddText(JsonElement value, string key, string label, List<DiagnosticFact> facts, List<CollectionIssue> issues)
    {
        string? text = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        bool valid = text is not null && text.Length <= 2048;
        facts.Add(new(key, label, valid ? text : null));
        if (!valid) issues.Add(new(text?.Length > 2048 ? IssueCode.LimitReached : IssueCode.ReadFailed, key + ": texto desconhecido ou acima de 2048 caracteres; original no JSON."));
    }
    private static long? AddNumber(JsonElement value, string key, string label, List<DiagnosticFact> facts, List<CollectionIssue> issues, long min, long max, bool detailed)
    {
        long? number = value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var source) && source >= min && source <= max ? source : null;
        facts.Add(new(key, label, NumberValue: number, Detailed: detailed));
        if (number is null) issues.Add(new(IssueCode.ReadFailed, key + ": inteiro original desconhecido ou fora do contrato, sem presumir zero."));
        return number;
    }
    private static void AddDate(JsonElement value, string key, string label, List<DiagnosticFact> facts, List<CollectionIssue> issues)
    {
        try
        {
            Exact(value, ["sourceType", "value", "kind"]);
            string? type = value.GetProperty("sourceType").GetString(), text = value.GetProperty("value").GetString(), kind = value.GetProperty("kind").GetString();
            bool valid = text is not null && text.Length <= 64 && ((type, kind) switch
            {
                ("System.DateTime", "Utc") => DateTime.TryParseExact(text, "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
                ("System.DateTime", "Unspecified") => DateTime.TryParseExact(text, "yyyy-MM-dd'T'HH:mm:ss.fffffff", CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
                ("System.DateTime", "Local") or ("System.DateTimeOffset", "Offset") => text.Length == 33 && (text[27] is '+' or '-')
                    && DateTimeOffset.TryParseExact(text, "yyyy-MM-dd'T'HH:mm:ss.fffffffzzz", CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
                _ => false
            });
            if (!valid) throw new JsonException("Formato de data/tipo/Kind divergente.");
            bool zero = text!.StartsWith("1899-12-30T00:00:00.0000000", StringComparison.Ordinal);
            string suffix = zero ? " (representação DATE zero; não prova execução)" : kind == "Unspecified" ? " (fuso não informado)" : "";
            facts.Add(new(key, label + suffix, text));
            facts.Add(new(key + ".sourceType", "Tipo original da data", type, Detailed: true));
            facts.Add(new(key + ".kind", "Kind/fuso original da data", kind, Detailed: true));
            facts.Add(new(key + ".oleDateZeroRepresentation", "Representação de calendário DATE zero observada, sem inferir execução", zero ? "true" : "false", Detailed: true));
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException)
        {
            facts.Add(new(key, label));
            issues.Add(new(IssueCode.ReadFailed, key + ": data desconhecida, sem atribuir fuso ou execução; valor original mantido no JSON. " + error.Message));
        }
    }
    private static void ParseCleanup(JsonElement value, List<DiagnosticFact> facts, List<CollectionIssue> issues)
    {
        Exact(value, ["succeeded", "pending", "issues"]);
        bool succeeded = value.GetProperty("succeeded").GetBoolean();
        var pending = value.GetProperty("pending");
        if (pending.ValueKind != JsonValueKind.Array || pending.GetArrayLength() > 256
            || pending.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String || item.GetString()!.Length > 128)
            || succeeded != (pending.GetArrayLength() == 0)) throw new JsonException("Estado de finalização COM inconsistente.");
        ReadIssues(value.GetProperty("issues"), "cleanup", issues, 33);
        facts.Add(new("comCleanupPending", "Referências COM cuja liberação explícita não foi confirmada (JSON)", pending.GetRawText(), Detailed: true));
        if (!succeeded) issues.Add(new(IssueCode.ReadFailed, "Referências COM próprias permaneceram retidas pelo processo filho até a finalização; nomes foram preservados."));
    }
    private static bool ExplicitDate(string? text, out DateTimeOffset value)
    {
        value = default;
        return text is { Length: 28 or 33 } && DateTimeOffset.TryParseExact(text,
            ["yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", "yyyy-MM-dd'T'HH:mm:ss.fffffffzzz"], CultureInfo.InvariantCulture,
            text.EndsWith('Z') ? DateTimeStyles.AssumeUniversal : DateTimeStyles.None, out value);
    }
    private static int Count(JsonElement value, int maximum) => value.TryGetInt32(out var number) && number >= 0 && number <= maximum ? number : throw new JsonException("Contagem fora do contrato.");
    private static void ReadIssues(JsonElement value, string scope, List<CollectionIssue> issues, int maximum)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > maximum) throw new JsonException("Limitações fora do contrato.");
        foreach (var item in value.EnumerateArray())
        {
            Exact(item, ["code", "message"]);
            string? message = item.GetProperty("message").GetString();
            if (message is null || message.Length > 2048) throw new JsonException("Detalhe de limitação fora do contrato.");
            var code = item.GetProperty("code").GetString() switch { "ReadFailed" => IssueCode.ReadFailed, "PermissionDenied" => IssueCode.PermissionDenied, "NotSupported" => IssueCode.NotSupported, "LimitReached" => IssueCode.LimitReached, _ => throw new JsonException("Código de limitação desconhecido.") };
            issues.Add(new(code, scope + ": " + message));
        }
    }
    private static void Exact(JsonElement value, string[] fields)
    {
        if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Count() != fields.Length || value.EnumerateObject().Any(item => !fields.Contains(item.Name, StringComparer.Ordinal))) throw new JsonException("Estrutura JSON não reconhecida.");
    }
    private static bool Duplicates(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in value.EnumerateObject()) if (!seen.Add(item.Name) || Duplicates(item.Value)) return true;
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) if (Duplicates(item)) return true;
        return false;
    }
    private static void RetainTransport(ToolRunResult execution, List<DiagnosticFact> facts, List<CollectionIssue> issues)
    {
        Add("toolStderr", execution.StandardError, 32768);
        facts.Add(new("toolPrimaryErrorCode", "Código primário do executor", execution.Error?.Code.ToString(), Detailed: true));
        Add("toolPrimaryErrorReason", execution.Error?.Message, 8192);
        var pending = execution.Error?.PendingCleanupPaths;
        facts.Add(new("toolPendingCleanupCount", "Recursos pendentes informados pelo executor", NumberValue: pending?.Count, Detailed: true));
        if (pending is null) Add("toolPendingCleanupPaths", null, 16384);
        else if (pending.Count <= 128 && pending.Sum(item => (long)(item?.Length ?? 0)) <= 16384) Add("toolPendingCleanupPaths", JsonSerializer.Serialize(pending), 16384);
        else { Add("toolPendingCleanupPaths", null, 16384); issues.Add(new(IssueCode.LimitReached, "Lista pendente excedeu 128 recursos/16384 caracteres; o executor mantém sua propriedade, sem presumir lista vazia.")); }
        void Add(string key, string? text, int maximum)
        {
            int? bytes = text is null ? null : Encoding.UTF8.GetByteCount(text);
            facts.Add(new(key + ".utf8Bytes", key + ": bytes UTF-8 observados", NumberValue: bytes, Unit: "bytes", Detailed: true));
            facts.Add(new(key, key + ": valor original do executor", bytes is null || bytes <= maximum ? text : null, Detailed: true));
            if (bytes > maximum) issues.Add(new(IssueCode.LimitReached, key + ": conteúdo excedeu o orçamento e não foi truncado ou tratado como vazio."));
        }
    }
}
