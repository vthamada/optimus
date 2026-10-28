using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Optimus.Application;
using Optimus.Domain;
using Optimus.Infrastructure.RegistryData;

namespace Optimus.Infrastructure.Updates;

/// <summary>Reports selected registry indicators; never concludes that a reboot is universally required or unnecessary.</summary>
public sealed class PendingRestartCollector(
    Func<PendingRestartIndicator, CancellationToken, PendingRestartRegistryObservation>? source = null,
    TimeProvider? clock = null) : IDiagnosticCollector
{
    public const int MaximumValueBytes = BoundedRegistryValueReader.MaximumValueBytes;
    public const int MaximumSampleSegments = 64;
    private const long SampleProjectionBytes = 64_000;
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private static readonly UnicodeEncoding Utf16 = new(false, false, true);
    private static readonly PendingRestartIndicator[] Indicators = [
        PendingRestartIndicator.ComponentBasedServicing, PendingRestartIndicator.WindowsUpdate, PendingRestartIndicator.FileRenameOperations];

    public string Id => "windows.pending-restart";
    public string DisplayName => "Indicadores selecionados de reinicialização";

    public Task<IReadOnlyList<DiagnosticEvidence>> CollectAsync(CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<DiagnosticEvidence>>(() => Collect(cancellationToken), cancellationToken);

    private IReadOnlyList<DiagnosticEvidence> Collect(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var timer = Stopwatch.StartNew();
        var facts = new List<DiagnosticFact>
        {
            new("universalRestartAssessed", "Todas as causas de reinicialização avaliadas", "false"),
            new("wuaComRequeried", "Estado WUA COM consultado novamente por este coletor", "false"),
            new("restartRequested", "Reinicialização ou alteração do Registro solicitada", "false")
        };
        var issues = new List<CollectionIssue>();
        int knownSources = 0;
        foreach (var indicator in Indicators)
        {
            token.ThrowIfCancellationRequested();
            var location = PendingRestartRegistrySource.Describe(indicator);
            string prefix = location.Prefix;
            facts.Add(new(prefix + ".source", location.Label + " · origem selecionada", location.SourcePath));
            facts.Add(new(prefix + ".view", "Visão explícita do Registro", location.View.ToString(), Detailed: true));
            PendingRestartRegistryObservation observation;
            try
            {
                observation = (source ?? ((selected, cancellation) => PendingRestartRegistrySource.Read(selected, cancellation, _clock)))(indicator, token);
                token.ThrowIfCancellationRequested();
                if (observation is null) throw new InvalidDataException("A fonte não entregou observação do indicador.");
            }
            catch (Exception error) when (PendingRestartRegistrySource.Expected(error))
            {
                bool denied = error is UnauthorizedAccessException or System.Security.SecurityException;
                observation = new(_clock.GetUtcNow(), denied ? PendingRestartRegistryReadState.AccessDenied : PendingRestartRegistryReadState.ReadFailed,
                    null, Issue: new(denied ? IssueCode.PermissionDenied : IssueCode.ReadFailed, error.Message));
            }
            if (ValidateObservation(observation, location.ValueName is not null) is { } invalid)
            {
                RetainInvalidObservation(observation, prefix, facts, issues);
                observation = new(observation.ObservedAt, PendingRestartRegistryReadState.ReadFailed, null,
                    Issue: new(IssueCode.ReadFailed, invalid));
            }
            facts.Add(new(prefix + ".observedAt", "Data original da consulta deste indicador", observation.ObservedAt.ToString("O", CultureInfo.InvariantCulture)));
            facts.Add(new(prefix + ".readState", location.Label + " · estado da leitura", observation.State.ToString()));
            facts.Add(new(prefix + ".keyExists", location.Label + " · chave encontrada", Boolean(observation.KeyExists)));
            if (observation.KeyExists.HasValue) knownSources++;
            if (location.ValueName is not null)
            {
                facts.Add(new(prefix + ".valueExists", "Valor selecionado encontrado", Boolean(observation.ValueExists)));
                facts.Add(new(prefix + ".kind", "Tipo original REG_* da leitura", NumberValue: observation.Kind, Detailed: true));
                facts.Add(new(prefix + ".reportedBytes", "Bytes reportados na última consulta quando definidos", NumberValue: observation.ReportedBytes, Unit: "bytes", Detailed: true));
                facts.Add(new(prefix + ".probedKind", "Tipo observado na sondagem anterior à leitura", NumberValue: observation.ProbedKind, Detailed: true));
                facts.Add(new(prefix + ".probedBytes", "Bytes observados na sondagem anterior à leitura", NumberValue: observation.ProbedBytes, Unit: "bytes", Detailed: true));
                facts.Add(new(prefix + ".nativeReturnCode", "Código Win32 original da última consulta quando disponível", NumberValue: observation.NativeErrorCode, Detailed: true));
                facts.Add(new(prefix + ".readAttempted", "Leitura limitada após sondagem solicitada", observation.ReadAttempted ? "true" : "false", Detailed: true));
                facts.Add(new(prefix + ".changedDuringRead", "Mudança entre sondagem e leitura observada", observation.ChangedDuringRead ? "true" : "false"));
                AddValue(observation, prefix, facts, issues);
            }
            if (observation.Issue is { } issue) issues.Add(new(issue.Code, prefix + ": " + issue.Message));
            else if (observation.State is PendingRestartRegistryReadState.AccessDenied or PendingRestartRegistryReadState.ReadFailed
                or PendingRestartRegistryReadState.ValueTooLarge or PendingRestartRegistryReadState.ChangedDuringRead)
                issues.Add(new(observation.State == PendingRestartRegistryReadState.AccessDenied ? IssueCode.PermissionDenied
                    : observation.State == PendingRestartRegistryReadState.ValueTooLarge ? IssueCode.LimitReached : IssueCode.ReadFailed,
                    prefix + ": fonte não confirmou leitura completa; nenhum estado ausente foi presumido."));
        }
        token.ThrowIfCancellationRequested();
        facts.Add(new("knownSources", "Fontes cuja presença/ausência de chave foi observada", NumberValue: knownSources));
        facts.Add(new("elapsedSeconds", "Tempo decorrido deste coletor local", NumberValue: timer.Elapsed.Ticks / (decimal)TimeSpan.TicksPerSecond, Unit: "s", Detailed: true));
        return [new(Guid.NewGuid(), Id, DisplayName, "Três indicadores fixos do Registro HKLM, visão 64 bits", _clock.GetUtcNow(),
            knownSources == 0 ? EvidenceStatus.Unavailable : issues.Count > 0 ? EvidenceStatus.Partial : EvidenceStatus.Complete,
            new("Somente existência de duas chaves convencionais CBS/Windows Update e valor PendingFileRenameOperations em HKLM64. Ausência ou presença não decide todas as causas de reinício, saúde ou necessidade de reiniciar. Não reconsulta WUA COM. Consulta não atômica, sem alterar chaves/valores, reiniciar ou executar nomes/caminhos. Valor limitado antes de alocar a 16 KiB; bytes e delimitadores originais retidos quando leitura consistente. Texto MULTI_SZ é decodificado uma vez com UTF-16 estrito; amostra de até 64 segmentos/64000 bytes estimados preserva vazios e índices. Segmentos incluem vazios de terminadores e não são contagem de operações. Não pareia nem executa renomes/exclusões; destino vazio pode ter significado no formato MoveFileEx e não é descartado.",
                Indicators.Length, Indicators.Length, issues.Any(issue => issue.Code == IssueCode.LimitReached)), facts, issues)];
    }

    private static string? ValidateObservation(PendingRestartRegistryObservation value, bool valueSelected)
    {
        if (!Enum.IsDefined(value.State) || value.Issue is { } issue && !Enum.IsDefined(issue.Code))
            return "Estado ou código da observação fora do contrato; dados reportados preservados sem inferir presença.";
        bool hasValueData = value.ValueExists is not null || value.Kind is not null || value.ReportedBytes is not null
            || value.RawBytes is not null || value.ProbedKind is not null || value.ProbedBytes is not null
            || value.ReadAttempted || value.ChangedDuringRead;
        bool valid = value.State switch
        {
            PendingRestartRegistryReadState.KeyPresent => value.KeyExists == true && !valueSelected && !hasValueData,
            PendingRestartRegistryReadState.KeyAbsent => value.KeyExists == false && !hasValueData,
            PendingRestartRegistryReadState.ValuePresent => valueSelected && value.KeyExists == true && value.ValueExists == true
                && value.Kind is not null && value.ReportedBytes is not null && value.RawBytes is not null
                && value.RawBytes.Length <= MaximumValueBytes && value.ReportedBytes == value.RawBytes.Length && !value.ChangedDuringRead,
            PendingRestartRegistryReadState.ValueAbsent => valueSelected && value.KeyExists == true && value.ValueExists == false
                && value.Kind is null && value.ReportedBytes is null && value.RawBytes is null
                && value.ProbedKind is null && value.ProbedBytes is null && !value.ReadAttempted && !value.ChangedDuringRead,
            PendingRestartRegistryReadState.AccessDenied or PendingRestartRegistryReadState.ReadFailed => value.KeyExists != false
                && value.ValueExists is null && value.RawBytes is null && value.Kind is null && value.ReportedBytes is null
                && !value.ChangedDuringRead && (valueSelected || !hasValueData),
            PendingRestartRegistryReadState.ValueTooLarge => valueSelected && value.KeyExists == true && value.ValueExists == true
                && value.RawBytes is null && value.ReportedBytes is { } oversized && oversized > MaximumValueBytes && !value.ReadAttempted && !value.ChangedDuringRead,
            PendingRestartRegistryReadState.ChangedDuringRead => valueSelected && value.KeyExists == true
                && value.RawBytes is null && value.ChangedDuringRead && value.ReadAttempted,
            _ => false
        };
        return valid ? null : "Presença, tipo, tamanho, bytes ou etapa da leitura divergem do estado declarado; a observação incoerente não foi interpretada como chave/valor encontrado ou ausente.";
    }

    private static void RetainInvalidObservation(PendingRestartRegistryObservation value, string prefix, List<DiagnosticFact> facts, List<CollectionIssue> issues)
    {
        facts.Add(new(prefix + ".reportedState", "Estado original reportado pela fonte incoerente", value.State.ToString(), Detailed: true));
        facts.Add(new(prefix + ".reportedKeyExists", "Presença de chave original, sem validação", Boolean(value.KeyExists), Detailed: true));
        facts.Add(new(prefix + ".reportedValueExists", "Presença de valor original, sem validação", Boolean(value.ValueExists), Detailed: true));
        facts.Add(new(prefix + ".reportedKind", "Tipo original reportado pela fonte incoerente", NumberValue: value.Kind, Detailed: true));
        facts.Add(new(prefix + ".reportedByteCount", "Tamanho original reportado pela fonte incoerente", NumberValue: value.ReportedBytes, Unit: "bytes", Detailed: true));
        facts.Add(new(prefix + ".reportedProbeKind", "Tipo da sondagem reportado, sem confirmação de consistência", NumberValue: value.ProbedKind, Detailed: true));
        facts.Add(new(prefix + ".reportedProbeBytes", "Bytes da sondagem reportados, sem confirmação de consistência", NumberValue: value.ProbedBytes, Unit: "bytes", Detailed: true));
        facts.Add(new(prefix + ".reportedNativeReturnCode", "Código nativo original reportado pela fonte incoerente", NumberValue: value.NativeErrorCode, Detailed: true));
        if (value.RawBytes is { } data)
        {
            facts.Add(new(prefix + ".reportedReceivedBytes", "Tamanho dos bytes entregues pela fonte incoerente", NumberValue: data.Length, Unit: "bytes", Detailed: true));
            if (data.Length <= MaximumValueBytes) facts.Add(new(prefix + ".reportedRawBase64", "Bytes originais entregues pela fonte incoerente (não interpretados)", Convert.ToBase64String(data), Detailed: true));
            else issues.Add(new(IssueCode.LimitReached, prefix + ": fonte incoerente entregou mais de 16 KiB; conteúdo não foi copiado ou decodificado."));
        }
        if (value.Issue is { } issue)
        {
            facts.Add(new(prefix + ".reportedIssueCode", "Código de limitação original reportado, sem validação", issue.Code.ToString(), Detailed: true));
            if (issue.Message.Length <= 4096) facts.Add(new(prefix + ".reportedIssueMessage", "Limitação original reportada pela fonte incoerente", issue.Message, Detailed: true));
            else issues.Add(new(IssueCode.LimitReached, prefix + ": mensagem da fonte incoerente excedeu 4096 caracteres; não foi truncada nem retida integralmente."));
        }
    }

    private static void AddValue(PendingRestartRegistryObservation observation, string prefix, List<DiagnosticFact> facts, List<CollectionIssue> issues)
    {
        if (observation.State != PendingRestartRegistryReadState.ValuePresent) return;
        // ValidateObservation owns presence, type/size metadata and bounded raw-byte consistency.
        var data = observation.RawBytes!;
        facts.Add(new(prefix + ".rawBase64", "Bytes originais do valor (base64)", Convert.ToBase64String(data), Detailed: true));
        if (observation.Issue is not null) return;
        if (observation.Kind != 7)
        {
            facts.Add(new(prefix + ".parseState", "Estado da interpretação do valor", "UnsupportedRegistryKind"));
            issues.Add(new(IssueCode.NotSupported, prefix + ": tipo original não é REG_MULTI_SZ (7); bytes preservados sem converter para fila ou comando."));
            return;
        }
        string text;
        try
        {
            if ((data.Length & 1) != 0) throw new DecoderFallbackException("Quantidade ímpar de bytes UTF-16.");
            text = Utf16.GetString(data);
        }
        catch (DecoderFallbackException error)
        {
            facts.Add(new(prefix + ".parseState", "Estado da interpretação do valor", "MalformedUtf16"));
            issues.Add(new(IssueCode.ReadFailed, prefix + ": UTF-16 não reconhecido; bytes preservados. " + error.Message));
            return;
        }
        facts.Add(new(prefix + ".originalTextJson", "Representação JSON de todo o texto original, incluindo NULs", JsonSerializer.Serialize(text), Detailed: true));
        bool terminated = text.EndsWith("\0\0", StringComparison.Ordinal);
        facts.Add(new(prefix + ".doubleNullTerminated", "Texto termina com dois caracteres NUL", terminated ? "true" : "false"));
        if (!terminated)
        {
            facts.Add(new(prefix + ".parseState", "Estado da interpretação do valor", "MissingMultiStringTermination"));
            issues.Add(new(IssueCode.ReadFailed, prefix + ": formato selecionado exige duplo NUL final; o texto original foi preservado sem completar terminadores ou inferir operações."));
            return;
        }
        var segments = text.Split('\0', StringSplitOptions.None);
        facts.Add(new(prefix + ".parseState", "Estado da interpretação do valor", "StrictUtf16MultiString"));
        facts.Add(new(prefix + ".segmentsObserved", "Segmentos originais, incluindo vazios de terminadores", NumberValue: segments.Length));
        facts.Add(new(prefix + ".operationCount", "Quantidade de operações de renome/exclusão (não inferida)"));
        facts.Add(new(prefix + ".containsNonEmptyText", "Há texto não vazio entre os delimitadores", segments.Any(segment => segment.Length > 0) ? "true" : "false"));
        long budget = SampleProjectionBytes;
        int retained = 0;
        for (int index = 0; index < segments.Length && retained < MaximumSampleSegments; index++)
        {
            var fact = new DiagnosticFact(prefix + $".segment.{index}", $"Segmento original {index}" + (segments[index].Length == 0 ? " (vazio preservado)" : " (texto, não executado)"), segments[index]);
            long cost = 256L + 6L * (fact.Key.Length + fact.Label.Length + segments[index].Length);
            if (cost > budget) break;
            budget -= cost;
            facts.Add(fact);
            retained++;
        }
        facts.Add(new(prefix + ".segmentsRetained", "Segmentos apresentados na amostra", NumberValue: retained));
        if (retained < segments.Length) issues.Add(new(IssueCode.LimitReached, prefix + ": a amostra de segmentos atingiu 64 itens ou 64000 bytes estimados; bytes e texto originais completos permanecem retidos."));
    }

    private static string? Boolean(bool? value) => value is { } known ? known ? "true" : "false" : null;
}
