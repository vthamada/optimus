using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Optimus.Application;
using Optimus.Domain;
using Optimus.Infrastructure.Files;

namespace Optimus.Infrastructure.AI;

public sealed class LlamaCppAssistantService(IExternalToolRunner runner, string workingRoot) : IAssistantService
{
    private const int MaximumRetainedRuns = 100;
    private const int MaximumToolOutputBytes = 65_536;
    private static readonly byte[] CapacityMarker = "optimus.assistant-capacity.v1\n"u8.ToArray();
    private readonly string _workingRoot = Path.GetFullPath(workingRoot);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public Task<Result<AssistantAnswer>> AnalyzeAsync(DiagnosticSession session, string question,
        LocalAiSettings settings, CancellationToken cancellationToken) =>
        Task.Run(() => AnalyzeCoreAsync(session, question, settings, cancellationToken), CancellationToken.None);

    private async Task<Result<AssistantAnswer>> AnalyzeCoreAsync(DiagnosticSession session, string question,
        LocalAiSettings settings, CancellationToken cancellationToken)
    {
        bool acquired = false;
        string? runDirectory = null;
        var retainedFiles = new List<string>();
        var heldFiles = new List<FileStream>();
        Result<AssistantAnswer>? result = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!SessionValidation.IsValid(session) || string.IsNullOrWhiteSpace(question)
                || question.Length > 800 || Encoding.UTF8.GetByteCount(question) > 1536)
                return Result<AssistantAnswer>.Failure(ErrorCode.InvalidData, "Selecione um diagnóstico válido e uma pergunta de até 800 caracteres e 1.536 bytes UTF-8.");
            if (!ValidSettings(settings))
                return Result<AssistantAnswer>.Failure(ErrorCode.InvalidConfiguration, "A configuração de IA precisa de caminhos locais absolutos de executável e GGUF, limites válidos e hashes SHA-256 válidos quando informados.");
            var executablePath = LocalPathGuard.LocalPath(settings.ExecutablePath);
            var modelPath = LocalPathGuard.LocalPath(settings.ModelPath);
            var workRoot = LocalPathGuard.LocalPath(_workingRoot);
            if (!Path.GetExtension(executablePath).Equals(".exe", StringComparison.OrdinalIgnoreCase)
                || !Path.GetExtension(modelPath).Equals(".gguf", StringComparison.OrdinalIgnoreCase))
                return Result<AssistantAnswer>.Failure(ErrorCode.InvalidConfiguration, "Selecione um executável .exe e um arquivo .gguf locais.");
            if (!File.Exists(executablePath) || !File.Exists(modelPath))
                return Result<AssistantAnswer>.Failure(ErrorCode.DependencyUnavailable, "Selecione o frontend llama-completion do llama.cpp e um modelo GGUF local. Nenhum arquivo será baixado automaticamente.");

            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            acquired = true;
            using var modelDirectory = LocalPathGuard.HoldDirectory(Path.GetDirectoryName(modelPath)!);
            using var executableDirectory = LocalPathGuard.HoldDirectory(Path.GetDirectoryName(executablePath)!);
            using var executable = new FileStream(executablePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
            var observedExecutableSha256 = settings.ExpectedExecutableSha256 is null
                ? Convert.ToHexString(await SHA256.HashDataAsync(executable, cancellationToken).ConfigureAwait(false))
                : null;
            var effectiveExecutableSha256 = settings.ExpectedExecutableSha256 ?? observedExecutableSha256!;
            using var model = new FileStream(modelPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
            var header = new byte[4];
            if (await model.ReadAsync(header, cancellationToken).ConfigureAwait(false) != 4 || !header.AsSpan().SequenceEqual("GGUF"u8))
                return Result<AssistantAnswer>.Failure(ErrorCode.InvalidData, "O arquivo selecionado não possui cabeçalho GGUF.");
            if (settings.ExpectedModelSha256 is not null)
            {
                model.Position = 0;
                var digest = Convert.ToHexString(await SHA256.HashDataAsync(model, cancellationToken).ConfigureAwait(false));
                if (!digest.Equals(settings.ExpectedModelSha256, StringComparison.OrdinalIgnoreCase))
                    return Result<AssistantAnswer>.Failure(ErrorCode.VerificationFailed, "O SHA-256 do modelo diverge da configuração. Nenhuma inferência foi iniciada.");
            }

            // UTF-8 bytes are a conservative input budget, not an asserted tokenizer measurement.
            var promptBudget = Math.Min(16_384, settings.ContextTokens - settings.MaximumOutputTokens - 1024);
            var selection = AssistantProtocol.Select(session, question, promptBudget, settings.MaximumSelectedFacts);
            if (selection is null) return Result<AssistantAnswer>.Failure(ErrorCode.InvalidData, "Nenhuma seleção de evidências coube no orçamento de entrada. Reduza a pergunta ou escolha outro diagnóstico.");
            Directory.CreateDirectory(workRoot);
            using var workRootLease = LocalPathGuard.HoldDirectory(workRoot);
            var reservation = ReserveRunDirectory(workRoot);
            if (!reservation.IsSuccess) return Result<AssistantAnswer>.Failure(reservation.Error!.Code, reservation.Error.Message);
            runDirectory = reservation.Value!;
            using var runDirectoryLease = LocalPathGuard.HoldDirectory(runDirectory);
            var promptPath = Path.Combine(runDirectory, "prompt.txt");
            var schemaPath = Path.Combine(runDirectory, "response-schema.json");
            var requestPath = Path.Combine(runDirectory, "request.json");
            WriteNewFile(requestPath, JsonSerializer.SerializeToUtf8Bytes(new
            {
                createdAt = DateTimeOffset.UtcNow,
                state = "Prepared",
                protocolVersion = AssistantProtocol.Version,
                transportVersion = CompletionTransportCodec.Version,
                sessionId = session.Id,
                settings.ExecutablePath,
                settings.ModelPath,
                settings.ExpectedExecutableSha256,
                settings.ExpectedModelSha256,
                observedExecutableSha256,
                effectiveExecutableSha256,
                settings.MaximumOutputTokens,
                settings.ContextTokens,
                settings.Threads,
                settings.TimeoutSeconds,
                settings.MaximumMemoryBytes,
                settings.MaximumSelectedFacts,
                selectedEvidenceIds = selection.EvidenceIds,
                selectedFactSources = selection.Facts.Values,
                selection.FilterStrategy,
                selection.FilterTerms,
                selection.LexicallyExcludedFacts,
                selection.OmittedEvidence,
                selection.OmittedFacts,
                input = selection.Prompt
            }, DiagnosticJson.Options), retainedFiles, heldFiles);
            WriteNewFile(promptPath, Encoding.UTF8.GetBytes(selection.Prompt), retainedFiles, heldFiles);
            WriteNewFile(schemaPath, Encoding.UTF8.GetBytes(selection.Schema), retainedFiles, heldFiles);
            cancellationToken.ThrowIfCancellationRequested();

            var request = new ToolRequest(executablePath, BuildArguments(settings, modelPath, promptPath, schemaPath),
                runDirectory, TimeSpan.FromSeconds(settings.TimeoutSeconds), MaximumToolOutputBytes,
                EnvironmentOverrides: new Dictionary<string, string>
                {
                    ["LLAMA_ARG_OFFLINE"] = "1"
                }, ExpectedExecutableSha256: effectiveExecutableSha256,
                MaximumMemoryBytes: settings.MaximumMemoryBytes,
                EnvironmentVariablesToRemove: ProviderEnvironmentNames());
            var execution = await runner.RunAsync(request, cancellationToken).ConfigureAwait(false);
            var responsePath = Path.Combine(runDirectory, "result.json");
            WriteNewFile(responsePath, JsonSerializer.SerializeToUtf8Bytes(new
            {
                completedAt = DateTimeOffset.UtcNow,
                sessionId = session.Id,
                protocolVersion = AssistantProtocol.Version,
                transportVersion = CompletionTransportCodec.Version,
                configuredExecutableSha256 = settings.ExpectedExecutableSha256,
                observedExecutableSha256,
                effectiveExecutableSha256,
                allowsPinnedTrailer = execution.ExitedSuccessfully && execution.Error is null
                    && CompletionTransportCodec.AllowsPinnedTrailer(effectiveExecutableSha256),
                execution.Status,
                execution.ExitCode,
                execution.StandardOutput,
                execution.StandardError,
                execution.Metrics,
                execution.Error
            }, DiagnosticJson.Options), retainedFiles, heldFiles);
            if (cancellationToken.IsCancellationRequested)
            {
                result = Result<AssistantAnswer>.Failure(ErrorCode.Cancelled,
                    $"Inferência cancelada antes da aceitação da resposta. Evidência local: {responsePath}", execution.Error?.PendingCleanupPaths);
            }
            else if (!execution.ExitedSuccessfully || execution.Error is not null)
            {
                var code = execution.Status switch
                {
                    ToolRunStatus.Cancelled => ErrorCode.Cancelled,
                    ToolRunStatus.TimedOut => ErrorCode.Timeout,
                    ToolRunStatus.OutputLimitExceeded => ErrorCode.OutputLimitExceeded,
                    _ => execution.Error?.Code ?? ErrorCode.ToolFailed
                };
                result = Result<AssistantAnswer>.Failure(code,
                    $"{execution.Error?.Message ?? "A inferência local não foi concluída."} Evidência local: {responsePath}", execution.Error?.PendingCleanupPaths);
            }
            else
            {
                var usage = new AssistantUsage(execution.Metrics.Elapsed, execution.Metrics.CpuTime,
                    execution.Metrics.PeakProcessFamilyMemoryBytes);
                result = AssistantProtocol.Parse(execution.StandardOutput, session, selection,
                    Path.GetFileName(settings.ModelPath), usage, responsePath,
                    CompletionTransportCodec.AllowsPinnedTrailer(effectiveExecutableSha256));
            }
        }
        catch (OperationCanceledException error) when (cancellationToken.IsCancellationRequested)
        {
            result = Result<AssistantAnswer>.Failure(ErrorCode.Cancelled,
                $"Inferência cancelada. {error.Message}{ReceiptLocation(runDirectory, retainedFiles)}");
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException or Win32Exception or System.Security.SecurityException or CryptographicException)
        {
            result = Result<AssistantAnswer>.Failure(error is UnauthorizedAccessException ? ErrorCode.PermissionDenied
                : error is InvalidDataException ? ErrorCode.InvalidData : ErrorCode.ToolFailed,
                $"{error.Message}{ReceiptLocation(runDirectory, retainedFiles)}");
        }
        catch (Exception error)
        {
            result = Result<AssistantAnswer>.Failure(ErrorCode.ToolFailed,
                $"A fronteira do motor local falhou: {error.Message}{ReceiptLocation(runDirectory, retainedFiles)}");
        }
        finally
        {
            foreach (var lease in heldFiles) lease.Dispose();
            if (acquired) _gate.Release();
        }
        if (cancellationToken.IsCancellationRequested && result?.IsSuccess == true)
            return Result<AssistantAnswer>.FailureWithValue(result.Value!, ErrorCode.Cancelled,
                $"Resposta produzida, mas cancelada durante a finalização.{ReceiptLocation(runDirectory, retainedFiles)}");
        return result!;
    }

    // The explicit completion frontend applies the selected GGUF's own instruct template.
    // No executable is substituted and the transport parser does not search for JSON in decoration.
    private static string[] BuildArguments(LocalAiSettings settings, string modelPath, string promptPath, string schemaPath) =>
    [
        "--model", modelPath, "--file", promptPath,
        "--json-schema-file", schemaPath, "--conversation", "--single-turn", "--no-display-prompt",
        "--color", "off", "--simple-io", "--log-verbosity", "0", "--no-perf", "--offline", "--no-context-shift",
        "--device", "none", "--gpu-layers", "0", "--no-warmup", "--temp", "0", "--seed", "42",
        "--ctx-size", settings.ContextTokens.ToString(System.Globalization.CultureInfo.InvariantCulture),
        "--predict", settings.MaximumOutputTokens.ToString(System.Globalization.CultureInfo.InvariantCulture),
        "--threads", (settings.Threads == 0 ? Math.Max(1, Math.Min(4, Environment.ProcessorCount / 2)) : settings.Threads).ToString(System.Globalization.CultureInfo.InvariantCulture)
    ];

    private static bool ValidSettings(LocalAiSettings? settings) => settings is not null
        && !string.IsNullOrWhiteSpace(settings.ExecutablePath) && !string.IsNullOrWhiteSpace(settings.ModelPath)
        && Hash(settings.ExpectedExecutableSha256) && Hash(settings.ExpectedModelSha256)
        && settings.MaximumOutputTokens is >= 64 and <= 768 && settings.ContextTokens is >= 4096 and <= 16384
        && settings.TimeoutSeconds is >= 1 and <= 300
        && settings.Threads >= 0 && settings.Threads <= Environment.ProcessorCount
        && settings.MaximumMemoryBytes is >= 536_870_912 and <= 17_179_869_184
        && settings.MaximumSelectedFacts is >= 1 and <= 6;

    private static bool Hash(string? value) => value is null || value.Length == 64 && value.All(Uri.IsHexDigit);
    private static string[] ProviderEnvironmentNames() => Environment.GetEnvironmentVariables().Keys.OfType<string>()
        .Where(name => name.StartsWith("LLAMA_ARG_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("GGML_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("GGUF_", StringComparison.OrdinalIgnoreCase))
        .Concat([
            "LLAMA_ARG_RPC", "LLAMA_ARG_MODEL_URL", "LLAMA_ARG_HF_REPO", "LLAMA_ARG_DOCKER_REPO",
            "LLAMA_ARG_LOG_FILE", "LLAMA_ARG_SERVER_BASE", "HF_TOKEN", "HUGGING_FACE_HUB_TOKEN"
        ]).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    private static string ReceiptLocation(string? directory, List<string> retained) =>
        directory is not null && retained.Count > 0 ? $" Recibo local: {directory}" : "";

    private static Result<string> ReserveRunDirectory(string root)
    {
        var lockPath = LocalPathGuard.LocalPath(Path.Combine(root, ".capacity.lock"));
        FileStream? capacity = null;
        try
        {
            bool created;
            try
            {
                capacity = new(lockPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
                created = true;
            }
            catch (IOException) when (File.Exists(lockPath))
            {
                try { capacity = new(lockPath, FileMode.Open, FileAccess.Read, FileShare.None); }
                catch (IOException)
                {
                    return Result<string>.Failure(ErrorCode.StorageFailure, "A reserva local de capacidade da IA está ocupada por outra chamada. Nenhum motor foi iniciado e não haverá repetição automática.");
                }
                created = false;
            }
            _ = LocalPathGuard.Identity(capacity.SafeFileHandle);
            if (created)
            {
                capacity.Write(CapacityMarker);
                capacity.Flush(flushToDisk: true);
            }
            else
            {
                if (capacity.Length != CapacityMarker.Length)
                    return Result<string>.Failure(ErrorCode.InvalidData, "O arquivo de capacidade existente não é reconhecido. Seu conteúdo foi preservado.");
                Span<byte> marker = stackalloc byte[CapacityMarker.Length];
                capacity.ReadExactly(marker);
                if (!marker.SequenceEqual(CapacityMarker))
                    return Result<string>.Failure(ErrorCode.InvalidData, "O arquivo de capacidade existente não é reconhecido. Seu conteúdo foi preservado.");
            }
            if (Directory.EnumerateDirectories(root, "run-*", SearchOption.TopDirectoryOnly).Take(MaximumRetainedRuns).Count() >= MaximumRetainedRuns)
                return Result<string>.Failure(ErrorCode.StorageFailure, "O limite de 100 recibos locais de IA foi atingido. Revise e arquive os recibos antes de novas chamadas; nenhum registro foi removido.");
            var directory = Path.Combine(root, $"run-{Guid.NewGuid():N}");
            LocalPathGuard.CreateNewDirectory(directory);
            return Result<string>.Success(directory);
        }
        finally { capacity?.Dispose(); }
    }

    private static void WriteNewFile(string path, byte[] contents, List<string> owned, List<FileStream> leases)
    {
        if (contents.Length > 1_048_576) throw new InvalidDataException("O recibo de IA excede o limite local de 1 MiB.");
        PathIdentity identity;
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            owned.Add(path);
            identity = LocalPathGuard.Identity(stream.SafeFileHandle);
            stream.Write(contents);
            stream.Flush(flushToDisk: true);
        }
        var lease = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            if (LocalPathGuard.Identity(lease.SafeFileHandle) != identity)
                throw new InvalidDataException("A identidade de uma evidência de IA mudou após a criação; nenhum substituto será alterado.");
            leases.Add(lease);
        }
        catch { lease.Dispose(); throw; }
    }
}
