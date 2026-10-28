using Optimus.Application;
using Optimus.Domain;

namespace Optimus.Infrastructure.Events;

/// <summary>Reads previously recorded results; never launches a diagnostic or infers a result from an event ID.</summary>
public sealed class MemoryDiagnosticResultsCollector : IDiagnosticCollector
{
    public const int EventLimit = 64;
    public const int MaximumRetainedXmlBytes = 256 * 1024;
    public const int MaximumPayloadBytes = 1024 * 1024;
    public const string ReportedFailEvents = "memory.reportedFailEvents";
    public const string ReportedPassEvents = "memory.reportedPassEvents";
    public const string UnknownResultEvents = "memory.unknownResultEvents";
    public const string ProviderName = "Microsoft-Windows-MemoryDiagnostics-Results";
    private readonly int _days;
    private readonly Func<DateTimeOffset, DateTimeOffset, CancellationToken, IEnumerable<SystemEventObservation>> _source;
    private readonly TimeProvider _clock;

    public MemoryDiagnosticResultsCollector(int days = 30,
        Func<DateTimeOffset, DateTimeOffset, CancellationToken, IEnumerable<SystemEventObservation>>? source = null,
        TimeProvider? clock = null)
    {
        if (days is < 1 or > 365) throw new ArgumentOutOfRangeException(nameof(days), "A janela deve conter de 1 a 365 dias.");
        _days = days; _source = source ?? WindowsEventReader.ReadMemoryDiagnosticResults; _clock = clock ?? TimeProvider.System;
    }
    public string Id => "windows.memory-diagnostic-results";
    public string DisplayName => "Resultados registrados do Diagnóstico de Memória do Windows";
    public Task<IReadOnlyList<DiagnosticEvidence>> CollectAsync(CancellationToken cancellationToken) =>
        SelectedSystemEventCollection.CollectAsync(Id, DisplayName, _days, EventLimit, MaximumRetainedXmlBytes, MaximumPayloadBytes,
            _source, _clock, Select, Counters,
            "Provider Microsoft-Windows-MemoryDiagnostics-Results, todos os IDs. Pass/Fail são apenas valores literais de um único CompletionType estruturado no EventData ou no Results de namespace explicitamente suportado; sem decisão pelo ID (inclusive 1201), nível, mensagem localizada ou NumBadPages. Campos duplicados, truncados, formatos desconhecidos e valores diferentes são resultado desconhecido. Eventos do mesmo teste podem se repetir entre IDs: contadores são registros, não testes físicos únicos, e um Pass histórico não atesta a memória atual.", cancellationToken);

    private static SelectedEventClassification? Select(SystemEventObservation record, WindowsEventXml parsed)
    {
        if (record.Provider != ProviderName) return null;
        var values = parsed.Data.Where(field => field.Name == "CompletionType" &&
            (field.SourcePath == "EventData/Data" || field.SourcePath == WindowsEventReader.MemoryResultsPath + "/{" + WindowsEventReader.MemoryResultsNamespace + "}CompletionType")).ToArray();
        string? completion = parsed.DataComplete && values.Length == 1 && values[0].Value is "Pass" or "Fail" ? values[0].Value : null;
        return new(completion switch { "Pass" => ReportedPassEvents, "Fail" => ReportedFailEvents, _ => UnknownResultEvents }, completion ?? "unknown");
    }
    private static IEnumerable<DiagnosticFact> Counters(IReadOnlyDictionary<string, int> counts, bool known)
    {
        yield return Counter(ReportedFailEvents, "Registros com CompletionType explícito Fail");
        yield return Counter(ReportedPassEvents, "Registros com CompletionType explícito Pass");
        yield return Counter(UnknownResultEvents, "Registros com resultado desconhecido");
        DiagnosticFact Counter(string key, string label) => new(key, label, NumberValue: known ? counts.GetValueOrDefault(key) : null, Unit: "records");
    }
}
