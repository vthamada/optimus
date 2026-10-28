using System.Text;
using System.Text.Json;

namespace Optimus.Infrastructure.AI;

// Owns the transport framing of the explicitly pinned completion frontend.
// It never searches for JSON inside text or changes the generated JSON document.
internal static class CompletionTransportCodec
{
    internal const string Version = "llama-completion-json-frame-v1";
    internal const string CompletionB11669Sha256 = "A36EFB43FFA55F3A861043D4BF63B28B99D40560542C07A0EA51EA2FE30EEF6F";
    private const int MaximumResponseBytes = 65_536;

    internal static bool AllowsPinnedTrailer(string? verifiedExpectedDigest) =>
        string.Equals(verifiedExpectedDigest, CompletionB11669Sha256, StringComparison.OrdinalIgnoreCase);

    internal static JsonDocument Parse(string raw, bool allowPinnedTrailer)
    {
        if (Encoding.UTF8.GetByteCount(raw) > MaximumResponseBytes)
            throw new JsonException("A resposta excede o limite do transporte de completion.");
        var bytes = Encoding.UTF8.GetBytes(raw);
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = 16 });
        var document = JsonDocument.ParseValue(ref reader);
        var remainder = TrimWhitespace(bytes.AsSpan(checked((int)reader.BytesConsumed)));
        if (remainder.IsEmpty) return document;
        ReadOnlySpan<byte> trailer = "[end of text]"u8;
        if (allowPinnedTrailer && remainder.StartsWith(trailer)
            && TrimWhitespace(remainder[trailer.Length..]).IsEmpty) return document;
        document.Dispose();
        throw new JsonException("O transporte contém prefixo, documento adicional ou sufixo que não pertence ao contrato do frontend verificado.");
    }

    private static ReadOnlySpan<byte> TrimWhitespace(ReadOnlySpan<byte> text)
    {
        int first = 0;
        int last = text.Length;
        while (first < last && IsWhitespace(text[first])) first++;
        while (last > first && IsWhitespace(text[last - 1])) last--;
        return text[first..last];
    }

    private static bool IsWhitespace(byte value) => value is 0x20 or 0x09 or 0x0A or 0x0D;
}
