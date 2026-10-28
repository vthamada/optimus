using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Optimus.Domain;
using Optimus.Infrastructure.Hardware;

namespace Optimus.Tests;

public sealed class FirmwareNotesTests
{
    [Fact]
    public async Task ExplicitOfficialHtmlUsesOneGetAndPublishesHeuristicParagraphCountsWithRawProvenance()
    {
        string html = "<html><head><meta charset='utf-8'></head><body><p>Improves memory stability.</p><ul><li>Atualiza módulo de segurança TPM.</li><li>Sem alteração de memória.</li></ul><script>memory crash TPM</script><img src='https://elsewhere.invalid/track'></body></html>";
        byte[] bytes = Encoding.UTF8.GetBytes(html);
        using var transport = new Handler(_ => Response(bytes)); using var reader = new FirmwareNotesReader(transport);
        var result = await reader.ReadAsync("https://www.dell.com/support/notes#selected", "  Dell Inc.  ", CancellationToken.None);
        Assert.True(result.IsSuccess, result.Error?.Message); var evidence = result.Value!;
        Assert.Equal("firmware.release-notes", evidence.CollectorId); Assert.Equal(EvidenceStatus.Partial, evidence.Status);
        Assert.Equal(1, transport.Calls); Assert.Equal(HttpMethod.Get, transport.Method); Assert.Equal("https://www.dell.com/support/notes", transport.Uri!.AbsoluteUri);
        Assert.Null(transport.Authorization); Assert.Null(transport.Cookie);
        Assert.Equal(2m, Fact(evidence, "notes.memoryMentions").NumberValue); Assert.Equal(1m, Fact(evidence, "notes.stabilityMentions").NumberValue); Assert.Equal(1m, Fact(evidence, "notes.securityModuleMentions").NumberValue);
        Assert.Equal(html, Fact(evidence, "sourceHtml").TextValue); Assert.Equal(bytes, Convert.FromBase64String(Fact(evidence, "sourceBytesBase64").TextValue!));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), Fact(evidence, "sourceSha256").TextValue);
        Assert.Equal("true", Fact(evidence, "bodyComplete").TextValue); Assert.Equal("false", Fact(evidence, "latestVersionConfirmed").TextValue);
        Assert.Equal("false", Fact(evidence, "imageCompatibilityConfirmed").TextValue);
        Assert.True(SessionValidation.IsValid(new(Guid.NewGuid(), evidence.CapturedAt, evidence.CapturedAt, SessionStatus.Partial, [evidence], [])));
    }

    [Theory]
    [InlineData("http://www.dell.com/support/notes", "Dell")]
    [InlineData("https://www.dell.com:444/support/notes", "Dell")]
    [InlineData("https://www.dell.com.attacker.invalid/support/notes", "Dell")]
    [InlineData("https://downloads.dell.com/notes", "Dell")]
    [InlineData("https://www.dell.com/support/notes", "Unknown vendor")]
    [InlineData("https://user:password@www.dell.com/support/notes", "Dell")]
    [InlineData("https://www.dell.com./support/notes", "Dell")]
    [InlineData("https://127.0.0.1/notes", "Dell")]
    public async Task UnknownHostSchemePortOrCredentialsFailBeforeAnyGet(string url, string manufacturer)
    {
        using var transport = new Handler(_ => throw new InvalidOperationException("Must not send")); using var reader = new FirmwareNotesReader(transport);
        var result = await reader.ReadAsync(url, manufacturer, CancellationToken.None);
        Assert.False(result.IsSuccess); Assert.Null(result.Value); Assert.Equal(0, transport.Calls); Assert.Equal(ErrorCode.InvalidConfiguration, result.Error!.Code);
    }

    [Theory]
    [InlineData("https://www.dell.com/other")]
    [InlineData("https://elsewhere.invalid/notes")]
    public async Task RedirectsAreRetainedAndNeverFollowedEvenOnTheSameHost(string destination)
    {
        using var transport = new Handler(_ => { var response = new HttpResponseMessage(HttpStatusCode.Found); response.Headers.Location = new(destination); return response; }); using var reader = new FirmwareNotesReader(transport);
        var result = await Read(reader);
        Assert.False(result.IsSuccess); Assert.Equal(1, transport.Calls); Assert.Equal("RedirectNotFollowed", Fact(result.Value!, "partialReason").TextValue);
        Assert.Equal(destination, Fact(result.Value!, "redirectLocation").TextValue); Assert.Null(Fact(result.Value!, "notes.memoryMentions").NumberValue);
        Assert.Null(Fact(result.Value!, "sourceBytesBase64").TextValue);
    }

    [Theory]
    [InlineData("application/octet-stream")]
    [InlineData("application/zip")]
    [InlineData("image/jpeg")]
    public async Task BinaryResponsesAreNotReadOrInterpreted(string mediaType)
    {
        var stream = new CountingStream(100);
        using var transport = new Handler(_ => StreamResponse(stream, mediaType, "utf-8")); using var reader = new FirmwareNotesReader(transport);
        var result = await Read(reader);
        Assert.Equal("UnsupportedContentType", Fact(result.Value!, "partialReason").TextValue); Assert.Equal(0, stream.BytesRead); Assert.True(stream.Disposed);
    }

    [Fact]
    public async Task DeclaredOversizeIsRefusedBeforeReadingOrAllocatingBody()
    {
        var stream = new CountingStream(10);
        using var transport = new Handler(_ => { var response = StreamResponse(stream); response.Content.Headers.ContentLength = FirmwareNotesReader.MaximumResponseBytes + 1; return response; }); using var reader = new FirmwareNotesReader(transport);
        var result = await Read(reader);
        Assert.Equal(ErrorCode.OutputLimitExceeded, result.Error!.Code); Assert.Equal(0, stream.BytesRead);
        Assert.Equal("DeclaredSizeLimit", Fact(result.Value!, "partialReason").TextValue); Assert.Null(Fact(result.Value!, "sourceBytesRetained").NumberValue);
    }

    [Fact]
    public async Task UnknownLengthStopsAtOneProbeAndRetainsOnlyAPrefixWithItsOwnDigest()
    {
        var stream = new CountingStream(FirmwareNotesReader.MaximumResponseBytes + 50);
        using var transport = new Handler(_ => StreamResponse(stream)); using var reader = new FirmwareNotesReader(transport);
        var result = await Read(reader); var evidence = result.Value!;
        Assert.Equal(FirmwareNotesReader.MaximumResponseBytes + 1, stream.BytesRead); Assert.True(stream.Disposed);
        Assert.Equal("false", Fact(evidence, "bodyComplete").TextValue); Assert.Null(Fact(evidence, "sourceSha256").TextValue);
        Assert.Equal("RetainedPrefix", Fact(evidence, "sourceDigestScope").TextValue);
        var prefix = Convert.FromBase64String(Fact(evidence, "sourceBytesBase64").TextValue!);
        Assert.Equal(FirmwareNotesReader.MaximumResponseBytes, prefix.Length);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(prefix)), Fact(evidence, "sourceRetainedPrefixSha256").TextValue);
        Assert.Null(Fact(evidence, "notes.memoryMentions").NumberValue); Assert.True(evidence.Coverage.LimitReached);
    }

    [Fact]
    public async Task HttpErrorsPreserveStatusAndBytesWithoutTreatingTheirPageAsReleaseNotes()
    {
        byte[] body = Encoding.UTF8.GetBytes("<p>Memory stability error page</p>");
        using var transport = new Handler(_ => Response(body, status: HttpStatusCode.NotFound)); using var reader = new FirmwareNotesReader(transport);
        var result = await Read(reader); Assert.Equal("HttpError", Fact(result.Value!, "partialReason").TextValue);
        Assert.Equal(404m, Fact(result.Value!, "httpStatus").NumberValue); Assert.Equal(body, Convert.FromBase64String(Fact(result.Value!, "sourceBytesBase64").TextValue!));
        Assert.Null(Fact(result.Value!, "notes.stabilityMentions").NumberValue);
    }

    [Theory]
    [InlineData("", "<p>memory</p>")]
    [InlineData("utf-8", "invalid bytes supplied independently")]
    public async Task MissingOrInvalidEncodingCannotCreateNormalizedMentionCounts(string charset, string html)
    {
        byte[] bytes = charset.Length == 0 ? Encoding.UTF8.GetBytes(html) : [0xFF];
        using var transport = new Handler(_ => Response(bytes, charset: charset)); using var reader = new FirmwareNotesReader(transport);
        var result = await Read(reader);
        Assert.False(result.IsSuccess); Assert.Equal("UnknownOrInvalidEncoding", Fact(result.Value!, "partialReason").TextValue);
        Assert.Null(Fact(result.Value!, "notes.memoryMentions").NumberValue); Assert.NotNull(Fact(result.Value!, "sourceBytesBase64").TextValue);
    }

    [Fact]
    public async Task ExplicitUtf16BomAndPlainTextAreSupportedWithoutGuessingOrLoadingResources()
    {
        var encoding = new UnicodeEncoding(false, true, true); string text = "Memória RAM\n\nStability fixes for TPM"; byte[] bytes = [.. encoding.GetPreamble(), .. encoding.GetBytes(text)];
        using var transport = new Handler(_ => Response(bytes, "text/plain", "utf-16")); using var reader = new FirmwareNotesReader(transport);
        var result = await Read(reader); Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal("utf-16le", Fact(result.Value!, "sourceEncoding").TextValue); Assert.Equal(text, Fact(result.Value!, "sourceText").TextValue);
        Assert.Equal(1m, Fact(result.Value!, "notes.memoryMentions").NumberValue); Assert.Equal(1m, Fact(result.Value!, "notes.securityModuleMentions").NumberValue);
    }

    [Fact]
    public async Task ConflictingBomAndCharsetIsUnknownInsteadOfSelectingAConvenientDecoder()
    {
        byte[] bytes = [0xFF, 0xFE, .. Encoding.Unicode.GetBytes("<p>memory</p>")];
        using var transport = new Handler(_ => Response(bytes, charset: "utf-8")); using var reader = new FirmwareNotesReader(transport);
        var result = await Read(reader); Assert.False(result.IsSuccess); Assert.Null(Fact(result.Value!, "notes.memoryMentions").NumberValue);
        Assert.Equal(bytes, Convert.FromBase64String(Fact(result.Value!, "sourceBytesBase64").TextValue!));
    }

    [Fact]
    public async Task ParagraphAndSnippetLimitsPreserveSeparateCountsAndOmissions()
    {
        string html = string.Concat(Enumerable.Repeat("<p>memory stability TPM " + new string('X', 1100) + "</p>", 513));
        // Keep this fixture within the byte limit while independently exercising paragraph and snippet limits.
        html = html.Replace(new string('X', 1100), "value", StringComparison.Ordinal);
        using var transport = new Handler(_ => Response(Encoding.UTF8.GetBytes(html))); using var reader = new FirmwareNotesReader(transport);
        var result = await Read(reader); Assert.True(result.IsSuccess); var evidence = result.Value!;
        Assert.Equal(512m, Fact(evidence, "notes.memoryMentions").NumberValue); Assert.Equal(16m, Fact(evidence, "notes.snippetsRetained").NumberValue);
        Assert.Equal(496m, Fact(evidence, "notes.snippetsOmitted").NumberValue); Assert.True(evidence.Coverage.LimitReached);
        Assert.Equal("true", Fact(evidence, "bodyComplete").TextValue);
    }

    [Fact]
    public async Task LongSnippetsAreExplicitPrefixesAndNestedParagraphsAreNotDoubleCounted()
    {
        string paragraph = "memory " + new string('X', 1100);
        using var transport = new Handler(_ => Response(Encoding.UTF8.GetBytes("<ul><li><p>" + paragraph + "</p></li></ul>"))); using var reader = new FirmwareNotesReader(transport);
        var result = await Read(reader); var evidence = result.Value!;
        Assert.Equal(1m, Fact(evidence, "notes.memoryMentions").NumberValue); Assert.Equal(1000, Fact(evidence, "snippets.0.text").TextValue!.Length);
        Assert.Equal("true", Fact(evidence, "snippets.0.prefixOnly").TextValue); Assert.Equal((decimal)paragraph.Length, Fact(evidence, "snippets.0.originalCharacters").NumberValue);
    }

    [Fact]
    public async Task UnknownPageStructureKeepsRawAndUnknownMentions()
    {
        using var transport = new Handler(_ => Response(Encoding.UTF8.GetBytes("<div>memory stability TPM</div>"))); using var reader = new FirmwareNotesReader(transport);
        var result = await Read(reader); Assert.Equal("UnknownPageStructure", Fact(result.Value!, "partialReason").TextValue);
        Assert.NotNull(Fact(result.Value!, "sourceHtml").TextValue); Assert.Null(Fact(result.Value!, "notes.memoryMentions").NumberValue);
    }

    [Fact]
    public async Task TimeoutAndInterruptedReadsKeepStructuredReasonAndReceivedPrefix()
    {
        using (var timeoutTransport = new Handler(_ => throw new TaskCanceledException("simulated deadline")))
        using (var reader = new FirmwareNotesReader(timeoutTransport))
        {
            var result = await Read(reader); Assert.Equal(ErrorCode.Timeout, result.Error!.Code); Assert.Equal("Timeout", Fact(result.Value!, "partialReason").TextValue);
        }
        var stream = new CountingStream(20, throwAfter: 5);
        using var transport = new Handler(_ => StreamResponse(stream)); using var interrupted = new FirmwareNotesReader(transport);
        var failed = await Read(interrupted); Assert.False(failed.IsSuccess);
        Assert.Equal(5m, Fact(failed.Value!, "sourceBytesRetained").NumberValue); Assert.Equal("false", Fact(failed.Value!, "bodyComplete").TextValue);
    }

    [Fact]
    public async Task PreCancelledRequestDoesNotSendAndActiveCancellationRetainsPartialEvidence()
    {
        using var before = new CancellationTokenSource(); before.Cancel(); using var transport = new Handler(_ => Response([])); using var reader = new FirmwareNotesReader(transport);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ReadAsync("https://www.dell.com/notes", "Dell", before.Token)); Assert.Equal(0, transport.Calls);
        using var during = new CancellationTokenSource();
        using var active = new Handler(_ => { during.Cancel(); throw new OperationCanceledException(during.Token); }); using var activeReader = new FirmwareNotesReader(active);
        var result = await activeReader.ReadAsync("https://www.dell.com/notes", "Dell", during.Token);
        Assert.Equal(ErrorCode.Cancelled, result.Error!.Code); Assert.NotNull(result.Value); Assert.Equal("Cancelled", Fact(result.Value!, "partialReason").TextValue);
    }

    private static Task<Result<DiagnosticEvidence>> Read(FirmwareNotesReader reader) => reader.ReadAsync("https://www.dell.com/notes", "Dell", CancellationToken.None);
    private static DiagnosticFact Fact(DiagnosticEvidence evidence, string key) => evidence.Facts.Single(fact => fact.Key == key);
    private static HttpResponseMessage Response(byte[] bytes, string mediaType = "text/html", string charset = "utf-8", HttpStatusCode status = HttpStatusCode.OK)
    {
        var response = new HttpResponseMessage(status) { Content = new ByteArrayContent(bytes) };
        response.Content.Headers.ContentType = new(mediaType) { CharSet = charset.Length > 0 ? charset : null }; return response;
    }
    private static HttpResponseMessage StreamResponse(Stream stream, string mediaType = "text/html", string charset = "utf-8")
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) }; response.Content.Headers.ContentType = new(mediaType) { CharSet = charset }; return response;
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public HttpMethod? Method { get; private set; }
        public Uri? Uri { get; private set; }
        public string? Authorization { get; private set; }
        public string? Cookie { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++; Method = request.Method; Uri = request.RequestUri; Authorization = request.Headers.Authorization?.ToString(); Cookie = request.Headers.TryGetValues("Cookie", out var cookie) ? string.Join(';', cookie) : null;
            return Task.FromResult(send(request));
        }
    }
    private sealed class CountingStream(int length, int? throwAfter = null) : Stream
    {
        public int BytesRead { get; private set; }
        public bool Disposed { get; private set; }
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => BytesRead; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            if (throwAfter is { } fail && BytesRead >= fail) throw new IOException("simulated interrupted response");
            int count = Math.Min(buffer.Length, Math.Max(0, (throwAfter ?? length) - BytesRead)); buffer[..count].Fill((byte)'X'); BytesRead += count; return count;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(Read(buffer.Span)); }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
        public override void Flush() => throw new NotSupportedException(); public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
