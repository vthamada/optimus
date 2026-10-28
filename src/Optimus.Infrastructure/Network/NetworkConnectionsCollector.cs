using System.Globalization;
using System.Text;
using System.Text.Json;
using Optimus.Application;
using Optimus.Domain;

namespace Optimus.Infrastructure.Network;

/// <summary>Owns two bounded local endpoint observations; never emits traffic or controls a reported PID.</summary>
public sealed class NetworkConnectionsCollector(IWindowsSystemQueryRunner runner) : IDiagnosticCollector
{
    public const int MaximumTcpRecords = 256;
    public const int MaximumUdpRecords = 128;
    public const int MaximumResponseBytes = 262_144;
    private const int MaximumFieldCharacters = 512;
    private const long ProjectionBytesPerQuery = 64_000;
    private enum Scalar { Text, UInt16, UInt32, State }
    private sealed record Field(string Name, string Label, Scalar Type, string? Unit = null, bool Detailed = false);
    private static readonly Field[] TcpFields = [
        new("LocalAddress", "endereço local original (texto)", Scalar.Text),
        new("LocalPort", "porta local reportada", Scalar.UInt16),
        new("RemoteAddress", "endereço remoto original (texto)", Scalar.Text),
        new("RemotePort", "porta remota reportada", Scalar.UInt16),
        new("State", "estado TCP original (código)", Scalar.State),
        new("OwningProcess", "PID reportado (não comprova ownership vigente)", Scalar.UInt32)
    ];
    private static readonly Field[] UdpFields = [
        new("LocalAddress", "endereço local original (texto)", Scalar.Text),
        new("LocalPort", "porta local reportada", Scalar.UInt16),
        new("OwningProcess", "PID reportado (não comprova ownership vigente)", Scalar.UInt32)
    ];

    public string Id => MaintenanceCollectors.Connections;
    public string DisplayName => "Conexões TCP e endpoints UDP locais";

    public async Task<IReadOnlyList<DiagnosticEvidence>> CollectAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var execution = await runner.RunAsync(WindowsSystemQuery.NetworkConnections, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var captured = DateTimeOffset.UtcNow;
        var facts = new List<DiagnosticFact>
        {
            new("query", "Consultas locais selecionadas", "NetTCPIP\\Get-NetTCPConnection; NetTCPIP\\Get-NetUDPEndpoint"),
            new("networkTrafficRequested", "Pacotes, DNS, conexão ou teste remoto solicitado", "false"),
            new("processIdentityResolved", "Identidade vigente dos PIDs resolvida pelo opTImus", "false"),
            new("toolStatus", "Estado do executor", execution.Status.ToString(), Detailed: true),
            new("toolExitCode", "Código de saída do executor", NumberValue: execution.ExitCode, Detailed: true),
            new("elapsedSeconds", "Duração do Job da consulta", NumberValue: execution.Metrics.Elapsed.Ticks / (decimal)TimeSpan.TicksPerSecond, Unit: "s", Detailed: true),
            new("cpuSeconds", "CPU acumulada da família do Job", NumberValue: execution.Metrics.CpuTime is { } cpu ? cpu.Ticks / (decimal)TimeSpan.TicksPerSecond : null, Unit: "s", Detailed: true),
            new("peakProcessFamilyMemoryBytes", "Pico de commit da família do Job", NumberValue: execution.Metrics.PeakProcessFamilyMemoryBytes, Unit: "bytes", Detailed: true)
        };
        var issues = new List<CollectionIssue>();
        RetainTransport(execution, facts, issues);
        int sourceBytes = Encoding.UTF8.GetByteCount(execution.StandardOutput);
        facts.Add(new("sourceJsonUtf8Bytes", "Tamanho original da resposta JSON", NumberValue: sourceBytes, Unit: "bytes", Detailed: true));
        if (sourceBytes > MaximumResponseBytes)
        {
            issues.Add(new(IssueCode.LimitReached, "JSON acima de 262144 bytes UTF-8; não foi interpretado nem substituído por um prefixo."));
            return [Evidence(EvidenceStatus.Unavailable, 0, true)];
        }
        facts.Add(new("sourceJson", "JSON original das consultas TCP e UDP locais", execution.StandardOutput, Detailed: true));
        if (!execution.ExitedSuccessfully)
        {
            if (execution.StandardOutputBytes is { } raw)
            {
                facts.Add(new("toolStdoutOriginalBytes", "Bytes de stdout retidos pelo executor", NumberValue: raw.Length, Unit: "bytes", Detailed: true));
                if (raw.Length <= MaximumResponseBytes) facts.Add(new("toolStdoutBytesBase64", "Stdout original do transporte falho (base64)", Convert.ToBase64String(raw), Detailed: true));
                else issues.Add(new(IssueCode.LimitReached, "Stdout binário acima do orçamento; conteúdo não copiado, quantidade original retida."));
            }
            issues.Add(new(execution.Error?.Code == ErrorCode.PermissionDenied ? IssueCode.PermissionDenied : IssueCode.ReadFailed,
                "A execução não confirmou conclusão; os dados e o motivo primário retidos não foram interpretados como saúde."));
            return [Evidence(EvidenceStatus.Unavailable, 0, execution.Status == ToolRunStatus.OutputLimitExceeded)];
        }
        if (execution.StandardError.Length > 0) issues.Add(new(IssueCode.ReadFailed, "A consulta retornou stderr; o resultado foi conservado com cobertura parcial."));
        int transportFacts = facts.Count, transportIssues = issues.Count;
        try
        {
            using var document = JsonDocument.Parse(execution.StandardOutput, new JsonDocumentOptions { MaxDepth = 12 });
            var root = document.RootElement;
            if (HasDuplicate(root)) throw new JsonException("Propriedade duplicada no documento de endpoints.");
            Exact(root, ["kind", "version", "observedAt", "tcp", "udp"]);
            if (root.GetProperty("kind").GetString() != "optimus.network-connections" || !root.GetProperty("version").TryGetInt32(out int version) || version != 1)
                throw new JsonException("Identidade ou versão do inventário não reconhecida.");
            var originalTime = root.GetProperty("observedAt").GetString();
            if (!TryTimestamp(originalTime, out captured)) throw new JsonException("A data de observação requer fuso explícito.");
            facts.Add(new("sourceObservedAt", "Data original das consultas selecionadas", originalTime, Detailed: true));
            var tcp = ParseQuery(root.GetProperty("tcp"), "tcp", "TCP", TcpFields, MaximumTcpRecords, facts, issues);
            var udp = ParseQuery(root.GetProperty("udp"), "udp", "UDP", UdpFields, MaximumUdpRecords, facts, issues);
            bool available = tcp.Succeeded || udp.Succeeded || tcp.Retained > 0 || udp.Retained > 0;
            return [Evidence(!available ? EvidenceStatus.Unavailable : issues.Count > 0 ? EvidenceStatus.Partial : EvidenceStatus.Complete,
                tcp.Examined + udp.Examined, tcp.Limited || udp.Limited)];
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException or OverflowException)
        {
            facts.RemoveRange(transportFacts, facts.Count - transportFacts);
            issues.RemoveRange(transportIssues, issues.Count - transportIssues);
            captured = DateTimeOffset.UtcNow;
            issues.Add(new(IssueCode.ReadFailed, "Contrato de endpoints inválido: " + error.Message));
            return [Evidence(EvidenceStatus.Unavailable, 0, false)];
        }

        DiagnosticEvidence Evidence(EvidenceStatus status, int examined, bool limited) => new(Guid.NewGuid(), Id, DisplayName,
            "NetTCPIP local: TCP e UDP", captured, status,
            new("Consultas locais não atômicas, até 256 registros TCP e 128 endpoints UDP mais sondas, na ordem de cada fonte. Texto até 512 caracteres, JSON até 256 KiB e projeção independente até 64000 bytes estimados por consulta. Endereços mantêm texto, inclusive IPv6/scope, sem DNS ou normalização. Portas e PIDs são valores originais; PID pode ter sido reutilizado e não comprova ownership vigente. Estado TCP mantém código/enum reportados, sem avaliar ameaça, conectividade ou saúde. UDP não informa destino remoto nesta seleção. Não envia pacotes, estabelece conexões, resolve nomes, encerra processos ou altera configuração.",
                examined, MaximumTcpRecords + MaximumUdpRecords, limited || issues.Any(issue => issue.Code == IssueCode.LimitReached)), facts.ToArray(), issues.ToArray());
    }

    private static QueryResult ParseQuery(JsonElement query, string prefix, string label, Field[] fields, int maximum,
        List<DiagnosticFact> facts, List<CollectionIssue> issues)
    {
        Exact(query, ["succeeded", "examined", "limitReached", "records", "issues"]);
        bool succeeded = query.GetProperty("succeeded").GetBoolean(), limited = query.GetProperty("limitReached").GetBoolean();
        if (!query.GetProperty("examined").TryGetInt32(out int examined) || examined < 0 || examined > maximum + 1)
            throw new JsonException(prefix + ": contagem de registros fora do contrato.");
        var records = query.GetProperty("records");
        if (records.ValueKind != JsonValueKind.Array || records.GetArrayLength() > maximum || records.GetArrayLength() > examined
            || examined > maximum && !limited || succeeded && examined > records.GetArrayLength() && !limited)
            throw new JsonException(prefix + ": cobertura declarada diverge dos registros recebidos.");
        long budget = ProjectionBytesPerQuery - 4096;
        limited |= ReadIssues(query.GetProperty("issues"), prefix, maximum * fields.Length + 2, issues, ref budget);
        if (!succeeded) issues.Add(new(IssueCode.ReadFailed, prefix + ": consulta não concluída; registros anteriores permanecem no JSON original."));
        if (limited) issues.Add(new(IssueCode.LimitReached, prefix + ": a fonte declarou omissões; o catálogo não é completo."));
        int index = 0, projected = 0;
        string[] names = fields.Select(field => field.Name).ToArray();
        foreach (var record in records.EnumerateArray())
        {
            Exact(record, names, requireAll: false);
            var candidate = new List<DiagnosticFact>
            { new($"{prefix}.{index}.sourceIndex", $"{label} {index + 1} · posição original na resposta (não é identidade estável)", NumberValue: index, Detailed: true) };
            var notes = new List<CollectionIssue>();
            foreach (var field in fields) AddField(record, field, $"{prefix}.{index}.{field.Name}", $"{label} {index + 1} · {field.Label}", candidate, notes);
            long cost = candidate.Sum(fact => 160L + 6L * (fact.Key.Length + fact.Label.Length + (fact.TextValue?.Length ?? 0) + (fact.Unit?.Length ?? 0)))
                + notes.Sum(issue => 128L + 6L * issue.Message.Length);
            if (cost <= budget) { budget -= cost; facts.AddRange(candidate); issues.AddRange(notes); projected++; }
            else limited = true;
            index++;
        }
        if (projected < records.GetArrayLength()) issues.Add(new(IssueCode.LimitReached, prefix + ": projeção de 64000 bytes estimados atingida; registros recebidos continuam íntegros no JSON original."));
        facts.Add(new(prefix + ".querySucceeded", label + ": conclusão reportada da consulta", succeeded ? "true" : "false"));
        facts.Add(new(prefix + ".examined", label + ": registros enumerados (inclui eventual sonda)", NumberValue: examined));
        facts.Add(new(prefix + ".retained", label + ": registros retidos no JSON original", NumberValue: records.GetArrayLength()));
        facts.Add(new(prefix + ".projected", label + ": registros apresentados como fatos", NumberValue: projected));
        return new(succeeded, examined, records.GetArrayLength(), limited);
    }

    private static void AddField(JsonElement record, Field field, string key, string label, List<DiagnosticFact> facts, List<CollectionIssue> issues)
    {
        bool present = record.TryGetProperty(field.Name, out var value);
        bool valid = false;
        string? text = null;
        decimal? number = null;
        if (present)
        {
            switch (field.Type)
            {
                case Scalar.Text when value.ValueKind == JsonValueKind.String:
                    text = value.GetString(); valid = text is not null && text.Length <= MaximumFieldCharacters;
                    if (!valid) text = null;
                    break;
                case Scalar.UInt32 when value.ValueKind == JsonValueKind.Number && value.TryGetUInt32(out uint u32):
                    number = u32; valid = true; break;
                case Scalar.UInt16 when value.ValueKind == JsonValueKind.Number && value.TryGetUInt32(out uint port) && port <= ushort.MaxValue:
                    number = port; valid = true; break;
                case Scalar.State when value.ValueKind == JsonValueKind.Number && value.TryGetUInt32(out uint state):
                    number = state; valid = true; break;
                case Scalar.State when value.ValueKind == JsonValueKind.Object:
                    valid = AddEnum(value, key, label, facts, out number); break;
            }
        }
        facts.Add(new(key, label, text, number, field.Unit, field.Detailed));
        if (valid && field.Type == Scalar.Text && string.IsNullOrWhiteSpace(text))
            issues.Add(new(IssueCode.NotSupported, key + ": texto vazio reportado; não foi convertido em endereço de wildcard, loopback ou ausência de endpoint."));
        if (!valid)
        {
            bool excessive = present && value.ValueKind == JsonValueKind.String && value.GetString()!.Length > MaximumFieldCharacters;
            issues.Add(new(excessive ? IssueCode.LimitReached : present && value.ValueKind == JsonValueKind.Null ? IssueCode.NotSupported : IssueCode.ReadFailed,
                key + ": campo ausente, nulo, excessivo ou com tipo divergente. Nenhum zero, texto normalizado ou estado saudável foi inferido; original preservado no JSON."));
        }
    }

    private static bool AddEnum(JsonElement value, string key, string label, List<DiagnosticFact> facts, out decimal? number)
    {
        number = null;
        try
        {
            Exact(value, ["sourceType", "value", "name"]);
            var sourceType = value.GetProperty("sourceType").GetString();
            var name = value.GetProperty("name").GetString();
            if (string.IsNullOrWhiteSpace(sourceType) || sourceType.Length > MaximumFieldCharacters
                || name is null || name.Length > MaximumFieldCharacters
                || !value.GetProperty("value").TryGetUInt32(out var code)) return false;
            number = code;
            facts.Add(new(key + ".name", label + ": nome original do enum da fonte", name));
            facts.Add(new(key + ".sourceType", label + ": tipo original do enum", sourceType, Detailed: true));
            return true;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException) { return false; }
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

    private static bool ReadIssues(JsonElement array, string prefix, int maximum, List<CollectionIssue> issues, ref long budget)
    {
        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > maximum) throw new JsonException(prefix + ": lista de limitações fora do contrato.");
        int omitted = 0;
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
            var issue = new CollectionIssue(code, prefix + ": " + text);
            long cost = 128L + 6L * issue.Message.Length;
            if (cost <= budget) { budget -= cost; issues.Add(issue); }
            else omitted++;
        }
        if (omitted > 0) issues.Add(new(IssueCode.LimitReached, prefix + $": {omitted} limitações da fonte não foram projetadas por orçamento de 64000 bytes; os textos integrais continuam no JSON original."));
        return omitted > 0;
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
    private sealed record QueryResult(bool Succeeded, int Examined, int Retained, bool Limited);
}
