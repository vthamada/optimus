using System.Globalization;
using System.Text;
using System.Text.Json;
using Optimus.Application;
using Optimus.Domain;

namespace Optimus.Infrastructure.Hardware;

/// <summary>Owns selected local CIM hardware metadata, without serial numbers or health/configuration inference.</summary>
public sealed class HardwareProfileCollector(IWindowsSystemQueryRunner runner) : IDiagnosticCollector
{
    public const int MaximumResponseBytes = 262_144;
    public const int MaximumMemoryModules = 32;
    private const long ProjectionBytesPerQuery = 24_000;
    private enum Scalar { Text, UInt32, UInt64, Date }
    private sealed record Field(string Name, Scalar Type = Scalar.Text, string? Unit = null, int MaximumCharacters = 512);
    private sealed record Query(string Name, string ClassName, int Maximum, Field[] Fields);
    private static readonly Query[] Queries = [
        new("baseboards", "Win32_BaseBoard", 8, [new("Manufacturer"), new("Product"), new("Version")]),
        new("system", "Win32_ComputerSystem", 8, [new("Manufacturer"), new("Model")]),
        new("bios", "Win32_BIOS", 8, [new("Manufacturer"), new("SMBIOSBIOSVersion"), new("ReleaseDate", Scalar.Date)]),
        new("processors", "Win32_Processor", 8, [new("Name", MaximumCharacters: 1024), new("Manufacturer"), new("MaxClockSpeed", Scalar.UInt32, "MHz")]),
        new("memory", "Win32_PhysicalMemory", MaximumMemoryModules, [new("DeviceLocator"), new("BankLabel"), new("Manufacturer"), new("PartNumber"),
            new("Capacity", Scalar.UInt64, "bytes"), new("Speed", Scalar.UInt32), new("ConfiguredClockSpeed", Scalar.UInt32, "MHz"), new("SMBIOSMemoryType", Scalar.UInt32)])
    ];
    public string Id => "windows.hardware-profile";
    public string DisplayName => "Perfil de hardware e firmware reportado pelo Windows";

    public async Task<IReadOnlyList<DiagnosticEvidence>> CollectAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var execution = await runner.RunAsync(WindowsSystemQuery.HardwareProfile, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var captured = DateTimeOffset.UtcNow;
        List<DiagnosticFact> facts = [
            new("query", "Classes CIM locais selecionadas", string.Join("; ", Queries.Select(query => query.ClassName))),
            new("healthTestRequested", "Teste físico ou inferência de defeito solicitado", "false"),
            new("xmpStateInferred", "Estado XMP/EXPO inferido das frequências", "false"),
            new("memory.Speed.unitPolicy", "Unidade de Speed", "Valor bruto sem conversão; o contrato CIM documenta ns, enquanto ConfiguredClockSpeed documenta MHz. Não são equiparados.", Detailed: true),
            new("toolStatus", "Estado do executor", execution.Status.ToString(), Detailed: true),
            new("toolExitCode", "Código de saída do executor", NumberValue: execution.ExitCode, Detailed: true),
            new("elapsedSeconds", "Duração do Job", NumberValue: execution.Metrics.Elapsed.Ticks / (decimal)TimeSpan.TicksPerSecond, Unit: "s", Detailed: true),
            new("cpuSeconds", "CPU da família do Job", NumberValue: execution.Metrics.CpuTime is { } cpu ? cpu.Ticks / (decimal)TimeSpan.TicksPerSecond : null, Unit: "s", Detailed: true),
            new("peakProcessFamilyMemoryBytes", "Pico de commit da família do Job", NumberValue: execution.Metrics.PeakProcessFamilyMemoryBytes, Unit: "bytes", Detailed: true)
        ];
        List<CollectionIssue> issues = [];
        RetainTransport(execution, facts, issues);
        int bytes = Encoding.UTF8.GetByteCount(execution.StandardOutput);
        facts.Add(new("sourceJsonUtf8Bytes", "Tamanho UTF-8 da resposta", NumberValue: bytes, Unit: "bytes", Detailed: true));
        if (bytes > MaximumResponseBytes)
        {
            issues.Add(new(IssueCode.LimitReached, "Resposta acima de 262144 bytes; documento não interpretado nem retido como completo."));
            return [Evidence(EvidenceStatus.Unavailable, 0)];
        }
        facts.Add(new("sourceJson", "JSON original das propriedades CIM selecionadas", execution.StandardOutput, Detailed: true));
        if (!execution.ExitedSuccessfully)
        {
            if (execution.StandardOutputBytes is { } raw)
            {
                facts.Add(new("toolStdoutOriginalBytes", "Bytes de stdout retidos", NumberValue: raw.Length, Unit: "bytes", Detailed: true));
                if (raw.Length <= MaximumResponseBytes) facts.Add(new("toolStdoutBytesBase64", "Stdout original do transporte falho", Convert.ToBase64String(raw), Detailed: true));
                else issues.Add(new(IssueCode.LimitReached, "Stdout binário excede o limite; conteúdo não copiado."));
            }
            issues.Add(new(execution.Error?.Code == ErrorCode.PermissionDenied ? IssueCode.PermissionDenied : IssueCode.ReadFailed, "Transporte não confirmou a consulta de hardware; motivo primário preservado."));
            return [Evidence(EvidenceStatus.Unavailable, 0)];
        }
        if (execution.StandardError.Length > 0) issues.Add(new(IssueCode.ReadFailed, "Stderr retornado; consulta com cobertura parcial."));
        int retainedFacts = facts.Count, retainedIssues = issues.Count;
        try
        {
            using var document = JsonDocument.Parse(execution.StandardOutput, new JsonDocumentOptions { MaxDepth = 12 });
            var root = document.RootElement;
            if (HasDuplicate(root)) throw new JsonException("Propriedade duplicada.");
            Exact(root, ["kind", "version", "observedAt", "baseboards", "system", "bios", "processors", "memory"]);
            if (root.GetProperty("kind").GetString() != "optimus.hardware-profile" || !root.GetProperty("version").TryGetInt32(out int version) || version != 1)
                throw new JsonException("Identidade ou versão divergente.");
            var observed = root.GetProperty("observedAt").GetString();
            if (!TryTimestamp(observed, out captured)) throw new JsonException("Data exige fuso explícito.");
            facts.Add(new("sourceObservedAt", "Data original da consulta", observed, Detailed: true));
            int examined = 0; bool available = false;
            foreach (var query in Queries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Each class owns its schema failure and projection budget; a broken BIOS cannot discard memory facts.
                try
                {
                    var result = ParseQuery(root.GetProperty(query.Name), query, facts, issues);
                    examined += result.Examined; available |= result.Succeeded || result.Retained > 0;
                }
                catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException or OverflowException)
                { issues.Add(new(IssueCode.ReadFailed, query.Name + ": contrato inválido; fatos não confirmados: " + error.Message)); }
            }
            return [Evidence(!available ? EvidenceStatus.Unavailable : issues.Count == 0 ? EvidenceStatus.Complete : EvidenceStatus.Partial, examined)];
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException or OverflowException)
        {
            facts.RemoveRange(retainedFacts, facts.Count - retainedFacts);
            issues.RemoveRange(retainedIssues, issues.Count - retainedIssues);
            captured = DateTimeOffset.UtcNow;
            issues.Add(new(IssueCode.ReadFailed, "Envelope de hardware inválido: " + error.Message));
            return [Evidence(EvidenceStatus.Unavailable, 0)];
        }
        DiagnosticEvidence Evidence(EvidenceStatus status, int examined) => new(Guid.NewGuid(), Id, DisplayName, "Metadados CIM locais selecionados", captured, status,
            new("Cinco consultas independentes somente leitura; até 32 módulos de memória e 8 itens por outra classe, mais uma sonda por classe. Payload de 42000 bytes e projeção de 24000 bytes estimados por classe; resposta de 256 KiB. Textos de 512 caracteres, nome de CPU de 1024. Datas conservam tipo/kind/representação; sem atribuir fuso a data sem fuso. Valores zero e códigos desconhecidos permanecem reportados, sem inventar capacidade. Sem números de série, UUID ou ProcessorID; sem executar teste físico ou inferir defeito, XMP/EXPO, compatibilidade, autenticidade ou vínculo causal entre módulos e erros. Snapshot não atômico.", examined, 64, issues.Any(issue => issue.Code == IssueCode.LimitReached)), facts.ToArray(), issues.ToArray());
    }

    private static QueryResult ParseQuery(JsonElement value, Query query, List<DiagnosticFact> facts, List<CollectionIssue> issues)
    {
        Exact(value, ["succeeded", "examined", "limitReached", "records", "issues"]);
        bool succeeded = value.GetProperty("succeeded").GetBoolean(), limited = value.GetProperty("limitReached").GetBoolean();
        if (!value.GetProperty("examined").TryGetInt32(out int examined) || examined < 0 || examined > query.Maximum + 1) throw new JsonException("Contagem inválida.");
        var records = value.GetProperty("records");
        if (records.ValueKind != JsonValueKind.Array || records.GetArrayLength() > query.Maximum || records.GetArrayLength() > examined
            || examined > query.Maximum && !limited || succeeded && examined != records.GetArrayLength() && !limited) throw new JsonException("Cobertura divergente.");
        List<DiagnosticFact> selected = []; List<CollectionIssue> notes = [];
        ReadIssues(value.GetProperty("issues"), query.Name, query.Maximum * query.Fields.Length + 2, notes);
        if (!succeeded) notes.Add(new(IssueCode.ReadFailed, query.Name + ": consulta não concluída."));
        if (limited) notes.Add(new(IssueCode.LimitReached, query.Name + ": omissões declaradas pela fonte."));
        int index = 0, projected = 0; long budget = ProjectionBytesPerQuery;
        foreach (var record in records.EnumerateArray())
        {
            Exact(record, query.Fields.Select(field => field.Name).ToArray(), requireAll: false);
            List<DiagnosticFact> candidate = []; List<CollectionIssue> recordIssues = [];
            foreach (var field in query.Fields) AddField(record, field, $"{query.Name}.{index}.{field.Name}", candidate, recordIssues);
            long cost = candidate.Sum(fact => 160L + 6L * (fact.Key.Length + fact.Label.Length + (fact.TextValue?.Length ?? 0) + (fact.Unit?.Length ?? 0))) + recordIssues.Sum(issue => 128L + 6L * issue.Message.Length);
            if (cost <= budget) { budget -= cost; selected.AddRange(candidate); notes.AddRange(recordIssues); projected++; }
            index++;
        }
        if (projected < records.GetArrayLength()) notes.Add(new(IssueCode.LimitReached, query.Name + ": orçamento de projeção atingido; registros permanecem no JSON original."));
        selected.AddRange([
            new(query.Name + ".querySucceeded", query.ClassName + ": consulta concluída", succeeded ? "true" : "false"),
            new(query.Name + ".examined", query.ClassName + ": itens enumerados incluindo sonda", NumberValue: examined),
            new(query.Name + ".retained", query.ClassName + ": itens no JSON", NumberValue: records.GetArrayLength()),
            new(query.Name + ".projected", query.ClassName + ": itens apresentados", NumberValue: projected)]);
        facts.AddRange(selected); issues.AddRange(notes);
        return new(succeeded, examined, records.GetArrayLength());
    }

    private static void AddField(JsonElement record, Field field, string key, List<DiagnosticFact> facts, List<CollectionIssue> issues)
    {
        bool present = record.TryGetProperty(field.Name, out var value), valid = false;
        string? text = null; decimal? number = null;
        if (present && value.ValueKind != JsonValueKind.Null)
        {
            if (field.Type == Scalar.Text && value.ValueKind == JsonValueKind.String && value.GetString() is { } original && original.Length <= field.MaximumCharacters) { text = original; valid = true; }
            else if (field.Type == Scalar.UInt32 && value.ValueKind == JsonValueKind.Number && value.TryGetUInt32(out uint u32)) { number = u32; valid = true; }
            else if (field.Type == Scalar.UInt64 && value.ValueKind == JsonValueKind.Number && value.TryGetUInt64(out ulong u64)) { number = u64; valid = true; }
            else if (field.Type == Scalar.Date)
            {
                if (value.ValueKind == JsonValueKind.String && value.GetString() is { Length: <= 512 } originalDate) { text = originalDate; valid = true; }
                else if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("value", out var date) && date.ValueKind == JsonValueKind.String)
                {
                    Exact(value, ["sourceType", "value", "kind"]);
                    string? sourceType = value.GetProperty("sourceType").GetString(), kind = value.GetProperty("kind").GetString();
                    text = date.GetString();
                    valid = text is { Length: <= 512 } && (sourceType is "System.DateTime" or "System.DateTimeOffset") && kind is { Length: <= 32 };
                    if (valid) { facts.Add(new(key + ".sourceType", field.Name + ": tipo original", sourceType, Detailed: true)); facts.Add(new(key + ".kind", field.Name + ": Kind/fuso original", kind, Detailed: true)); }
                }
            }
        }
        if (!valid)
        {
            text = null; number = null;
            bool excessive = present && value.ValueKind == JsonValueKind.String && value.GetString()!.Length > field.MaximumCharacters;
            issues.Add(new(excessive ? IssueCode.LimitReached : IssueCode.ReadFailed, key + ": campo ausente, nulo, excessivo ou tipo não reconhecido; original no JSON, sem valor padrão."));
        }
        facts.Add(new(key, field.Name + " reportado (" + key + ")", text, number, field.Unit));
        if (number == 0 && field.Name is "Speed" or "ConfiguredClockSpeed" or "MaxClockSpeed" or "SMBIOSMemoryType")
            issues.Add(new(IssueCode.NotSupported, key + ": zero reportado não estabelece frequência/tipo conhecido."));
    }

    private static void RetainTransport(ToolRunResult execution, List<DiagnosticFact> facts, List<CollectionIssue> issues)
    {
        facts.Add(new("toolOutputCodePage", "Página de código informada pelo executor", NumberValue: execution.OutputCodePage, Detailed: true));
        facts.Add(new("toolErrorCode", "Código primário do executor", execution.Error?.Code.ToString(), Detailed: true));
        Retain("toolStderr", "Stderr original", execution.StandardError, 16_384);
        Retain("toolErrorMessage", "Motivo primário original", execution.Error?.Message, 8192);
        var pending = execution.Error?.PendingCleanupPaths;
        facts.Add(new("toolPendingCleanupCount", "Recursos com finalização pendente", NumberValue: pending?.Count, Detailed: true));
        long? characters = pending is { Count: <= 128 } ? 0 : null;
        if (characters is not null) foreach (var path in pending!) characters += path?.Length ?? 0;
        if (pending is null || pending.Count <= 128 && characters <= 8192)
            Retain("toolPendingCleanupPaths", "Lista original de recursos pendentes (JSON)", pending is null ? null : JsonSerializer.Serialize(pending), 16_384);
        else
        {
            facts.Add(new("toolPendingCleanupPaths", "Recursos pendentes não retidos por limite", Detailed: true));
            facts.Add(new("toolPendingCleanupCharacters", "Caracteres observados nos recursos pendentes", NumberValue: characters, Detailed: true));
            issues.Add(new(IssueCode.LimitReached, "A lista de recursos pendentes excedeu 128 itens ou 8192 caracteres; não foi truncada nem tratada como vazia. O executor mantém a propriedade."));
        }
        if (execution.StandardErrorBytes is { } raw)
        {
            facts.Add(new("toolStderrOriginalBytes", "Bytes de stderr retidos pelo executor", NumberValue: raw.Length, Unit: "bytes", Detailed: true));
            if (raw.Length <= 16_384) facts.Add(new("toolStderrBytesBase64", "Stderr original em base64", Convert.ToBase64String(raw), Detailed: true));
            else issues.Add(new(IssueCode.LimitReached, "Stderr binário acima de 16384 bytes; conteúdo não copiado, quantidade original retida."));
        }
        void Retain(string key, string label, string? text, int maximumBytes)
        {
            int? size = text is null ? null : Encoding.UTF8.GetByteCount(text);
            facts.Add(new(key + ".utf8Bytes", label + ": bytes UTF-8 observados", NumberValue: size, Unit: "bytes", Detailed: true));
            if (size is null || size <= maximumBytes) facts.Add(new(key, label, text, Detailed: true));
            else
            {
                facts.Add(new(key, label + " (conteúdo não retido por limite)", Detailed: true));
                issues.Add(new(IssueCode.LimitReached, key + $": conteúdo acima de {maximumBytes} bytes; nenhum prefixo foi apresentado como texto original completo."));
            }
        }
    }

    private static void ReadIssues(JsonElement array, string prefix, int maximum, List<CollectionIssue> issues)
    {
        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > maximum) throw new JsonException(prefix + ": lista de limitações fora do contrato.");
        foreach (var item in array.EnumerateArray())
        {
            Exact(item, ["code", "message"]);
            var text = item.GetProperty("message").GetString();
            if (text is null || text.Length > 2048) throw new JsonException(prefix + ": mensagem fora do orçamento.");
            var code = item.GetProperty("code").GetString() switch
            {
                "ReadFailed" => IssueCode.ReadFailed,
                "PermissionDenied" => IssueCode.PermissionDenied,
                "NotSupported" => IssueCode.NotSupported,
                "LimitReached" => IssueCode.LimitReached,
                _ => throw new JsonException(prefix + ": código de limitação não reconhecido.")
            };
            issues.Add(new(code, prefix + ": " + text));
        }
    }
    private static bool TryTimestamp(string? text, out DateTimeOffset stamp)
    {
        stamp = default;
        return text is { Length: >= 20 and <= 64 } && DateTimeOffset.TryParseExact(text,
            ["yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz", "yyyy-MM-dd'T'HH:mm:sszzz", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", "yyyy-MM-dd'T'HH:mm:ss'Z'"],
            CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out stamp);
    }
    private static void Exact(JsonElement value, string[] names, bool requireAll = true)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new JsonException("Objeto JSON esperado.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!seen.Add(property.Name) || !names.Contains(property.Name, StringComparer.Ordinal)) throw new JsonException("Propriedade duplicada ou não reconhecida: " + property.Name);
        if (requireAll && seen.Count != names.Length) throw new JsonException("Propriedade obrigatória ausente.");
    }
    private static bool HasDuplicate(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in value.EnumerateObject()) if (!names.Add(item.Name) || HasDuplicate(item.Value)) return true;
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) if (HasDuplicate(item)) return true;
        return false;
    }
    private sealed record QueryResult(bool Succeeded, int Examined, int Retained);
}
