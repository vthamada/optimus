using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Optimus.Domain;

namespace Optimus.Infrastructure.Files;

public enum AtomicPublicationStage { Reserved, Flushed, Published }
public sealed record AtomicPublicationProgress(AtomicPublicationStage Stage, string TargetPath, string StagingPath, string Identity);

/// <summary>Owns a newly created file through write, flush and no-replace rename. Paths are never cleanup authority.</summary>
public static class AtomicNewFileWriter
{
    public const int MaximumSupportedUtf8Bytes = 32 * 1024 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly ConcurrentDictionary<string, OwnedPublication> Pending = new(StringComparer.Ordinal);
    public static IReadOnlyList<string> PendingCleanupIds => Pending.Keys.ToArray();

    public static Task<Result<string>> PublishUtf8Async(string target, string content, CancellationToken cancellationToken,
        int maximumBytes = MaximumSupportedUtf8Bytes, IProgress<AtomicPublicationProgress>? progress = null) =>
        Task.Run(() => PublishCore(target, content, maximumBytes, progress, cancellationToken), CancellationToken.None);

    public static Task<Result<bool>> RetryCleanupAsync(string cleanupId, CancellationToken cancellationToken) =>
        Task.Run(async () =>
        {
            if (!Pending.TryGetValue(cleanupId, out var owned)) return Result<bool>.Failure(ErrorCode.InvalidData, "A pendência não pertence ao publicador de arquivos novos.");
            await owned.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var failure = DeleteOwnedBeforePublication(owned);
                if (failure is not null) return Result<bool>.Failure(ErrorCode.StorageFailure, failure, [cleanupId, owned.StagingPath]);
                Pending.TryRemove(cleanupId, out _);
                return Result<bool>.Success(true);
            }
            finally { owned.Gate.Release(); }
        }, CancellationToken.None);

    private static Result<string> PublishCore(string selectedTarget, string content, int maximumBytes,
        IProgress<AtomicPublicationProgress>? progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows()) return Result<string>.Failure(ErrorCode.InvalidConfiguration, "A publicação por handle requer Windows.");
        if (content is null || maximumBytes is < 1 or > MaximumSupportedUtf8Bytes)
            return Result<string>.Failure(ErrorCode.InvalidData, "Conteúdo ou limite de publicação inválido.");
        OwnedPublication? owned = null;
        Result<string>? result = null;
        OperationCanceledException? cancelled = null;
        Exception? unexpected = null;
        try
        {
            // Encoding expansion is checked before allocating the UTF-8 byte buffer.
            var length = Utf8.GetByteCount(content);
            if (length > maximumBytes) return Result<string>.Failure(ErrorCode.InvalidData, $"O conteúdo excede o limite de {maximumBytes} bytes UTF-8.");
            var target = LocalPathGuard.LocalPath(Path.GetFullPath(selectedTarget));
            if (File.Exists(target) || Directory.Exists(target)) return Result<string>.Failure(ErrorCode.OutputExists, "O destino já existe e não será sobrescrito.");
            var parentPath = Path.GetDirectoryName(target);
            if (parentPath is null || !Directory.Exists(parentPath)) return Result<string>.Failure(ErrorCode.InvalidData, "A pasta de destino precisa existir.");
            var name = Path.GetFileName(target);
            if (name.Length is < 1 or > 255) return Result<string>.Failure(ErrorCode.InvalidData, "O nome do arquivo excede o componente de nome suportado pelo filesystem Windows.");
            owned = Reserve(target, parentPath);
            progress?.Report(owned.Progress(AtomicPublicationStage.Reserved));
            token.ThrowIfCancellationRequested();
            var bytes = Utf8.GetBytes(content);
            using (var borrowedHandle = new SafeFileHandle(owned.File.DangerousGetHandle(), ownsHandle: false))
            using (var stream = new FileStream(borrowedHandle, FileAccess.Write, 65_536, isAsync: false))
            {
                for (int offset = 0; offset < bytes.Length; offset += 65_536)
                {
                    token.ThrowIfCancellationRequested();
                    stream.Write(bytes, offset, Math.Min(65_536, bytes.Length - offset));
                }
                stream.Flush(flushToDisk: true);
            }
            progress?.Report(owned.Progress(AtomicPublicationStage.Flushed));
            token.ThrowIfCancellationRequested();
            RenameOwned(owned, name);
            owned.Published = true;
            // A cancellation after rename does not pretend that the observed publication was undone.
            if (!FlushFileBuffers(owned.File)) throw NativeFailure("O arquivo foi publicado, mas seu flush final não foi confirmado");
            progress?.Report(owned.Progress(AtomicPublicationStage.Published));
            result = token.IsCancellationRequested
                ? Result<string>.FailureWithValue(target, ErrorCode.Cancelled, "O cancelamento chegou após a publicação por handle; o arquivo novo foi preservado.")
                : Result<string>.Success(target);
        }
        catch (OperationCanceledException error) when (token.IsCancellationRequested) { cancelled = error; }
        catch (Exception error) when (Expected(error))
        {
            var code = error is PublicationConflictException ? ErrorCode.OutputExists
                : error is UnauthorizedAccessException ? ErrorCode.PermissionDenied
                : error is ArgumentException or InvalidDataException ? ErrorCode.InvalidData : ErrorCode.StorageFailure;
            var pending = (error as PendingPublicationException)?.PendingPaths;
            result = owned is { Published: true }
                ? Result<string>.FailureWithValue(owned.TargetPath, code, error.Message)
                : Result<string>.Failure(code, error.Message, pending);
        }
        catch (Exception error) { unexpected = error; }

        if (owned is not null)
        {
            string? cleanupFailure = null;
            if (!owned.Published) cleanupFailure = DeleteOwnedBeforePublication(owned);
            else owned.Close();
            if (cleanupFailure is not null)
            {
                var id = "atomic-file:" + Guid.NewGuid().ToString("N");
                Pending[id] = owned;
                var reason = $"{cancelled?.Message ?? unexpected?.Message ?? result?.Error?.Message} Limpeza própria pendente ({owned.IdentityText}): {cleanupFailure} Recurso: {id}; caminho observado: {owned.StagingPath}";
                if (cancelled is not null)
                {
                    var combined = new OperationCanceledException(reason, new AggregateException(cancelled, new IOException(cleanupFailure)), token);
                    combined.Data["PendingCleanupPaths"] = new[] { id, owned.StagingPath };
                    throw combined;
                }
                if (unexpected is not null) throw new IOException(reason, new AggregateException(unexpected, new IOException(cleanupFailure)));
                return Result<string>.Failure(result?.Error?.Code ?? ErrorCode.StorageFailure, reason, [id, owned.StagingPath]);
            }
        }
        if (cancelled is not null)
        {
            if (owned is { Published: true }) return Result<string>.FailureWithValue(owned.TargetPath, ErrorCode.Cancelled, "O arquivo novo foi publicado antes do cancelamento e foi preservado.");
            ExceptionDispatchInfo.Capture(cancelled).Throw();
        }
        if (unexpected is not null) ExceptionDispatchInfo.Capture(unexpected).Throw();
        // Include cancellation that arrived while closing the published file's resources.
        if (token.IsCancellationRequested && result is { IsSuccess: true })
            return Result<string>.FailureWithValue(result.Value!, ErrorCode.Cancelled, "O arquivo novo foi publicado; o cancelamento chegou na finalização.");
        return result!;
    }

    private static OwnedPublication Reserve(string target, string parentPath)
    {
        var directories = new List<SafeFileHandle>();
        SafeFileHandle? file = null;
        string? staging = null;
        try
        {
            var rootPath = Path.GetPathRoot(parentPath)!;
            var parts = parentPath[rootPath.Length..].Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 128) throw new InvalidDataException("A publicação segura está limitada a 128 componentes de diretório por operação.");
            // Bind the drive root once, then resolve every component relative to an already held handle.
            // OPEN_REPARSE_POINT + OBJ_DONT_REPARSE means namespace changes never become alias traversal.
            var root = CreateFile(rootPath, parts.Length == 0 ? 0x1000A2u : 0x100080u, 0x1 | 0x2,
                IntPtr.Zero, 3, 0x02000000 | 0x00200000, IntPtr.Zero);
            if (root.IsInvalid) { root.Dispose(); throw NativeFailure("Não foi possível vincular a raiz local"); }
            directories.Add(root);
            VerifyDirectory(root, rootPath);
            var selected = rootPath;
            for (int index = 0; index < parts.Length; index++)
            {
                var handle = OpenRelative(directories[^1], parts[index], directory: true, lastDirectory: index == parts.Length - 1);
                directories.Add(handle);
                selected = Path.Combine(selected, parts[index]);
                VerifyDirectory(handle, selected);
            }
            var name = $".optimus-atomic-{Guid.NewGuid():N}.tmp";
            staging = Path.Combine(parentPath, name);
            file = OpenRelative(directories[^1], name, directory: false, lastDirectory: false);
            return new(target, staging, directories, file, LocalPathGuard.Identity(file));
        }
        catch (Exception primary)
        {
            if (file is { IsInvalid: false, IsClosed: false } && directories.Count > 0)
            {
                var retained = new OwnedPublication(target, staging!, directories, file, null);
                var cleanup = DeleteOwnedBeforePublication(retained);
                if (cleanup is null) ExceptionDispatchInfo.Capture(primary).Throw();
                var id = "atomic-file:" + Guid.NewGuid().ToString("N");
                Pending[id] = retained;
                throw new PendingPublicationException($"{primary.Message} A reserva própria não confirmou identidade e sua limpeza falhou: {cleanup}. Recurso retido: {id}; {staging}.",
                    new AggregateException(primary, new IOException(cleanup)), [id, staging!]);
            }
            file?.Dispose();
            for (int index = directories.Count - 1; index >= 0; index--) directories[index].Dispose();
            throw;
        }
    }

    private static SafeFileHandle OpenRelative(SafeFileHandle parent, string name, bool directory, bool lastDirectory)
    {
        if (name.Length is < 1 or > 255 || name.IndexOfAny(['\\', '/', ':', '\0']) >= 0)
            throw new InvalidDataException("Componente relativo inválido para publicação por handle.");
        var unicodeBuffer = Marshal.StringToHGlobalUni(name);
        var unicodePointer = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeName>());
        try
        {
            var unicode = new UnicodeName { Length = checked((ushort)(name.Length * 2)), MaximumLength = checked((ushort)(name.Length * 2 + 2)), Buffer = unicodeBuffer };
            Marshal.StructureToPtr(unicode, unicodePointer, false);
            var attributes = new ObjectAttributes
            {
                Length = (uint)Marshal.SizeOf<ObjectAttributes>(),
                RootDirectory = parent.DangerousGetHandle(),
                ObjectName = unicodePointer,
                Attributes = 0x40 | 0x1000
            };
            uint access = directory ? (lastDirectory ? 0x1000A2u : 0x100080u) : 0x40110080u;
            uint options = directory ? 0x00200021u : 0x00200062u;
            var status = NtCreateFile(out var native, access, ref attributes, out _, IntPtr.Zero,
                directory ? 0u : 0x80u, directory ? 0x3u : 0x1u, directory ? 1u : 2u, options, IntPtr.Zero, 0);
            if (status < 0) throw NativeNtFailure("A resolução/criação relativa ao handle próprio não foi confirmada", status);
            if (native == IntPtr.Zero || native == new IntPtr(-1)) throw new IOException("O filesystem não entregou um handle válido para a operação relativa.");
            return new SafeFileHandle(native, ownsHandle: true);
        }
        finally { Marshal.FreeHGlobal(unicodePointer); Marshal.FreeHGlobal(unicodeBuffer); }
    }

    private static void VerifyDirectory(SafeFileHandle handle, string selected)
    {
        if (!GetFileInformationByHandleEx(handle, 9, out var attributes, (uint)Marshal.SizeOf<AttributeTagInformation>()))
            throw NativeFailure("Não foi possível verificar o tipo do diretório vinculado");
        if ((attributes.Attributes & 0x10) == 0 || (attributes.Attributes & 0x400) != 0)
            throw new InvalidDataException("O componente vinculado não é um diretório local direto; nenhum redirecionamento será seguido.");
        _ = LocalPathGuard.Identity(handle);
        var final = new StringBuilder(32768);
        var count = GetFinalPathNameByHandle(handle, final, (uint)final.Capacity, 0);
        if (count == 0 || count >= final.Capacity) throw NativeFailure("Não foi possível confirmar o nome final do diretório vinculado");
        var actual = final.ToString();
        if (!actual.StartsWith(@"\\?\", StringComparison.Ordinal)
            || !string.Equals(Path.TrimEndingDirectorySeparator(actual[4..]), Path.TrimEndingDirectorySeparator(selected), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("A identidade vinculada usa representação diferente da seleção original; o alias não foi corrigido automaticamente.");
    }
    private static void RenameOwned(OwnedPublication owned, string targetName)
    {
        if (owned.Identity is null || LocalPathGuard.Identity(owned.File) != owned.Identity)
            throw new InvalidDataException("A identidade do arquivo próprio não foi confirmada; nenhuma publicação foi realizada.");
        var nameBytes = Encoding.Unicode.GetBytes(targetName);
        var offset = Marshal.OffsetOf<RenameInformation>(nameof(RenameInformation.FirstNameCharacter)).ToInt32();
        int size = Math.Max(Marshal.SizeOf<RenameInformation>(), offset + nameBytes.Length + 2);
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.Copy(new byte[size], 0, buffer, size);
            var information = new RenameInformation { ReplaceIfExists = 0, RootDirectory = owned.Parent.DangerousGetHandle(), NameLength = (uint)nameBytes.Length };
            Marshal.StructureToPtr(information, buffer, false);
            Marshal.Copy(nameBytes, 0, IntPtr.Add(buffer, offset), nameBytes.Length);
            var status = NtSetInformationFile(owned.File, out _, buffer, (uint)size, 10);
            if (status < 0)
            {
                var code = RtlNtStatusToDosError(status);
                if (code is 80 or 183) throw new PublicationConflictException("O destino passou a existir; o arquivo existente foi preservado.");
                throw NativeNtFailure("O filesystem não confirmou o rename relativo do handle próprio", status);
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static string? DeleteOwnedBeforePublication(OwnedPublication owned)
    {
        if (owned.Published) return "O arquivo já foi publicado; a exclusão automática não é aplicável.";
        try
        {
            // An exclusively created, continuously held native handle proves ownership independently
            // of metadata readiness. Even a failed identity query does not authorize path deletion.
            if (owned.File.IsClosed || owned.File.IsInvalid)
                return "Não foi possível reconfirmar a identidade do handle próprio; nenhuma exclusão por caminho foi tentada.";
            var disposition = Marshal.AllocHGlobal(1);
            try
            {
                Marshal.WriteByte(disposition, 1);
                if (!SetFileInformationByHandle(owned.File, 4, disposition, 1))
                    return NativeFailure("O filesystem não confirmou a exclusão pelo handle próprio").Message;
            }
            finally { Marshal.FreeHGlobal(disposition); }
            owned.Close();
            return null;
        }
        catch (Exception error) when (Expected(error)) { return error.Message; }
    }

    private static bool Expected(Exception error) => error is IOException or InvalidDataException or UnauthorizedAccessException
        or ArgumentException or NotSupportedException or Win32Exception or System.Security.SecurityException;
    private static IOException NativeFailure(string message, int? errorCode = null)
    {
        var code = errorCode ?? Marshal.GetLastWin32Error();
        return new IOException($"{message} (Win32 {code}: {new Win32Exception(code).Message}).");
    }
    private sealed class PublicationConflictException(string message) : IOException(message);
    private sealed class PendingPublicationException(string message, Exception inner, IReadOnlyList<string> pendingPaths) : IOException(message, inner)
    {
        public IReadOnlyList<string> PendingPaths { get; } = pendingPaths;
    }

    private sealed class OwnedPublication(string targetPath, string stagingPath, IReadOnlyList<SafeFileHandle> directories, SafeFileHandle file, PathIdentity? identity)
    {
        public string TargetPath { get; } = targetPath;
        public string StagingPath { get; } = stagingPath;
        public SafeFileHandle Parent => directories[^1];
        public SafeFileHandle File { get; } = file;
        public PathIdentity? Identity { get; } = identity;
        public bool Published { get; set; }
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public string IdentityText => Identity is null ? "identidade desconhecida" : $"volume {Identity.Volume:X8}, arquivo {Identity.FileId:X16}";
        public AtomicPublicationProgress Progress(AtomicPublicationStage stage) => new(stage, TargetPath, StagingPath, IdentityText);
        public void Close() { File.Dispose(); for (int index = directories.Count - 1; index >= 0; index--) directories[index].Dispose(); }
    }

    private static IOException NativeNtFailure(string message, int status)
    {
        var code = RtlNtStatusToDosError(status);
        return new IOException($"{message} (NTSTATUS 0x{unchecked((uint)status):X8}, Win32 {code}: {new Win32Exception((int)code).Message}).");
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeName { public ushort Length, MaximumLength; public IntPtr Buffer; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ObjectAttributes
    {
        public uint Length;
        public IntPtr RootDirectory, ObjectName;
        public uint Attributes;
        public IntPtr SecurityDescriptor, SecurityQualityOfService;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock { public IntPtr Status; public UIntPtr Information; }
    [StructLayout(LayoutKind.Sequential)]
    private struct AttributeTagInformation { public uint Attributes, Tag; }
    [DllImport("ntdll.dll")]
    private static extern int NtCreateFile(out IntPtr file, uint access, ref ObjectAttributes attributes, out IoStatusBlock status,
        IntPtr allocationSize, uint fileAttributes, uint share, uint disposition, uint options, IntPtr extendedAttributes, uint extendedAttributesLength);
    [DllImport("ntdll.dll")]
    private static extern int NtSetInformationFile(SafeFileHandle file, out IoStatusBlock status, IntPtr information, uint length, int informationClass);
    [DllImport("ntdll.dll")]
    private static extern uint RtlNtStatusToDosError(int status);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int informationClass, out AttributeTagInformation information, uint size);
    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint size, uint flags);
    [StructLayout(LayoutKind.Sequential)]
    private struct RenameInformation
    {
        public uint ReplaceIfExists;
        public IntPtr RootDirectory;
        public uint NameLength;
        public ushort FirstNameCharacter;
    }
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr attributes, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int informationClass, IntPtr information, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlushFileBuffers(SafeFileHandle file);
}
