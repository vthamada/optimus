using System.Globalization;
using System.Text;
using System.Text.Json;
using Optimus.Application;
using Optimus.Domain;

namespace Optimus.Infrastructure.Security;

/// <summary>Reads the fixed local ActiveStore profile snapshot without evaluating traffic or interface activation.</summary>
public sealed class FirewallProfilesCollector(IWindowsSystemQueryRunner runner) : IDiagnosticCollector
{
    public const int MaximumProfiles = 8;
    public const int MaximumResponseBytes = 65_536;
    private const int MaximumText = 512;
    private const long ProjectionBudget = 32_000;
    private const string EnumPrefix = "Microsoft.PowerShell.Cmdletization.GeneratedTypes.NetSecurity.";
    private static readonly string[] Fields = ["Name", "Enabled", "DefaultInboundAction", "DefaultOutboundAction", "AllowInboundRules", "AllowLocalFirewallRules", "AllowLocalIPsecRules", "NotifyOnListen"];

    public string Id => MaintenanceCollectors.Firewall;
    public string DisplayName => "Perfis do Firewall do Windows: política local efetiva reportada";

    public async Task<IReadOnlyList<DiagnosticEvidence>> CollectAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var execution = await runner.RunAsync(WindowsSystemQuery.FirewallProfiles, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var captured = DateTimeOffset.UtcNow;
        List<DiagnosticFact> facts = [
            new("policyStore", "PolicyStore local selecionado", "ActiveStore"),
            new("interfaceProfileActivationResolved", "Perfil ativo por interface identificado", "false"),
            new("trafficEffectivenessTested", "Eficácia sobre tráfego avaliada", "false"),
            new("rulesQueried", "Regras do firewall consultadas", "false"),
            new("toolStatus", "Estado do executor da consulta", execution.Status.ToString(), Detailed: true),
            new("toolExitCode", "Código de saída do executor", NumberValue: execution.ExitCode, Detailed: true),
            new("elapsedSeconds", "Duração do Job da consulta", NumberValue: execution.Metrics.Elapsed.Ticks / (decimal)TimeSpan.TicksPerSecond, Unit: "s", Detailed: true),
            new("cpuSeconds", "CPU da família do Job da consulta", NumberValue: execution.Metrics.CpuTime is { } cpu ? cpu.Ticks / (decimal)TimeSpan.TicksPerSecond : null, Unit: "s", Detailed: true),
            new("peakProcessFamilyMemoryBytes", "Pico de commit da família do Job", NumberValue: execution.Metrics.PeakProcessFamilyMemoryBytes, Unit: "bytes", Detailed: true)
        ];
        List<CollectionIssue> issues = [];
        RetainTransport(execution, facts, issues);
        int bytes = Encoding.UTF8.GetByteCount(execution.StandardOutput);
        facts.Add(new("sourceJsonUtf8Bytes", "Tamanho UTF-8 da resposta original", NumberValue: bytes, Unit: "bytes", Detailed: true));
        if (bytes > MaximumResponseBytes)
        {
            issues.Add(new(IssueCode.LimitReached, "Resposta acima de 65536 bytes; não foi interpretada nem retida como documento completo."));
            return [Evidence(EvidenceStatus.Unavailable, 0)];
        }
        facts.Add(new("sourceJson", "JSON original: tipos, códigos, nomes e valores CIM dos campos selecionados", execution.StandardOutput, Detailed: true));
        if (!execution.ExitedSuccessfully)
        {
            if (execution.StandardOutputBytes is { } raw)
            {
                facts.Add(new("toolStdoutOriginalBytes", "Bytes de stdout retidos pelo executor", NumberValue: raw.Length, Unit: "bytes", Detailed: true));
                if (raw.Length <= MaximumResponseBytes) facts.Add(new("toolStdoutBytesBase64", "Stdout original do transporte falho (base64)", Convert.ToBase64String(raw), Detailed: true));
                else issues.Add(new(IssueCode.LimitReached, "Stdout binário acima de 65536 bytes; conteúdo não copiado, quantidade original retida."));
            }
            issues.Add(new(execution.Error?.Code == ErrorCode.PermissionDenied ? IssueCode.PermissionDenied : IssueCode.ReadFailed,
                "A consulta não confirmou conclusão; os dados e o motivo primário foram preservados sem inferir estado de proteção."));
            return [Evidence(EvidenceStatus.Unavailable, 0)];
        }
        if (execution.StandardError.Length > 0) issues.Add(new(IssueCode.ReadFailed, "Stderr foi retornado; dados preservados com cobertura parcial."));
        int transportFacts = facts.Count, transportIssues = issues.Count;
        try
        {
            using var document = JsonDocument.Parse(execution.StandardOutput, new JsonDocumentOptions { MaxDepth = 12 });
            var root = document.RootElement;
            if (Duplicate(root)) throw new JsonException("Propriedade JSON duplicada.");
            Exact(root, ["kind", "version", "observedAt", "policyStore", "query"]);
            if (root.GetProperty("kind").GetString() != "optimus.firewall-profiles" || !root.GetProperty("version").TryGetInt32(out int version) || version != 1
                || root.GetProperty("policyStore").GetString() != "ActiveStore") throw new JsonException("Identidade, versão ou PolicyStore não reconhecido.");
            var observed = root.GetProperty("observedAt").GetString();
            if (!Timestamp(observed, out captured)) throw new JsonException("Data de observação exige fuso explícito.");
            facts.Add(new("sourceObservedAt", "Data original da consulta local", observed, Detailed: true));
            var query = root.GetProperty("query");
            Exact(query, ["succeeded", "examined", "limitReached", "records", "issues"]);
            bool succeeded = query.GetProperty("succeeded").GetBoolean(), limited = query.GetProperty("limitReached").GetBoolean();
            if (!query.GetProperty("examined").TryGetInt32(out int examined) || examined < 0 || examined > MaximumProfiles + 1) throw new JsonException("Contagem fora do catálogo.");
            var records = query.GetProperty("records");
            if (records.ValueKind != JsonValueKind.Array || records.GetArrayLength() > MaximumProfiles || records.GetArrayLength() > examined
                || examined > MaximumProfiles && !limited || succeeded && examined > records.GetArrayLength() && !limited)
                throw new JsonException("Cobertura declarada diverge dos perfis retidos.");
            ReadIssues(query.GetProperty("issues"), issues);
            if (!succeeded) issues.Add(new(IssueCode.ReadFailed, "A fonte não confirmou conclusão; registros anteriores continuam no JSON original."));
            if (limited) issues.Add(new(IssueCode.LimitReached, "A fonte declarou omissões de registros, campos ou retenção."));
            int index = 0, projected = 0;
            long budget = ProjectionBudget;
            foreach (var record in records.EnumerateArray())
            {
                Exact(record, Fields, requireAll: false);
                List<DiagnosticFact> candidate = [];
                List<CollectionIssue> notes = [];
                foreach (var field in Fields)
                {
                    string key = $"profiles.{index}.{field}";
                    if (record.TryGetProperty(field, out var value)) AddField(value, field, key, $"Perfil {index + 1} · {Label(field)}", candidate, notes);
                    else { candidate.Add(new(key, Label(field))); notes.Add(new(IssueCode.ReadFailed, key + ": campo ausente; nenhum estado foi presumido.")); }
                }
                long cost = candidate.Sum(fact => 128L + 6L * (fact.Key.Length + fact.Label.Length + (fact.TextValue?.Length ?? 0)))
                    + notes.Sum(issue => 128L + 6L * issue.Message.Length);
                if (cost <= budget) { budget -= cost; facts.AddRange(candidate); issues.AddRange(notes); projected++; }
                index++;
            }
            if (projected < records.GetArrayLength()) issues.Add(new(IssueCode.LimitReached, "Projeção de 32000 bytes estimados atingida; perfis recebidos permanecem no JSON original."));
            facts.Add(new("querySucceeded", "Conclusão reportada pela consulta", succeeded ? "true" : "false"));
            facts.Add(new("examinedProfiles", "Perfis enumerados, incluindo eventual sonda", NumberValue: examined));
            facts.Add(new("retainedProfiles", "Perfis retidos no JSON original", NumberValue: records.GetArrayLength()));
            facts.Add(new("projectedProfiles", "Perfis apresentados como fatos", NumberValue: projected));
            return [Evidence(!succeeded && records.GetArrayLength() == 0 ? EvidenceStatus.Unavailable : issues.Count > 0 ? EvidenceStatus.Partial : EvidenceStatus.Complete, examined)];
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException or OverflowException)
        {
            facts.RemoveRange(transportFacts, facts.Count - transportFacts);
            issues.RemoveRange(transportIssues, issues.Count - transportIssues);
            captured = DateTimeOffset.UtcNow;
            issues.Add(new(IssueCode.ReadFailed, "Contrato de perfis inválido: " + error.Message));
            return [Evidence(EvidenceStatus.Unavailable, 0)];
        }

        DiagnosticEvidence Evidence(EvidenceStatus status, int examined) => new(Guid.NewGuid(), Id, DisplayName, "Get-NetFirewallProfile local -PolicyStore ActiveStore", captured, status,
            new("Somente oito campos selecionados de até oito perfis mais sonda, com 512 caracteres por campo textual, resposta de 64 KiB e projeção conservadora de 32000 bytes. Tipos, nomes/códigos de enums e valores CIM originais ficam no JSON retido. Enums GPO não viram booleanos; NotConfigured não vira false. Não identifica quais perfis estão ativos por interface nem avalia regras, tráfego, cobertura ou eficácia de proteção. Nenhuma configuração foi alterada; snapshot não atômico.",
                examined, MaximumProfiles, issues.Any(issue => issue.Code == IssueCode.LimitReached)), facts.ToArray(), issues.ToArray());
    }

    private static void AddField(JsonElement envelope, string field, string key, string label, List<DiagnosticFact> facts, List<CollectionIssue> issues)
    {
        try
        {
            Exact(envelope, ["kind", "sourceType", "underlyingType", "value", "name", "cimType", "cimValue"]);
            string? kind = Text(envelope.GetProperty("kind"), nullable: false), sourceType = Text(envelope.GetProperty("sourceType")), underlying = Text(envelope.GetProperty("underlyingType")), name = Text(envelope.GetProperty("name"));
            string? cimType = Text(envelope.GetProperty("cimType"));
            var value = envelope.GetProperty("value");
            var raw = envelope.GetProperty("cimValue");
            if (kind is "null" or "omitted")
            {
                if (value.ValueKind != JsonValueKind.Null || raw.ValueKind != JsonValueKind.Null || name is not null || underlying is not null
                    || kind == "null" && sourceType is not null) throw new JsonException("Representação nula/omitida inconsistente.");
                facts.Add(new(key, label));
                issues.Add(new(IssueCode.NotSupported, key + ": valor CIM nulo/omitido. Getter adaptado não foi usado para transformar ausência em enum False."));
                return;
            }
            if (kind == "unavailable")
            {
                if (sourceType is not null || underlying is not null || name is not null || value.ValueKind != JsonValueKind.Null || cimType is null)
                    throw new JsonException("Forma adaptada indisponível inconsistente.");
                ValidateCim(raw, cimType);
                facts.Add(new(key, label));
                issues.Add(new(IssueCode.ReadFailed, key + ": getter ou representação adaptada indisponível; o tipo adaptado permanece desconhecido e o escalar CIM original foi retido."));
                return;
            }
            if (sourceType is null || cimType is null) throw new JsonException("Tipo de origem obrigatório ausente.");
            ValidateCim(raw, cimType);
            switch (kind)
            {
                case "enum":
                    if (underlying is null || name is null || !Integer(value, underlying, out decimal code)) throw new JsonException("Enum sem tipo integral, código ou nome original válido.");
                    bool numericMatch = raw.ValueKind == JsonValueKind.Number && raw.TryGetDecimal(out var rawCode) && rawCode == code;
                    bool nameMatch = field == "Name" && raw.ValueKind == JsonValueKind.String && raw.GetString() == name;
                    if (!numericMatch && !nameMatch) throw new JsonException("Código do enum diverge do valor CIM original.");
                    string? expected = ExpectedName(field, sourceType, underlying, code);
                    bool recognized = expected is not null && expected == name;
                    facts.Add(new(key, label + " (nome de enum original)", expected is not null && expected != name ? null : name));
                    facts.Add(new(key + ".code", "Código original do enum de " + field, NumberValue: code, Detailed: true));
                    if (!recognized)
                    {
                        facts.Add(new(key + ".reportedName", "Nome original do enum não interpretado", name, Detailed: true));
                        issues.Add(new(expected is null ? IssueCode.NotSupported : IssueCode.ReadFailed, key + ": tipo/código/nome de enum não mapeado ou divergente; originais preservados, sem conversão em booleano ou decisão de proteção."));
                    }
                    break;
                case "boolean":
                    if (sourceType != "System.Boolean" || underlying is not null || name is not null || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new JsonException("Booleano original inconsistente.");
                    bool booleanMatches = raw.ValueKind == value.ValueKind || raw.ValueKind == JsonValueKind.Number && raw.TryGetUInt16(out ushort booleanCode)
                        && (booleanCode == 0 && value.ValueKind == JsonValueKind.False || booleanCode == 1 && value.ValueKind == JsonValueKind.True);
                    if (!booleanMatches) throw new JsonException("Booleano adaptado não corresponde ao CIM booleano ou código 0/1; NotConfigured não foi convertido.");
                    facts.Add(new(key, label + " (booleano original)", value.GetRawText()));
                    if (field is "Name" or "DefaultInboundAction" or "DefaultOutboundAction") issues.Add(new(IssueCode.NotSupported, key + ": booleano retido como observação, sem inferir nome ou ação de firewall."));
                    break;
                case "text":
                    if (sourceType != "System.String" || underlying is not null || name is not null || value.ValueKind != JsonValueKind.String
                        || raw.ValueKind != JsonValueKind.String || Text(value, false) != Text(raw, false)) throw new JsonException("Texto original inconsistente.");
                    facts.Add(new(key, label + " (texto original)", value.GetString()));
                    if (field != "Name") issues.Add(new(IssueCode.NotSupported, key + ": texto original não foi convertido em booleano ou ação configurada."));
                    break;
                case "integer":
                    if (underlying is not null || name is not null || !Integer(value, sourceType, out decimal integer)
                        || raw.ValueKind != JsonValueKind.Number || !raw.TryGetDecimal(out var original) || integer != original) throw new JsonException("Inteiro original inconsistente.");
                    facts.Add(new(key, label + " (código integral sem enum)", NumberValue: integer));
                    issues.Add(new(IssueCode.NotSupported, key + ": a fonte não forneceu o enum adaptado; o número original não foi convertido em estado booleano ou ação."));
                    break;
                case "unsupported":
                    if (value.ValueKind != JsonValueKind.Null || underlying is not null || name is not null) throw new JsonException("Forma não suportada inconsistente.");
                    facts.Add(new(key, label));
                    issues.Add(new(IssueCode.NotSupported, key + ": tipo adaptado não suportado; tipo e valor CIM conhecido permanecem no JSON original."));
                    break;
                default: throw new JsonException("Representação de campo não reconhecida.");
            }
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException or OverflowException)
        {
            facts.Add(new(key, label));
            issues.Add(new(IssueCode.ReadFailed, key + ": forma/tipo original inválido; nenhum estado foi inferido. Fonte retida no JSON. " + error.Message));
        }
    }

    private static string? ExpectedName(string field, string type, string underlying, decimal code)
    {
        if (underlying != "System.UInt16") return null;
        if (field == "Name" && type == EnumPrefix + "Profile") return code switch { 0 => "Any", 1 => "Domain", 2 => "Private", 4 => "Public", 65535 => "NotApplicable", _ => null };
        if (field is "DefaultInboundAction" or "DefaultOutboundAction" && type == EnumPrefix + "Action") return code switch { 0 => "NotConfigured", 2 => "Allow", 4 => "Block", _ => null };
        if (field is "Enabled" or "AllowInboundRules" or "AllowLocalFirewallRules" or "AllowLocalIPsecRules" or "NotifyOnListen" && type == EnumPrefix + "GpoBoolean") return code switch { 0 => "False", 1 => "True", 2 => "NotConfigured", _ => null };
        return null;
    }
    private static void ValidateCim(JsonElement value, string type)
    {
        bool valid = type switch
        {
            "String" => value.ValueKind == JsonValueKind.String && Text(value, false) is not null,
            "Boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "UInt8" => Integer(value, "System.Byte", out _),
            "SInt8" => Integer(value, "System.SByte", out _),
            "UInt16" => Integer(value, "System.UInt16", out _),
            "SInt16" => Integer(value, "System.Int16", out _),
            "UInt32" => Integer(value, "System.UInt32", out _),
            "SInt32" => Integer(value, "System.Int32", out _),
            "UInt64" => Integer(value, "System.UInt64", out _),
            "SInt64" => Integer(value, "System.Int64", out _),
            _ => false
        };
        if (!valid) throw new JsonException("Tipo/valor CIM original divergente.");
    }
    private static bool Integer(JsonElement value, string type, out decimal number)
    {
        number = 0;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDecimal(out decimal parsed) || parsed != decimal.Truncate(parsed)) return false;
        bool valid = type switch
        {
            "System.Byte" => parsed is >= byte.MinValue and <= byte.MaxValue,
            "System.SByte" => parsed is >= sbyte.MinValue and <= sbyte.MaxValue,
            "System.UInt16" => parsed is >= ushort.MinValue and <= ushort.MaxValue,
            "System.Int16" => parsed is >= short.MinValue and <= short.MaxValue,
            "System.UInt32" => parsed is >= uint.MinValue and <= uint.MaxValue,
            "System.Int32" => parsed is >= int.MinValue and <= int.MaxValue,
            "System.UInt64" => parsed is >= ulong.MinValue and <= ulong.MaxValue,
            "System.Int64" => parsed is >= long.MinValue and <= long.MaxValue,
            _ => false
        };
        if (valid) number = parsed;
        return valid;
    }
    private static string? Text(JsonElement value, bool nullable = true)
    {
        if (nullable && value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String) throw new JsonException("Texto ausente ou de tipo divergente.");
        var text = value.GetString();
        if (text is null || text.Length > MaximumText) throw new JsonException("Texto ausente ou acima de 512 caracteres.");
        return text;
    }
    private static void RetainTransport(ToolRunResult run, List<DiagnosticFact> facts, List<CollectionIssue> issues)
    {
        facts.Add(new("toolErrorCode", "Código primário do executor", run.Error?.Code.ToString(), Detailed: true));
        facts.Add(new("toolOutputCodePage", "Página de código do executor", NumberValue: run.OutputCodePage, Detailed: true));
        Add("toolStderr", run.StandardError, 8192);
        Add("toolErrorMessage", run.Error?.Message, 8192);
        var pending = run.Error?.PendingCleanupPaths;
        facts.Add(new("toolPendingCleanupCount", "Recursos pendentes do executor", NumberValue: pending?.Count, Detailed: true));
        if (pending is null) Add("toolPendingCleanupPaths", null, 8192);
        else if (pending.Count <= 64 && pending.Sum(path => (long)(path?.Length ?? 0)) <= 4096) Add("toolPendingCleanupPaths", JsonSerializer.Serialize(pending), 8192);
        else { Add("toolPendingCleanupPaths", null, 8192); issues.Add(new(IssueCode.LimitReached, "Lista pendente excedeu 64 recursos/4096 caracteres; conteúdo não truncado, ownership permanece no executor.")); }
        if (run.StandardErrorBytes is { } raw)
        {
            facts.Add(new("toolStderrOriginalBytes", "Bytes de stderr retidos pelo executor", NumberValue: raw.Length, Unit: "bytes", Detailed: true));
            if (raw.Length <= 8192) facts.Add(new("toolStderrBytesBase64", "Stderr original (base64)", Convert.ToBase64String(raw), Detailed: true));
            else issues.Add(new(IssueCode.LimitReached, "Stderr binário acima de 8192 bytes; conteúdo não copiado, quantidade retida."));
        }
        void Add(string key, string? text, int maximum)
        {
            int? bytes = text is null ? null : Encoding.UTF8.GetByteCount(text);
            facts.Add(new(key + ".utf8Bytes", key + ": tamanho UTF-8 observado", NumberValue: bytes, Unit: "bytes", Detailed: true));
            facts.Add(new(key, key + ": conteúdo original do executor", bytes is null || bytes <= maximum ? text : null, Detailed: true));
            if (bytes > maximum) issues.Add(new(IssueCode.LimitReached, key + ": conteúdo acima do orçamento; nenhum prefixo foi apresentado como original completo."));
        }
    }
    private static string Label(string field) => field switch
    {
        "Name" => "nome reportado",
        "Enabled" => "habilitação configurada reportada",
        "DefaultInboundAction" => "ação padrão de entrada reportada",
        "DefaultOutboundAction" => "ação padrão de saída reportada",
        "AllowInboundRules" => "AllowInboundRules reportado",
        "AllowLocalFirewallRules" => "mescla local de regras reportada",
        "AllowLocalIPsecRules" => "mescla local IPsec reportada",
        "NotifyOnListen" => "notificação ao escutar reportada",
        _ => field
    };
    private static void ReadIssues(JsonElement array, List<CollectionIssue> issues)
    {
        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > 66) throw new JsonException("Lista de limitações fora do contrato.");
        foreach (var item in array.EnumerateArray())
        {
            Exact(item, ["code", "message"]);
            var message = item.GetProperty("message").GetString();
            if (message is null || message.Length > 1024) throw new JsonException("Detalhe de limitação fora do orçamento.");
            var code = item.GetProperty("code").GetString() switch { "ReadFailed" => IssueCode.ReadFailed, "NotSupported" => IssueCode.NotSupported, "LimitReached" => IssueCode.LimitReached, "PermissionDenied" => IssueCode.PermissionDenied, _ => throw new JsonException("Código de limitação desconhecido.") };
            issues.Add(new(code, message));
        }
    }
    private static bool Timestamp(string? text, out DateTimeOffset value)
    {
        value = default;
        return text is { Length: >= 20 and <= 64 } && DateTimeOffset.TryParseExact(text,
            ["yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz", "yyyy-MM-dd'T'HH:mm:sszzz", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", "yyyy-MM-dd'T'HH:mm:ss'Z'"], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out value);
    }
    private static void Exact(JsonElement value, string[] fields, bool requireAll = true)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new JsonException("Objeto JSON esperado.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in value.EnumerateObject()) if (!seen.Add(item.Name) || !fields.Contains(item.Name, StringComparer.Ordinal)) throw new JsonException("Propriedade duplicada ou não reconhecida.");
        if (requireAll && seen.Count != fields.Length) throw new JsonException("Propriedade obrigatória ausente.");
    }
    private static bool Duplicate(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in value.EnumerateObject()) if (!seen.Add(item.Name) || Duplicate(item.Value)) return true;
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) if (Duplicate(item)) return true;
        return false;
    }
}
