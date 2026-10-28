using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Optimus.Application;
using Optimus.Domain;
using Optimus.Infrastructure.Files;

namespace Optimus.Infrastructure.Backup;

public sealed class ResticBackupService(IExternalToolRunner runner, string journalRoot, TimeProvider? clock = null) : IBackupService
{
    private const int MaximumOutputBytes = 2 * 1024 * 1024;
    private static readonly TimeSpan StageTimeout = TimeSpan.FromMinutes(10);
    private static readonly JsonSerializerOptions JournalJson = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };
    private static readonly string[] Coverage = [
        "Backup de uma pasta selecionada para repositório local novo; não é imagem do Windows.",
        "VSS não está habilitado: arquivos abertos ou alterados durante a leitura podem não formar uma cópia consistente.",
        "ACLs, EFS, ADS, placeholders de nuvem e recuperação de sistema não foram homologados neste fluxo.",
        "A localização em disco físico diferente não foi verificada; cópia no mesmo disco não protege contra sua falha.",
        "Limite de dez minutos por etapa e saída combinada de 2 MiB. Nenhuma repetição, remoção de lock ou prune é automática.",
        "A prévia vale por quinze minutos e é de uso único; no máximo dezesseis prévias recentes de cada operação são retidas.",
        "Integridade do repositório e restauração são verificações distintas; preserve o arquivo de senha fora do computador."
    ];
    private readonly string _journalRoot = Path.GetFullPath(journalRoot);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly ConcurrentDictionary<Guid, IssuedPlan> _backupPlans = new();
    private readonly ConcurrentDictionary<Guid, IssuedPlan> _restorePlans = new();
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly object _planSync = new();

    public Task<Result<BackupPlan>> PlanAsync(BackupSettings settings, CancellationToken cancellationToken) =>
        Task.Run(() => PlanCore(settings, cancellationToken), cancellationToken);

    public Task<Result<RestorePlan>> PlanRestoreAsync(BackupSettings settings, string snapshotId, string destinationDirectory, CancellationToken cancellationToken) =>
        Task.Run(() => PlanRestoreCore(settings, snapshotId, destinationDirectory, cancellationToken), cancellationToken);

    public Task<Result<BackupReceipt>> ExecuteAsync(BackupPlan plan, Guid confirmationId, CancellationToken cancellationToken) =>
        Task.Run(() => ExecuteCoreAsync(plan, confirmationId, cancellationToken), CancellationToken.None);

    public Task<Result<RestoreReceipt>> RestoreAsync(RestorePlan plan, Guid confirmationId, CancellationToken cancellationToken) =>
        Task.Run(() => RestoreCoreAsync(plan, confirmationId, cancellationToken), CancellationToken.None);

    private Result<BackupPlan> PlanCore(BackupSettings settings, CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            ValidateJournalRoot();
            var paths = Resolve(settings, requireSource: true);
            BackupPathGuard.RequireSeparated(paths.Source, paths.Repository, paths.Password, _journalRoot);
            DestinationReservation.RequireAvailable(_journalRoot, paths.Repository);
            BackupPathGuard.RequireEmptyDirectory(paths.Repository);
            var identities = Capture(paths, token);
            var plan = new BackupPlan(Guid.NewGuid(), _clock.GetUtcNow(), settings, paths.Source,
                paths.Repository, paths.Executable, paths.Password, identities.ExecutableHash, Array.AsReadOnly(Coverage));
            Register(_backupPlans, plan.Id, new(Fingerprint(plan), identities, null, _clock.GetUtcNow().AddMinutes(15)));
            return Result<BackupPlan>.Success(plan);
        }
        catch (Exception error) when (Expected(error)) { return Result<BackupPlan>.Failure(Code(error), error.Message); }
    }

    private Result<RestorePlan> PlanRestoreCore(BackupSettings settings, string snapshotId, string destination, CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            ValidateJournalRoot();
            if (!HexId(snapshotId)) throw new InvalidDataException("Use o ID completo do snapshot (64 caracteres hexadecimais), sem aliases como latest.");
            var paths = Resolve(settings, requireSource: false);
            if (!Directory.Exists(paths.Repository) || !File.Exists(Path.Combine(paths.Repository, "config")))
                throw new InvalidDataException("O repositório selecionado não contém configuração restic; nenhum destino foi criado.");
            if (LocalPathGuard.Related(paths.Repository, _journalRoot))
                throw new InvalidDataException("O diário operacional deve ficar separado do repositório de restauração.");
            var target = LocalPathGuard.LocalPath(destination);
            DestinationReservation.RequireAvailable(_journalRoot, target);
            if (Directory.Exists(target) || File.Exists(target)) throw new InvalidDataException("A restauração exige um destino que ainda não existe.");
            if (LocalPathGuard.Related(target, paths.Repository) || LocalPathGuard.Related(target, _journalRoot)
                || LocalPathGuard.IsWithin(paths.Password, target) || LocalPathGuard.IsWithin(paths.Executable, target))
                throw new InvalidDataException("O destino da restauração precisa ser separado do repositório, diário, ferramenta e arquivo de senha.");
            var parent = Path.GetDirectoryName(target)!;
            if (!Directory.Exists(parent)) throw new InvalidDataException("A pasta pai do destino de restauração deve existir.");
            var identities = Capture(paths, token, requireSource: false);
            var plan = new RestorePlan(Guid.NewGuid(), _clock.GetUtcNow(), settings, snapshotId,
                paths.Repository, target, paths.Executable, paths.Password, identities.ExecutableHash, Array.AsReadOnly(Coverage));
            Register(_restorePlans, plan.Id, new(Fingerprint(plan), identities, LocalPathGuard.DirectoryIdentity(parent), _clock.GetUtcNow().AddMinutes(15)));
            return Result<RestorePlan>.Success(plan);
        }
        catch (Exception error) when (Expected(error)) { return Result<RestorePlan>.Failure(Code(error), error.Message); }
    }

    private async Task<Result<BackupReceipt>> ExecuteCoreAsync(BackupPlan plan, Guid confirmationId, CancellationToken token)
    {
        if (plan is null || confirmationId == Guid.Empty || plan.Id != confirmationId
            || !_backupPlans.TryGetValue(plan.Id, out var issued) || issued.ExpiresAt <= _clock.GetUtcNow() || issued.Fingerprint != Fingerprint(plan))
            return Result<BackupReceipt>.Failure(ErrorCode.InvalidConfiguration, "Confirmação inválida ou plano não emitido por esta instância. Prepare uma nova prévia.");
        if (!_backupPlans.TryRemove(plan.Id, out _)) return Result<BackupReceipt>.Failure(ErrorCode.InvalidConfiguration, "Este plano já foi utilizado; não haverá repetição automática.");

        using var operation = new OperationJournal(_journalRoot, plan.Id);
        var commands = new List<BackupCommandReceipt>();
        var state = BackupState.Prepared;
        string? snapshot = null;
        bool gate = false;
        bool effectRequested = false;
        bool integrityVerified = false;
        bool journalValidated = false;
        BackupReceipt Receipt(string? recovery = null) => new(plan.Id, state, plan.SourceDirectory, plan.RepositoryDirectory,
            snapshot, integrityVerified, false, operation.DirectoryPath, commands.ToArray(), plan.Limitations, recovery);
        try
        {
            await _operationGate.WaitAsync(token).ConfigureAwait(false); gate = true;
            if (plan.Id != confirmationId || issued.Fingerprint != Fingerprint(plan) || issued.ExpiresAt <= _clock.GetUtcNow())
                throw new InvalidDataException("O plano expirou ou mudou enquanto aguardava a operação anterior. Prepare uma nova prévia.");
            ValidateJournalRoot(); journalValidated = true;
            var paths = Resolve(plan.Settings, requireSource: true);
            BackupPathGuard.RequireSeparated(paths.Source, paths.Repository, paths.Password, _journalRoot);
            DestinationReservation.RequireAvailable(_journalRoot, paths.Repository);
            BackupPathGuard.RequireEmptyDirectory(paths.Repository);
            using var source = LocalPathGuard.HoldDirectory(paths.Source);
            using var parent = LocalPathGuard.HoldDirectory(Path.GetDirectoryName(paths.Repository)!);
            using var password = OpenPassword(paths.Password);
            using var executable = new FileStream(paths.Executable, FileMode.Open, FileAccess.Read, FileShare.Read);
            Revalidate(paths, issued.Identities, source, parent, password, executable);
            using var existingRepository = issued.Identities.Repository is not null ? LocalPathGuard.HoldDirectory(paths.Repository) : null;
            if (existingRepository is not null && LocalPathGuard.Identity(existingRepository) != issued.Identities.Repository)
                throw new InvalidDataException("A identidade da pasta de repositório mudou desde a prévia.");
            await operation.WriteAsync("prepared", plan, Receipt()).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();

            await operation.WriteAsync("before-initialize", plan, Receipt()).ConfigureAwait(false);
            using var reservation = DestinationReservation.Claim(_journalRoot, paths.Repository, "backup", plan.Id, operation.DirectoryPath);
            token.ThrowIfCancellationRequested();
            if (issued.Identities.Repository is null) { LocalPathGuard.CreateNewDirectory(paths.Repository); effectRequested = true; }
            using var repository = LocalPathGuard.HoldDirectory(paths.Repository);
            BackupPathGuard.RequireEmptyDirectory(paths.Repository);
            token.ThrowIfCancellationRequested();
            effectRequested = true;
            var initialized = await InvokeAsync("initialize", paths, issued.Identities.ExecutableHash, ["init"], commands, token).ConfigureAwait(false);
            await operation.WriteAsync("after-initialize", plan, Receipt()).ConfigureAwait(false);
            if (!initialized.ExitedSuccessfully) return await BackupFailure(initialized, "A inicialização não foi confirmada.").ConfigureAwait(false);
            state = BackupState.RepositoryInitialized;

            await operation.WriteAsync("before-repository-readback", plan, Receipt()).ConfigureAwait(false);
            var readback = await InvokeAsync("repository-readback", paths, issued.Identities.ExecutableHash, ["cat", "config"], commands, token).ConfigureAwait(false);
            if (!readback.ExitedSuccessfully || !ValidRepositoryConfig(readback.StandardOutput))
                return await BackupFailure(readback, "A leitura da configuração do novo repositório não foi confirmada.", ErrorCode.InvalidResponse).ConfigureAwait(false);

            await operation.WriteAsync("before-backup", plan, Receipt()).ConfigureAwait(false);
            var backedUp = await InvokeAsync("backup", paths, issued.Identities.ExecutableHash, ["backup", "--", paths.Source], commands, token).ConfigureAwait(false);
            var parsed = ParseSnapshot(backedUp.StandardOutput);
            snapshot = parsed.SnapshotId;
            if (snapshot is not null) state = BackupState.SnapshotCreated;
            await operation.WriteAsync("after-backup", plan, Receipt()).ConfigureAwait(false);
            if (!backedUp.ExitedSuccessfully || parsed.Failure is not null)
            {
                if (backedUp.Status == ToolRunStatus.Exited && backedUp.ExitCode == 3) state = BackupState.Partial;
                return await BackupFailure(backedUp, parsed.Failure ?? "O backup não confirmou a leitura completa dos arquivos; um snapshot pode ter sido criado.",
                    parsed.Failure is null ? ErrorCode.ToolFailed : ErrorCode.InvalidResponse).ConfigureAwait(false);
            }

            await operation.WriteAsync("before-integrity-check", plan, Receipt()).ConfigureAwait(false);
            var checkedData = await InvokeAsync("integrity-check", paths, issued.Identities.ExecutableHash, ["check", "--read-data"], commands, token).ConfigureAwait(false);
            if (!checkedData.ExitedSuccessfully) return await BackupFailure(checkedData, "O snapshot existe, mas a verificação integral de dados não foi confirmada.", ErrorCode.VerificationFailed).ConfigureAwait(false);
            state = BackupState.IntegrityVerified;
            integrityVerified = true;
            var receipt = Receipt();
            await operation.WriteAsync("completed", plan, receipt).ConfigureAwait(false);
            if (token.IsCancellationRequested)
            {
                const string message = "Cancelamento observado durante a finalização. A integridade já foi verificada; não repita o backup automaticamente.";
                await operation.WriteAsync("completed-cancelled", plan, receipt, message).ConfigureAwait(false);
                return Result<BackupReceipt>.FailureWithValue(receipt, ErrorCode.Cancelled, message);
            }
            return Result<BackupReceipt>.Success(receipt);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            state = effectRequested ? BackupState.OutcomeUnknown : BackupState.Cancelled;
            return await PersistFailure(ErrorCode.Cancelled, "Operação cancelada. Efeitos já solicitados precisam ser reconciliados antes de qualquer nova execução.").ConfigureAwait(false);
        }
        catch (Exception error) when (Expected(error))
        {
            state = effectRequested ? BackupState.OutcomeUnknown : BackupState.Failed;
            return await PersistFailure(Code(error), error.Message).ConfigureAwait(false);
        }
        finally { if (gate) _operationGate.Release(); }

        async Task<Result<BackupReceipt>> BackupFailure(ToolRunResult result, string message, ErrorCode defaultCode = ErrorCode.ToolFailed)
        {
            if (result.Status != ToolRunStatus.Exited) state = BackupState.OutcomeUnknown;
            else if (state is not (BackupState.Partial or BackupState.SnapshotCreated)) state = BackupState.Failed;
            return await PersistFailure(ToolCode(result, defaultCode), message + " " + result.Error?.Message).ConfigureAwait(false);
        }
        async Task<Result<BackupReceipt>> PersistFailure(ErrorCode code, string message)
        {
            var receipt = Receipt("Preserve o repositório e este diário. Consulte snapshots/check antes de decidir outra operação; não repita init/backup nem remova locks automaticamente.");
            if (!journalValidated) return Result<BackupReceipt>.FailureWithValue(receipt, code, message + " Nenhum diário foi escrito em uma raiz não validada.");
            try { await operation.WriteAsync("stopped", plan, receipt, message).ConfigureAwait(false); }
            catch (Exception journalError) when (Expected(journalError))
            {
                return Result<BackupReceipt>.FailureWithValue(receipt, code, message + " Diário final não confirmado: " + journalError.Message,
                    commands.SelectMany(item => item.PendingCleanupPaths ?? []).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
            }
            return Result<BackupReceipt>.FailureWithValue(receipt, code, message,
                commands.SelectMany(item => item.PendingCleanupPaths ?? []).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
        }
    }

    private async Task<Result<RestoreReceipt>> RestoreCoreAsync(RestorePlan plan, Guid confirmationId, CancellationToken token)
    {
        if (plan is null || plan.Id != confirmationId || confirmationId == Guid.Empty
            || !_restorePlans.TryGetValue(plan.Id, out var issued) || issued.ExpiresAt <= _clock.GetUtcNow() || issued.Fingerprint != Fingerprint(plan))
            return Result<RestoreReceipt>.Failure(ErrorCode.InvalidConfiguration, "Confirmação inválida ou plano de restauração não emitido por esta instância.");
        if (!_restorePlans.TryRemove(plan.Id, out _)) return Result<RestoreReceipt>.Failure(ErrorCode.InvalidConfiguration, "Plano de restauração já utilizado; prepare outra prévia.");
        using var operation = new OperationJournal(_journalRoot, plan.Id);
        var commands = new List<BackupCommandReceipt>();
        var state = RestoreState.Prepared;
        bool gate = false;
        bool effectRequested = false;
        bool restorationVerified = false;
        bool journalValidated = false;
        RestoreReceipt Receipt(string? recovery = null) => new(plan.Id, state, plan.SnapshotId, plan.DestinationDirectory,
            operation.DirectoryPath, commands.ToArray(), plan.Limitations, recovery, restorationVerified);
        try
        {
            await _operationGate.WaitAsync(token).ConfigureAwait(false); gate = true;
            if (plan.Id != confirmationId || issued.Fingerprint != Fingerprint(plan) || issued.ExpiresAt <= _clock.GetUtcNow())
                throw new InvalidDataException("O plano de restauração expirou ou mudou enquanto aguardava. Prepare uma nova prévia.");
            ValidateJournalRoot(); journalValidated = true;
            var paths = Resolve(plan.Settings, requireSource: false);
            DestinationReservation.RequireAvailable(_journalRoot, plan.DestinationDirectory);
            if (Directory.Exists(plan.DestinationDirectory) || File.Exists(plan.DestinationDirectory)) throw new InvalidDataException("O destino passou a existir; não será sobrescrito.");
            LocalPathGuard.LocalPath(plan.DestinationDirectory);
            using var parent = LocalPathGuard.HoldDirectory(Path.GetDirectoryName(plan.DestinationDirectory)!);
            using var repository = LocalPathGuard.HoldDirectory(paths.Repository);
            using var password = OpenPassword(paths.Password);
            using var executable = new FileStream(paths.Executable, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (LocalPathGuard.Identity(parent) != issued.RestoreParent || LocalPathGuard.Identity(repository) != issued.Identities.Repository)
                throw new InvalidDataException("A identidade do repositório ou da pasta pai do destino mudou desde a prévia.");
            RevalidateFiles(issued.Identities, password, executable);
            await operation.WriteAsync("before-restore", plan, Receipt()).ConfigureAwait(false);
            using var reservation = DestinationReservation.Claim(_journalRoot, plan.DestinationDirectory, "restore", plan.Id, operation.DirectoryPath);
            token.ThrowIfCancellationRequested();
            effectRequested = true;
            LocalPathGuard.CreateNewDirectory(plan.DestinationDirectory);
            using var target = LocalPathGuard.HoldDirectory(plan.DestinationDirectory);
            var restored = await InvokeAsync("restore-and-verify", paths, issued.Identities.ExecutableHash,
                ["restore", plan.SnapshotId, "--target", plan.DestinationDirectory, "--overwrite", "never", "--verify"], commands, token).ConfigureAwait(false);
            if (!restored.ExitedSuccessfully)
            {
                state = restored.Status == ToolRunStatus.Exited ? RestoreState.Failed : RestoreState.OutcomeUnknown;
                return await Failure(ToolCode(restored), "Restauração/verificação não confirmada. " + restored.Error?.Message).ConfigureAwait(false);
            }
            state = RestoreState.RestoredAndVerified;
            restorationVerified = true;
            var receipt = Receipt();
            await operation.WriteAsync("completed", plan, receipt).ConfigureAwait(false);
            if (token.IsCancellationRequested)
            {
                const string message = "Cancelamento observado durante a finalização. A restauração já foi verificada; preserve o destino e não repita automaticamente.";
                await operation.WriteAsync("completed-cancelled", plan, receipt, message).ConfigureAwait(false);
                return Result<RestoreReceipt>.FailureWithValue(receipt, ErrorCode.Cancelled, message);
            }
            return Result<RestoreReceipt>.Success(receipt);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            state = effectRequested ? RestoreState.OutcomeUnknown : RestoreState.Cancelled;
            return await Failure(ErrorCode.Cancelled, "Restauração cancelada; preserve os arquivos já produzidos para reconciliar o resultado.").ConfigureAwait(false);
        }
        catch (Exception error) when (Expected(error))
        {
            state = effectRequested ? RestoreState.OutcomeUnknown : RestoreState.Failed;
            return await Failure(Code(error), error.Message).ConfigureAwait(false);
        }
        finally { if (gate) _operationGate.Release(); }

        async Task<Result<RestoreReceipt>> Failure(ErrorCode code, string message)
        {
            var receipt = Receipt("Preserve o destino e o diário. Não repita a restauração sobre os arquivos já criados; selecione um novo destino após reconciliar o resultado.");
            if (!journalValidated) return Result<RestoreReceipt>.FailureWithValue(receipt, code, message + " Nenhum diário foi escrito em uma raiz não validada.");
            try { await operation.WriteAsync("stopped", plan, receipt, message).ConfigureAwait(false); }
            catch (Exception journalError) when (Expected(journalError))
            {
                return Result<RestoreReceipt>.FailureWithValue(receipt, code, message + " Diário final não confirmado: " + journalError.Message,
                    commands.SelectMany(item => item.PendingCleanupPaths ?? []).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
            }
            return Result<RestoreReceipt>.FailureWithValue(receipt, code, message,
                commands.SelectMany(item => item.PendingCleanupPaths ?? []).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
        }
    }

    private async Task<ToolRunResult> InvokeAsync(string stage, ResolvedPaths paths, string executableHash, IReadOnlyList<string> command, List<BackupCommandReceipt> receipts, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var args = new List<string> { "--repo", paths.Repository, "--password-file", paths.Password, "--no-cache", "--json", "--quiet", "--retry-lock", "0s" };
        args.AddRange(command);
        // Only names are selected; ambient values are neither persisted nor passed as overrides.
        var removeNames = Environment.GetEnvironmentVariables().Keys.Cast<string>()
            .Where(name => name.StartsWith("RESTIC_", StringComparison.OrdinalIgnoreCase))
            .Concat(["RESTIC_PASSWORD", "RESTIC_PASSWORD_COMMAND", "RESTIC_PASSWORD_FILE", "RESTIC_REPOSITORY", "RESTIC_REPOSITORY_FILE", "RESTIC_KEY_HINT", "RESTIC_CACHE_DIR"])
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var result = await runner.RunAsync(new(paths.Executable, args, Path.GetDirectoryName(paths.Repository)!, StageTimeout,
            MaximumOutputBytes, ExpectedExecutableSha256: executableHash, MaximumMemoryBytes: 1_073_741_824,
            EnvironmentVariablesToRemove: removeNames), token).ConfigureAwait(false);
        receipts.Add(new(stage, result.Status.ToString(), result.ExitCode, result.StandardOutput, result.StandardError,
            result.Metrics.Elapsed.TotalSeconds, result.Metrics.CpuTime?.TotalSeconds,
            result.Metrics.PeakProcessFamilyMemoryBytes, result.Error?.Message, result.Error?.PendingCleanupPaths));
        return result;
    }

    private static ResolvedPaths Resolve(BackupSettings settings, bool requireSource)
    {
        if (settings is null) throw new InvalidDataException("Configuração de backup ausente.");
        var paths = new ResolvedPaths(LocalPathGuard.LocalPath(settings.ExecutablePath),
            requireSource ? LocalPathGuard.LocalPath(settings.SourceDirectory) : settings.SourceDirectory,
            LocalPathGuard.LocalPath(settings.RepositoryDirectory),
            LocalPathGuard.LocalPath(settings.PasswordFilePath));
        if (!File.Exists(paths.Executable)) throw new FileNotFoundException("Executável restic selecionado não foi encontrado.");
        if (!File.Exists(paths.Password)) throw new FileNotFoundException("Arquivo de senha selecionado não foi encontrado.");
        if (requireSource && !Directory.Exists(paths.Source)) throw new DirectoryNotFoundException("A pasta de origem selecionada não existe.");
        if (settings.ExpectedExecutableSha256 is not null
            && (!HexId(settings.ExpectedExecutableSha256) || !string.Equals(LocalPathGuard.HashFile(paths.Executable), settings.ExpectedExecutableSha256, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("O digest do executável não corresponde à configuração selecionada.");
        return paths;
    }

    private static CapturedIdentities Capture(ResolvedPaths paths, CancellationToken token, bool requireSource = true)
    {
        token.ThrowIfCancellationRequested();
        using var password = OpenPassword(paths.Password);
        using var executable = new FileStream(paths.Executable, FileMode.Open, FileAccess.Read, FileShare.Read);
        return new(requireSource ? LocalPathGuard.DirectoryIdentity(paths.Source) : null,
            LocalPathGuard.DirectoryIdentity(Path.GetDirectoryName(paths.Repository)!),
            Directory.Exists(paths.Repository) ? LocalPathGuard.DirectoryIdentity(paths.Repository) : null,
            LocalPathGuard.Identity(password.SafeFileHandle), LocalPathGuard.HashStream(password),
            LocalPathGuard.Identity(executable.SafeFileHandle), LocalPathGuard.HashStream(executable));
    }

    private static void Revalidate(ResolvedPaths paths, CapturedIdentities identities,
        Microsoft.Win32.SafeHandles.SafeFileHandle source, Microsoft.Win32.SafeHandles.SafeFileHandle parent,
        FileStream password, FileStream executable)
    {
        if (LocalPathGuard.Identity(source) != identities.Source || LocalPathGuard.Identity(parent) != identities.RepositoryParent)
            throw new InvalidDataException("A identidade da origem ou da pasta pai do repositório mudou desde a prévia.");
        if (identities.Repository is null && Directory.Exists(paths.Repository)) throw new InvalidDataException("O destino passou a existir; prepare outra prévia.");
        RevalidateFiles(identities, password, executable);
    }

    private static void RevalidateFiles(CapturedIdentities identities, FileStream password, FileStream executable)
    {
        if (LocalPathGuard.Identity(password.SafeFileHandle) != identities.Password || LocalPathGuard.HashStream(password) != identities.PasswordHash
            || LocalPathGuard.Identity(executable.SafeFileHandle) != identities.Executable || LocalPathGuard.HashStream(executable) != identities.ExecutableHash)
            throw new InvalidDataException("O executável ou arquivo de senha mudou desde a prévia. Nada será executado.");
    }

    private static FileStream OpenPassword(string path)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is < 1 or > 65_536) { stream.Dispose(); throw new InvalidDataException("O arquivo de senha deve ser não vazio e limitado a 64 KiB."); }
        return stream;
    }

    private static bool ValidRepositoryConfig(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text); return document.RootElement.ValueKind == JsonValueKind.Object
            && document.RootElement.EnumerateObject().Select(item => item.Name).Distinct(StringComparer.Ordinal).Count() == document.RootElement.EnumerateObject().Count()
            && document.RootElement.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && HexId(id.GetString());
        }
        catch (JsonException) { return false; }
    }

    private void ValidateJournalRoot()
    {
        var path = LocalPathGuard.LocalPath(_journalRoot);
        if (File.Exists(path)) throw new InvalidDataException("A raiz do diário de backup é um arquivo; selecione uma pasta local apropriada.");
    }

    private static (string? SnapshotId, string? Failure) ParseSnapshot(string text)
    {
        string? snapshot = null;
        try
        {
            foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                using var item = JsonDocument.Parse(line);
                if (item.RootElement.ValueKind != JsonValueKind.Object) return (snapshot, "A saída de backup contém um registro que não é objeto JSON.");
                if (item.RootElement.EnumerateObject().Select(property => property.Name).Distinct(StringComparer.Ordinal).Count() != item.RootElement.EnumerateObject().Count())
                    return (snapshot, "A saída de backup contém propriedades JSON duplicadas e ambíguas.");
                if (!item.RootElement.TryGetProperty("message_type", out var kind) || kind.ValueKind != JsonValueKind.String)
                    return (snapshot, "A saída de backup contém registro sem message_type textual.");
                if (kind.GetString() != "summary") continue;
                if (snapshot is not null || !item.RootElement.TryGetProperty("snapshot_id", out var id)
                    || id.ValueKind != JsonValueKind.String || !HexId(id.GetString()))
                    return (snapshot, "O resumo de backup não contém uma identidade única e completa de snapshot.");
                snapshot = id.GetString();
            }
            return snapshot is null ? (null, "Nenhum resumo com ID completo de snapshot foi recebido.") : (snapshot, null);
        }
        catch (JsonException error) { return (snapshot, "JSON de backup inválido: " + error.Message); }
    }

    private static string Fingerprint<T>(T plan) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(plan, JournalJson))));
    private static bool HexId(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    private static bool Expected(Exception error) => error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or JsonException or System.Security.SecurityException;
    private static ErrorCode Code(Exception error) => error switch
    {
        FileNotFoundException => ErrorCode.DependencyUnavailable,
        UnauthorizedAccessException or System.Security.SecurityException => ErrorCode.PermissionDenied,
        InvalidDataException or ArgumentException or JsonException => ErrorCode.InvalidConfiguration,
        _ => ErrorCode.StorageFailure
    };
    private static ErrorCode ToolCode(ToolRunResult result, ErrorCode fallback = ErrorCode.ToolFailed) => result.Status switch
    {
        ToolRunStatus.Cancelled => ErrorCode.Cancelled,
        ToolRunStatus.TimedOut => ErrorCode.Timeout,
        ToolRunStatus.OutputLimitExceeded => ErrorCode.OutputLimitExceeded,
        _ => result.Error?.Code ?? fallback
    };

    private sealed record ResolvedPaths(string Executable, string Source, string Repository, string Password);
    private sealed record CapturedIdentities(PathIdentity? Source, PathIdentity RepositoryParent, PathIdentity? Repository,
        PathIdentity Password, string PasswordHash, PathIdentity Executable, string ExecutableHash);
    private sealed record IssuedPlan(string Fingerprint, CapturedIdentities Identities, PathIdentity? RestoreParent, DateTimeOffset ExpiresAt);

    private void Register(ConcurrentDictionary<Guid, IssuedPlan> plans, Guid id, IssuedPlan issued)
    {
        lock (_planSync)
        {
            foreach (var expired in plans.Where(item => item.Value.ExpiresAt <= _clock.GetUtcNow())) plans.TryRemove(expired.Key, out _);
            while (plans.Count >= 16) plans.TryRemove(plans.OrderBy(item => item.Value.ExpiresAt).First().Key, out _);
            plans[id] = issued;
        }
    }

    private sealed class OperationJournal(string root, Guid id) : IDisposable
    {
        public string DirectoryPath { get; } = Path.Combine(root, id.ToString("N"));
        private int _sequence;
        private Microsoft.Win32.SafeHandles.SafeFileHandle? _directory;
        public async Task WriteAsync<TPlan, TReceipt>(string phase, TPlan plan, TReceipt receipt, string? failure = null)
        {
            LocalPathGuard.RejectReparseAncestors(DirectoryPath);
            if (_directory is null)
            {
                Directory.CreateDirectory(root);
                LocalPathGuard.CreateNewDirectory(DirectoryPath);
                _directory = LocalPathGuard.HoldDirectory(DirectoryPath);
            }
            else if (LocalPathGuard.DirectoryIdentity(DirectoryPath) != LocalPathGuard.Identity(_directory))
                throw new InvalidDataException("A identidade do diretório de diário mudou; nenhuma nova etapa será executada.");
            var path = Path.Combine(DirectoryPath, $"{++_sequence:D3}-{phase}.json");
            var record = new { schemaVersion = 1, operationId = id, phase, recordedAt = DateTimeOffset.UtcNow, plan, receipt, failure };
            var bytes = JsonSerializer.SerializeToUtf8Bytes(record, JournalJson);
            if (bytes.Length > 16 * 1024 * 1024) throw new InvalidDataException("O diário excedeu o limite de 16 MiB; os registros anteriores foram preservados.");
            await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            await stream.WriteAsync(bytes, CancellationToken.None).ConfigureAwait(false);
            await stream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }
        public void Dispose() => _directory?.Dispose();
    }
}
