using System.Globalization;
using System.Text;
using System.Text.Json;
using Optimus.Application;
using Optimus.Domain;

namespace Optimus.Infrastructure.Security;

/// <summary>Reads the selected Defender status contract; does not scan, update or change protection.</summary>
public sealed class DefenderCollector(IWindowsSystemQueryRunner runner) : IDiagnosticCollector
{
    public const int MaximumResponseBytes = 65_536;
    private const int MaximumFieldCharacters = 512;
    private const string Kind = "optimus.defender-status";
    private enum FieldType { Boolean, Text, Date, UnsignedInteger }
    private sealed record Field(string Name, string Label, FieldType Type);
    private static readonly Field[] Fields = [
        new("AMServiceEnabled", "Serviço antimalware habilitado, segundo a fonte", FieldType.Boolean),
        new("AntivirusEnabled", "Antivírus habilitado, segundo a fonte", FieldType.Boolean),
        new("AntispywareEnabled", "Antispyware habilitado, segundo a fonte", FieldType.Boolean),
        new("BehaviorMonitorEnabled", "Monitoramento de comportamento habilitado", FieldType.Boolean),
        new("IoavProtectionEnabled", "Proteção IOAV habilitada, segundo a fonte", FieldType.Boolean),
        new("NISEnabled", "Inspeção de rede habilitada, segundo a fonte", FieldType.Boolean),
        new("OnAccessProtectionEnabled", "Proteção ao acessar arquivos habilitada", FieldType.Boolean),
        new("RealTimeProtectionEnabled", "Proteção em tempo real habilitada", FieldType.Boolean),
        new("DefenderSignaturesOutOfDate", "Assinaturas desatualizadas, segundo a fonte", FieldType.Boolean),
        new("IsTamperProtected", "Proteção contra adulteração, segundo a fonte", FieldType.Boolean),
        new("AMEngineVersion", "Versão informada do mecanismo antimalware", FieldType.Text),
        new("AMProductVersion", "Versão informada do produto antimalware", FieldType.Text),
        new("AMRunningMode", "Modo de execução informado pelo Defender", FieldType.Text),
        new("AntivirusSignatureVersion", "Versão informada das assinaturas", FieldType.Text),
        new("AntivirusSignatureLastUpdated", "Atualização das assinaturas: data informada", FieldType.Date),
        new("ComputerState", "ComputerState: código original, sem avaliação de saúde", FieldType.UnsignedInteger)
    ];
    private static readonly HashSet<string> FieldNames = Fields.Select(field => field.Name).ToHashSet(StringComparer.Ordinal);
    private static readonly HashSet<string> NoteReasons = new(StringComparer.Ordinal)
    { "Missing", "Null", "UnsupportedType", "SizeLimit", "ReadFailed", "UnspecifiedDateTime" };

    public string Id => MaintenanceCollectors.Defender;
    public string DisplayName => "Estado informado pelo Microsoft Defender";

    public async Task<IReadOnlyList<DiagnosticEvidence>> CollectAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var execution = await runner.RunAsync(WindowsSystemQuery.DefenderStatus, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return [Interpret(execution)];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return [Evidence([], [new(error is UnauthorizedAccessException ? IssueCode.PermissionDenied
                : error is NotSupportedException ? IssueCode.NotSupported : IssueCode.ReadFailed, error.Message)], EvidenceStatus.Unavailable, DateTimeOffset.UtcNow, 0)];
        }
    }

    private DiagnosticEvidence Interpret(ToolRunResult execution)
    {
        var captured = DateTimeOffset.UtcNow;
        var facts = new List<DiagnosticFact>
        {
            new("query", "Consulta selecionada", "Defender\\Get-MpComputerStatus (local)"),
            new("processStatus", "Estado da execução da consulta", execution.Status.ToString(), Detailed: true),
            new("processExitCode", "Código de saída do processo", NumberValue: execution.ExitCode, Detailed: true)
        };
        var issues = new List<CollectionIssue>();
        long outputBytes = (long)Encoding.UTF8.GetByteCount(execution.StandardOutput) + Encoding.UTF8.GetByteCount(execution.StandardError);
        if (outputBytes > MaximumResponseBytes)
        {
            facts.Add(new("responseBytes", "Bytes UTF-8 recebidos; conteúdo acima do limite não retido", NumberValue: outputBytes, Unit: "bytes", Detailed: true));
            issues.Add(new(IssueCode.LimitReached, "A saída ultrapassou 64 KiB; nenhum estado de proteção foi interpretado."));
            return Evidence(facts, issues, EvidenceStatus.Unavailable, captured, 0);
        }
        facts.Add(new("sourceJson", "JSON original da consulta selecionada", execution.StandardOutput, Detailed: true));
        if (execution.StandardError.Length > 0) facts.Add(new("sourceError", "Saída de erro original da consulta", execution.StandardError, Detailed: true));
        if (execution.Error?.PendingCleanupPaths is { Count: > 0 } pending)
        {
            var encoded = JsonSerializer.Serialize(pending);
            if (Encoding.UTF8.GetByteCount(encoded) <= MaximumResponseBytes)
                facts.Add(new("pendingCleanup", "Recursos cuja finalização permanece pendente", encoded, Detailed: true));
            else issues.Add(new(IssueCode.LimitReached, "A lista de finalização pendente excede o orçamento de evidência; consulte o dono do executor."));
        }
        if (!execution.ExitedSuccessfully)
        {
            issues.Add(new(execution.Error?.Code == ErrorCode.PermissionDenied ? IssueCode.PermissionDenied : IssueCode.ReadFailed,
                $"Consulta não concluída: {execution.Status}. {execution.Error?.Message ?? "O resultado do comando não foi confirmado."}"));
            return Evidence(facts, issues, EvidenceStatus.Unavailable, captured, 0);
        }

        int known = 0;
        try
        {
            using var document = JsonDocument.Parse(execution.StandardOutput, new JsonDocumentOptions { MaxDepth = 12 });
            var root = document.RootElement;
            RequireProperties(root, ["kind", "version", "observedAt", "properties", "unavailable"]);
            if (!Unique(root) || root.GetProperty("kind").ValueKind != JsonValueKind.String
                || root.GetProperty("kind").GetString() != Kind || !root.GetProperty("version").TryGetInt32(out var version) || version != 1)
                throw new JsonException("O documento Defender tem identidade, versão ou propriedades ambíguas.");
            var properties = root.GetProperty("properties");
            if (properties.ValueKind != JsonValueKind.Object || properties.EnumerateObject().Any(property => !FieldNames.Contains(property.Name)))
                throw new JsonException("O documento contém propriedades Defender fora do contrato selecionado.");
            var notes = ReadNotes(root.GetProperty("unavailable"));
            var time = root.GetProperty("observedAt");
            if (time.ValueKind == JsonValueKind.String && TryDate(time.GetString(), out var originalTime))
            {
                captured = originalTime;
                facts.Add(new("sourceObservedAt", "Data original da consulta selecionada", time.GetString(), Detailed: true));
            }
            else issues.Add(new(IssueCode.ReadFailed, "Data de observação ausente ou sem fuso explícito; CapturedAt identifica o recebimento desta resposta."));

            foreach (var field in Fields)
            {
                bool valid = false;
                try { valid = !notes.ContainsKey(field.Name) && properties.TryGetProperty(field.Name, out var value) && AddField(field, value, facts); }
                catch (InvalidOperationException) { issues.Add(new(IssueCode.ReadFailed, $"{field.Name}: o texto JSON não pôde ser decodificado; a fonte original foi preservada.")); }
                if (valid) known++;
                if (!valid || notes.TryGetValue(field.Name, out _))
                {
                    notes.TryGetValue(field.Name, out var note);
                    issues.Add(new(note == "SizeLimit" ? IssueCode.LimitReached : note is "Missing" or "UnspecifiedDateTime" ? IssueCode.NotSupported : IssueCode.ReadFailed,
                        $"{field.Name}: {note ?? "valor ausente, nulo ou de tipo/formato não reconhecido"}; nenhum estado foi presumido. O JSON selecionado foi preservado."));
                }
                if (!valid) facts.Add(new(field.Name, field.Label));
            }
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException)
        {
            // The envelope must be valid before any fields can be trusted as the selected query.
            issues.Add(new(IssueCode.ReadFailed, $"Contrato Defender inválido: {error.Message}"));
        }
        return Evidence(facts, issues, known == 0 ? EvidenceStatus.Unavailable : issues.Count > 0 ? EvidenceStatus.Partial : EvidenceStatus.Complete,
            captured, known);
    }

    private static bool AddField(Field field, JsonElement value, List<DiagnosticFact> facts)
    {
        switch (field.Type)
        {
            case FieldType.Boolean when value.ValueKind is JsonValueKind.True or JsonValueKind.False:
                facts.Add(new(field.Name, field.Label, value.GetRawText())); return true;
            case FieldType.UnsignedInteger when value.ValueKind == JsonValueKind.Number && value.TryGetUInt32(out var number):
                facts.Add(new(field.Name, field.Label, NumberValue: number)); return true;
            case FieldType.Text when value.ValueKind == JsonValueKind.String:
                var text = value.GetString();
                if (string.IsNullOrWhiteSpace(text) || text.Length > MaximumFieldCharacters) return false;
                facts.Add(new(field.Name, field.Label, text)); return true;
            case FieldType.Date when value.ValueKind == JsonValueKind.String:
                var date = value.GetString();
                if (date is null || date.Length > MaximumFieldCharacters || !TryDate(date, out _)) return false;
                facts.Add(new(field.Name, field.Label, date)); return true;
            default: return false;
        }
    }

    private static bool TryDate(string? text, out DateTimeOffset value)
    {
        value = default;
        return text is { Length: >= 20 and <= MaximumFieldCharacters } && text.Contains('T')
            && (text.EndsWith('Z') || text.Length >= 6 && text[^6] is '+' or '-' && text[^3] == ':')
            && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out value);
    }

    private static Dictionary<string, string> ReadNotes(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() > Fields.Length)
            throw new JsonException("Lista de campos indisponíveis fora do contrato.");
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var note in element.EnumerateArray())
        {
            RequireProperties(note, ["property", "reason", "sourceType", "reportedCharacters"]);
            var name = note.GetProperty("property"); var reason = note.GetProperty("reason"); var type = note.GetProperty("sourceType"); var characters = note.GetProperty("reportedCharacters");
            if (name.ValueKind != JsonValueKind.String || name.GetString() is not { } nameText || !FieldNames.Contains(nameText)
                || reason.ValueKind != JsonValueKind.String || reason.GetString() is not { } reasonText || !NoteReasons.Contains(reasonText)
                || !(type.ValueKind == JsonValueKind.Null || type.ValueKind == JsonValueKind.String && type.GetString()!.Length <= MaximumFieldCharacters)
                || !(characters.ValueKind == JsonValueKind.Null || characters.ValueKind == JsonValueKind.Number && characters.TryGetUInt32(out _))
                || !result.TryAdd(nameText, reasonText)) throw new JsonException("Nota de campo indisponível inválida ou duplicada.");
        }
        return result;
    }

    private static void RequireProperties(JsonElement element, string[] allowed)
    {
        if (element.ValueKind != JsonValueKind.Object || element.EnumerateObject().Count() != allowed.Length
            || element.EnumerateObject().Any(property => !allowed.Contains(property.Name, StringComparer.Ordinal)))
            throw new JsonException("Estrutura JSON ausente ou com membros fora do contrato Defender.");
    }

    private static bool Unique(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
                if (!names.Add(property.Name) || !Unique(property.Value)) return false;
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) if (!Unique(item)) return false;
        return true;
    }

    private DiagnosticEvidence Evidence(IReadOnlyList<DiagnosticFact> facts, IReadOnlyList<CollectionIssue> issues,
        EvidenceStatus status, DateTimeOffset captured, int known) => new(Guid.NewGuid(), Id, DisplayName, "Computador local", captured, status,
            new($"Snapshot de 16 propriedades selecionadas de Get-MpComputerStatus; {known} valor(es) reconhecido(s). Limites de 64 KiB de resposta e 512 caracteres por campo textual. Não consulta ameaças, não executa varredura e não altera proteções. Estados reportados não comprovam ausência de malware ou proteção completa", known, Fields.Length,
                issues.Any(issue => issue.Code == IssueCode.LimitReached)), facts, issues);
}
