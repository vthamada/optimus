using System.Globalization;
using System.Text;
using System.Text.Json;
using Optimus.Application;
using Optimus.Domain;

namespace Optimus.Infrastructure.Updates;

/// <summary>Interprets a bounded local WUA history slice, not an available-update search.</summary>
public sealed class WindowsUpdateHistoryCollector(IWindowsSystemQueryRunner runner) : IDiagnosticCollector
{
    public const int MaximumEntries = 128;
    public const int MaximumResponseBytes = 262_144;
    private const long MaximumProjectionBytes = 128_000;
    private static readonly string[] EntryFields = ["updateId", "revisionNumber", "title", "date", "operation", "resultCode", "hResult", "clientApplicationId"];
    public string Id => MaintenanceCollectors.Updates;
    public string DisplayName => "Histórico local do Windows Update";

    public async Task<IReadOnlyList<DiagnosticEvidence>> CollectAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var execution = await runner.RunAsync(WindowsSystemQuery.UpdateHistory, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var captured = DateTimeOffset.UtcNow;
        var facts = new List<DiagnosticFact>
        {
            new("toolStatus", "Estado do executor da consulta", execution.Status.ToString(), Detailed: true),
            new("toolExitCode", "Código de saída da consulta", NumberValue: execution.ExitCode, Detailed: true),
            new("queryJobElapsedSeconds", "Executor/job da consulta: tempo decorrido", NumberValue: execution.Metrics.Elapsed.Ticks / (decimal)TimeSpan.TicksPerSecond, Unit: "seconds", Detailed: true),
            new("queryJobCpuSeconds", "Job da consulta: CPU acumulada", NumberValue: execution.Metrics.CpuTime is { } cpu ? cpu.Ticks / (decimal)TimeSpan.TicksPerSecond : null, Unit: "seconds", Detailed: true),
            new("queryJobPeakCommitBytes", "Job da consulta: pico de memória comprometida", NumberValue: execution.Metrics.PeakProcessFamilyMemoryBytes, Unit: "bytes", Detailed: true),
            new("availableUpdatesSearched", "Atualizações disponíveis pesquisadas nesta consulta", "false"),
            new("otherRebootSourcesChecked", "CBS e outros sinais de reinício avaliados", "false")
        };
        var issues = new List<CollectionIssue>();
        RetainTransport(execution, facts, issues);
        int bytes = Encoding.UTF8.GetByteCount(execution.StandardOutput);
        facts.Add(new("sourceJsonUtf8Bytes", "Tamanho da resposta JSON original", NumberValue: bytes, Unit: "bytes", Detailed: true));
        if (bytes > MaximumResponseBytes)
        {
            issues.Add(new(IssueCode.LimitReached, "A resposta excedeu 262144 bytes UTF-8 e não foi interpretada nem retida integralmente."));
            return [Evidence(EvidenceStatus.Unavailable, 0)];
        }
        facts.Add(new("sourceJson", "JSON original do histórico WUA e sinal de reinício", execution.StandardOutput, Detailed: true));
        if (!execution.ExitedSuccessfully)
        {
            issues.Add(new(execution.Error?.Code == ErrorCode.PermissionDenied ? IssueCode.PermissionDenied : IssueCode.ReadFailed,
                "A execução não confirmou conclusão. Consulte os dados originais do executor e os recursos pendentes preservados."));
            return [Evidence(EvidenceStatus.Unavailable, 0)];
        }
        int transportFacts = facts.Count, transportIssues = issues.Count;
        try
        {
            using var document = JsonDocument.Parse(execution.StandardOutput, new JsonDocumentOptions { MaxDepth = 12 });
            var root = document.RootElement;
            if (Duplicates(root)) throw new JsonException("Propriedade duplicada.");
            Exact(root, ["kind", "version", "observedAt", "history", "reboot", "cleanup"]);
            if (root.GetProperty("kind").GetString() != "optimus.update-history" || !root.GetProperty("version").TryGetInt32(out var version) || version != 1)
                throw new JsonException("Identidade/versão do histórico não reconhecida.");
            var observed = root.GetProperty("observedAt");
            if (observed.ValueKind == JsonValueKind.String && ExplicitDate(observed.GetString(), out var date)) captured = date;
            else issues.Add(new(IssueCode.ReadFailed, "Data da consulta sem fuso explícito reconhecido; CapturedAt identifica o recebimento."));
            bool historyKnown = ParseHistory(root.GetProperty("history"), facts, issues, out int examined);
            bool rebootKnown = ParseReboot(root.GetProperty("reboot"), facts, issues);
            ParseCleanup(root.GetProperty("cleanup"), facts, issues);
            return [Evidence(!historyKnown && !rebootKnown ? EvidenceStatus.Unavailable : issues.Count > 0 ? EvidenceStatus.Partial : EvidenceStatus.Complete, examined)];
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException or OverflowException)
        {
            facts.RemoveRange(transportFacts, facts.Count - transportFacts);
            issues.RemoveRange(transportIssues, issues.Count - transportIssues);
            issues.Add(new(IssueCode.ReadFailed, "Contrato do histórico WUA inválido: " + error.Message));
            return [Evidence(EvidenceStatus.Unavailable, 0)];
        }

        DiagnosticEvidence Evidence(EvidenceStatus status, int examined) => new(Guid.NewGuid(), Id, DisplayName,
            "WUA local: histórico e Microsoft.Update.SystemInfo", captured, status,
            new("Histórico local WUA em ordem cronológica descendente, até 128 entradas recentes e 128000 bytes estimados de projeção; a leitura não é transacional. Não pesquisa atualizações disponíveis, não baixa/instala, não comprova sistema atualizado. O sinal RebootRequired pertence ao WUA para concluir instalação/desinstalação de updates; não cobre CBS ou todos os motivos de reinício do Windows. Datas mantêm tipo e fuso informados, sem inferir UTC.",
                examined, MaximumEntries, issues.Any(issue => issue.Code == IssueCode.LimitReached)), facts, issues);
    }

    private static bool ParseHistory(JsonElement value, List<DiagnosticFact> facts, List<CollectionIssue> issues, out int examined)
    {
        Exact(value, ["succeeded", "totalCount", "confirmedTotalCount", "requested", "examined", "limitReached", "entries", "issues"]);
        bool succeeded = value.GetProperty("succeeded").GetBoolean(), limited = value.GetProperty("limitReached").GetBoolean();
        int? total = CountOrNull(value.GetProperty("totalCount")), after = CountOrNull(value.GetProperty("confirmedTotalCount"));
        int requested = Count(value.GetProperty("requested"), MaximumEntries);
        examined = Count(value.GetProperty("examined"), MaximumEntries);
        var entries = value.GetProperty("entries");
        if (entries.ValueKind != JsonValueKind.Array || entries.GetArrayLength() > MaximumEntries || entries.GetArrayLength() > examined || examined > requested
            || (total is { } n && requested != Math.Min(n, MaximumEntries)) || (total is null && requested != 0)) throw new JsonException("Cobertura do histórico inconsistente.");
        if (succeeded && (total is null || after is null || total != after || (!limited && examined != requested))) throw new JsonException("Histórico declarou completude sem confirmar descoberta e leitura.");
        if (succeeded && entries.GetArrayLength() < examined && !limited) throw new JsonException("Entradas omitidas sem limite declarado.");
        ReadIssues(value.GetProperty("issues"), "history", issues, 2048);
        if (total > MaximumEntries) limited = true;
        if (limited) issues.Add(new(IssueCode.LimitReached, "O histórico contém omissões de entradas/campos ou atingiu o limite declarado; a seleção não é o catálogo completo."));
        if (!succeeded) issues.Add(new(IssueCode.ReadFailed, "A descoberta/leitura do histórico não confirmou conclusão; fatos parciais entregues foram preservados."));
        facts.Add(new("history.totalCount", "Eventos de histórico informados pelo WUA antes da leitura", NumberValue: total));
        facts.Add(new("history.confirmedTotalCount", "Eventos informados na confirmação da contagem", NumberValue: after));
        facts.Add(new("history.requested", "Entradas recentes solicitadas", NumberValue: requested));
        facts.Add(new("history.examined", "Entradas examinadas", NumberValue: examined));
        facts.Add(new("history.retained", "Entradas preservadas no JSON original", NumberValue: entries.GetArrayLength()));
        facts.Add(new("history.querySucceeded", "Consulta da lista e confirmação da contagem concluídas", succeeded ? "true" : "false"));
        long budget = MaximumProjectionBytes;
        int projected = 0, index = 0;
        foreach (var entry in entries.EnumerateArray())
        {
            Exact(entry, EntryFields);
            var selected = new List<DiagnosticFact>();
            var notes = new List<CollectionIssue>();
            string prefix = $"history.{index}";
            AddText(entry.GetProperty("updateId"), prefix + ".updateId", $"Evento {index + 1}: UpdateID original", selected, notes, detailed: true);
            AddInteger(entry.GetProperty("revisionNumber"), prefix + ".revisionNumber", $"Evento {index + 1}: revisão original", selected, notes, int.MinValue, int.MaxValue, true);
            AddText(entry.GetProperty("title"), prefix + ".title", $"Evento {index + 1}: título registrado", selected, notes);
            AddDate(entry.GetProperty("date"), prefix + ".date", $"Evento {index + 1}: data original", selected, notes);
            var operation = AddInteger(entry.GetProperty("operation"), prefix + ".operationCode", $"Evento {index + 1}: operação WUA original", selected, notes, int.MinValue, int.MaxValue, true);
            string? operationName = operation switch { 1 => "uoInstallation", 2 => "uoUninstallation", _ => null };
            selected.Add(new(prefix + ".operation", $"Evento {index + 1}: operação registrada", operationName));
            if (operationName is null) notes.Add(new(IssueCode.NotSupported, prefix + ": operação não mapeada na enumeração WUA; código original preservado."));
            var result = AddInteger(entry.GetProperty("resultCode"), prefix + ".resultCode", $"Evento {index + 1}: resultado WUA original", selected, notes, int.MinValue, int.MaxValue, true);
            string? resultName = result switch { 0 => "orcNotStarted", 1 => "orcInProgress", 2 => "orcSucceeded", 3 => "orcSucceededWithErrors", 4 => "orcFailed", 5 => "orcAborted", _ => null };
            selected.Add(new(prefix + ".result", $"Evento {index + 1}: resultado registrado (não avalia o estado atual)", resultName));
            if (resultName is null) notes.Add(new(IssueCode.NotSupported, prefix + ": resultado não mapeado na enumeração WUA; código original preservado."));
            var hresult = AddInteger(entry.GetProperty("hResult"), prefix + ".hResult", $"Evento {index + 1}: HRESULT numérico original", selected, notes, int.MinValue, uint.MaxValue, true);
            selected.Add(new(prefix + ".hResultHex", $"Evento {index + 1}: HRESULT em hexadecimal", hresult is { } code ? "0x" + unchecked((uint)code).ToString("X8", CultureInfo.InvariantCulture) : null));
            AddText(entry.GetProperty("clientApplicationId"), prefix + ".clientApplicationId", $"Evento {index + 1}: aplicativo cliente original", selected, notes, detailed: true);
            long cost = selected.Sum(fact => 128L + 6L * (fact.Key.Length + fact.Label.Length + (fact.TextValue?.Length ?? 0)))
                + notes.Sum(issue => 128L + 6L * issue.Message.Length);
            if (cost <= budget) { budget -= cost; facts.AddRange(selected); issues.AddRange(notes); projected++; }
            index++;
        }
        if (projected < entries.GetArrayLength()) issues.Add(new(IssueCode.LimitReached, "Limite de projeção de 128000 bytes estimados atingido; o JSON original retém as entradas recebidas."));
        facts.Add(new("history.projected", "Entradas apresentadas como fatos", NumberValue: projected));
        return succeeded || total.HasValue || entries.GetArrayLength() > 0;
    }

    private static bool ParseReboot(JsonElement value, List<DiagnosticFact> facts, List<CollectionIssue> issues)
    {
        Exact(value, ["succeeded", "required", "issues"]);
        bool succeeded = value.GetProperty("succeeded").GetBoolean();
        ReadIssues(value.GetProperty("issues"), "reboot", issues, 16);
        var required = value.GetProperty("required");
        bool known = succeeded && (required.ValueKind is JsonValueKind.True or JsonValueKind.False);
        facts.Add(new("reboot.wuaRequired", "Sinal WUA: reinício para concluir instalação/desinstalação de updates", known ? required.GetRawText() : null));
        facts.Add(new("reboot.querySucceeded", "Consulta do sinal de reinício WUA concluída", succeeded ? "true" : "false", Detailed: true));
        if (!known) issues.Add(new(IssueCode.ReadFailed, "Sinal de reinício WUA indisponível ou de tipo não reconhecido; não foi convertido em false."));
        return known;
    }

    private static void ParseCleanup(JsonElement value, List<DiagnosticFact> facts, List<CollectionIssue> issues)
    {
        Exact(value, ["succeeded", "pending", "issues"]);
        bool succeeded = value.GetProperty("succeeded").GetBoolean();
        var pending = value.GetProperty("pending");
        if (pending.ValueKind != JsonValueKind.Array || pending.GetArrayLength() > 260 || pending.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String || item.GetString()!.Length > 128)
            || succeeded != (pending.GetArrayLength() == 0)) throw new JsonException("Estado de liberação COM inconsistente.");
        ReadIssues(value.GetProperty("issues"), "cleanup", issues, 65);
        facts.Add(new("comCleanupPending", "Referências COM cuja liberação explícita não foi confirmada no processo filho (JSON)", pending.GetRawText(), Detailed: true));
        if (!succeeded) issues.Add(new(IssueCode.ReadFailed, "A liberação explícita de referências COM não foi confirmada; seus nomes foram preservados. O processo filho conservou as referências até sua finalização."));
    }

    private static void AddText(JsonElement value, string key, string label, List<DiagnosticFact> facts, List<CollectionIssue> issues, bool detailed = false)
    {
        string? text = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        bool valid = text is not null && text.Length <= 2048;
        facts.Add(new(key, label, valid ? text : null, Detailed: detailed));
        if (!valid) issues.Add(new(text?.Length > 2048 ? IssueCode.LimitReached : IssueCode.ReadFailed, key + ": texto desconhecido ou acima de 2048 caracteres; original mantido no JSON."));
    }
    private static long? AddInteger(JsonElement value, string key, string label, List<DiagnosticFact> facts, List<CollectionIssue> issues, long min, long max, bool detailed)
    {
        long? number = value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var n) && n >= min && n <= max ? n : null;
        facts.Add(new(key, label, NumberValue: number, Detailed: detailed));
        if (number is null) issues.Add(new(IssueCode.ReadFailed, key + ": inteiro original indisponível ou fora do contrato; nenhum zero foi presumido."));
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
            if (!valid) throw new JsonException("Data, tipo ou Kind divergente.");
            facts.Add(new(key, kind == "Unspecified" ? label + " (fuso não informado)" : label, text));
            facts.Add(new(key + ".sourceType", "Tipo original da data WUA", type, Detailed: true));
            facts.Add(new(key + ".kind", "Kind/fuso original da data WUA", kind, Detailed: true));
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException)
        {
            facts.Add(new(key, label)); issues.Add(new(IssueCode.ReadFailed, key + ": data desconhecida; nenhum fuso foi inferido. " + error.Message));
        }
    }
    private static bool ExplicitDate(string? value, out DateTimeOffset date)
    {
        date = default;
        return value is { Length: 28 or 33 } && DateTimeOffset.TryParseExact(value,
            ["yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", "yyyy-MM-dd'T'HH:mm:ss.fffffffzzz"], CultureInfo.InvariantCulture,
            value.EndsWith('Z') ? DateTimeStyles.AssumeUniversal : DateTimeStyles.None, out date);
    }
    private static int Count(JsonElement value, int max = int.MaxValue) => value.TryGetInt32(out var count) && count >= 0 && count <= max ? count : throw new JsonException("Contagem inválida.");
    private static int? CountOrNull(JsonElement value) => value.ValueKind == JsonValueKind.Null ? null : Count(value);
    private static void ReadIssues(JsonElement value, string scope, List<CollectionIssue> issues, int max)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > max) throw new JsonException("Limitações fora do contrato.");
        foreach (var item in value.EnumerateArray())
        {
            Exact(item, ["code", "message"]);
            string? message = item.GetProperty("message").GetString();
            if (message is null || message.Length > 2048) throw new JsonException("Mensagem de limitação inválida.");
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
        else { Add("toolPendingCleanupPaths", null, 16384); issues.Add(new(IssueCode.LimitReached, "Lista pendente acima do limite de 128 recursos/16384 caracteres; não foi tratada como vazia. O executor mantém a propriedade dos recursos.")); }
        void Add(string key, string? text, int max)
        {
            int? count = text is null ? null : Encoding.UTF8.GetByteCount(text);
            facts.Add(new(key + ".utf8Bytes", key + ": bytes UTF-8 observados", NumberValue: count, Unit: "bytes", Detailed: true));
            facts.Add(new(key, key + ": valor original do executor", count is null || count <= max ? text : null, Detailed: true));
            if (count > max) issues.Add(new(IssueCode.LimitReached, key + $": excedeu {max} bytes UTF-8 e não foi truncado ou retido integralmente."));
        }
    }
}
