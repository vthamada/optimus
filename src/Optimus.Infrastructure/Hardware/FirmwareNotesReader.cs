using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Optimus.Application;
using Optimus.Domain;

namespace Optimus.Infrastructure.Hardware;

/// <summary>One explicit GET to one exact catalogued host. HTML is parsed without loaders, scripts, cookies or credentials.</summary>
public sealed class FirmwareNotesReader : IFirmwareNotesReader, IDisposable
{
    public const int MaximumResponseBytes = 524_288;
    public const int MaximumParagraphs = 512;
    public const int MaximumSnippets = 16;
    private static readonly HttpClient DefaultClient = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        Credentials = null,
        DefaultProxyCredentials = null,
        PreAuthenticate = false,
        AutomaticDecompression = DecompressionMethods.None,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        MaxResponseHeadersLength = 16,
        MaxResponseDrainSize = 0
    })
    { Timeout = Timeout.InfiniteTimeSpan };
    private readonly HttpClient _client;
    private readonly bool _ownsClient;
    private static readonly Regex MemoryTerms = Terms(@"\b(?:mem[oó]rias?|memory|ram|dram|ddr[0-9]?|xmp|expo)\b");
    private static readonly Regex StabilityTerms = Terms(@"\b(?:estabilidade|stability|crash(?:es)?|freez(?:e|es|ing)|hang(?:s)?|travamentos?|congelamentos?)\b");
    private static readonly Regex SecurityTerms = Terms(@"\b(?:tpm|ftpm|ptt|secure\s+boot|inicializa[cç][aã]o\s+segura|m[oó]dulo\s+de\s+seguran[cç]a|security\s+module)\b");

    public FirmwareNotesReader() { _client = DefaultClient; }
    /// <summary>Trusted transport injection for tests; this instance owns and disposes the supplied handler.</summary>
    public FirmwareNotesReader(HttpMessageHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _client = new(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        _ownsClient = true;
    }
    public void Dispose() { if (_ownsClient) _client.Dispose(); }

    public async Task<Result<DiagnosticEvidence>> ReadAsync(string url, string manufacturer, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (url is not { Length: > 0 and <= 4096 } || manufacturer is not { Length: > 0 and <= 512 }
            || FirmwareSupportCatalog.OfficialUrl(manufacturer) is not { } officialText
            || !Uri.TryCreate(url, UriKind.Absolute, out var selected) || !Uri.TryCreate(officialText, UriKind.Absolute, out var official)
            || !Allowed(selected, official))
            return Result<DiagnosticEvidence>.Failure(ErrorCode.InvalidConfiguration, "Escolha explicitamente uma URL HTTPS na porta 443, sem usuário/senha, no host exato do suporte oficial do fabricante conhecido.");

        var requested = new UriBuilder(selected) { Fragment = "" }.Uri;
        List<DiagnosticFact> facts = [
            new("selectedUrl", "URL escolhida explicitamente", url), new("requestedUrl", "URL enviada no GET (sem fragmento)", requested.AbsoluteUri),
            new("manufacturer", "Fabricante usado localmente para validar o host", manufacturer),
            new("officialHost", "Host exato autorizado pelo catálogo", official.IdnHost),
            new("requestMethod", "Método solicitado", "GET"), new("automaticRedirects", "Redirecionamentos automáticos", "false"),
            new("cookiesOrCredentialsRequested", "Cookies ou credenciais configurados para envio", "false"),
            new("notes.memoryMentions", "Parágrafos selecionados com termos de memória"),
            new("notes.stabilityMentions", "Parágrafos selecionados com termos de estabilidade"),
            new("notes.securityModuleMentions", "Parágrafos selecionados com termos de módulos de segurança"),
            new("latestVersionConfirmed", "Firmware mais recente confirmado", "false"),
            new("imageCompatibilityConfirmed", "Imagem de firmware compatível confirmada", "false"),
            new("firmwareDownloadOrFlashRequested", "Download de imagem ou gravação de firmware solicitado", "false")
        ];
        List<CollectionIssue> issues = [];
        byte[]? buffer = null; int observedBytes = 0, status = 0, paragraphs = 0;
        bool bodyComplete = false, limited = false;
        string reason = "Unknown", mediaType = "", charset = "";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, requested);
            request.Headers.Accept.ParseAdd("text/html, application/xhtml+xml;q=0.9, text/plain;q=0.8");
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            status = (int)response.StatusCode;
            facts.Add(new("httpStatus", "Status HTTP reportado", NumberValue: status));
            facts.Add(new("responseUrl", "URL associada à resposta", response.RequestMessage?.RequestUri?.AbsoluteUri, Detailed: true));
            if (response.RequestMessage?.RequestUri is { } responseUri && (!Allowed(responseUri, official) || responseUri != requested))
                return Failed("UnexpectedResponseUrl", ErrorCode.InvalidResponse, "A resposta declarou URL divergente; conteúdo não foi interpretado.");
            if (status is >= 300 and <= 399)
            {
                string? location = response.Headers.Location?.OriginalString;
                facts.Add(new("redirectLocation", "Destino reportado, não seguido", location is { Length: <= 4096 } ? location : null, Detailed: true));
                return Failed("RedirectNotFollowed", ErrorCode.InvalidResponse, "A página respondeu com redirecionamento; nenhum outro endereço foi consultado.");
            }
            var contentType = response.Content.Headers.ContentType;
            mediaType = contentType?.MediaType?.ToLowerInvariant() ?? "";
            charset = contentType?.CharSet ?? "";
            facts.Add(new("contentType", "Tipo de conteúdo declarado", contentType?.ToString() is { Length: <= 1024 } type ? type : null, Detailed: true));
            facts.Add(new("contentLength", "Tamanho declarado pelo servidor", NumberValue: response.Content.Headers.ContentLength, Unit: "bytes", Detailed: true));
            facts.Add(new("declaredCharset", "Charset HTTP original", charset.Length <= 128 ? charset : null, Detailed: true));
            if (mediaType is not ("text/html" or "application/xhtml+xml" or "text/plain"))
                return Failed("UnsupportedContentType", ErrorCode.InvalidResponse, "Somente HTML/XHTML ou texto simples são aceitos; imagens de firmware, arquivos e conteúdo desconhecido não foram lidos.");
            if (response.Content.Headers.ContentEncoding.Any(value => !value.Equals("identity", StringComparison.OrdinalIgnoreCase)))
                return Failed("UnsupportedContentEncoding", ErrorCode.InvalidResponse, "Content-Encoding não suportado; nenhuma descompressão ou interpretação automática foi realizada.");
            if (response.Content.Headers.ContentLength is > MaximumResponseBytes)
            { limited = true; return Failed("DeclaredSizeLimit", ErrorCode.OutputLimitExceeded, "Resposta declarada acima de 512 KiB; corpo não alocado nem lido."); }
            buffer = new byte[MaximumResponseBytes + 1]; // One bounded extra byte probes unknown or inaccurate Content-Length.
            using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            while (observedBytes < buffer.Length)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(observedBytes, buffer.Length - observedBytes), timeout.Token).ConfigureAwait(false);
                if (read == 0) { bodyComplete = true; break; }
                observedBytes += read;
            }
            if (observedBytes > MaximumResponseBytes)
            { limited = true; return Failed("BodySizeLimit", ErrorCode.OutputLimitExceeded, "Resposta ultrapassou 512 KiB; somente o prefixo limitado foi retido, sem interpretação como página completa."); }
            if (response.Content.Headers.ContentLength is { } length && length != observedBytes)
            { bodyComplete = false; return Failed("LengthMismatch", ErrorCode.InvalidResponse, "Tamanho recebido diverge do Content-Length; bytes retidos sem interpretação como documento íntegro."); }
            if (!response.IsSuccessStatusCode)
                return Failed("HttpError", ErrorCode.InvalidResponse, "O servidor retornou erro HTTP " + status.ToString(CultureInfo.InvariantCulture) + "; conteúdo retido, sem inferir existência de notas.");
            string text = Decode(buffer.AsSpan(0, observedBytes), charset, out string encoding, out int bom);
            facts.Add(new("sourceEncoding", "Codificação determinada por charset/BOM explícito", encoding, Detailed: true));
            facts.Add(new("sourceBomBytes", "Comprimento do BOM original", NumberValue: bom, Detailed: true));
            facts.Add(new(mediaType == "text/plain" ? "sourceText" : "sourceHtml", "Conteúdo original decodificado (inerte)", text, Detailed: true));
            if (text.Contains('\0')) throw new InvalidDataException("Texto contém NUL; codificação não será inferida por padrão de bytes.");
            int memory = 0, stability = 0, security = 0, snippets = 0, matching = 0;
            if (mediaType == "text/plain")
            {
                foreach (string paragraph in TextParagraphs(text)) if (!Inspect(paragraph)) break;
            }
            else
            {
                int elements = 0;
                var parser = new HtmlParser(new HtmlParserOptions
                {
                    IsScripting = false,
                    IsNotSupportingFrames = true,
                    MaximumDepth = 128,
                    OnCreated = (_, _) => { timeout.Token.ThrowIfCancellationRequested(); if (++elements > 10_000) throw new NotesLimitException("HTML excedeu 10000 elementos; DOM interrompido."); }
                });
                using var document = await parser.ParseDocumentAsync(text, timeout.Token).ConfigureAwait(false);
                int metaCount = 0;
                foreach (var meta in document.QuerySelectorAll("meta[charset]"))
                {
                    if (++metaCount > 16) { limited = true; issues.Add(new(IssueCode.LimitReached, "Mais de 16 declarações meta charset; demais permanecem no HTML original.")); break; }
                    if (meta.GetAttribute("charset") is { } declared && !EquivalentCharset(declared, encoding))
                        issues.Add(new(IssueCode.ReadFailed, "Meta charset diverge do charset/BOM HTTP; texto original preservado sem trocar o decoder."));
                }
                foreach (var paragraph in document.QuerySelectorAll("p,li"))
                {
                    if (paragraph.QuerySelector("p,li") is not null || paragraph.Closest("script,style,template,noscript") is not null) continue;
                    if (!Inspect(paragraph.TextContent)) break;
                }
            }
            if (paragraphs == 0) return Failed("UnknownPageStructure", ErrorCode.InvalidResponse, "Nenhum parágrafo textual selecionável; página desconhecida não foi declarada como notas de firmware.");
            SetNumber("notes.memoryMentions", memory); SetNumber("notes.stabilityMentions", stability); SetNumber("notes.securityModuleMentions", security);
            facts.Add(new("notes.paragraphsExamined", "Parágrafos selecionados examinados (sem sonda)", NumberValue: Math.Min(paragraphs, MaximumParagraphs)));
            facts.Add(new("notes.matchingParagraphs", "Parágrafos selecionados com algum termo", NumberValue: matching));
            facts.Add(new("notes.snippetsRetained", "Trechos retidos", NumberValue: snippets));
            facts.Add(new("notes.snippetsOmitted", "Trechos correspondentes omitidos do resumo", NumberValue: matching - snippets));
            if (matching > snippets) { limited = true; issues.Add(new(IssueCode.LimitReached, "Mais de 16 parágrafos corresponderam aos termos; demais trechos permanecem no corpo original.")); }
            issues.Add(new(IssueCode.NotSupported, "Contagem lexical PT/EN é heurística; menções, ausência de menções e texto da página não comprovam que sejam notas, versão mais recente, imagem compatível ou configuração/defeito do equipamento."));
            reason = limited ? "SelectionLimit" : "HeuristicTextSelection";
            return Finish(null);

            bool Inspect(string original)
            {
                timeout.Token.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(original)) return true;
                paragraphs++;
                if (paragraphs > MaximumParagraphs)
                { limited = true; issues.Add(new(IssueCode.LimitReached, "Mais de 512 parágrafos selecionados; somente os primeiros 512 foram examinados lexicalmente.")); return false; }
                bool hasMemory = MemoryTerms.IsMatch(original), hasStability = StabilityTerms.IsMatch(original), hasSecurity = SecurityTerms.IsMatch(original);
                if (hasMemory) memory++; if (hasStability) stability++; if (hasSecurity) security++;
                if (!hasMemory && !hasStability && !hasSecurity) return true;
                matching++;
                if (snippets >= MaximumSnippets) return true;
                if (original.Length > 1000 && !limited) { limited = true; issues.Add(new(IssueCode.LimitReached, "Trecho com mais de 1000 caracteres: somente prefixo identificado foi projetado; parágrafo completo permanece no corpo original.")); }
                string key = "snippets." + snippets++;
                facts.Add(new(key + ".text", "Trecho textual selecionado do parágrafo (prefixo DOM)", original.Length <= 1000 ? original : original[..1000]));
                facts.Add(new(key + ".paragraphIndex", "Posição no conjunto de parágrafos selecionados (base zero)", NumberValue: paragraphs - 1, Detailed: true));
                facts.Add(new(key + ".originalCharacters", "Comprimento do parágrafo DOM", NumberValue: original.Length, Detailed: true));
                facts.Add(new(key + ".prefixOnly", "Trecho limitado aos primeiros 1000 caracteres", original.Length > 1000 ? "true" : "false", Detailed: true));
                return true;
            }
        }
        catch (OperationCanceledException)
        { return Failed(cancellationToken.IsCancellationRequested ? "Cancelled" : "Timeout", cancellationToken.IsCancellationRequested ? ErrorCode.Cancelled : ErrorCode.Timeout, cancellationToken.IsCancellationRequested ? "Leitura cancelada; bytes recebidos permanecem como prefixo parcial." : "Prazo total de 20 segundos atingido; bytes recebidos permanecem como prefixo parcial."); }
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException or ArgumentException or RegexMatchTimeoutException or NotesLimitException)
        {
            if (error is NotesLimitException) limited = true;
            string detail = error.Message.Length <= 2048 ? error.Message : "Mensagem de erro acima de 2048 caracteres; conteúdo integral omitido.";
            return Failed(error is NotesLimitException ? "DomLimit" : error is DecoderFallbackException or InvalidDataException or ArgumentException ? "UnknownOrInvalidEncoding" : "ReadFailed",
                error is NotesLimitException ? ErrorCode.OutputLimitExceeded : ErrorCode.InvalidResponse, detail);
        }

        Result<DiagnosticEvidence> Failed(string code, ErrorCode error, string message)
        {
            reason = code;
            issues.Add(new(error == ErrorCode.OutputLimitExceeded ? IssueCode.LimitReached : IssueCode.ReadFailed, message));
            return Finish(new(error, message));
        }
        Result<DiagnosticEvidence> Finish(AppError? error)
        {
            int retained = Math.Min(observedBytes, MaximumResponseBytes);
            facts.Add(new("partialReason", "Motivo estruturado da limitação", reason));
            facts.Add(new("bodyComplete", "Leitura do corpo completo confirmada", bodyComplete && observedBytes <= MaximumResponseBytes ? "true" : "false", Detailed: true));
            facts.Add(new("sourceBytesObserved", "Bytes observados, incluindo eventual sonda", NumberValue: buffer is null ? null : observedBytes, Unit: "bytes", Detailed: true));
            facts.Add(new("sourceBytesRetained", "Bytes originais retidos (prefixo quando parcial)", NumberValue: buffer is null ? null : retained, Unit: "bytes", Detailed: true));
            facts.Add(new("sourceBytesBase64", "Bytes originais; consulte bodyComplete e escopo do digest", buffer is null ? null : Convert.ToBase64String(buffer.AsSpan(0, retained)), Detailed: true));
            facts.Add(new("sourceDigestScope", "Escopo do SHA-256", buffer is null ? "NotRead" : bodyComplete && observedBytes <= MaximumResponseBytes ? "CompleteReceivedBody" : "RetainedPrefix", Detailed: true));
            string? digest = buffer is null ? null : Convert.ToHexString(SHA256.HashData(buffer.AsSpan(0, retained)));
            facts.Add(new("sourceSha256", "SHA-256 do corpo completo recebido", bodyComplete && observedBytes <= MaximumResponseBytes ? digest : null, Detailed: true));
            facts.Add(new("sourceRetainedPrefixSha256", "SHA-256 dos bytes retidos quando corpo incompleto", !bodyComplete || observedBytes > MaximumResponseBytes ? digest : null, Detailed: true));
            var evidence = new DiagnosticEvidence(Guid.NewGuid(), "firmware.release-notes", "Página oficial consultada explicitamente", url, DateTimeOffset.UtcNow, EvidenceStatus.Partial,
                new("Um GET explícito a host exato do catálogo oficial, HTTPS porta 443; sem redirects, cookies, credenciais, scripts ou recursos carregados. Até 512 KiB mais um byte de sonda; Content-Length excessivo recusado antes de alocar corpo, prazo total de 20 s. Bytes recebidos e digest conservam escopo completo/prefixo. Charset HTTP ou BOM explícitos determinam decoder estrito; ausência/conflito ficam desconhecidos. DOM de até 10000 elementos/profundidade 128, sem loader; expansão DOM/texto/base64 não foi medida como memória física. Até 512 parágrafos-folha p/li (ou blocos de texto simples), 16 trechos de até 1000 caracteres. Contagens lexicais PT/EN se referem somente aos parágrafos selecionados; negativos e negações também podem conter termos. A seleção não autentica notas, produto, versão mais recente, imagem de firmware, compatibilidade ou diagnóstico causal. Não baixa binário nem grava firmware; não acrescenta evidências ou identificadores de equipamento à URL selecionada.", paragraphs, MaximumParagraphs, limited), facts.ToArray(), issues.ToArray());
            return error is null ? Result<DiagnosticEvidence>.Success(evidence) : Result<DiagnosticEvidence>.FailureWithValue(evidence, error.Code, error.Message, error.PendingCleanupPaths);
        }
        void SetNumber(string key, decimal value) { int index = facts.FindIndex(fact => fact.Key == key); facts[index] = facts[index] with { NumberValue = value }; }
    }

    private static bool Allowed(Uri selected, Uri official) => selected.Scheme == Uri.UriSchemeHttps && selected.Port == 443 && selected.UserInfo.Length == 0
        && selected.IdnHost.Equals(official.IdnHost, StringComparison.OrdinalIgnoreCase);
    private static Regex Terms(string pattern) => new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
    private static string Decode(ReadOnlySpan<byte> bytes, string charset, out string name, out int bom)
    {
        string declared = charset.Trim().Trim('"', '\'').ToLowerInvariant();
        name = ""; bom = 0;
        if (bytes.StartsWith(new byte[] { 0xFF, 0xFE, 0, 0 }) || bytes.StartsWith(new byte[] { 0, 0, 0xFE, 0xFF })) throw new InvalidDataException("UTF-32 não suportado.");
        if (bytes.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) { name = "utf-8"; bom = 3; }
        else if (bytes.StartsWith(new byte[] { 0xFF, 0xFE })) { name = "utf-16le"; bom = 2; }
        else if (bytes.StartsWith(new byte[] { 0xFE, 0xFF })) { name = "utf-16be"; bom = 2; }
        if (bom > 0 && declared.Length > 0 && !EquivalentCharset(declared, name)) throw new InvalidDataException("Charset HTTP e BOM divergem; texto não interpretado.");
        if (bom == 0) name = declared switch { "utf8" or "utf-8" => "utf-8", "utf-16le" => "utf-16le", "utf-16be" => "utf-16be", "us-ascii" => "us-ascii", "iso-8859-1" => "iso-8859-1", _ => throw new InvalidDataException("Charset ausente, ambíguo ou não suportado e nenhum BOM explícito; sem tentativa de adivinhação.") };
        Encoding encoding = name switch
        {
            "utf-8" => new UTF8Encoding(false, true),
            "utf-16le" => new UnicodeEncoding(false, false, true),
            "utf-16be" => new UnicodeEncoding(true, false, true),
            "iso-8859-1" => Encoding.Latin1,
            _ => Encoding.GetEncoding("us-ascii", EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback)
        };
        return encoding.GetString(bytes[bom..]);
    }
    private static bool EquivalentCharset(string declared, string encoding)
    {
        string normalized = declared.Trim().Trim('"', '\'').ToLowerInvariant();
        return normalized == encoding || encoding == "utf-8" && normalized == "utf8" || encoding.StartsWith("utf-16", StringComparison.Ordinal) && normalized == "utf-16";
    }
    private static IEnumerable<string> TextParagraphs(string text)
    {
        using var reader = new StringReader(text);
        StringBuilder paragraph = new();
        while (reader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) { if (paragraph.Length > 0) { yield return paragraph.ToString(); paragraph.Clear(); } }
            else { if (paragraph.Length > 0) paragraph.Append('\n'); paragraph.Append(line); }
        }
        if (paragraph.Length > 0) yield return paragraph.ToString();
    }
    private sealed class NotesLimitException(string message) : Exception(message) { }
}
