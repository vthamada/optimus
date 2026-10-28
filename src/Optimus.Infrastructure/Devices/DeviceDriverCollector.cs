using System.Globalization;
using System.Text;
using System.Text.Json;
using Optimus.Application;
using Optimus.Domain;

namespace Optimus.Infrastructure.Devices;

/// <summary>Owns the fixed local CIM inventory contract; never installs or enables a device/driver.</summary>
public sealed class DeviceDriverCollector(IWindowsSystemQueryRunner runner) : IDiagnosticCollector
{
    public const int MaximumRecordsPerQuery = 256;
    public const int MaximumJsonBytes = 300_000;
    private const int MaximumStderrTextBytes = 32_768;
    private const int MaximumPrimaryReasonBytes = 8_192;
    private const int MaximumPendingJsonBytes = 16_384;
    private const long MaximumProjectedJsonBytes = 256_000;
    private static readonly string[] DeviceFields = ["DeviceID", "Name", "PNPClass", "Manufacturer", "Status", "ConfigManagerErrorCode", "Present"];
    private static readonly string[] DriverFields = ["DeviceID", "DeviceName", "DriverVersion", "DriverProviderName", "DriverDate", "IsSigned", "Signer", "InfName"];

    public string Id => MaintenanceCollectors.DeviceDrivers;
    public string DisplayName => "Dispositivos e drivers registrados";

    public async Task<IReadOnlyList<DiagnosticEvidence>> CollectAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var execution = await runner.RunAsync(WindowsSystemQuery.DeviceInventory, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var facts = new List<DiagnosticFact>
        {
            new("toolStatus", "Estado da consulta externa", execution.Status.ToString()),
            new("toolExitCode", "Código de saída da consulta", NumberValue: execution.ExitCode)
        };
        var issues = new List<CollectionIssue>();
        RetainTransport(execution, facts, issues);
        string raw = execution.StandardOutput;
        if (Encoding.UTF8.GetByteCount(raw) > MaximumJsonBytes)
        {
            issues.Add(new(IssueCode.LimitReached, "A saída excedeu 300000 bytes UTF-8; não foi interpretada nem publicada como inventário completo."));
            return [Evidence(EvidenceStatus.Unavailable, 0, true)];
        }
        facts.Add(new("sourceJson", "JSON original da consulta local", raw, Detailed: true));
        if (!execution.ExitedSuccessfully)
        {
            issues.Add(new(execution.Error?.Code == ErrorCode.PermissionDenied ? IssueCode.PermissionDenied : IssueCode.ReadFailed,
                $"Consulta {execution.Status}, saída {execution.ExitCode?.ToString() ?? "desconhecida"}: {BoundReason(execution.Error?.Message ?? execution.StandardError)}"));
            return [Evidence(EvidenceStatus.Unavailable, 0, execution.Status == ToolRunStatus.OutputLimitExceeded)];
        }
        int retainedTransportFacts = facts.Count;
        int retainedTransportIssues = issues.Count;
        try
        {
            using var document = JsonDocument.Parse(raw, new JsonDocumentOptions { MaxDepth = 12 });
            var root = document.RootElement;
            if (HasDuplicateProperties(root)) throw new JsonException("Propriedade JSON duplicada.");
            ExactObject(root, ["kind", "version", "devices", "drivers"]);
            if (root.GetProperty("kind").GetString() != "optimus.device-inventory" || !root.GetProperty("version").TryGetInt32(out var version) || version != 1)
                throw new JsonException("Identidade/versão da consulta não reconhecida.");
            // Each selected source owns half of the projection budget; enumeration order cannot
            // consume the other source's representation before its records are examined.
            long devicesBudget = MaximumProjectedJsonBytes / 2;
            long driversBudget = MaximumProjectedJsonBytes - devicesBudget;
            var devices = ParseQuery(root.GetProperty("devices"), "devices", "Dispositivo", DeviceFields, facts, issues, ref devicesBudget);
            var drivers = ParseQuery(root.GetProperty("drivers"), "drivers", "Driver", DriverFields, facts, issues, ref driversBudget);
            bool limited = devices.Limited || drivers.Limited || issues.Any(issue => issue.Code == IssueCode.LimitReached);
            bool available = devices.Succeeded || drivers.Succeeded || devices.Retained > 0 || drivers.Retained > 0;
            return [Evidence(!available ? EvidenceStatus.Unavailable : issues.Count > 0 ? EvidenceStatus.Partial : EvidenceStatus.Complete,
                devices.Examined + drivers.Examined, limited)];
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException or OverflowException)
        {
            // A malformed envelope is not authority for publishing partially interpreted records.
            facts.RemoveRange(retainedTransportFacts, facts.Count - retainedTransportFacts);
            issues.RemoveRange(retainedTransportIssues, issues.Count - retainedTransportIssues);
            issues.Add(new(IssueCode.ReadFailed, "Contrato de dispositivos inválido: " + error.Message));
            return [Evidence(EvidenceStatus.Unavailable, 0, false)];
        }

        DiagnosticEvidence Evidence(EvidenceStatus status, int examined, bool limited) => new(Guid.NewGuid(), Id, DisplayName,
            "CIM local: Win32_PnPEntity e Win32_PnPSignedDriver", DateTimeOffset.UtcNow, status,
            new("Consulta local somente leitura; até 256 dispositivos e 256 registros de driver, com orçamento de transporte/projeção. IDs e registros repetidos são preservados sem junção por nome. Status e assinatura são valores reportados, não testes de saúde, autenticidade ou compatibilidade. Nenhum driver foi instalado, atualizado ou habilitado.",
                examined, MaximumRecordsPerQuery * 2, limited || issues.Any(issue => issue.Code == IssueCode.LimitReached)), facts, issues);
    }

    private static void RetainTransport(ToolRunResult execution, List<DiagnosticFact> facts, List<CollectionIssue> issues)
    {
        facts.Add(new("toolOutputCodePage", "Página de código declarada pelo executor", NumberValue: execution.OutputCodePage, Detailed: true));
        ExactBoundedText("toolStderr", "Stderr retornado pelo executor", execution.StandardError, MaximumStderrTextBytes);
        facts.Add(new("toolErrorCode", "Código do erro primário do executor", execution.Error?.Code.ToString(), Detailed: true));
        ExactBoundedText("toolErrorMessage", "Motivo primário retornado pelo executor", execution.Error?.Message, MaximumPrimaryReasonBytes);
        var pending = execution.Error?.PendingCleanupPaths;
        facts.Add(new("toolPendingCleanupCount", "Recursos com limpeza pendente informados pelo executor", NumberValue: pending?.Count, Detailed: true));
        if (pending is null) facts.Add(new("toolPendingCleanupPaths", "Lista original de recursos pendentes (JSON)", Detailed: true));
        else
        {
            long characters = 0;
            foreach (var value in pending) characters += value?.Length ?? 0;
            if (pending.Count <= 128 && characters <= MaximumPendingJsonBytes)
            {
                // The character bound limits allocation of escaped JSON before the exact byte check.
                ExactBoundedText("toolPendingCleanupPaths", "Lista original de recursos pendentes (JSON)",
                    JsonSerializer.Serialize(pending), MaximumPendingJsonBytes);
            }
            else
            {
                facts.Add(new("toolPendingCleanupPaths", "Lista original de recursos pendentes (não retida por limite)", Detailed: true));
                facts.Add(new("toolPendingCleanupCharacters", "Caracteres observados na lista pendente não retida", NumberValue: characters, Detailed: true));
                issues.Add(new(IssueCode.LimitReached, "A lista pendente excedeu 128 recursos ou o limite de preparação de 16384 caracteres; não foi truncada ou tratada como vazia. O executor conserva a propriedade dos recursos."));
            }
        }
        if (execution.StandardErrorBytes is { } stderrBytes)
        {
            facts.Add(new("toolStderrOriginalBytes", "Quantidade de bytes originais de stderr retidos pelo executor", NumberValue: stderrBytes.Length, Unit: "bytes", Detailed: true));
            if (stderrBytes.Length <= MaximumStderrTextBytes)
                facts.Add(new("toolStderrBytesBase64", "Bytes originais de stderr (base64)", Convert.ToBase64String(stderrBytes), Detailed: true));
            else issues.Add(new(IssueCode.LimitReached, "O stderr binário excedeu 32768 bytes e não foi copiado para a evidência; o texto e a quantidade observada têm seus próprios limites explícitos."));
        }

        void ExactBoundedText(string key, string label, string? value, int maximumBytes)
        {
            int? bytes = value is null ? null : Encoding.UTF8.GetByteCount(value);
            facts.Add(new(key + ".utf8Bytes", label + ": tamanho UTF-8", NumberValue: bytes, Unit: "bytes", Detailed: true));
            if (bytes is null || bytes <= maximumBytes) facts.Add(new(key, label, value, Detailed: true));
            else
            {
                facts.Add(new(key, label + " (não retido integralmente por limite)", Detailed: true));
                issues.Add(new(IssueCode.LimitReached, $"{key} excedeu {maximumBytes} bytes UTF-8; o conteúdo não foi truncado nem substituído por um prefixo. A quantidade original observada foi preservada."));
            }
        }
    }

    private static QueryResult ParseQuery(JsonElement query, string prefix, string label, string[] fields,
        List<DiagnosticFact> facts, List<CollectionIssue> issues, ref long remaining)
    {
        ExactObject(query, ["succeeded", "examined", "limitReached", "records", "issues"]);
        bool succeeded = query.GetProperty("succeeded").GetBoolean();
        bool limited = query.GetProperty("limitReached").GetBoolean();
        if (!query.GetProperty("examined").TryGetInt32(out int examined) || examined < 0 || examined > MaximumRecordsPerQuery + 1)
            throw new JsonException("Contagem de registros fora do contrato.");
        var records = query.GetProperty("records");
        if (records.ValueKind != JsonValueKind.Array || records.GetArrayLength() > MaximumRecordsPerQuery || records.GetArrayLength() > examined
            || (examined > MaximumRecordsPerQuery && !limited)
            || (succeeded && examined > records.GetArrayLength() && !limited)) throw new JsonException("Cobertura e quantidade de registros divergem.");
        var sourceIssues = query.GetProperty("issues");
        if (sourceIssues.ValueKind != JsonValueKind.Array || sourceIssues.GetArrayLength() > 4096) throw new JsonException("Lista de limitações inválida.");
        foreach (var issue in sourceIssues.EnumerateArray())
        {
            ExactObject(issue, ["code", "message"]);
            string? message = issue.GetProperty("message").GetString();
            if (message is null || message.Length > 2048) throw new JsonException("Limitação excede seu limite de texto.");
            var code = issue.GetProperty("code").GetString() switch
            {
                "ReadFailed" => IssueCode.ReadFailed,
                "LimitReached" => IssueCode.LimitReached,
                "PermissionDenied" => IssueCode.PermissionDenied,
                "NotSupported" => IssueCode.NotSupported,
                _ => throw new JsonException("Código de limitação não reconhecido.")
            };
            issues.Add(new(code, prefix + ": " + message));
            limited |= code == IssueCode.LimitReached;
        }
        if (!succeeded) issues.Add(new(IssueCode.ReadFailed, prefix + ": a consulta não confirmou conclusão; registros anteriores, se houver, foram preservados."));
        if (limited) issues.Add(new(IssueCode.LimitReached, prefix + ": parte do catálogo foi omitida pelo limite declarado."));
        int projected = 0;
        int index = 0;
        foreach (var record in records.EnumerateArray())
        {
            ExactObject(record, fields, requireAll: false);
            var candidate = new List<DiagnosticFact>();
            var candidateIssues = new List<CollectionIssue>();
            foreach (var field in fields)
            {
                var factKey = $"{prefix}.{index}.{field}";
                var factLabel = $"{label} {index + 1} · {Label(field)}";
                if (!record.TryGetProperty(field, out var value))
                {
                    candidate.Add(new(factKey, factLabel));
                    candidateIssues.Add(new(IssueCode.ReadFailed, $"{factKey}: propriedade não entregue; nenhum valor foi presumido."));
                }
                else if (value.ValueKind == JsonValueKind.Null)
                {
                    candidate.Add(new(factKey, factLabel));
                    if (field == "DeviceID") candidateIssues.Add(new(IssueCode.NotSupported, factKey + ": identidade não informada pela fonte."));
                }
                else if (field is "Present" or "IsSigned")
                {
                    bool valid = value.ValueKind is JsonValueKind.True or JsonValueKind.False;
                    candidate.Add(new(factKey, factLabel, valid ? (value.GetBoolean() ? "true" : "false") : null));
                    if (!valid) candidateIssues.Add(new(IssueCode.ReadFailed, factKey + ": booleano não reconhecido; representação original mantida no JSON."));
                }
                else if (field == "ConfigManagerErrorCode")
                {
                    bool valid = value.ValueKind == JsonValueKind.Number && value.TryGetUInt32(out _);
                    candidate.Add(new(factKey, factLabel, NumberValue: valid ? value.GetUInt32() : null));
                    if (!valid) candidateIssues.Add(new(IssueCode.ReadFailed, factKey + ": código uint32 não reconhecido; não convertido em zero."));
                }
                else if (field == "DriverDate" && value.ValueKind == JsonValueKind.Object)
                {
                    AddDriverDate(value, factKey, factLabel, candidate, candidateIssues);
                }
                else
                {
                    string? text = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
                    bool valid = text is not null && text.Length <= 2048;
                    candidate.Add(new(factKey, factLabel, valid ? text : null));
                    if (!valid) candidateIssues.Add(new(text?.Length > 2048 ? IssueCode.LimitReached : IssueCode.ReadFailed,
                        factKey + ": texto ausente, de tipo divergente ou acima de 2048 caracteres; representação original mantida no JSON."));
                }
            }
            long cost = candidate.Sum(fact => 128L + 6L * (fact.Key.Length + fact.Label.Length + (fact.TextValue?.Length ?? 0)))
                + candidateIssues.Sum(issue => 128L + 6L * issue.Message.Length);
            if (cost <= remaining)
            {
                remaining -= cost;
                facts.AddRange(candidate);
                issues.AddRange(candidateIssues);
                limited |= candidateIssues.Any(issue => issue.Code == IssueCode.LimitReached);
                projected++;
            }
            else limited = true;
            index++;
        }
        if (projected < records.GetArrayLength()) issues.Add(new(IssueCode.LimitReached, prefix + $": limite de projeção de {MaximumProjectedJsonBytes / 2} bytes estimados por consulta atingido; o JSON original retém os registros recebidos."));
        facts.Add(new(prefix + ".querySucceeded", label + ": consulta concluída", succeeded ? "true" : "false"));
        facts.Add(new(prefix + ".examined", label + ": registros enumerados", NumberValue: examined));
        facts.Add(new(prefix + ".retained", label + ": registros no JSON original", NumberValue: records.GetArrayLength()));
        facts.Add(new(prefix + ".projected", label + ": registros projetados em fatos", NumberValue: projected));
        return new(succeeded, examined, records.GetArrayLength(), limited);
    }

    private static void AddDriverDate(JsonElement value, string key, string label, List<DiagnosticFact> facts, List<CollectionIssue> issues)
    {
        try
        {
            ExactObject(value, ["sourceType", "value", "kind"]);
            string? type = value.GetProperty("sourceType").GetString();
            string? text = value.GetProperty("value").GetString();
            string? kind = value.GetProperty("kind").GetString();
            bool valid = text is not null && text.Length <= 64 && ((type, kind) switch
            {
                ("System.DateTime", "Utc") => DateTime.TryParseExact(text, "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
                ("System.DateTime", "Unspecified") => DateTime.TryParseExact(text, "yyyy-MM-dd'T'HH:mm:ss.fffffff", CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
                ("System.DateTime", "Local") or ("System.DateTimeOffset", "Offset") => text.Length == 33 && (text[27] is '+' or '-')
                    && DateTimeOffset.TryParseExact(text, "yyyy-MM-dd'T'HH:mm:ss.fffffffzzz", CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
                _ => false
            });
            if (!valid) throw new JsonException("Tipo, kind ou representação O divergente do envelope de data do driver.");
            // Parsing above validates only shape/calendar/explicit offset. It never supplies a local
            // timezone or converts the value; the exact producer text is the observation.
            facts.Add(new(key, kind == "Unspecified" ? label + " (fuso não informado)" : label, text));
            facts.Add(new(key + ".sourceType", "Tipo original retornado pelo CIM para a data", type, Detailed: true));
            facts.Add(new(key + ".kind", "Kind/fuso declarado pelo CIM para a data", kind, Detailed: true));
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException)
        {
            facts.Add(new(key, label));
            issues.Add(new(IssueCode.ReadFailed, key + ": envelope de data não reconhecido; nenhum fuso ou timestamp foi inferido. Original preservado no JSON. " + error.Message));
        }
    }

    private static string Label(string field) => field switch
    {
        "DeviceID" => "ID original",
        "Name" or "DeviceName" => "nome reportado",
        "PNPClass" => "classe PnP",
        "Manufacturer" => "fabricante reportado",
        "Status" => "estado reportado (sem teste físico)",
        "ConfigManagerErrorCode" => "código reportado pelo gerenciador",
        "Present" => "presença reportada",
        "DriverVersion" => "versão reportada",
        "DriverProviderName" => "fornecedor reportado",
        "DriverDate" => "data do driver reportada pela consulta (sem normalização)",
        "IsSigned" => "assinatura reportada (não verificada pelo opTImus)",
        "Signer" => "signatário reportado",
        "InfName" => "INF reportado",
        _ => field
    };

    private static void ExactObject(JsonElement element, string[] names, bool requireAll = true)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new JsonException("Era esperado um objeto JSON.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            if (!seen.Add(property.Name) || !names.Contains(property.Name, StringComparer.Ordinal)) throw new JsonException("Propriedade duplicada ou não reconhecida: " + property.Name);
        if (requireAll && seen.Count != names.Length) throw new JsonException("Propriedade obrigatória ausente.");
    }
    private static string BoundReason(string text) => text.Length <= 2048 ? text : "A mensagem externa excedeu o limite de texto; consulte o resultado da execução.";
    private static bool HasDuplicateProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
                if (!names.Add(property.Name) || HasDuplicateProperties(property.Value)) return true;
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) if (HasDuplicateProperties(item)) return true;
        return false;
    }
    private sealed record QueryResult(bool Succeeded, int Examined, int Retained, bool Limited);
}
