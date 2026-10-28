using System.Text;
using System.Text.Json;
using Optimus.Application;
using Optimus.Domain;
using Optimus.Infrastructure.Files;

namespace Optimus.Infrastructure;

public static class RuntimeProfileFiles
{
    private sealed record ProfileDocument(string Kind, int Version, RuntimeProfile Profile);
    public const int MaximumProfileBytes = 65_536;

    public static async Task<Result<RuntimeProfile>> ReadAsync(string path, CancellationToken cancellationToken)
    {
        var source = await ReadUtf8Async(path, MaximumProfileBytes, cancellationToken).ConfigureAwait(false);
        if (!source.IsSuccess) return Result<RuntimeProfile>.Failure(source.Error!.Code, source.Error.Message);
        try
        {
            using var parsed = JsonDocument.Parse(source.Value!, new JsonDocumentOptions { MaxDepth = 16 });
            if (HasDuplicateProperties(parsed.RootElement)) return Result<RuntimeProfile>.Failure(ErrorCode.InvalidConfiguration, "O perfil contém propriedades JSON duplicadas e ambíguas.");
            var document = JsonSerializer.Deserialize<ProfileDocument>(source.Value!, DiagnosticJson.Options);
            if (document is null || document.Kind != "optimus.runtime-profile" || document.Version != 1 || !IsValid(document.Profile))
                return Result<RuntimeProfile>.Failure(ErrorCode.InvalidConfiguration, "O arquivo não é um perfil de ferramentas reconhecido do opTImus.");
            return Result<RuntimeProfile>.Success(document.Profile);
        }
        catch (JsonException error) { return Result<RuntimeProfile>.Failure(ErrorCode.InvalidConfiguration, error.Message); }
    }

    public static Task<Result<string>> WriteNewAsync(RuntimeProfile profile, string path, CancellationToken cancellationToken) =>
        Task.Run(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsValid(profile)) return Result<string>.Failure(ErrorCode.InvalidConfiguration, "Use caminhos locais absolutos e digests SHA-256 válidos.");
            // Bound escaped JSON before serialization; no ambient environment or credentials are encoded.
            long budget = 1024;
            foreach (var value in new[] { profile.LlamaExecutablePath, profile.ModelPath, profile.ResticExecutablePath, profile.SmartctlExecutablePath,
                profile.LlamaSha256, profile.ModelSha256, profile.ResticSha256, profile.CdbExecutablePath, profile.SymbolDirectory, profile.CdbSha256 }) budget += (long)(value?.Length ?? 0) * 6;
            if (budget > MaximumProfileBytes) return Result<string>.Failure(ErrorCode.InvalidConfiguration, "O perfil excede o orçamento de preparação de 64 KiB.");
            var json = JsonSerializer.Serialize(new ProfileDocument("optimus.runtime-profile", 1, profile), DiagnosticJson.Options);
            return await AtomicNewFileWriter.PublishUtf8Async(path, json, cancellationToken, MaximumProfileBytes).ConfigureAwait(false);
        }, CancellationToken.None);
    public static async Task<Result<string>> ReadUtf8Async(string path, int maximumBytes, CancellationToken cancellationToken)
    {
        if (maximumBytes is <= 0 or > 4_194_304) return Result<string>.Failure(ErrorCode.InvalidConfiguration, "Limite de leitura inválido.");
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > maximumBytes) return Result<string>.Failure(ErrorCode.InvalidData, $"O arquivo excede {maximumBytes} bytes.");
            var bytes = new byte[checked((int)stream.Length)];
            await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
            var text = new UTF8Encoding(false, true).GetString(bytes);
            if (text.Length > 0 && text[0] == '\uFEFF') text = text[1..];
            return Result<string>.Success(text);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return Result<string>.Failure(error is UnauthorizedAccessException ? ErrorCode.PermissionDenied : ErrorCode.InvalidData, error.Message);
        }
    }

    private static bool IsValid(RuntimeProfile? profile)
    {
        if (profile is null) return false;
        foreach (var path in new[] { profile.LlamaExecutablePath, profile.ModelPath, profile.ResticExecutablePath, profile.SmartctlExecutablePath, profile.CdbExecutablePath, profile.SymbolDirectory })
            if (path is not null && (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)
                || path.StartsWith("\\\\", StringComparison.Ordinal))) return false;
        foreach (var hash in new[] { profile.LlamaSha256, profile.ModelSha256, profile.ResticSha256, profile.CdbSha256 })
            if (hash is not null && (hash.Length != 64 || !hash.All(char.IsAsciiHexDigit))) return false;
        return true;
    }

    private static bool HasDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
                if (!names.Add(property.Name) || HasDuplicateProperties(property.Value)) return true;
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) if (HasDuplicateProperties(item)) return true;
        return false;
    }
}
