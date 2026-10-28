using System.Globalization;
using System.Text;
using System.Text.Json;
using Optimus.Application;
using Optimus.Domain;

namespace Optimus.Infrastructure.Recovery;

/// <summary>Owns selected local SystemRestore properties; it never invokes class methods or tests recovery.</summary>
public sealed class RestorePointsCollector(IWindowsSystemQueryRunner runner) : IDiagnosticCollector
{
    public const int MaximumRecords = 64;
    public const int MaximumJsonBytes = 262_144;
    private const int MaximumFieldCharacters = 1024;
    private const long MaximumProjectedBytes = 128_000;
    private static readonly string[] Fields = ["Description", "RestorePointType", "EventType", "SequenceNumber", "CreationTime"];

    public const string CollectorId = "windows.restore-points";
    public string Id => CollectorId;
    public string DisplayName => "Pontos de restauração observados";

    public async Task<IReadOnlyList<DiagnosticEvidence>> CollectAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = await runner.RunAsync(WindowsSystemQuery.RestorePoints, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var facts = new List<DiagnosticFact>
        {
            new("query", "Consulta selecionada", "CIM local: root/default SystemRestore"),
            new("toolStatus", "Estado retornado pelo executor", result.Status.ToString(), Detailed: true),
            new("toolExitCode", "Código de saída do processo", NumberValue: result.ExitCode, Detailed: true),
            new("elapsedSeconds", "Duração do Job da consulta reportada pelo executor", NumberValue: (decimal)result.Metrics.Elapsed.Ticks / TimeSpan.TicksPerSecond, Unit: "s", Detailed: true),
            new("cpuSeconds", "CPU da família do Job da consulta", NumberValue: result.Metrics.CpuTime is { } cpu ? (decimal)cpu.Ticks / TimeSpan.TicksPerSecond : null, Unit: "s", Detailed: true),
            new("peakProcessFamilyMemoryBytes", "Pico de memória comprometida da família do Job da consulta", NumberValue: result.Metrics.PeakProcessFamilyMemoryBytes, Unit: "bytes", Detailed: true),
            new("toolOutputCodePage", "Página de código informada pelo executor", NumberValue: result.OutputCodePage, Detailed: true),
            new("toolErrorCode", "Código do erro primário do executor", result.Error?.Code.ToString(), Detailed: true),
            new("protectionConfigurationChecked", "Configuração da Proteção do Sistema consultada", "false"),
            new("recoveryTested", "Viabilidade de recuperação testada", "false"),
            new("restorePointChangesRequested", "Criação, restauração, exclusão ou alteração solicitada", "false")
        };
        var issues = new List<CollectionIssue>();
        RetainText("toolStderr", "Stderr original", result.StandardError, 16_384);
        RetainText("toolErrorMessage", "Motivo primário original do executor", result.Error?.Message, 8192);
        var pending = result.Error?.PendingCleanupPaths;
        facts.Add(new("toolPendingCleanupCount", "Quantidade de recursos com finalização pendente", NumberValue: pending?.Count, Detailed: true));
        if (pending is null) facts.Add(new("toolPendingCleanupPaths", "Recursos pendentes originais (JSON)", Detailed: true));
        else
        {
            long characters = 0;
            foreach (var value in pending) characters += value?.Length ?? 0;
            if (pending.Count <= 128 && characters <= 8192) RetainText("toolPendingCleanupPaths", "Recursos pendentes originais (JSON)", JsonSerializer.Serialize(pending), 16_384);
            else
            {
                facts.Add(new("toolPendingCleanupPaths", "Recursos pendentes não retidos integralmente por limite", Detailed: true));
                facts.Add(new("toolPendingCleanupCharacters", "Caracteres observados nos recursos pendentes", NumberValue: characters, Detailed: true));
                issues.Add(new(IssueCode.LimitReached, "A lista pendente excedeu 128 recursos ou 8192 caracteres; não foi truncada nem tratada como vazia. O executor continua dono desses recursos."));
            }
        }
        if (result.StandardErrorBytes is { } stderrBytes)
        {
            facts.Add(new("toolStderrOriginalBytes", "Quantidade original de bytes de stderr", NumberValue: stderrBytes.Length, Unit: "bytes", Detailed: true));
            if (stderrBytes.Length <= 16_384) facts.Add(new("toolStderrBytesBase64", "Bytes originais de stderr (base64)", Convert.ToBase64String(stderrBytes), Detailed: true));
            else issues.Add(new(IssueCode.LimitReached, "Stderr binário acima de 16384 bytes; conteúdo não copiado para a evidência, quantidade original preservada."));
        }
        int sourceBytes = Encoding.UTF8.GetByteCount(result.StandardOutput);
        facts.Add(new("sourceJsonUtf8Bytes", "Tamanho UTF-8 do JSON recebido", NumberValue: sourceBytes, Unit: "bytes", Detailed: true));
        if (sourceBytes > MaximumJsonBytes)
        {
            issues.Add(new(IssueCode.LimitReached, "O JSON excedeu 256 KiB e não foi retido ou interpretado; nenhum prefixo foi apresentado como documento completo."));
            return [Evidence(EvidenceStatus.Unavailable, 0, true, DateTimeOffset.UtcNow)];
        }
        facts.Add(new("sourceJson", "JSON original da consulta local de pontos de restauração", result.StandardOutput, Detailed: true));
        if (!result.ExitedSuccessfully)
        {
            if (result.StandardOutputBytes is { } stdoutBytes)
            {
                facts.Add(new("toolStdoutOriginalBytes", "Quantidade original de bytes de stdout", NumberValue: stdoutBytes.Length, Unit: "bytes", Detailed: true));
                if (stdoutBytes.Length <= MaximumJsonBytes) facts.Add(new("toolStdoutBytesBase64", "Bytes originais de stdout de transporte falho (base64)", Convert.ToBase64String(stdoutBytes), Detailed: true));
                else issues.Add(new(IssueCode.LimitReached, "Stdout binário acima de 256 KiB; conteúdo não copiado para a evidência, quantidade original preservada."));
            }
            issues.Add(new(result.Error?.Code == ErrorCode.PermissionDenied ? IssueCode.PermissionDenied : IssueCode.ReadFailed,
                $"Consulta não confirmada: {result.Status}; saída {result.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "desconhecida"}. O erro primário e suas limitações permanecem na evidência."));
            return [Evidence(EvidenceStatus.Unavailable, 0, result.Status == ToolRunStatus.OutputLimitExceeded, DateTimeOffset.UtcNow)];
        }
        if (result.StandardError.Length > 0) issues.Add(new(IssueCode.ReadFailed, "A consulta retornou stderr; os dados foram preservados com cobertura parcial."));
        int transportFacts = facts.Count;
        int transportIssues = issues.Count;
        try
        {
            using var document = JsonDocument.Parse(result.StandardOutput, new JsonDocumentOptions { MaxDepth = 12 });
            var root = document.RootElement;
            Exact(root, ["kind", "version", "observedAt", "query"]);
            if (!Unique(root) || root.GetProperty("kind").GetString() != "optimus.restore-points"
                || !root.GetProperty("version").TryGetInt32(out var version) || version != 1)
                throw new JsonException("Identidade, versão ou propriedades repetidas da consulta de pontos de restauração.");
            var observed = root.GetProperty("observedAt").GetString();
            if (!TryObservedAt(observed, out var captured)) throw new JsonException("A observação exige data ISO com fuso explícito.");
            var query = root.GetProperty("query");
            Exact(query, ["succeeded", "examined", "limitReached", "records", "issues"]);
            bool succeeded = query.GetProperty("succeeded").GetBoolean();
            bool limited = query.GetProperty("limitReached").GetBoolean();
            if (!query.GetProperty("examined").TryGetInt32(out int examined) || examined < 0 || examined > MaximumRecords + 1)
                throw new JsonException("Contagem de pontos de restauração fora do contrato.");
            var records = query.GetProperty("records");
            if (records.ValueKind != JsonValueKind.Array || records.GetArrayLength() > MaximumRecords || records.GetArrayLength() > examined
                || examined > MaximumRecords && !limited || succeeded && examined > records.GetArrayLength() && !limited)
                throw new JsonException("A cobertura declarada diverge dos registros recebidos.");
            foreach (var record in records.EnumerateArray()) Exact(record, Fields, requireAll: false);
            // Raw JSON/bytes are retained separately. Budget all other facts and notes, reserving summaries.
            long budget = Math.Max(0, MaximumProjectedBytes - 16_384
                - facts.Where(fact => fact.Key != "sourceJson").Sum(ProjectionCost) - issues.Sum(ProjectionCost));
            limited |= ReadIssues(query.GetProperty("issues"), issues, ref budget);
            if (!succeeded) issues.Add(new(IssueCode.ReadFailed, "A consulta CIM não confirmou conclusão; registros anteriores, se houver, foram preservados."));
            if (limited) issues.Add(new(IssueCode.LimitReached, "A fonte declarou limite de registros, de campo ou de transporte; o catálogo não é completo."));
            int projected = 0;
            foreach (var record in records.EnumerateArray())
            {
                var candidate = new List<DiagnosticFact>();
                var candidateIssues = new List<CollectionIssue>();
                foreach (var field in Fields)
                {
                    string key = $"restorePoints.{projected}.{field}";
                    string label = $"Ponto {projected + 1} · {Label(field)}";
                    bool valid = false;
                    if (record.TryGetProperty(field, out var value))
                    {
                        if ((field is "RestorePointType" or "EventType" or "SequenceNumber") && value.ValueKind == JsonValueKind.Number && value.TryGetUInt32(out var number))
                        {
                            candidate.Add(new(key, label, NumberValue: number)); valid = true;
                            if (field != "SequenceNumber") candidate.Add(new(key + ".name", "Rótulo conhecido do código original (sem conclusão de saúde)", CodeName(field, number), Detailed: true));
                        }
                        else if (field == "Description" && value.ValueKind == JsonValueKind.String)
                        {
                            var text = value.GetString();
                            if (text is not null && text.Length <= MaximumFieldCharacters)
                            {
                                candidate.Add(new(key, label, text)); valid = true;
                            }
                            else candidateIssues.Add(new(IssueCode.LimitReached, key + ": texto acima de 1024 caracteres; o valor integral permanece no JSON, sem projetar um prefixo."));
                        }
                        else if (field == "CreationTime") valid = AddCreationTime(value, key, label, candidate, candidateIssues);
                    }
                    if (!valid)
                    {
                        candidate.Add(new(key, label));
                        candidateIssues.Add(new(IssueCode.ReadFailed, key + ": valor ausente, nulo, excessivo ou com tipo inesperado; original preservado no JSON, sem conversão implícita."));
                    }
                }
                long cost = candidate.Sum(ProjectionCost) + candidateIssues.Sum(ProjectionCost);
                if (cost > budget) { limited = true; break; }
                budget -= cost;
                facts.AddRange(candidate);
                issues.AddRange(candidateIssues);
                projected++;
            }
            if (projected < records.GetArrayLength()) issues.Add(new(IssueCode.LimitReached, "Limite conservador de projeção de 128000 bytes atingido; os demais registros continuam no JSON original, sem publicação parcial de uma entrada."));
            facts.Add(new("querySucceeded", "Conclusão da consulta reportada pelo produtor", succeeded ? "true" : "false"));
            facts.Add(new("examinedRecords", "Entradas enumeradas (inclui eventual registro de sondagem)", NumberValue: examined));
            facts.Add(new("retainedRecords", "Registros retidos no JSON original", NumberValue: records.GetArrayLength()));
            facts.Add(new("projectedRecords", "Registros apresentados como fatos", NumberValue: projected));
            facts.Add(new("sourceObservedAt", "Data original da consulta local", observed, Detailed: true));
            limited |= issues.Any(issue => issue.Code == IssueCode.LimitReached);
            return [Evidence(!succeeded && records.GetArrayLength() == 0 ? EvidenceStatus.Unavailable
                : issues.Count > 0 ? EvidenceStatus.Partial : EvidenceStatus.Complete, examined, limited, captured)];
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException or OverflowException)
        {
            facts.RemoveRange(transportFacts, facts.Count - transportFacts);
            issues.RemoveRange(transportIssues, issues.Count - transportIssues);
            issues.Add(new(IssueCode.ReadFailed, "Contrato de pontos de restauração inválido: " + error.Message));
            return [Evidence(EvidenceStatus.Unavailable, 0, false, DateTimeOffset.UtcNow)];
        }

        void RetainText(string key, string label, string? text, int limit)
        {
            int? bytes = text is null ? null : Encoding.UTF8.GetByteCount(text);
            facts.Add(new(key + ".utf8Bytes", label + ": tamanho UTF-8 original", NumberValue: bytes, Unit: "bytes", Detailed: true));
            if (bytes is null || bytes <= limit) facts.Add(new(key, label, text, Detailed: true));
            else
            {
                facts.Add(new(key, label + " (conteúdo não retido por limite)", Detailed: true));
                issues.Add(new(IssueCode.LimitReached, $"{key} excedeu {limit} bytes UTF-8; o conteúdo não foi truncado nem tratado como vazio."));
            }
        }
        DiagnosticEvidence Evidence(EvidenceStatus status, int examined, bool limited, DateTimeOffset captured) => new(Guid.NewGuid(), Id, DisplayName,
            "CIM local: root/default SystemRestore", captured, status,
            new("Enumeração local de root/default SystemRestore, até 64 registros mais sonda, na ordem da fonte e sem ordenar o catálogo completo. Texto limitado a 1024 caracteres, resposta a 256 KiB e projeção conservadora a 128000 bytes. SequenceNumber, tipos/códigos e CreationTime são valores reportados. CreationTime DMTF é conservada como texto, sem conversão ou fuso inferido; datas CLR adaptadas mantêm tipo/Kind. A consulta não é snapshot atômico. Lista vazia não indica proteção desabilitada/configurada; presença de pontos não comprova integridade, abrangência ou recuperação viável. Nenhum método de criação, restauração, exclusão ou habilitação é chamado.",
                examined, MaximumRecords, limited || issues.Any(issue => issue.Code == IssueCode.LimitReached)), facts, issues);
    }

    private static bool ReadIssues(JsonElement value, List<CollectionIssue> issues, ref long budget)
    {
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > MaximumRecords * Fields.Length + 2)
            throw new JsonException("Lista de limitações fora do contrato.");
        int omitted = 0;
        foreach (var issue in value.EnumerateArray())
        {
            Exact(issue, ["code", "message"]);
            var message = issue.GetProperty("message").GetString();
            if (message is null || message.Length > MaximumFieldCharacters) throw new JsonException("Mensagem de limitação excede o limite de texto.");
            var code = issue.GetProperty("code").GetString() switch
            {
                "ReadFailed" => IssueCode.ReadFailed,
                "LimitReached" => IssueCode.LimitReached,
                "PermissionDenied" => IssueCode.PermissionDenied,
                "NotSupported" => IssueCode.NotSupported,
                _ => throw new JsonException("Tipo de limitação não reconhecido.")
            };
            var fullMessage = "SystemRestore: " + message;
            long cost = ProjectionCost(new CollectionIssue(code, fullMessage));
            if (cost <= budget) { issues.Add(new(code, fullMessage)); budget -= cost; }
            else omitted++;
        }
        if (omitted > 0) issues.Add(new(IssueCode.LimitReached, $"{omitted} limitações da fonte não foram projetadas por orçamento de 128000 bytes; todas permanecem no JSON original."));
        return omitted > 0;
    }
    private static bool TryObservedAt(string? text, out DateTimeOffset value)
    {
        value = default;
        return text is { Length: >= 20 and <= 64 }
            && DateTimeOffset.TryParseExact(text,
                ["yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz", "yyyy-MM-dd'T'HH:mm:sszzz", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", "yyyy-MM-dd'T'HH:mm:ss'Z'"],
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out value);
    }
    private static string Label(string field) => field switch
    {
        "Description" => "descrição original",
        "SequenceNumber" => "número de sequência original da fonte",
        "RestorePointType" => "RestorePointType numérico original",
        "EventType" => "EventType numérico original",
        "CreationTime" => "CreationTime original (sem converter o fuso)",
        _ => field
    };
    private static string? CodeName(string field, uint value) => field == "RestorePointType" ? value switch
    {
        0 => "APPLICATION_INSTALL",
        1 => "APPLICATION_UNINSTALL",
        10 => "DEVICE_DRIVER_INSTALL",
        12 => "MODIFY_SETTINGS",
        13 => "CANCELLED_OPERATION",
        _ => null
    } : value switch { 100 => "BEGIN_SYSTEM_CHANGE", 101 => "END_SYSTEM_CHANGE", 102 => "BEGIN_NESTED_SYSTEM_CHANGE", 103 => "END_NESTED_SYSTEM_CHANGE", _ => null };

    private static bool AddCreationTime(JsonElement value, string key, string label, List<DiagnosticFact> facts, List<CollectionIssue> issues)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            string? text = value.GetString();
            if (text is null || text.Length > MaximumFieldCharacters)
            {
                if (text?.Length > MaximumFieldCharacters) issues.Add(new(IssueCode.LimitReached, key + ": texto acima de 1024 caracteres; original no JSON, sem truncamento."));
                return false;
            }
            bool dmtf = FullySpecifiedDmtf(text);
            facts.Add(new(key, label, text));
            facts.Add(new(key + ".sourceType", "Tipo original de CreationTime", "System.String", Detailed: true));
            facts.Add(new(key + ".sourceFormat", "Representação observada de CreationTime", dmtf ? "DMTF original com componentes explícitos; não convertido" : "Texto original não convertido em instante", Detailed: true));
            if (!dmtf) issues.Add(new(IssueCode.NotSupported, key + ": texto não reconhecido como DMTF totalmente especificado; mantido sem atribuir horário ou UTC."));
            return true;
        }
        if (value.ValueKind != JsonValueKind.Object) return false;
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
            if (!valid) return false;
            facts.Add(new(key, kind == "Unspecified" ? label + " (fuso não informado)" : label, text));
            facts.Add(new(key + ".sourceType", "Tipo original de CreationTime adaptado pelo CIM", type, Detailed: true));
            facts.Add(new(key + ".kind", "Kind/fuso original da data adaptada", kind, Detailed: true));
            return true;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException) { return false; }
    }
    private static bool FullySpecifiedDmtf(string text)
    {
        if (text.Length != 25 || text[14] != '.' || text[21] is not ('+' or '-')) return false;
        for (int index = 0; index < text.Length; index++)
            if (index is not (14 or 21) && text[index] is not (>= '0' and <= '9')) return false;
        return DateTime.TryParseExact(text[..21], "yyyyMMddHHmmss.ffffff", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
    }
    private static long ProjectionCost(DiagnosticFact fact) => 160L + 6L * (fact.Key.Length + fact.Label.Length + (fact.TextValue?.Length ?? 0));
    private static long ProjectionCost(CollectionIssue issue) => 128L + 6L * issue.Message.Length;
    private static void Exact(JsonElement value, string[] names, bool requireAll = true)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new JsonException("Objeto JSON esperado.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!seen.Add(property.Name) || !names.Contains(property.Name, StringComparer.Ordinal)) throw new JsonException("Propriedade duplicada ou não reconhecida.");
        if (requireAll && seen.Count != names.Length) throw new JsonException("Propriedade obrigatória ausente.");
    }
    private static bool Unique(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject()) if (!names.Add(property.Name) || !Unique(property.Value)) return false;
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) if (!Unique(item)) return false;
        return true;
    }
}
