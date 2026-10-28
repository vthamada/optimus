using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using Optimus.Infrastructure.Files;

namespace Optimus.Infrastructure.Backup;

// An intentionally retained claim, not a temporary lock or a cleanup candidate.
internal sealed class DestinationReservation(FileStream record, SafeFileHandle directory) : IDisposable
{
    private const string Owner = "opTImus.backup-destination";
    private const int MaximumRecordBytes = 1_048_576;
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    public static void RequireAvailable(string root, string target)
    {
        var path = RecordPath(root, target);
        LocalPathGuard.RejectReparseAncestors(path);
        FileAttributes attributes;
        try { attributes = File.GetAttributes(path); }
        catch (FileNotFoundException) { return; }
        catch (DirectoryNotFoundException) { return; }
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw Ambiguous(path);
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.Length is < 1 or > MaximumRecordBytes) throw Ambiguous(path);
            using var document = JsonDocument.Parse(stream);
            var item = document.RootElement;
            if (item.ValueKind != JsonValueKind.Object
                || item.EnumerateObject().Select(property => property.Name).Distinct(StringComparer.Ordinal).Count() != item.EnumerateObject().Count()
                || !item.TryGetProperty("owner", out var owner) || owner.ValueKind != JsonValueKind.String || owner.GetString() != Owner
                || !item.TryGetProperty("schemaVersion", out var schema) || schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out var version) || version != 1
                || !item.TryGetProperty("target", out var storedTarget) || storedTarget.ValueKind != JsonValueKind.String
                || !string.Equals(storedTarget.GetString(), target, StringComparison.OrdinalIgnoreCase)
                || !item.TryGetProperty("kind", out var kind) || kind.ValueKind != JsonValueKind.String || kind.GetString() is not ("backup" or "restore")
                || !item.TryGetProperty("operationId", out var operation) || operation.ValueKind != JsonValueKind.String || !Guid.TryParse(operation.GetString(), out var id) || id == Guid.Empty
                || !item.TryGetProperty("journalDirectory", out var journal) || journal.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(journal.GetString()))
                throw Ambiguous(path);
            throw new InvalidDataException($"Destino reservado pela operação {id} ({kind.GetString()}). A reserva também permanece após uma conclusão e não significa, por si, estado pendente. Consulte o diário {journal.GetString()} e preserve a reserva {path}; selecione outro destino ou realize reconciliação explícita antes de reutilizá-lo.");
        }
        catch (JsonException error) { throw new InvalidDataException($"Reserva de destino malformada; isso não equivale a ausência. Preserve {path} para reconciliação.", error); }
    }

    public static DestinationReservation Claim(string root, string target, string kind, Guid operationId, string journalDirectory)
    {
        RequireAvailable(root, target);
        var path = RecordPath(root, target);
        var parent = Path.GetDirectoryName(path)!;
        LocalPathGuard.RejectReparseAncestors(parent);
        Directory.CreateDirectory(parent);
        SafeFileHandle? heldDirectory = null;
        FileStream? stream = null;
        try
        {
            heldDirectory = LocalPathGuard.HoldDirectory(parent);
            // CreateNew atomically refuses claims from another instance, including incomplete claims.
            stream = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                owner = Owner,
                schemaVersion = 1,
                operationId,
                kind,
                target,
                journalDirectory,
                claimedAt = DateTimeOffset.UtcNow,
                meaning = "Exclusive retained destination claim; consult operation journal for actual completion state."
            }, Json);
            if (bytes.Length > MaximumRecordBytes) throw new InvalidDataException("O registro de reserva excede 1 MiB; o arquivo próprio incompleto foi preservado para reconciliação.");
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
            return new(stream, heldDirectory);
        }
        catch
        {
            stream?.Dispose(); heldDirectory?.Dispose();
            throw;
        }
    }

    private static string RecordPath(string root, string target) => Path.Combine(root, "destination-reservations",
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(target.ToUpperInvariant()))) + ".json");
    private static InvalidDataException Ambiguous(string path) => new($"A reserva do destino tem identidade/formato desconhecido. Não será tratada como ausente. Preserve {path} e os diários antes de reconciliar.");
    public void Dispose() { record.Dispose(); directory.Dispose(); }
}
