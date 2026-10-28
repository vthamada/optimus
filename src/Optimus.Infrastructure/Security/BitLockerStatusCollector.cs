using System.Globalization;
using System.Text;
using System.Text.Json;
using Optimus.Application;
using Optimus.Domain;

namespace Optimus.Infrastructure.Security;

/// <summary>Owns a fixed read-only volume/status-getter snapshot. Never queries key protectors or recovery material.</summary>
public sealed class BitLockerStatusCollector(IWindowsSystemQueryRunner runner) : IDiagnosticCollector
{
    public const int MaximumVolumes = 32;
    public const int MaximumResponseBytes = 262_144;
    public const string SourceNamespace = "root/CIMV2/Security/MicrosoftVolumeEncryption";
    private const long ProjectionBudget = 128_000;
    private static readonly string[] VolumeFields = ["DeviceID", "DriveLetter", "PersistentVolumeID"];
    private sealed record Getter(string Name, string[] Fields);
    private static readonly Getter[] Getters = [
        new("GetConversionStatus", ["ConversionStatus", "EncryptionPercentage", "EncryptionFlags"]),
        new("GetProtectionStatus", ["ProtectionStatus"]),
        new("GetEncryptionMethod", ["EncryptionMethod"]),
        new("GetLockStatus", ["LockStatus"])
    ];

    public string Id => "windows.bitlocker-status";
    public string DisplayName => "Estado BitLocker reportado pelo provedor local";

    public async Task<IReadOnlyList<DiagnosticEvidence>> CollectAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var execution = await runner.RunAsync(WindowsSystemQuery.BitLockerStatus, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var captured = DateTimeOffset.UtcNow;
        List<DiagnosticFact> facts = [
            new("sourceNamespace", "Namespace CIM local selecionado", SourceNamespace),
            new("keyMaterialRequested", "Chaves, senhas de recuperação ou identificadores de protetores solicitados", "false"),
            new("configurationChangesRequested", "Criptografia, suspensão, desbloqueio ou alteração solicitada", "false"),
            new("toolStatus", "Estado do executor", execution.Status.ToString(), Detailed: true),
            new("toolExitCode", "Código de saída do executor", NumberValue: execution.ExitCode, Detailed: true),
            new("elapsedSeconds", "Duração do Job da consulta", NumberValue: execution.Metrics.Elapsed.Ticks / (decimal)TimeSpan.TicksPerSecond, Unit: "s", Detailed: true),
            new("cpuSeconds", "CPU acumulada da família do Job", NumberValue: execution.Metrics.CpuTime is { } cpu ? cpu.Ticks / (decimal)TimeSpan.TicksPerSecond : null, Unit: "s", Detailed: true),
            new("peakProcessFamilyMemoryBytes", "Pico de commit da família do Job", NumberValue: execution.Metrics.PeakProcessFamilyMemoryBytes, Unit: "bytes", Detailed: true)
        ];
        List<CollectionIssue> issues = [];
        RetainTransport(execution, facts, issues);
        int bytes = Encoding.UTF8.GetByteCount(execution.StandardOutput);
        facts.Add(new("sourceJsonUtf8Bytes", "Tamanho UTF-8 da resposta original", NumberValue: bytes, Unit: "bytes", Detailed: true));
        if (bytes > MaximumResponseBytes)
        {
            issues.Add(new(IssueCode.LimitReached, "Resposta acima de 262144 bytes; não foi interpretada nem retida como documento completo."));
            return [Evidence(EvidenceStatus.Unavailable, 0)];
        }
        facts.Add(new("sourceJson", "JSON original dos identificadores e dos quatro getters selecionados", execution.StandardOutput, Detailed: true));
        if (!execution.ExitedSuccessfully)
        {
            if (execution.StandardOutputBytes is { } stdout)
            {
                facts.Add(new("toolStdoutOriginalBytes", "Bytes de stdout retidos pelo executor", NumberValue: stdout.Length, Unit: "bytes", Detailed: true));
                if (stdout.Length <= MaximumResponseBytes) facts.Add(new("toolStdoutBytesBase64", "Stdout original do transporte falho (base64)", Convert.ToBase64String(stdout), Detailed: true));
                else issues.Add(new(IssueCode.LimitReached, "Stdout binário acima do orçamento; conteúdo não copiado, quantidade original retida."));
            }
            issues.Add(new(execution.Error?.Code == ErrorCode.PermissionDenied ? IssueCode.PermissionDenied : IssueCode.ReadFailed,
                "O transporte não confirmou a consulta. Ausência, edição ou permissão insuficiente não foi interpretada como BitLocker desativado."));
            return [Evidence(EvidenceStatus.Unavailable, 0)];
        }
        if (execution.StandardError.Length > 0) issues.Add(new(IssueCode.ReadFailed, "Stderr foi retornado; os dados foram preservados com cobertura parcial."));
        int transportFacts = facts.Count, transportIssues = issues.Count;
        try
        {
            using var document = JsonDocument.Parse(execution.StandardOutput, new JsonDocumentOptions { MaxDepth = 12 });
            var root = document.RootElement;
            if (Duplicates(root)) throw new JsonException("Propriedade duplicada.");
            Exact(root, ["kind", "version", "observedAt", "sourceNamespace", "query"]);
            if (root.GetProperty("kind").GetString() != "optimus.bitlocker-status" || !root.GetProperty("version").TryGetInt32(out int version) || version != 1
                || root.GetProperty("sourceNamespace").GetString() != SourceNamespace) throw new JsonException("Identidade, versão ou namespace fora do catálogo.");
            var observed = root.GetProperty("observedAt").GetString();
            if (!Timestamp(observed, out captured)) throw new JsonException("Data de observação exige fuso explícito.");
            facts.Add(new("sourceObservedAt", "Data original da consulta", observed, Detailed: true));
            var query = root.GetProperty("query");
            Exact(query, ["succeeded", "examined", "limitReached", "records", "issues"]);
            bool succeeded = query.GetProperty("succeeded").GetBoolean(), limited = query.GetProperty("limitReached").GetBoolean();
            if (!query.GetProperty("examined").TryGetInt32(out int examined) || examined < 0 || examined > MaximumVolumes + 1) throw new JsonException("Contagem de volumes fora do contrato.");
            var records = query.GetProperty("records");
            if (records.ValueKind != JsonValueKind.Array || records.GetArrayLength() > MaximumVolumes || records.GetArrayLength() > examined
                || examined > MaximumVolumes && !limited || succeeded && examined > records.GetArrayLength() && !limited)
                throw new JsonException("Cobertura declarada diverge dos volumes retidos.");
            ReadIssues(query.GetProperty("issues"), issues, 8, "query");
            if (!succeeded) issues.Add(new(IssueCode.ReadFailed, "A descoberta não confirmou conclusão. Namespace ausente, edição ou acesso negado não equivalem a proteção desligada."));
            if (limited) issues.Add(new(IssueCode.LimitReached, "Parte da descoberta ou dos getters foi omitida por limite; o catálogo não é completo."));
            int index = 0, projected = 0;
            long budget = ProjectionBudget;
            foreach (var record in records.EnumerateArray())
            {
                Exact(record, ["DeviceID", "DriveLetter", "PersistentVolumeID", "getters", "issues"], requireAll: false);
                List<DiagnosticFact> candidate = [];
                List<CollectionIssue> notes = [];
                string prefix = "volumes." + index;
                foreach (var field in VolumeFields)
                {
                    if (record.TryGetProperty(field, out var value)) AddText(value, prefix + "." + field, $"Volume {index + 1} · {field} original", field != "DeviceID", candidate, notes);
                    else { candidate.Add(new(prefix + "." + field, field + " não informado")); notes.Add(new(IssueCode.ReadFailed, prefix + "." + field + ": propriedade selecionada ausente.")); }
                }
                if (record.TryGetProperty("issues", out var recordIssues)) ReadIssues(recordIssues, notes, 8, prefix);
                else notes.Add(new(IssueCode.ReadFailed, prefix + ": cobertura dos identificadores ausente."));
                if (!record.TryGetProperty("getters", out var getters)) throw new JsonException("Catálogo de getters ausente no volume.");
                Exact(getters, Getters.Select(getter => getter.Name).ToArray(), requireAll: false);
                foreach (var getter in Getters)
                {
                    if (getters.TryGetProperty(getter.Name, out var value)) ParseGetter(value, getter, prefix + "." + getter.Name, index + 1, candidate, notes);
                    else notes.Add(new(IssueCode.ReadFailed, prefix + "." + getter.Name + ": getter selecionado não foi entregue; estado permanece desconhecido."));
                }
                long cost = candidate.Sum(fact => 128L + 6L * (fact.Key.Length + fact.Label.Length + (fact.TextValue?.Length ?? 0) + (fact.Unit?.Length ?? 0)))
                    + notes.Sum(issue => 128L + 6L * issue.Message.Length);
                if (cost <= budget) { budget -= cost; facts.AddRange(candidate); issues.AddRange(notes); projected++; }
                index++;
            }
            if (projected < records.GetArrayLength()) issues.Add(new(IssueCode.LimitReached, "Projeção de 128000 bytes estimados atingida; os volumes recebidos permanecem no JSON original."));
            facts.Add(new("querySucceeded", "Descoberta local concluída segundo a fonte", succeeded ? "true" : "false"));
            facts.Add(new("examinedVolumes", "Volumes enumerados (inclui eventual sonda)", NumberValue: examined));
            facts.Add(new("retainedVolumes", "Volumes retidos no JSON original", NumberValue: records.GetArrayLength()));
            facts.Add(new("projectedVolumes", "Volumes apresentados como fatos", NumberValue: projected));
            return [Evidence(!succeeded && records.GetArrayLength() == 0 ? EvidenceStatus.Unavailable : issues.Count > 0 ? EvidenceStatus.Partial : EvidenceStatus.Complete, examined)];
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException or OverflowException)
        {
            facts.RemoveRange(transportFacts, facts.Count - transportFacts);
            issues.RemoveRange(transportIssues, issues.Count - transportIssues);
            captured = DateTimeOffset.UtcNow;
            issues.Add(new(IssueCode.ReadFailed, "Contrato BitLocker inválido: " + error.Message));
            return [Evidence(EvidenceStatus.Unavailable, 0)];
        }

        DiagnosticEvidence Evidence(EvidenceStatus status, int examined) => new(Guid.NewGuid(), Id, DisplayName, "Win32_EncryptableVolume local; quatro getters de estado", captured, status,
            new("Somente DeviceID, DriveLetter, PersistentVolumeID e resultados selecionados de GetConversionStatus/GetProtectionStatus/GetEncryptionMethod/GetLockStatus. Até 32 volumes mais sonda, campos de 2048 caracteres, resposta de 256 KiB e projeção conservadora de 128000 bytes. Getters exigem permissões administrativas e provedor/edição disponíveis; falha não significa proteção desligada. Apenas ReturnValue zero permite interpretar as saídas. GetConversionStatus usa PrecisionFactor 0; percentual 0/100 não determina conversão completa. IDs não autorizam acesso nem são associados a outras consultas. Snapshot não atômico. Não solicita chaves, senhas, IDs de protetores ou alterações/desbloqueio. Prazo cooperativo de 90 segundos entre chamadas nativas, que não são preemptíveis.",
                examined, MaximumVolumes, issues.Any(issue => issue.Code == IssueCode.LimitReached)), facts.ToArray(), issues.ToArray());
    }

    private static void ParseGetter(JsonElement value, Getter getter, string prefix, int ordinal, List<DiagnosticFact> facts, List<CollectionIssue> issues)
    {
        try
        {
            Exact(value, ["invoked", "ReturnValue", "values", "issues"]);
            bool invoked = value.GetProperty("invoked").GetBoolean();
            var returned = value.GetProperty("ReturnValue");
            uint? code = UInt32(returned);
            if (!invoked && code is not null) throw new JsonException("Método não invocado declarou ReturnValue.");
            ReadIssues(value.GetProperty("issues"), issues, 8, prefix);
            bool accepted = invoked && code == 0;
            facts.Add(new(prefix + ".invoked", $"Volume {ordinal} · {getter.Name}: resposta recebida", invoked ? "true" : "false", Detailed: true));
            facts.Add(new(prefix + ".ReturnValue", $"Volume {ordinal} · {getter.Name}: retorno original", NumberValue: code, Detailed: accepted));
            facts.Add(new(prefix + ".ReturnValueHex", getter.Name + ": ReturnValue hexadecimal original", code is { } known ? "0x" + known.ToString("X8", CultureInfo.InvariantCulture) : null, Detailed: true));
            facts.Add(new(prefix + ".outputAccepted", getter.Name + ": saída com retorno zero confirmada", accepted ? "true" : "false", Detailed: true));
            if (!accepted) issues.Add(new(IssueCode.ReadFailed, prefix + ": getter sem retorno zero confirmado; saídas não foram interpretadas como proteção, criptografia ou desbloqueio."));
            var outputs = value.GetProperty("values");
            Exact(outputs, getter.Fields, requireAll: false);
            foreach (var field in getter.Fields)
            {
                uint? number = outputs.TryGetProperty(field, out var source) ? UInt32(source) : null;
                bool percentage = field == "EncryptionPercentage";
                bool usable = accepted && number is not null && (!percentage || number <= 100);
                facts.Add(new(prefix + "." + field, $"Volume {ordinal} · {field} reportado", NumberValue: usable ? number : null, Unit: percentage ? "%" : null, Detailed: field == "EncryptionFlags"));
                if (!usable && number is not null) facts.Add(new(prefix + "." + field + ".reportedValue", field + ": saída bruta sem interpretação válida", NumberValue: number, Detailed: true));
                if (accepted && !usable) issues.Add(new(IssueCode.ReadFailed, prefix + "." + field + ": saída uint32 ausente ou percentual fora de 0..100; nenhum zero foi presumido."));
                if (usable && !percentage && field != "EncryptionFlags")
                {
                    string? name = Meaning(field, number!.Value);
                    facts.Add(new(prefix + "." + field + ".name", $"Volume {ordinal} · {field}: significado reportado", name));
                    if (name is null || field == "ProtectionStatus" && number == 2 || field == "EncryptionMethod" && number == uint.MaxValue)
                        issues.Add(new(IssueCode.NotSupported, prefix + "." + field + ": estado indeterminado ou código não mapeado; original preservado, sem conclusão de proteção."));
                }
            }
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException or OverflowException)
        {
            issues.Add(new(IssueCode.ReadFailed, prefix + ": forma de getter inválida; fonte retida sem inferir estado. " + error.Message));
        }
    }
    private static string? Meaning(string field, uint value) => field switch
    {
        "ConversionStatus" => value switch { 0 => "FullyDecrypted", 1 => "FullyEncrypted", 2 => "EncryptionInProgress", 3 => "DecryptionInProgress", 4 => "EncryptionPaused", 5 => "DecryptionPaused", _ => null },
        "ProtectionStatus" => value switch { 0 => "Unprotected", 1 => "Protected", 2 => "Unknown", _ => null },
        "EncryptionMethod" => value switch { 0 => "None", 1 => "AES_128_WITH_DIFFUSER", 2 => "AES_256_WITH_DIFFUSER", 3 => "AES_128", 4 => "AES_256", 5 => "HARDWARE_ENCRYPTION", 6 => "XTS_AES_128", 7 => "XTS_AES_256", uint.MaxValue => "UNKNOWN", _ => null },
        "LockStatus" => value switch { 0 => "Unlocked", 1 => "Locked", _ => null },
        _ => null
    };
    private static uint? UInt32(JsonElement value) => value.ValueKind == JsonValueKind.Number && value.TryGetUInt32(out uint number) ? number : null;
    private static void AddText(JsonElement value, string key, string label, bool nullable, List<DiagnosticFact> facts, List<CollectionIssue> issues)
    {
        string? text = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        bool valid = text is { Length: <= 2048 } && (nullable || !string.IsNullOrWhiteSpace(text)) || nullable && value.ValueKind == JsonValueKind.Null;
        facts.Add(new(key, label, valid ? text : null, Detailed: key.EndsWith(".PersistentVolumeID", StringComparison.Ordinal)));
        if (!valid) issues.Add(new(text?.Length > 2048 ? IssueCode.LimitReached : IssueCode.ReadFailed, key + ": texto selecionado ausente, divergente ou acima de 2048 caracteres; original no JSON."));
    }
    private static void RetainTransport(ToolRunResult execution, List<DiagnosticFact> facts, List<CollectionIssue> issues)
    {
        facts.Add(new("toolErrorCode", "Código primário do executor", execution.Error?.Code.ToString(), Detailed: true));
        facts.Add(new("toolOutputCodePage", "Página de código informada pelo executor", NumberValue: execution.OutputCodePage, Detailed: true));
        Add("toolStderr", execution.StandardError, 16_384);
        Add("toolErrorMessage", execution.Error?.Message, 8192);
        var pending = execution.Error?.PendingCleanupPaths;
        facts.Add(new("toolPendingCleanupCount", "Recursos pendentes informados pelo executor", NumberValue: pending?.Count, Detailed: true));
        if (pending is null) Add("toolPendingCleanupPaths", null, 16384);
        else if (pending.Count <= 128 && pending.Sum(path => (long)(path?.Length ?? 0)) <= 8192) Add("toolPendingCleanupPaths", JsonSerializer.Serialize(pending), 16384);
        else { Add("toolPendingCleanupPaths", null, 16384); issues.Add(new(IssueCode.LimitReached, "Lista pendente excedeu 128 recursos/8192 caracteres; executor conserva ownership, sem presumir lista vazia.")); }
        if (execution.StandardErrorBytes is { } stderr)
        {
            facts.Add(new("toolStderrOriginalBytes", "Bytes de stderr retidos pelo executor", NumberValue: stderr.Length, Unit: "bytes", Detailed: true));
            if (stderr.Length <= 16_384) facts.Add(new("toolStderrBytesBase64", "Stderr original em base64", Convert.ToBase64String(stderr), Detailed: true));
            else issues.Add(new(IssueCode.LimitReached, "Stderr binário acima de 16384 bytes; conteúdo não copiado, quantidade retida."));
        }
        void Add(string key, string? text, int maximum)
        {
            int? bytes = text is null ? null : Encoding.UTF8.GetByteCount(text);
            facts.Add(new(key + ".utf8Bytes", key + ": tamanho UTF-8 observado", NumberValue: bytes, Unit: "bytes", Detailed: true));
            facts.Add(new(key, key + ": conteúdo original", bytes is null || bytes <= maximum ? text : null, Detailed: true));
            if (bytes > maximum) issues.Add(new(IssueCode.LimitReached, key + ": conteúdo acima do limite; não foi truncado ou tratado como vazio."));
        }
    }
    private static void ReadIssues(JsonElement array, List<CollectionIssue> issues, int maximum, string scope)
    {
        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > maximum) throw new JsonException("Lista de limitações fora do contrato.");
        foreach (var item in array.EnumerateArray())
        {
            Exact(item, ["code", "message"]);
            var text = item.GetProperty("message").GetString();
            if (text is null || text.Length > 1024) throw new JsonException("Detalhe de limitação fora do orçamento.");
            var code = item.GetProperty("code").GetString() switch { "ReadFailed" => IssueCode.ReadFailed, "PermissionDenied" => IssueCode.PermissionDenied, "NotSupported" => IssueCode.NotSupported, "LimitReached" => IssueCode.LimitReached, _ => throw new JsonException("Código de limitação não reconhecido.") };
            issues.Add(new(code, scope + ": " + text));
        }
    }
    private static bool Timestamp(string? text, out DateTimeOffset value)
    {
        value = default;
        return text is { Length: >= 20 and <= 64 } && DateTimeOffset.TryParseExact(text,
            ["yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz", "yyyy-MM-dd'T'HH:mm:sszzz", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", "yyyy-MM-dd'T'HH:mm:ss'Z'"], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out value);
    }
    private static void Exact(JsonElement value, string[] names, bool requireAll = true)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new JsonException("Objeto JSON esperado.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in value.EnumerateObject()) if (!seen.Add(item.Name) || !names.Contains(item.Name, StringComparer.Ordinal)) throw new JsonException("Propriedade duplicada ou não reconhecida.");
        if (requireAll && seen.Count != names.Length) throw new JsonException("Propriedade obrigatória ausente.");
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
}
