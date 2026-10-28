using System.Text.Json;
using Optimus.Domain;
using Optimus.Infrastructure.Files;

namespace Optimus.Infrastructure.Integrity;

internal static class IntegrityJournal
{
    private const int MaximumOperations = 100;
    private const int MaximumJournalBytes = 8 * 1024 * 1024;
    private static readonly byte[] Marker = "optimus.integrity-capacity.v1\n"u8.ToArray();

    public static Result<string> Reserve(string root, Guid id)
    {
        var path = LocalPathGuard.LocalPath(Path.Combine(root, ".capacity.lock"));
        FileStream? lease = null;
        try
        {
            bool created;
            try { lease = new(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None); created = true; }
            catch (IOException) when (File.Exists(path))
            {
                try { lease = new(path, FileMode.Open, FileAccess.Read, FileShare.None); }
                catch (IOException) { return Result<string>.Failure(ErrorCode.StorageFailure, "Outra operação mantém a reserva de capacidade. Nenhuma ferramenta foi chamada e não há repetição automática."); }
                created = false;
            }
            _ = LocalPathGuard.Identity(lease.SafeFileHandle);
            if (created) { lease.Write(Marker); lease.Flush(flushToDisk: true); }
            else
            {
                if (lease.Length != Marker.Length) return Unknown();
                Span<byte> actual = stackalloc byte[Marker.Length];
                lease.ReadExactly(actual);
                if (!actual.SequenceEqual(Marker)) return Unknown();
            }
            if (Directory.EnumerateDirectories(root, "operation-*", SearchOption.TopDirectoryOnly).Take(MaximumOperations).Count() >= MaximumOperations)
                return Result<string>.Failure(ErrorCode.StorageFailure, "O limite de cem diretórios de verificação retidos foi atingido. Nenhuma evidência foi removida e nenhuma ferramenta foi chamada.");
            var directory = Path.Combine(root, "operation-" + id.ToString("N"));
            LocalPathGuard.CreateNewDirectory(directory);
            return Result<string>.Success(directory);
        }
        finally { lease?.Dispose(); }

        static Result<string> Unknown() => Result<string>.Failure(ErrorCode.InvalidData,
            "O arquivo existente de reserva não pertence ao formato conhecido. Seu conteúdo foi preservado; nenhuma ferramenta foi chamada.");
    }

    public static async Task WriteAsync(string directory, string fileName, object document)
    {
        var json = JsonSerializer.Serialize(document, DiagnosticJson.Options);
        var result = await AtomicNewFileWriter.PublishUtf8Async(Path.Combine(directory, fileName), json,
            CancellationToken.None, MaximumJournalBytes).ConfigureAwait(false);
        if (!result.IsSuccess) throw new IntegrityJournalException(result.Error!.Message, result.Error.PendingCleanupPaths);
    }
}

internal sealed class IntegrityJournalException(string message, IReadOnlyList<string>? pendingCleanupPaths) : IOException(message)
{
    public IReadOnlyList<string>? PendingCleanupPaths { get; } = pendingCleanupPaths;
}
