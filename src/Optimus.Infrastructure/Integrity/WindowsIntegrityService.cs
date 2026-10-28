using System.Security.Principal;
using System.Text.Json;
using Optimus.Application;
using Optimus.Domain;
using Optimus.Infrastructure.Files;

namespace Optimus.Infrastructure.Integrity;

/// <summary>Named verification commands only. No operation in this catalog requests a repair.</summary>
public sealed class WindowsIntegrityService(
    IExternalToolRunner runner, string journalRoot, TimeProvider? clock = null,
    Func<bool>? isElevated = null, Func<IntegrityOperation, string>? executablePath = null,
    Func<string>? systemDirectory = null) : IIntegrityService
{
    private const int MaximumOutputBytes = 256 * 1024;
    private const int MaximumRetainedPlans = 16;
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);
    private static readonly IReadOnlyList<string> Coverage = Array.AsReadOnly(new[]
    {
        "Somente SFC /verifyonly e DISM /CheckHealth ou /ScanHealth; nenhuma opção de reparo, limpeza ou reinicialização é emitida.",
        "A execução requer esta instância já elevada; o aplicativo não solicita UAC nem se eleva automaticamente.",
        "As ferramentas podem gravar logs e metadados operacionais próprios do Windows. O diário do opTImus é adicional.",
        "SFC verifica arquivos protegidos; DISM CheckHealth consulta sinalização de corrupção e ScanHealth faz uma verificação da imagem. Esses escopos não são intercambiáveis.",
        "Código zero confirma somente o término do comando no transporte; a saída original exige revisão e não é convertida em laudo de saúde.",
        "Até 256 KiB de stdout/stderr combinados são capturados. O limite não controla os logs próprios de CBS/DISM.",
        "Cancelamento encerra somente o Job Object próprio; serviços externos do Windows podem continuar trabalho que já tenham recebido.",
        "Não há repetição automática. Prévia válida por cinco minutos, de uso único; até dezesseis prévias e cem diretórios de execução retidos por raiz."
    });
    private readonly string _journalRoot = Path.GetFullPath(journalRoot);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Func<bool> _isElevated = isElevated ?? NativeElevation;
    private readonly Func<string> _systemDirectory = systemDirectory ?? NativeSystemDirectory;
    private readonly Dictionary<Guid, IssuedPlan> _plans = [];
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _planSync = new();

    public Task<Result<IntegrityPlan>> PlanAsync(IntegrityOperation operation, CancellationToken cancellationToken) =>
        Task.Run(() => PlanCore(operation, cancellationToken), cancellationToken);

    public Task<Result<IntegrityReceipt>> ExecuteAsync(IntegrityPlan plan, Guid confirmationId, CancellationToken cancellationToken) =>
        Task.Run(() => ExecuteCoreAsync(plan, confirmationId, cancellationToken), CancellationToken.None);

    private Result<IntegrityPlan> PlanCore(IntegrityOperation operation, CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            var command = Describe(operation);
            var directory = LocalPathGuard.LocalPath(_systemDirectory());
            var executable = ResolveExecutable(operation, command, directory);
            ValidateJournalLocation(directory);
            using var directoryLease = LocalPathGuard.HoldDirectory(directory);
            using var file = new FileStream(executable, FileMode.Open, FileAccess.Read, FileShare.Read);
            var identity = LocalPathGuard.Identity(file.SafeFileHandle);
            var hash = LocalPathGuard.HashStream(file);
            token.ThrowIfCancellationRequested();
            var now = _clock.GetUtcNow();
            var id = Guid.NewGuid();
            var limitations = Array.AsReadOnly(Coverage.Concat(new[]
            {
                $"Diário próprio selecionado: {Path.Combine(_journalRoot, "operation-" + id.ToString("N"))}. A prévia ainda não criou esse diretório.",
                $"Logs próprios do Windows podem ser atualizados sob {Path.Combine(Path.GetDirectoryName(directory)!, "Logs")}; eles não são removidos ou limitados pelo opTImus."
            }).ToArray());
            var plan = new IntegrityPlan(id, now, operation, command.DisplayName, executable, hash,
                Array.AsReadOnly(command.Arguments), directory, _isElevated(), command.Timeout, limitations, command.Encoding);
            lock (_planSync)
            {
                foreach (var existing in _plans.Where(pair => Expired(pair.Value.Plan)).ToArray()) _plans.Remove(existing.Key);
                while (_plans.Count >= MaximumRetainedPlans)
                    _plans.Remove(_plans.Values.OrderBy(item => item.Plan.CreatedAt).First().Plan.Id);
                _plans[plan.Id] = new(plan, identity, LocalPathGuard.Identity(directoryLease));
            }
            return Result<IntegrityPlan>.Success(plan);
        }
        catch (Exception error) when (Expected(error)) { return Result<IntegrityPlan>.Failure(Code(error), error.Message); }
    }

    private async Task<Result<IntegrityReceipt>> ExecuteCoreAsync(IntegrityPlan plan, Guid confirmationId, CancellationToken token)
    {
        IssuedPlan issued;
        lock (_planSync)
        {
            if (plan is null || confirmationId == Guid.Empty || plan.Id != confirmationId
                || !_plans.TryGetValue(plan.Id, out var retained) || !ReferenceEquals(plan, retained.Plan) || Expired(plan))
                return Result<IntegrityReceipt>.Failure(ErrorCode.InvalidConfiguration, "Confirmação inválida, prévia expirada ou plano não emitido por esta instância.");
            issued = retained;
            _plans.Remove(plan.Id);
        }

        bool acquired = false;
        bool invocationRequested = false;
        string? operationDirectory = null;
        IntegrityCommandReceipt? commandReceipt = null;
        DiagnosticEvidence? evidence = null;
        var state = IntegrityState.Prepared;
        try
        {
            await _gate.WaitAsync(token).ConfigureAwait(false);
            acquired = true;
            token.ThrowIfCancellationRequested();
            if (Expired(plan)) return Result<IntegrityReceipt>.Failure(ErrorCode.InvalidConfiguration, "A prévia expirou enquanto aguardava outra operação; nenhum comando foi emitido.");
            if (!plan.IsElevated || !_isElevated())
                return Result<IntegrityReceipt>.Failure(ErrorCode.PermissionDenied, "A verificação exige uma nova prévia nesta instância já executada como administrador. Nenhum comando foi emitido e não houve solicitação de elevação.");

            var descriptor = Describe(plan.Operation);
            var directory = LocalPathGuard.LocalPath(_systemDirectory());
            var executable = ResolveExecutable(plan.Operation, descriptor, directory);
            ValidateJournalLocation(directory);
            if (!string.Equals(directory, plan.WorkingDirectory, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(executable, plan.ExecutablePath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("A localização da ferramenta mudou desde a prévia; nenhum comando foi emitido.");
            using var systemLease = LocalPathGuard.HoldDirectory(directory);
            if (LocalPathGuard.Identity(systemLease) != issued.DirectoryIdentity)
                throw new InvalidDataException("A identidade do diretório do sistema mudou desde a prévia.");
            using var executableLease = new FileStream(executable, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (LocalPathGuard.Identity(executableLease.SafeFileHandle) != issued.ExecutableIdentity
                || !LocalPathGuard.HashStream(executableLease).Equals(plan.ExecutableSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("A identidade ou o conteúdo da ferramenta mudou desde a prévia; nenhum comando foi emitido.");
            token.ThrowIfCancellationRequested();

            Directory.CreateDirectory(_journalRoot);
            using var rootLease = LocalPathGuard.HoldDirectory(_journalRoot);
            var reserved = IntegrityJournal.Reserve(_journalRoot, plan.Id);
            if (!reserved.IsSuccess) return Result<IntegrityReceipt>.Failure(reserved.Error!.Code, reserved.Error.Message);
            operationDirectory = reserved.Value!;
            using var operationLease = LocalPathGuard.HoldDirectory(operationDirectory);
            await IntegrityJournal.WriteAsync(operationDirectory, "01-prepared.json", new
            {
                kind = "optimus.integrity-operation",
                version = 1,
                createdAt = _clock.GetUtcNow(),
                plan,
                state = IntegrityState.Prepared,
                repairRequested = false
            }).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (Expired(plan) || !_isElevated()) throw new InvalidDataException("A validade da prévia ou a elevação mudou antes da chamada; nenhum comando foi emitido.");
            await IntegrityJournal.WriteAsync(operationDirectory, "02-before-command.json", new
            {
                kind = "optimus.integrity-stage",
                version = 1,
                operationId = plan.Id,
                recordedAt = _clock.GetUtcNow(),
                stage = "before-command",
                repairRequested = false
            }).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (Expired(plan) || !_isElevated()) throw new InvalidDataException("A validade da prévia ou a elevação mudou durante a gravação anterior à chamada; nenhum comando foi emitido.");
            invocationRequested = true;
            var result = await runner.RunAsync(new ToolRequest(executable, descriptor.Arguments, directory,
                descriptor.Timeout, MaximumOutputBytes,
                EnvironmentOverrides: SystemEnvironment(directory), ExpectedExecutableSha256: plan.ExecutableSha256,
                OutputEncoding: descriptor.Encoding, PreserveOutputBytes: true), token).ConfigureAwait(false);
            commandReceipt = ToReceipt(result, descriptor.Encoding);
            bool rawAvailable = result.StandardOutputBytes is not null && result.StandardErrorBytes is not null;
            state = result.ExitedSuccessfully && rawAvailable ? IntegrityState.CommandCompleted
                : result.Status == ToolRunStatus.Cancelled ? IntegrityState.Cancelled
                : result.Status == ToolRunStatus.Exited && result.ExitCode is not null && result.Error is null && rawAvailable
                    ? IntegrityState.CommandReportedFailure : IntegrityState.OutcomeUnknown;
            var receipt = Receipt();
            await IntegrityJournal.WriteAsync(operationDirectory, "03-result.json", receipt).ConfigureAwait(false);
            if (token.IsCancellationRequested)
                return Result<IntegrityReceipt>.FailureWithValue(receipt, ErrorCode.Cancelled,
                    "Cancelamento recebido na execução/finalização. Os fatos observados e o recibo foram preservados; não há repetição automática.", result.Error?.PendingCleanupPaths);
            if (state == IntegrityState.CommandCompleted) return Result<IntegrityReceipt>.Success(receipt);
            var reason = result.Error?.Message ?? (!rawAvailable
                ? "O executor não entregou os bytes de saída solicitados; o transporte não foi confirmado."
                : "O comando não confirmou conclusão com código zero. Consulte a saída original sem presumir saúde ou reparo.");
            return Result<IntegrityReceipt>.FailureWithValue(receipt,
                result.ExitedSuccessfully && !rawAvailable ? ErrorCode.InvalidResponse : FailureCode(result), reason, result.Error?.PendingCleanupPaths);
        }
        catch (OperationCanceledException error) when (token.IsCancellationRequested)
        {
            state = invocationRequested ? IntegrityState.OutcomeUnknown : IntegrityState.Cancelled;
            if (operationDirectory is null) return Result<IntegrityReceipt>.Failure(ErrorCode.Cancelled, error.Message);
            return await PreserveFailure(ErrorCode.Cancelled, error.Message, PendingFrom(error)).ConfigureAwait(false);
        }
        catch (Exception error) when (Expected(error))
        {
            if (invocationRequested && state == IntegrityState.Prepared) state = IntegrityState.OutcomeUnknown;
            if (operationDirectory is null) return Result<IntegrityReceipt>.Failure(Code(error), error.Message);
            return await PreserveFailure(Code(error), error.Message, PendingFrom(error)).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            if (invocationRequested && state == IntegrityState.Prepared) state = IntegrityState.OutcomeUnknown;
            var reason = $"Falha inesperada na verificação ({error.GetType().Name}): {error.Message}";
            if (operationDirectory is null) return Result<IntegrityReceipt>.Failure(ErrorCode.ToolFailed, reason, PendingFrom(error));
            return await PreserveFailure(ErrorCode.ToolFailed, reason, PendingFrom(error)).ConfigureAwait(false);
        }
        finally { if (acquired) _gate.Release(); }

        IntegrityReceipt Receipt() => new(plan.Id, plan.Operation, state, false, operationDirectory!,
            evidence ??= Evidence(plan, state, commandReceipt, invocationRequested), commandReceipt, plan.Limitations,
            state == IntegrityState.CommandCompleted ? "Revise a saída original. Código zero não é um laudo de ausência de corrupção."
                : "Preserve este diário e os logs próprios do Windows antes de decidir repetir. Serviços externos podem continuar após cancelamento do processo próprio.");

        async Task<Result<IntegrityReceipt>> PreserveFailure(ErrorCode code, string reason, IReadOnlyList<string>? pending)
        {
            var receipt = Receipt();
            try
            {
                await IntegrityJournal.WriteAsync(operationDirectory!, "04-interrupted.json", new { receipt, failure = reason }).ConfigureAwait(false);
                return Result<IntegrityReceipt>.FailureWithValue(receipt, code, reason, pending);
            }
            catch (Exception journalError) when (Expected(journalError))
            {
                return Result<IntegrityReceipt>.FailureWithValue(receipt, code,
                    $"{reason} O registro final adicional falhou: {journalError.Message}. Preserve {operationDirectory}; nenhuma repetição foi feita.",
                    [.. pending ?? [], .. PendingFrom(journalError) ?? [], operationDirectory!]);
            }
        }
    }

    private DiagnosticEvidence Evidence(IntegrityPlan plan, IntegrityState state, IntegrityCommandReceipt? command, bool invocationRequested)
    {
        List<DiagnosticFact> facts = [new("operation", "Verificação selecionada", plan.Operation.ToString()),
            new("executable", "Executável selecionado", plan.ExecutablePath),
            new("executableSha256", "SHA-256 vinculado à chamada", plan.ExecutableSha256),
            new("arguments", "Argumentos fixos da verificação", JsonSerializer.Serialize(plan.Arguments)),
            new("invocationRequested", "Chamada ao executor solicitada", invocationRequested ? "true" : "false"),
            new("commandState", "Estado observado do comando", state.ToString()),
            new("repairRequested", "Opção de reparo solicitada", "false"),
            new("healthConclusionMade", "Conclusão de saúde inferida automaticamente", "false")];
        if (command is not null)
        {
            facts.AddRange([new("exitCode", "Código de saída original", NumberValue: command.ExitCode),
                new("outputEncodingRequested", "Codificação de saída solicitada", command.RequestedEncoding),
                new("outputCodePage", "Página de código usada na decodificação", NumberValue: command.OutputCodePage),
                new("stdout", "Saída padrão original decodificada", command.StandardOutput, Detailed: true),
                new("stderr", "Saída de erro original decodificada", command.StandardError, Detailed: true),
                new("stdoutBytes", "Bytes originais stdout (base64)", command.StandardOutputBase64, Detailed: true),
                new("stderrBytes", "Bytes originais stderr (base64)", command.StandardErrorBase64, Detailed: true)]);
        }
        IReadOnlyList<CollectionIssue> issues = state == IntegrityState.CommandCompleted ? []
            : [new(IssueCode.ReadFailed, command?.FailureReason ?? "A execução não confirmou conclusão; os dados disponíveis não foram interpretados como saúde ou reparo.")];
        return new(Guid.NewGuid(), MaintenanceCollectors.Integrity, plan.DisplayName, "Windows em execução", _clock.GetUtcNow(),
            command is null ? EvidenceStatus.Unavailable : state == IntegrityState.CommandCompleted ? EvidenceStatus.Complete : EvidenceStatus.Partial,
            new("Execução de verificação sem opções de reparo; código, texto e bytes originais retidos. Conclusão de comando não equivale a integridade confirmada", command is null ? 0 : 1), facts, issues);
    }

    private string ResolveExecutable(IntegrityOperation operation, Descriptor descriptor, string directory)
    {
        var path = LocalPathGuard.LocalPath(executablePath?.Invoke(operation) ?? Path.Combine(directory, descriptor.FileName));
        if (!string.Equals(Path.GetDirectoryName(path), directory, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetFileName(path), descriptor.FileName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("A ferramenta deve ser a operação nomeada no diretório do sistema selecionado; argumentos/caminhos livres não são aceitos.");
        if (!File.Exists(path)) throw new FileNotFoundException("A ferramenta do Windows não foi encontrada.", path);
        return path;
    }

    private void ValidateJournalLocation(string systemDirectory)
    {
        _ = LocalPathGuard.LocalPath(_journalRoot);
        if (LocalPathGuard.Related(_journalRoot, systemDirectory))
            throw new InvalidDataException("O diário próprio precisa ficar separado da pasta do sistema.");
    }
    private bool Expired(IntegrityPlan plan) => _clock.GetUtcNow() < plan.CreatedAt || _clock.GetUtcNow() - plan.CreatedAt >= Lifetime;
    private static Descriptor Describe(IntegrityOperation operation) => operation switch
    {
        IntegrityOperation.SfcVerifyOnly => new("SFC · verificar arquivos protegidos", "sfc.exe", ["/verifyonly"], TimeSpan.FromMinutes(15), ToolTextEncoding.Utf16LittleEndian),
        IntegrityOperation.DismCheckHealth => new("DISM · consultar sinalização de corrupção", "dism.exe", ["/Online", "/Cleanup-Image", "/CheckHealth", "/English"], TimeSpan.FromMinutes(2), ToolTextEncoding.WindowsOem),
        IntegrityOperation.DismScanHealth => new("DISM · verificar imagem do Windows", "dism.exe", ["/Online", "/Cleanup-Image", "/ScanHealth", "/English"], TimeSpan.FromMinutes(30), ToolTextEncoding.WindowsOem),
        _ => throw new ArgumentException("Verificação não reconhecida; nenhuma ferramenta foi selecionada.")
    };
    private static IReadOnlyDictionary<string, string> SystemEnvironment(string directory)
    {
        var windows = Path.GetDirectoryName(directory)!;
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["PATH"] = directory + ";" + windows,
            ["SystemRoot"] = windows,
            ["windir"] = windows,
            ["TEMP"] = Path.Combine(windows, "Temp"),
            ["TMP"] = Path.Combine(windows, "Temp")
        };
    }
    private static IntegrityCommandReceipt ToReceipt(ToolRunResult result, ToolTextEncoding encoding) =>
        new(result.Status.ToString(), result.ExitCode, result.StandardOutput, result.StandardError, encoding.ToString(), result.OutputCodePage,
            result.StandardOutputBytes is { } stdout ? Convert.ToBase64String(stdout) : null,
            result.StandardErrorBytes is { } stderr ? Convert.ToBase64String(stderr) : null,
            result.Metrics.Elapsed.TotalSeconds, result.Metrics.CpuTime?.TotalSeconds,
            result.Metrics.PeakProcessFamilyMemoryBytes, result.Error?.Message, result.Error?.PendingCleanupPaths);
    private static ErrorCode FailureCode(ToolRunResult result) => result.Status switch
    {
        ToolRunStatus.Cancelled => ErrorCode.Cancelled,
        ToolRunStatus.TimedOut => ErrorCode.Timeout,
        ToolRunStatus.OutputLimitExceeded => ErrorCode.OutputLimitExceeded,
        _ => result.Error?.Code ?? ErrorCode.ToolFailed
    };
    private static bool NativeElevation()
    {
        if (!OperatingSystem.IsWindows()) return false;
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
    private static string NativeSystemDirectory()
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitOperatingSystem || !Environment.Is64BitProcess)
            throw new PlatformNotSupportedException("A verificação requer Windows e processo x64; nenhuma redireção para SysWOW64 é feita.");
        return Environment.GetFolderPath(Environment.SpecialFolder.System);
    }
    private static bool Expected(Exception error) => error is IOException or InvalidDataException or UnauthorizedAccessException
        or System.Security.SecurityException or ArgumentException or NotSupportedException or System.ComponentModel.Win32Exception;
    private static IReadOnlyList<string>? PendingFrom(Exception error) => error is IntegrityJournalException journal
        ? journal.PendingCleanupPaths : error.Data["PendingCleanupPaths"] as IReadOnlyList<string>;
    private static ErrorCode Code(Exception error) => error is UnauthorizedAccessException or System.Security.SecurityException
        ? ErrorCode.PermissionDenied : error is FileNotFoundException ? ErrorCode.DependencyUnavailable
        : error is InvalidDataException or ArgumentException or NotSupportedException ? ErrorCode.InvalidConfiguration : ErrorCode.StorageFailure;
    private sealed record Descriptor(string DisplayName, string FileName, string[] Arguments, TimeSpan Timeout, ToolTextEncoding Encoding);
    private sealed record IssuedPlan(IntegrityPlan Plan, PathIdentity ExecutableIdentity, PathIdentity DirectoryIdentity);
}
