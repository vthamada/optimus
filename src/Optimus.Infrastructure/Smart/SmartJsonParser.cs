using System.Text;
using System.Text.Json;
using Optimus.Domain;

namespace Optimus.Infrastructure.Smart;

public static class SmartJsonParser
{
    public const int MaximumJsonBytes = 1_048_576;

    public static Result<DiagnosticEvidence> Parse(string json, int? exitCode = null)
    {
        if (json is null || Encoding.UTF8.GetByteCount(json) > MaximumJsonBytes)
            return Result<DiagnosticEvidence>.Failure(ErrorCode.InvalidData, "O JSON SMART está ausente ou excede o limite de 1 MiB.");
        var facts = new List<DiagnosticFact> { new("sourceJson", "JSON original do smartctl", json, Detailed: true) };
        var issues = new List<CollectionIssue>();
        var captured = DateTimeOffset.UtcNow;
        var subject = "Dispositivo com identidade não informada";
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new JsonException("O documento smartctl deve ser um objeto JSON.");
            if (!HasUniqueProperties(root)) throw new JsonException("O documento contém propriedades duplicadas e interpretação ambígua.");
            int? reportedExit = null;
            if (Get(root, "smartctl", "exit_status") is { } reported)
            {
                if (reported.ValueKind == JsonValueKind.Number && reported.TryGetInt32(out var value) && value is >= 0 and <= 255)
                    reportedExit = value;
                else issues.Add(new(IssueCode.ReadFailed, "smartctl.exit_status não é um inteiro de 0 a 255; permanece desconhecido."));
                facts.Add(new("reportedExitStatus", "Código de saída informado no JSON", reported.GetRawText()));
            }
            if (exitCode is not null)
            {
                facts.Add(new("processExitStatus", "Código de saída do processo", NumberValue: exitCode));
                if (exitCode is < 0 or > 255) issues.Add(new(IssueCode.ReadFailed, "O código do processo está fora da máscara de 8 bits de smartctl."));
                if (reportedExit is not null && reportedExit != exitCode)
                    issues.Add(new(IssueCode.ReadFailed, "O código do processo diverge de smartctl.exit_status; ambos foram preservados."));
            }
            var status = exitCode is >= 0 and <= 255 ? exitCode : reportedExit;
            if (status is null) issues.Add(new(IssueCode.ReadFailed, "Não foi informado um código de saída SMART válido; a completude da consulta permanece desconhecida."));
            if (status is { } mask)
            {
                facts.Add(new("exitStatusMask", "Máscara de saída utilizada na avaliação da cobertura", NumberValue: mask));
                string[] descriptions = [
                    "Falha de interpretação da linha de comando (bit 0)",
                    "Falha de abertura/identificação ou saída por baixo consumo (bit 1)",
                    "Comando SMART/ATA falhou ou houve erro de checksum (bit 2)",
                    "O dispositivo reportou estado SMART de falha (bit 3)",
                    "Atributo de pré-falha atingiu o limiar atual (bit 4)",
                    "Atributo atingiu limiar em registro anterior (bit 5)",
                    "O log de erros contém registros (bit 6)",
                    "O log de autotestes contém registros de erro (bit 7)"
                ];
                for (int bit = 0; bit < 8; bit++)
                {
                    if ((mask & (1 << bit)) == 0) continue;
                    facts.Add(new($"exitBit{bit}", "Sinal informado pelo smartctl", descriptions[bit]));
                    if (bit < 3) issues.Add(new(IssueCode.ReadFailed, descriptions[bit]));
                }
            }

            int observations = 0;
            Text("deviceName", "Dispositivo informado", ["device", "name"]);
            Text("deviceProtocol", "Protocolo informado", ["device", "protocol"]);
            Text("modelName", "Modelo", ["model_name"]);
            Text("modelFamily", "Família de modelo", ["model_family"]);
            Text("serialNumber", "Número de série informado", ["serial_number"]);
            Text("firmwareVersion", "Firmware informado", ["firmware_version"]);
            Scalar("smartAvailable", "Suporte SMART disponível, segundo a fonte", ["smart_support", "available"], JsonValueKind.True, JsonValueKind.False);
            Scalar("smartEnabled", "SMART habilitado, segundo a fonte", ["smart_support", "enabled"], JsonValueKind.True, JsonValueKind.False);
            Scalar("smartPassed", "Estado SMART passed informado pela fonte", ["smart_status", "passed"], JsonValueKind.True, JsonValueKind.False);
            Number("capacityBytes", "Capacidade informada", ["user_capacity", "bytes"], "bytes");
            Number("temperatureCelsius", "Temperatura informada", ["temperature", "current"], "°C");
            Number("powerOnHours", "Horas de funcionamento informadas", ["power_on_time", "hours"], "horas");
            Number("sourceTimeUnix", "Data original informada pela fonte (Unix)", ["local_time", "time_t"], "segundos");
            Number("nvmeCriticalWarning", "NVMe critical_warning, máscara original", ["nvme_smart_health_information_log", "critical_warning"]);
            Number("nvmePercentageUsed", "NVMe percentage_used informado", ["nvme_smart_health_information_log", "percentage_used"], "%");
            Number("nvmeAvailableSpare", "NVMe available_spare informado", ["nvme_smart_health_information_log", "available_spare"], "%");
            Number("nvmeMediaErrors", "NVMe media_errors informado", ["nvme_smart_health_information_log", "media_errors"]);
            Number("nvmeErrorLogEntries", "NVMe registros de log de erro", ["nvme_smart_health_information_log", "num_err_log_entries"]);

            var deviceName = facts.FirstOrDefault(fact => fact.Key == "deviceName")?.TextValue;
            if (deviceName is not null) subject = deviceName;
            if (Get(root, "smart_support", "available") is { ValueKind: JsonValueKind.False })
                issues.Add(new(IssueCode.NotSupported, "A fonte informa que SMART não está disponível neste dispositivo/conexão."));
            if (observations == 0)
                issues.Add(new(IssueCode.ReadFailed, "O JSON não contém observações reconhecidas do dispositivo; ausência de dados não significa saúde."));
            else if (!facts.Any(fact => fact.Key is "smartPassed" or "temperatureCelsius" or "nvmeCriticalWarning" or "nvmeMediaErrors" or "nvmePercentageUsed"))
                issues.Add(new(IssueCode.ReadFailed, "A fonte contém metadados, mas não entregou indicadores SMART selecionados suficientes para esta consulta."));
            facts.Add(new("interpretationLimit", "Limite de interpretação", "Indicadores SMART não garantem vida útil restante nem comprovam sozinhos a causa de um problema."));
            facts.Add(new("freshnessLimit", "Data da evidência", "CapturedAt identifica a importação/consulta; a idade dos dados originais depende da data informada pela fonte."));
            var evidence = Evidence(observations == 0 ? EvidenceStatus.Unavailable : issues.Count > 0 ? EvidenceStatus.Partial : EvidenceStatus.Complete);
            return Result<DiagnosticEvidence>.Success(evidence);

            void Text(string key, string label, string[] path)
            {
                if (Get(root, path) is not { } value) return;
                if (value.ValueKind == JsonValueKind.String) { facts.Add(new(key, label, value.GetString())); observations++; }
                else { facts.Add(new(key, label)); issues.Add(new(IssueCode.ReadFailed, $"O campo {string.Join('.', path)} não contém texto; o valor original permanece no JSON.")); }
            }
            void Number(string key, string label, string[] path, string? unit = null)
            {
                if (Get(root, path) is not { } value) return;
                if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var numeric))
                { facts.Add(new(key, label, NumberValue: numeric, Unit: unit)); observations++; }
                else
                { facts.Add(new(key, label)); issues.Add(new(IssueCode.ReadFailed, $"O campo {string.Join('.', path)} não contém um número representável; seu valor permanece no JSON.")); }
            }
            void Scalar(string key, string label, string[] path, params JsonValueKind[] accepted)
            {
                if (Get(root, path) is not { } value) return;
                if (accepted.Contains(value.ValueKind)) { facts.Add(new(key, label, value.GetRawText())); observations++; }
                else { facts.Add(new(key, label)); issues.Add(new(IssueCode.ReadFailed, $"O campo {string.Join('.', path)} tem tipo desconhecido; o valor original permanece no JSON.")); }
            }
        }
        catch (JsonException error)
        {
            issues.Add(new(IssueCode.ReadFailed, error.Message));
            return Result<DiagnosticEvidence>.FailureWithValue(Evidence(EvidenceStatus.Unavailable), ErrorCode.InvalidData, "O JSON SMART é inválido; a fonte limitada foi preservada na evidência.");
        }

        DiagnosticEvidence Evidence(EvidenceStatus status) => new(Guid.NewGuid(), "smartctl.device", "Consulta SMART selecionada", subject,
            captured, status, new("Campos reconhecidos de um único JSON smartctl; fonte completa e campos não interpretados preservados", facts.Count), facts, issues);
    }

    private static JsonElement? Get(JsonElement root, params string[] path)
    {
        var current = root;
        foreach (var segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current)) return null;
        }
        return current;
    }

    private static bool HasUniqueProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
                if (!names.Add(property.Name) || !HasUniqueProperties(property.Value)) return false;
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray()) if (!HasUniqueProperties(child)) return false;
        return true;
    }
}
