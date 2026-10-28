using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Optimus.Domain;

namespace Optimus.Infrastructure.Events;

public sealed record WindowsEventDataValue(int Index, string? Name, string? Value)
{
    public string SourcePath { get; init; } = "EventData/Data";
}

/// <summary>Immutable facts parsed by the owner of event XML; original text remains on the observation.</summary>
public sealed class WindowsEventXml
{
    internal WindowsEventXml(SystemEventObservation source, int bytes, string? channel, string? systemTime,
        IReadOnlyList<WindowsEventDataValue> values, IReadOnlyList<CollectionIssue> issues, bool dataParsed, DateTimeOffset? occurredAt,
        bool metadataValid, int? level, bool dataComplete)
    { SourceXml = source.OriginalXml; SourceProvider = source.Provider; SourceId = source.EventId; RecordId = source.RecordId; XmlUtf8Bytes = bytes; Channel = channel; SystemTime = systemTime; Data = values; Issues = issues; DataParsed = dataParsed; OccurredAt = occurredAt; MetadataValid = metadataValid; Level = level; DataComplete = dataComplete; }
    internal string SourceXml { get; }
    internal string? SourceProvider { get; }
    internal int SourceId { get; }
    internal bool DataParsed { get; }
    public ulong? RecordId { get; }
    public DateTimeOffset? OccurredAt { get; }
    public int XmlUtf8Bytes { get; }
    public string? Channel { get; }
    public string? SystemTime { get; }
    public IReadOnlyList<WindowsEventDataValue> Data { get; }
    public IReadOnlyList<CollectionIssue> Issues { get; }
    public bool MetadataValid { get; }
    public int? Level { get; }
    public bool DataComplete { get; }
}

/// <summary>Named local EventLog queries and one format-aware XML interpretation boundary.</summary>
public static class WindowsEventReader
{
    public const int MaximumXmlBytes = 32 * 1024;
    public const int MaximumDataFields = 32;
    private const int MaximumScalarCharacters = 2048;
    private static readonly XNamespace Namespace = "http://schemas.microsoft.com/win/2004/08/events/event";
    public const string MemoryResultsNamespace = "http://manifests.microsoft.com/win/2005/08/windows/Reliability/Postboot/Events";
    public const string MemoryResultsPath = "UserData/{" + MemoryResultsNamespace + "}Results";

    public static IEnumerable<SystemEventObservation> ReadSystem(DateTimeOffset cutoff, CancellationToken token) =>
        Read("System", $"(Level=1 or Level=2 or Level=3) and TimeCreated[@SystemTime >= '{QueryTime(cutoff)}']", SystemEventsCollector.EventLimit, token);

    public static IEnumerable<SystemEventObservation> ReadApplication(DateTimeOffset cutoff, CancellationToken token) =>
        Read("Application", "((Provider[@Name='Application Error'] and EventID=1000) or (Provider[@Name='Windows Error Reporting'] and EventID=1001) or (Provider[@Name='Application Hang'] and EventID=1002))"
            + $" and TimeCreated[@SystemTime >= '{QueryTime(cutoff)}' and @SystemTime <= '{QueryTime(cutoff.AddDays(7))}']", ApplicationFailuresCollector.EventLimit, token, readData: true);

    public static IEnumerable<SystemEventObservation> ReadRestartHistory(DateTimeOffset from, DateTimeOffset to, CancellationToken token) =>
        ReadQuery("System", BuildRestartQuery(from, to), RestartHistoryCollector.EventLimit, token, readData: true);

    // Event Log limits compound XPath expressions. Separate fixed selectors use its structured Query schema.
    internal static string BuildRestartQuery(DateTimeOffset from, DateTimeOffset to)
    {
        string[] selections =
        [
            "Provider[@Name='Microsoft-Windows-Kernel-Power'] and EventID=41",
            "Provider[@Name='EventLog'] and EventID=6008",
            "Provider[@Name='User32'] and EventID=1074",
            "(Provider[@Name='Microsoft-Windows-WER-SystemErrorReporting'] or Provider[@Name='BugCheck']) and EventID=1001",
            "Provider[@Name='Microsoft-Windows-WHEA-Logger'] and (Level=2 or Level=3)"
        ];
        return new XElement("QueryList", new XElement("Query", new XAttribute("Id", "0"),
            selections.Select(selection => new XElement("Select", new XAttribute("Path", "System"), "*[System[(" + selection + ")" + Window(from, to) + "]]")))).ToString(SaveOptions.DisableFormatting);
    }

    public static IEnumerable<SystemEventObservation> ReadMemoryDiagnosticResults(DateTimeOffset from, DateTimeOffset to, CancellationToken token) =>
        Read("System", "Provider[@Name='Microsoft-Windows-MemoryDiagnostics-Results']" + Window(from, to), MemoryDiagnosticResultsCollector.EventLimit, token, readData: true);

    private static string Window(DateTimeOffset from, DateTimeOffset to) =>
        $" and TimeCreated[@SystemTime >= '{QueryTime(from)}' and @SystemTime <= '{QueryTime(to)}']";

    private static string QueryTime(DateTimeOffset value) => value.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);

    private static IEnumerable<SystemEventObservation> Read(string channel, string selection, int limit, CancellationToken token, bool readData = false) =>
        ReadQuery(channel, "*[System[" + selection + "]]", limit, token, readData);

    private static IEnumerable<SystemEventObservation> ReadQuery(string channel, string queryText, int limit, CancellationToken token, bool readData)
    {
        var timer = Stopwatch.StartNew();
        var query = new EventLogQuery(channel, PathType.LogName, queryText) { ReverseDirection = true };
        using var reader = new EventLogReader(query) { BatchSize = 1 };
        int returned = 0;
        while (true)
        {
            Check();
            // Native EvtNext distinguishes ERROR_TIMEOUT from ERROR_NO_MORE_ITEMS. A timeout throws; it is not EOF.
            using var record = reader.ReadEvent(TimeSpan.FromSeconds(2));
            Check();
            if (record is null) yield break;
            if (returned == limit)
            {
                // Only presence is needed for the probe; do not read metadata that will never be published.
                yield return new(null, 0, null, "");
                yield break;
            }
            string? provider = record.ProviderName;
            int id = record.Id;
            long? sourceRecordId = record.RecordId;
            ulong? recordId = sourceRecordId is >= 0 ? (ulong)sourceRecordId.Value : null;
            // ToXml allocates inside the BCL before its size can be checked. The cap bounds retention/parser input only.
            var observation = Interpret(new(provider, id, null, record.ToXml(), recordId), readData);
            token.ThrowIfCancellationRequested();
            yield return observation;
            returned++;
        }
        void Check()
        {
            token.ThrowIfCancellationRequested();
            if (timer.Elapsed >= TimeSpan.FromSeconds(30)) throw new TimeoutException("Prazo cooperativo de leitura de eventos atingido; cobertura restante desconhecida.");
        }
    }

    public static SystemEventObservation Interpret(SystemEventObservation source) => Interpret(source, readData: true);

    private static SystemEventObservation Interpret(SystemEventObservation source, bool readData)
    {
        if (source.Xml is { } existing && ReferenceEquals(existing.SourceXml, source.OriginalXml)
            && existing.SourceProvider == source.Provider && existing.SourceId == source.EventId && existing.RecordId == source.RecordId && existing.DataParsed == readData)
            return source with { OccurredAt = existing.OccurredAt };
        var issues = new List<CollectionIssue>();
        var data = new List<WindowsEventDataValue>();
        string? systemTime = null, channel = null;
        DateTimeOffset? occurred = null;
        bool metadataValid = false, dataComplete = true;
        int? level = null;
        ulong? recordId = source.RecordId;
        int bytes = Encoding.UTF8.GetByteCount(source.OriginalXml);
        if (bytes > MaximumXmlBytes)
        {
            dataComplete = false;
            issues.Add(new(IssueCode.LimitReached, "XML original excedeu 32 KiB UTF-8; conteúdo não retido nem interpretado, sem aceitar um prefixo truncado."));
            return Finish();
        }
        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaximumXmlBytes,
                MaxCharactersFromEntities = 0,
                ConformanceLevel = ConformanceLevel.Document
            };
            using var text = new StringReader(source.OriginalXml);
            using var reader = XmlReader.Create(text, settings);
            var document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
            if (document.Root?.Name != Namespace + "Event") throw new XmlException("Raiz/namespace de evento não reconhecida.");
            var system = Single(document.Root, "System", required: true)!;
            var provider = ScalarElement(system, "Provider", required: true)?.Attribute("Name")?.Value;
            string? id = ScalarElement(system, "EventID", required: true)?.Value;
            if (!string.Equals(provider, source.Provider, StringComparison.Ordinal)
                || !int.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out int parsedId) || parsedId > ushort.MaxValue || parsedId != source.EventId)
                issues.Add(new(IssueCode.ReadFailed, "Provider/ID do XML divergem dos metadados observados; nenhuma equivalência foi presumida."));
            channel = ScalarElement(system, "Channel", required: false)?.Value;
            if (channel is { Length: > MaximumScalarCharacters }) { channel = null; issues.Add(new(IssueCode.LimitReached, "Canal XML acima de 2048 caracteres; original permanece no XML.")); }
            systemTime = ScalarElement(system, "TimeCreated", required: true)?.Attribute("SystemTime")?.Value;
            if (TrySystemTime(systemTime, out var instant)) occurred = instant;
            else issues.Add(new(IssueCode.ReadFailed, "SystemTime ausente ou sem fuso/formato explícito suportado; nenhum horário local foi convertido ou inferido."));
            var recordText = ScalarElement(system, "EventRecordID", required: false)?.Value;
            if (recordText is not null)
            {
                if (ulong.TryParse(recordText, NumberStyles.None, CultureInfo.InvariantCulture, out ulong xmlId))
                {
                    if (recordId is { } nativeId && nativeId != xmlId) issues.Add(new(IssueCode.ReadFailed, "EventRecordID do XML diverge do identificador da API; ambos permanecem nas fontes originais."));
                    else recordId = xmlId;
                }
                else issues.Add(new(IssueCode.ReadFailed, "EventRecordID não é inteiro sem sinal suportado; original permanece no XML."));
            }
            string? levelText = ScalarElement(system, "Level", required: false)?.Value;
            if (levelText is not null)
            {
                if (byte.TryParse(levelText, NumberStyles.None, CultureInfo.InvariantCulture, out byte parsedLevel)) level = parsedLevel;
                else issues.Add(new(IssueCode.ReadFailed, "Level do XML não é um código UInt8 válido; original permanece no XML."));
            }
            metadataValid = occurred is not null && issues.Count == 0;
            var eventData = readData ? Single(document.Root, "EventData", required: false) : null;
            int index = 0;
            foreach (var value in eventData?.Elements() ?? [])
            {
                if (index == MaximumDataFields) { dataComplete = false; issues.Add(new(IssueCode.LimitReached, "Há mais de 32 campos EventData; os restantes permanecem apenas no XML original.")); break; }
                int fieldIndex = index++;
                if (value.Name != Namespace + "Data") { dataComplete = false; issues.Add(new(IssueCode.NotSupported, "Elemento EventData não é Data; representação preservada apenas no XML original.")); continue; }
                string? name = value.Attribute("Name")?.Value;
                string? scalar = value.HasElements ? null : value.Value;
                if (name is { Length: > MaximumScalarCharacters }) { name = null; issues.Add(new(IssueCode.LimitReached, "Nome de campo EventData acima de 2048 caracteres; original no XML.")); }
                if (value.HasElements) issues.Add(new(IssueCode.NotSupported, "Data contém XML estruturado; nenhum texto achatado foi apresentado como valor original."));
                else if (scalar is { Length: > MaximumScalarCharacters }) { scalar = null; issues.Add(new(IssueCode.LimitReached, "Valor EventData acima de 2048 caracteres; conteúdo não truncado, original no XML.")); }
                data.Add(new(fieldIndex, name, scalar));
                if ((name is null && value.Attribute("Name") is not null) || scalar is null) dataComplete = false;
            }
            if (readData && source.Provider == "Microsoft-Windows-MemoryDiagnostics-Results")
            {
                var userData = Single(document.Root, "UserData", required: false);
                if (userData is not null)
                {
                    XNamespace resultsNamespace = MemoryResultsNamespace;
                    var children = userData.Elements().Take(2).ToArray();
                    if (children.Length != 1 || children[0].Name != resultsNamespace + "Results")
                    { dataComplete = false; issues.Add(new(IssueCode.NotSupported, "UserData de memória não contém um único Results no namespace suportado; XML original preservado, resultado desconhecido.")); }
                    else foreach (var value in children[0].Elements())
                    {
                        if (index == MaximumDataFields) { dataComplete = false; issues.Add(new(IssueCode.LimitReached, "Mais de 32 campos estruturados; os demais permanecem apenas no XML original.")); break; }
                        int fieldIndex = index++;
                        string? scalar = value.HasElements ? null : value.Value;
                        if (value.Name.Namespace != resultsNamespace || scalar is null)
                        { dataComplete = false; issues.Add(new(IssueCode.NotSupported, "Campo Results fora do formato escalar/namespace suportado; original permanece no XML.")); continue; }
                        if (value.Name.LocalName.Length > MaximumScalarCharacters || scalar.Length > MaximumScalarCharacters)
                        { dataComplete = false; issues.Add(new(IssueCode.LimitReached, "Campo Results acima de 2048 caracteres; não truncado, original permanece no XML.")); continue; }
                        data.Add(new(fieldIndex, value.Name.LocalName, scalar) { SourcePath = MemoryResultsPath + "/{" + MemoryResultsNamespace + "}" + value.Name.LocalName });
                    }
                }
            }
        }
        catch (XmlException error) { metadataValid = false; dataComplete = false; issues.Add(new(IssueCode.ReadFailed, "XML de evento inválido ou não suportado: " + error.Message)); }
        return Finish();

        SystemEventObservation Finish() => source with
        {
            OccurredAt = occurred,
            RecordId = recordId,
            Xml = new WindowsEventXml(source with { RecordId = recordId }, bytes, channel, systemTime is { Length: <= 128 } ? systemTime : null, data.ToArray(), issues.ToArray(), readData, occurred, metadataValid, level, dataComplete)
        };
    }

    private static XElement? Single(XElement parent, string name, bool required)
    {
        var values = parent.Elements(Namespace + name).Take(2).ToArray();
        if (values.Length > 1 || required && values.Length != 1) throw new XmlException("Elemento de evento ausente ou duplicado: " + name);
        return values.FirstOrDefault();
    }
    private static XElement? ScalarElement(XElement parent, string name, bool required)
    {
        var value = Single(parent, name, required);
        if (value?.HasElements == true) throw new XmlException("Metadado de evento contém estrutura em vez de escalar: " + name);
        return value;
    }
    private static bool TrySystemTime(string? text, out DateTimeOffset value)
    {
        value = default;
        return text is { Length: >= 20 and <= 40 } && (text.EndsWith('Z') || text.Length >= 6 && text[^6] is '+' or '-' && text[^3] == ':')
            && DateTimeOffset.TryParseExact(text,
                ["yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", "yyyy-MM-dd'T'HH:mm:sszzz", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz"],
                CultureInfo.InvariantCulture, text.EndsWith('Z') ? DateTimeStyles.AssumeUniversal : DateTimeStyles.None, out value);
    }
}
