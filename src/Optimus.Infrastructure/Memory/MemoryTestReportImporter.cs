using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Optimus.Application;
using Optimus.Domain;
using Optimus.Infrastructure.Files;

namespace Optimus.Infrastructure.Memory;

/// <summary>Imports an explicitly selected, bounded HTML report as inert evidence. Never executes MemTest86 or report content.</summary>
public sealed class MemoryTestReportImporter : IMemoryTestReportImporter
{
    public const int MaximumReportBytes = 1_048_576;
    public const int MaximumErrorRows = 256;
    private const int MaximumElements = 20_000;
    private const int MaximumPairs = 128;
    private const int MaximumCellCharacters = 4096;
    private static readonly Regex DataError = new(@"^(?:(?<time>[^\[\r\n]{1,64}) - )?\[(?:Data Error|MEM ERROR - Data)\] Test: (?<test>[0-9]{1,3}), CPU: (?<cpu>[0-9]{1,6}), Address: (?<address>(?:0x)?[0-9A-Fa-f]{1,16})(?<suffix>/[^,]{1,128})?, Expected: (?<expected>(?:0x)?[0-9A-Fa-f]{1,16}), Actual: (?<actual>(?:0x)?[0-9A-Fa-f]{1,16})$", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, TimeSpan.FromMilliseconds(50));
    private static readonly Regex Producer = new(@"^MemTest86 V(?<version>[0-9]{1,3}\.[0-9]{1,3}(?:[A-Za-z][0-9]{1,3})?)(?:\s|$)", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, TimeSpan.FromMilliseconds(50));

    public async Task<Result<DiagnosticEvidence>> ImportAsync(string reportPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            string path = LocalPathGuard.LocalPath(reportPath);
            if (!Path.GetExtension(path).Equals(".html", StringComparison.OrdinalIgnoreCase) && !Path.GetExtension(path).Equals(".htm", StringComparison.OrdinalIgnoreCase))
                return Result<DiagnosticEvidence>.Failure(ErrorCode.InvalidConfiguration, "Selecione explicitamente um relatório HTML do MemTest86 (.html ou .htm).");
            string parent = Path.GetDirectoryName(path)!;
            using var directoryLease = LocalPathGuard.HoldDirectory(parent);
            var directoryIdentity = LocalPathGuard.Identity(directoryLease);
            _ = LocalPathGuard.LocalPath(path);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var identity = LocalPathGuard.Identity(stream.SafeFileHandle);
            if (LocalPathGuard.DirectoryIdentity(parent) != directoryIdentity) throw new InvalidDataException("A pasta selecionada mudou durante a abertura.");
            long length = stream.Length;
            if (length > MaximumReportBytes) return Result<DiagnosticEvidence>.Failure(ErrorCode.InvalidData, "Relatório acima de 1 MiB; conteúdo não alocado nem interpretado.");
            var bytes = new byte[checked((int)length)];
            await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
            if (stream.Length != length || LocalPathGuard.Identity(stream.SafeFileHandle) != identity || LocalPathGuard.DirectoryIdentity(parent) != directoryIdentity)
                throw new InvalidDataException("A identidade ou o tamanho da fonte mudou durante a leitura.");
            var evidence = await ParseBytesAsync(bytes, reportPath, cancellationToken).ConfigureAwait(false);
            evidence = evidence with { Facts = evidence.Facts.Append(new DiagnosticFact("resolvedSourcePath", "Caminho local vinculado para leitura", path, Detailed: true)).ToArray() };
            cancellationToken.ThrowIfCancellationRequested();
            return Result<DiagnosticEvidence>.Success(evidence);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return Result<DiagnosticEvidence>.Failure(error is UnauthorizedAccessException or System.Security.SecurityException ? ErrorCode.PermissionDenied : ErrorCode.InvalidData, error.Message);
        }
    }

    /// <summary>Format boundary for already supplied bytes. Source label is provenance, not a path authorization.</summary>
    public static async Task<DiagnosticEvidence> ParseBytesAsync(byte[] bytes, string sourcePath, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        cancellationToken.ThrowIfCancellationRequested();
        if (bytes.Length > MaximumReportBytes) throw new ArgumentException("Relatório acima do limite de 1 MiB.", nameof(bytes));
        if (sourcePath is null || sourcePath.Length > 32_768) throw new ArgumentException("Rótulo da fonte fora do limite.", nameof(sourcePath));
        List<DiagnosticFact> facts = [
            new("selectedSourcePath", "Arquivo selecionado pelo usuário", sourcePath),
            new("sourceSha256", "SHA-256 dos bytes originais", Convert.ToHexString(SHA256.HashData(bytes))),
            new("sourceBytes", "Tamanho original", NumberValue: bytes.Length, Unit: "bytes"),
            new("sourceBytesBase64", "Bytes originais do relatório (base64)", Convert.ToBase64String(bytes), Detailed: true),
            new("physicalTestExecuted", "Teste físico executado pelo opTImus", "false"),
            new("tests.reportedErrors", "Total de erros explicitamente informado pela fonte"),
            new("tests.errorRowsObserved", "Linhas de erro observadas na seleção do relatório"),
            new("tests.singleBitRowsObserved", "Linhas de dados com XOR de um bit na seleção")
        ];
        List<CollectionIssue> issues = [];
        int observedRows = 0, parsedRows = 0, singleBits = 0;
        bool recognized = false;
        try
        {
            string encodingName; int bom;
            Encoding encoding;
            if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE, 0, 0 }) || bytes.AsSpan().StartsWith(new byte[] { 0, 0, 0xFE, 0xFF })) throw new DecoderFallbackException("UTF-32 não é suportado.");
            if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE })) { encoding = new UnicodeEncoding(false, false, true); encodingName = "utf-16le"; bom = 2; }
            else if (bytes.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF })) { encoding = new UnicodeEncoding(true, false, true); encodingName = "utf-16be"; bom = 2; }
            else { encoding = new UTF8Encoding(false, true); encodingName = "utf-8"; bom = bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }) ? 3 : 0; }
            facts.Add(new("sourceEncoding", "Codificação selecionada por BOM explícito ou UTF-8 estrito", encodingName, Detailed: true));
            facts.Add(new("sourceBomBytes", "Bytes do BOM original", NumberValue: bom, Detailed: true));
            string html = encoding.GetString(bytes, bom, bytes.Length - bom);
            facts.Add(new("sourceHtml", "HTML original decodificado estritamente (conteúdo inerte)", html, Detailed: true));
            if (html.Contains('\0')) throw new InvalidDataException("NUL no HTML; UTF-16 sem BOM não é inferido.");
            int elements = 0;
            var parser = new HtmlParser(new HtmlParserOptions
            {
                IsScripting = false,
                IsNotSupportingFrames = true,
                MaximumDepth = 128,
                OnCreated = (_, _) => { cancellationToken.ThrowIfCancellationRequested(); if (++elements > MaximumElements) throw new ReportLimitException("HTML excedeu 20000 elementos; DOM interrompido."); }
            });
            using var document = await parser.ParseDocumentAsync(html, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            int metaIndex = 0;
            foreach (var meta in document.QuerySelectorAll("meta"))
            {
                if (metaIndex >= 16) { issues.Add(new(IssueCode.LimitReached, "Mais de 16 metadados charset; demais permanecem no HTML original.")); break; }
                string? declared = meta.GetAttribute("charset");
                if (declared is null && string.Equals(meta.GetAttribute("http-equiv"), "content-type", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var part in (meta.GetAttribute("content") ?? "").Split(';'))
                        if (part.TrimStart().StartsWith("charset=", StringComparison.OrdinalIgnoreCase)) declared = part.Trim()[8..].Trim(' ', '\'', '"');
                }
                if (declared is null) continue;
                facts.Add(new($"sourceMetaCharset.{metaIndex++}", "Declaração original de charset", declared.Length <= 512 ? declared : null, Detailed: true));
                string normalized = declared.Trim().ToLowerInvariant();
                bool matches = normalized == encodingName || encodingName == "utf-8" && normalized == "utf8" || bom == 2 && normalized == "utf-16";
                if (!matches) issues.Add(new(IssueCode.ReadFailed, "Meta charset diverge da codificação explícita; nenhum decoder alternativo ou regravação foi usado."));
            }

            // A report is recognized through actual tabular producer metadata, never filename/title/logo alone.
            var summaryRows = document.QuerySelectorAll(".summary table tr");
            int pairIndex = 0;
            List<string> generators = [], results = [], totals = [];
            foreach (var row in summaryRows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var cells = Cells(row);
                if (cells.Length != 2) continue;
                string label = Plain(cells[0]), value = Plain(cells[1]);
                if (pairIndex >= MaximumPairs) { issues.Add(new(IssueCode.LimitReached, "Mais de 128 pares de resumo; excedentes permanecem no original.")); break; }
                RetainPair("summary", pairIndex++, label, value);
                if (label == "Generated by") generators.Add(value);
                if (label == "Result") results.Add(value);
                if (label is "Total Errors" or "Total errors" or "# Errors") totals.Add(value);
            }
            for (int generatorIndex = 0; generatorIndex < generators.Count; generatorIndex++)
            {
                var generator = generators[generatorIndex];
                var producer = Producer.Match(generator);
                if (producer.Success)
                {
                    recognized = true;
                    facts.Add(new($"producerOccurrences.{generatorIndex}.source", "Produtor original do relatório", generator));
                    facts.Add(new($"producerOccurrences.{generatorIndex}.version", "Versão reportada do MemTest86", producer.Groups["version"].Value));
                    if (generators.Count == 1)
                    {
                        facts.Add(new("producer.name", "Produtor declarado", "MemTest86"));
                        facts.Add(new("producer.version", "Versão declarada", producer.Groups["version"].Value));
                    }
                }
            }
            if (!recognized)
            {
                issues.Add(new(IssueCode.NotSupported, "Formato HTML não reconhecido como relatório MemTest86; origem, bytes e HTML preservados. Resultado e contagens permanecem desconhecidos."));
                return Evidence();
            }
            if (generators.Count != 1) issues.Add(new(IssueCode.ReadFailed, "Produtor repetido/ambíguo; todas as ocorrências foram preservadas separadamente."));
            for (int i = 0; i < results.Count; i++) facts.Add(new($"reportedResult.{i}", "Resultado textual reportado (sem laudo do opTImus)", results[i]));
            facts.Add(new("tests.reportedResult", "Resultado declarado pelo produtor", results.Count == 1 ? results[0] : null));
            if (results.Count != 1 || results.Any(result => result is not ("PASS" or "FAIL" or "INCOMPLETE"))) issues.Add(new(IssueCode.ReadFailed, "Resultado reportado ausente, desconhecido ou repetido; sem escolha silenciosa de um valor."));

            int resultPair = 0, errorTableCount = 0;
            foreach (var table in document.QuerySelectorAll(".results table"))
            {
                var rows = table.QuerySelectorAll("tr").Where(row => row.Closest("table") == table).ToArray();
                if (rows.Length == 0) continue;
                var headers = Cells(rows[0]);
                bool errorTable = headers.Length == 1 && Plain(headers[0]) == "Last 10 Errors";
                if (errorTable)
                {
                    errorTableCount++;
                    foreach (var row in rows.Skip(1))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var cells = Cells(row);
                        if (cells.Length == 0) continue;
                        observedRows++;
                        if (observedRows > MaximumErrorRows) continue;
                        string key = $"tests.errorRows.{observedRows - 1}";
                        string original = string.Join(" | ", cells.Select(cell => cell.TextContent));
                        facts.Add(new(key + ".sourceText", "Linha original de erro (texto DOM)", original.Length <= MaximumCellCharacters ? original : null, Detailed: true));
                        if (cells.Length != 1 || original.Length > MaximumCellCharacters) { issues.Add(new(IssueCode.ReadFailed, key + ": estrutura/tamanho divergente; original no HTML.")); continue; }
                        string normalized = Plain(cells[0]);
                        var match = DataError.Match(normalized);
                        if (!match.Success) { issues.Add(new(IssueCode.NotSupported, key + ": erro ECC ou linha com formato não suportado; não foi tratado como ausência de erro.")); continue; }
                        ulong address = Hex(match.Groups["address"].Value), expected = Hex(match.Groups["expected"].Value), actual = Hex(match.Groups["actual"].Value), mask = expected ^ actual;
                        parsedRows++; int bits = BitOperations.PopCount(mask);
                        if (bits == 1) singleBits++;
                        facts.AddRange([
                            new(key + ".address", "Endereço reportado", NumberValue: address, Unit: "bytes"),
                            new(key + ".expected", "Valor esperado original", NumberValue: expected),
                            new(key + ".actual", "Valor obtido original", NumberValue: actual),
                            new(key + ".xor", "XOR derivado: esperado ^ obtido", NumberValue: mask),
                            new(key + ".bitsDifferent", "Bits diferentes dentro do valor", NumberValue: bits),
                            new(key + ".singleBitPosition", "Posição de bit no valor (base zero; não identifica DIMM)", NumberValue: bits == 1 ? BitOperations.TrailingZeroCount(mask) : null),
                            new(key + ".test", "Número do teste reportado", NumberValue: uint.Parse(match.Groups["test"].Value, CultureInfo.InvariantCulture), Detailed: true),
                            new(key + ".cpu", "CPU reportada na linha (sem inferência causal)", NumberValue: uint.Parse(match.Groups["cpu"].Value, CultureInfo.InvariantCulture), Detailed: true),
                            new(key + ".addressSource", "Texto original do endereço e sufixo", match.Groups["address"].Value + match.Groups["suffix"].Value, Detailed: true),
                            new(key + ".expectedSource", "Hexadecimal esperado original", match.Groups["expected"].Value, Detailed: true),
                            new(key + ".actualSource", "Hexadecimal obtido original", match.Groups["actual"].Value, Detailed: true)
                        ]);
                        if (mask == 0) issues.Add(new(IssueCode.ReadFailed, key + ": linha de erro reporta valores iguais; inconsistência preservada."));
                    }
                    continue;
                }
                // Preserve result-summary pairs in source order; do not flatten duplicate fields or sum test-table totals.
                foreach (var row in rows)
                {
                    var cells = Cells(row);
                    if (cells.Length != 2) continue;
                    string label = Plain(cells[0]), value = Plain(cells[1]);
                    if (resultPair < MaximumPairs) RetainPair("resultSummary", resultPair++, label, value);
                    else if (resultPair++ == MaximumPairs) issues.Add(new(IssueCode.LimitReached, "Mais de 128 pares de resultados; demais no original."));
                    if (label is "Total Errors" or "Total errors" or "# Errors") totals.Add(value);
                }
            }
            if (errorTableCount != 1) issues.Add(new(IssueCode.ReadFailed, "Tabela Last 10 Errors ausente ou repetida; cobertura do formato é parcial."));
            if (observedRows > MaximumErrorRows) issues.Add(new(IssueCode.LimitReached, "Mais de 256 linhas de erro; somente as primeiras 256 foram interpretadas."));
            SetNumber("tests.errorRowsObserved", observedRows);
            SetNumber("tests.singleBitRowsObserved", singleBits);
            facts.Add(new("tests.dataErrorRowsParsed", "Linhas Data Error interpretadas", NumberValue: parsedRows));
            facts.Add(new("tests.errorRowsScope", "Escopo das linhas de erro", "Last 10 Errors: seleção retida pelo produtor; não representa necessariamente todos os erros do teste."));
            for (int i = 0; i < totals.Count; i++) facts.Add(new($"tests.reportedErrorsSource.{i}", "Total de erros original", totals[i], Detailed: true));
            if (totals.Count == 1 && ulong.TryParse(totals[0], NumberStyles.None, CultureInfo.InvariantCulture, out ulong total)) SetNumber("tests.reportedErrors", total);
            else if (totals.Count > 0) issues.Add(new(IssueCode.ReadFailed, "Total de erros repetido ou inválido; não somado nem convertido em zero."));
            issues.Add(new(IssueCode.NotSupported, "Importação interpreta a seleção HTML e não verifica execução, integridade do teste, identidade do equipamento ou saúde física. PASS é apenas texto do produtor."));
        }
        catch (Exception error) when (error is DecoderFallbackException or InvalidDataException or RegexMatchTimeoutException or FormatException or OverflowException or ReportLimitException)
        { issues.Add(new(error is ReportLimitException ? IssueCode.LimitReached : IssueCode.ReadFailed, "Relatório parcialmente interpretado: " + error.Message)); }
        cancellationToken.ThrowIfCancellationRequested();
        return Evidence();

        void SetNumber(string key, decimal number) { int index = facts.FindIndex(fact => fact.Key == key); facts[index] = facts[index] with { NumberValue = number }; }
        void RetainPair(string prefix, int index, string label, string value)
        {
            facts.Add(new($"{prefix}.{index}.label", "Rótulo original da tabela", label.Length <= MaximumCellCharacters ? label : null, Detailed: true));
            facts.Add(new($"{prefix}.{index}.value", "Valor original da tabela", value.Length <= MaximumCellCharacters ? value : null, Detailed: true));
            if (label.Length > MaximumCellCharacters || value.Length > MaximumCellCharacters) issues.Add(new(IssueCode.LimitReached, "Célula acima de 4096 caracteres; projeção omitida sem truncar o original."));
        }
        DiagnosticEvidence Evidence() => new(Guid.NewGuid(), "memory.test-report", "Relatório MemTest86 importado", sourcePath, DateTimeOffset.UtcNow,
            EvidenceStatus.Partial, new("Arquivo explicitamente selecionado; até 1 MiB de bytes originais antes da alocação, UTF-8 estrito ou UTF-16 com BOM, DOM inerte de até 20000 elementos/profundidade 128. HTML5 pode corrigir marcação; nenhuma URL, script ou recurso é executado/carregado. Até 128 pares por seção, células de 4096 caracteres e 256 linhas de erro. Bytes, digest, HTML e ocorrências repetidas retidos; DOM/textos/base64 expandem a memória além do tamanho do arquivo. Last 10 Errors é seleção do produtor; total só é preenchido quando explícito e único. XOR e posição base zero se referem ao valor; não atribuem causa a DIMM, canal ou chip. Não executa teste físico, altera arquivo/configuração nem valida PASS ou autenticidade.", observedRows, MaximumErrorRows, issues.Any(issue => issue.Code == IssueCode.LimitReached)), facts.ToArray(), issues.ToArray());
    }

    private static IElement[] Cells(IElement row) => row.Children.Where(element => element.LocalName is "td" or "th").ToArray();
    private static string Plain(IElement cell) => string.Join(' ', cell.TextContent.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    private static ulong Hex(string value) => ulong.Parse(value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value[2..] : value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
    private sealed class ReportLimitException(string message) : Exception(message) { }
}
