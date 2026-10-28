using System.Collections;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Optimus.Application;
using Optimus.Domain;

namespace Optimus.Infrastructure.Tools;

/// <summary>Internal provider boundary. Callers supply named, fixed provider operations, never assistant-generated commands.</summary>
public sealed class WindowsToolRunner : IExternalToolRunner
{
    private readonly SemaphoreSlim _capacity = new(2, 2);
    private readonly ConcurrentDictionary<string, OwnedRun> _pending = new(StringComparer.Ordinal);
    public IReadOnlyList<string> PendingCleanupIds => _pending.Keys.ToArray();

    public async Task<ToolRunResult> RunAsync(ToolRequest request, CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        try
        {
            Validate(request);
            await _capacity.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Failure(ToolRunStatus.Cancelled, ErrorCode.Cancelled, "Execução cancelada antes da inicialização.", timer.Elapsed);
        }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return Failure(ToolRunStatus.StartFailed, error is FileNotFoundException ? ErrorCode.DependencyUnavailable : ErrorCode.InvalidConfiguration, error.Message, timer.Elapsed);
        }

        try { return await RunOwnedAsync(request, cancellationToken, timer).ConfigureAwait(false); }
        finally { _capacity.Release(); }
    }

    public async Task<Result<bool>> RetryCleanupAsync(string cleanupId, CancellationToken cancellationToken)
    {
        if (!_pending.TryGetValue(cleanupId, out var run)) return Result<bool>.Failure(ErrorCode.InvalidData, "Recurso pendente não pertence a este executor.");
        await run.CleanupGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var reason = await StopAndDrainAsync(run).ConfigureAwait(false);
            if (reason is not null) return Result<bool>.Failure(ErrorCode.ToolFailed, reason, [cleanupId]);
            var closeErrors = run.CloseAll();
            if (closeErrors.Count > 0) return Result<bool>.Failure(ErrorCode.ToolFailed, string.Join(" ", closeErrors), [cleanupId]);
            _pending.TryRemove(cleanupId, out _);
            return Result<bool>.Success(true);
        }
        finally { run.CleanupGate.Release(); }
    }

    private async Task<ToolRunResult> RunOwnedAsync(ToolRequest request, CancellationToken cancellationToken, Stopwatch timer)
    {
        var run = new OwnedRun(request.MaximumOutputBytes, request.OutputEncoding);
        var status = ToolRunStatus.StartFailed;
        AppError? error = null;
        int? exitCode = null;
        TimeSpan? cpu = null;
        long? peakMemory = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            run.ExecutableLease = new FileStream(request.ExecutablePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (request.ExpectedExecutableSha256 is { } expected)
            {
                var digest = Convert.ToHexString(SHA256.HashData(run.ExecutableLease));
                if (!digest.Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("O executável selecionado mudou desde a verificação de integridade.");
                run.ExecutableLease.Position = 0;
            }

            CreateSuspendedOwned(request, run);
            run.OutputPump = Task.Run(() => Pump(run.OutputRead, run.Output, run), CancellationToken.None);
            run.ErrorPump = Task.Run(() => Pump(run.ErrorRead, run.ErrorOutput, run), CancellationToken.None);
            cancellationToken.ThrowIfCancellationRequested();
            if (WindowsNative.ResumeThread(run.Thread) == uint.MaxValue) throw NativeError("Não foi possível iniciar a thread já atribuída ao job próprio");
            run.Resumed = true;
            status = ToolRunStatus.Exited;
            while (true)
            {
                var wait = WindowsNative.WaitForSingleObject(run.Process, 0);
                if (wait == WindowsNative.WaitFailed) throw NativeError("Não foi possível consultar o processo próprio");
                if (cancellationToken.IsCancellationRequested)
                {
                    status = ToolRunStatus.Cancelled;
                    error = new(ErrorCode.Cancelled, "Execução cancelada; finalizando apenas o job próprio.");
                    break;
                }
                if (run.Capture.LimitReached)
                {
                    status = ToolRunStatus.OutputLimitExceeded;
                    error = new(ErrorCode.OutputLimitExceeded, "A saída combinada excedeu o limite de retenção; apenas o prefixo observado foi preservado.");
                    break;
                }
                if (run.PumpFailure is { } pumpFailure) throw new IOException("A leitura da saída da ferramenta falhou.", pumpFailure);
                if (timer.Elapsed >= request.Timeout)
                {
                    status = ToolRunStatus.TimedOut;
                    error = new(ErrorCode.Timeout, "O limite de tempo da ferramenta foi atingido.");
                    break;
                }
                if (wait == 0) break;
                await Task.Delay(20, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            status = ToolRunStatus.Cancelled;
            error = new(ErrorCode.Cancelled, "Execução cancelada antes de retomar o processo suspenso.");
        }
        catch (Exception caught) when (caught is Win32Exception or IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            error = new(caught is FileNotFoundException ? ErrorCode.DependencyUnavailable : ErrorCode.ToolFailed, caught.Message);
            status = run.Resumed ? ToolRunStatus.Failed : ToolRunStatus.StartFailed;
        }

        // Finalization has its own bound and is deliberately not abandoned when the caller cancels.
        var cleanupFailure = await StopAndDrainAsync(run).ConfigureAwait(false);
        if (run.Process != IntPtr.Zero && WindowsNative.WaitForSingleObject(run.Process, 0) == 0 && WindowsNative.GetExitCodeProcess(run.Process, out var nativeExit))
            exitCode = unchecked((int)nativeExit);
        if (run.Assigned && run.Job != IntPtr.Zero)
        {
            if (WindowsNative.QueryAccounting(run.Job, 1, out var accounting, (uint)Marshal.SizeOf<WindowsNative.AccountingInformation>(), IntPtr.Zero))
                cpu = TimeSpan.FromTicks(accounting.UserTime + accounting.KernelTime);
            if (WindowsNative.QueryLimits(run.Job, 9, out var limits, (uint)Marshal.SizeOf<WindowsNative.ExtendedLimitInformation>(), IntPtr.Zero))
                peakMemory = checked((long)limits.PeakJobMemoryUsed.ToUInt64());
        }
        if (run.Capture.LimitReached && status == ToolRunStatus.Exited)
        {
            status = ToolRunStatus.OutputLimitExceeded;
            error = new(ErrorCode.OutputLimitExceeded, "A saída combinada excedeu o limite, inclusive durante a finalização.");
        }
        if (cancellationToken.IsCancellationRequested && status == ToolRunStatus.Exited && error is null)
        {
            status = ToolRunStatus.Cancelled;
            error = new(ErrorCode.Cancelled, "Cancelamento recebido durante a finalização; os recursos próprios foram acompanhados até o encerramento.");
        }
        var stdout = run.Capture.Snapshot(run.Output, request.PreserveOutputBytes);
        var stderr = run.Capture.Snapshot(run.ErrorOutput, request.PreserveOutputBytes);
        if ((run.PumpFailure is not null || run.Capture.InvalidEncoding) && error is null)
        {
            status = ToolRunStatus.Failed;
            error = new(ErrorCode.InvalidResponse, $"A saída da ferramenta não pôde ser lida integralmente na codificação declarada (página {run.Capture.CodePage}); nenhum sucesso foi inferido.");
        }
        if (cleanupFailure is null)
        {
            var closeErrors = run.CloseAll();
            if (closeErrors.Count > 0) cleanupFailure = string.Join(" ", closeErrors);
        }
        if (cleanupFailure is not null)
        {
            var id = "native-job:" + Guid.NewGuid().ToString("N");
            _pending[id] = run;
            status = ToolRunStatus.CleanupFailed;
            error = new(ErrorCode.ToolFailed, $"{error?.Message} Finalização própria pendente: {cleanupFailure}", [id]);
        }
        if (cancellationToken.IsCancellationRequested && status == ToolRunStatus.Exited && error is null)
        {
            status = ToolRunStatus.Cancelled;
            error = new(ErrorCode.Cancelled, "Cancelamento recebido durante o fechamento dos recursos próprios.");
        }
        timer.Stop();
        return new(status, exitCode, stdout.Text, stderr.Text, new(timer.Elapsed, cpu, peakMemory), error,
            stdout.Bytes, stderr.Bytes, run.Capture.CodePage);
    }

    private static async Task<string?> StopAndDrainAsync(OwnedRun run)
    {
        if (run.Process != IntPtr.Zero)
        {
            if (run.Assigned)
            {
                if (!WindowsNative.QueryAccounting(run.Job, 1, out var initial, (uint)Marshal.SizeOf<WindowsNative.AccountingInformation>(), IntPtr.Zero))
                    return NativeError("A identidade do job próprio não pôde ser consultada na finalização").Message;
                if (initial.ActiveProcesses > 0 && !WindowsNative.TerminateJobObject(run.Job, 1))
                    return NativeError("Não foi possível finalizar o job próprio").Message;
            }
            else if (WindowsNative.WaitForSingleObject(run.Process, 0) != 0 && !WindowsNative.TerminateProcess(run.Process, 1))
                return NativeError("Não foi possível encerrar o processo próprio ainda suspenso").Message;

            var deadline = Stopwatch.StartNew();
            while (deadline.Elapsed < TimeSpan.FromSeconds(5))
            {
                var rootExited = WindowsNative.WaitForSingleObject(run.Process, 0) == 0;
                bool allExited = !run.Assigned;
                if (run.Assigned)
                {
                    if (!WindowsNative.QueryAccounting(run.Job, 1, out var accounting, (uint)Marshal.SizeOf<WindowsNative.AccountingInformation>(), IntPtr.Zero))
                        return NativeError("Não foi possível confirmar os descendentes próprios na finalização").Message;
                    allExited = accounting.ActiveProcesses == 0;
                }
                if (rootExited && allExited) break;
                await Task.Delay(20, CancellationToken.None).ConfigureAwait(false);
            }
            if (WindowsNative.WaitForSingleObject(run.Process, 0) != 0) return "O processo próprio não confirmou encerramento dentro da janela de limpeza.";
            if (run.Assigned && (!WindowsNative.QueryAccounting(run.Job, 1, out var final, (uint)Marshal.SizeOf<WindowsNative.AccountingInformation>(), IntPtr.Zero) || final.ActiveProcesses != 0))
                return "Nem todos os processos do job próprio confirmaram encerramento.";
        }
        run.CloseChildPipeEnds();
        var pumps = Task.WhenAll(run.OutputPump ?? Task.CompletedTask, run.ErrorPump ?? Task.CompletedTask);
        if (await Task.WhenAny(pumps, Task.Delay(TimeSpan.FromSeconds(3))).ConfigureAwait(false) != pumps)
            return "A drenagem de saída própria ainda está pendente.";
        await pumps.ConfigureAwait(false);
        return null;
    }

    private static void CreateSuspendedOwned(ToolRequest request, OwnedRun run)
    {
        run.Job = WindowsNative.CreateJobObjectW(IntPtr.Zero, null);
        if (run.Job == IntPtr.Zero) throw NativeError("Não foi possível criar um job próprio");
        var limits = new WindowsNative.ExtendedLimitInformation();
        limits.Basic.Flags = WindowsNative.KillOnJobClose;
        if (request.MaximumMemoryBytes is { } memory)
        {
            limits.Basic.Flags |= WindowsNative.LimitJobMemory;
            limits.JobMemoryLimit = new UIntPtr((ulong)memory);
        }
        if (!WindowsNative.SetInformationJobObject(run.Job, 9, ref limits, (uint)Marshal.SizeOf<WindowsNative.ExtendedLimitInformation>()))
            throw NativeError("Não foi possível configurar os limites do job próprio");
        var attributes = new WindowsNative.SecurityAttributes { Length = (uint)Marshal.SizeOf<WindowsNative.SecurityAttributes>(), Inherit = true };
        if (!WindowsNative.CreatePipe(out run.OutputRead, out run.OutputWrite, ref attributes, 0)
            || !WindowsNative.CreatePipe(out run.ErrorRead, out run.ErrorWrite, ref attributes, 0)
            || !WindowsNative.CreatePipe(out run.InputRead, out run.InputWrite, ref attributes, 0))
            throw NativeError("Não foi possível reservar os pipes próprios");
        foreach (var parent in new[] { run.OutputRead, run.ErrorRead, run.InputWrite })
            if (!WindowsNative.SetHandleInformation(parent, 1, 0)) throw NativeError("Não foi possível restringir a herança de handles");
        using var startup = new StartupAllocation(run, request.EnvironmentOverrides, request.EnvironmentVariablesToRemove);
        var info = startup.Information;
        var command = new StringBuilder(string.Join(" ", new[] { request.ExecutablePath }.Concat(request.Arguments).Select(QuoteArgument)));
        var flags = WindowsNative.CreateSuspended | WindowsNative.CreateNoWindow | WindowsNative.CreateUnicodeEnvironment | WindowsNative.ExtendedStartupInfoPresent;
        if (!WindowsNative.CreateProcessW(request.ExecutablePath, command, IntPtr.Zero, IntPtr.Zero, true, flags,
            startup.Environment, request.WorkingDirectory, ref info, out var created))
            throw NativeError("Não foi possível criar o processo suspenso");
        run.Process = created.Process;
        run.Thread = created.Thread;
        if (!WindowsNative.AssignProcessToJobObject(run.Job, run.Process)) throw NativeError("O processo suspenso não pôde ser associado ao job próprio; não será executado");
        run.Assigned = true;
        run.CloseChildPipeEnds();
    }

    private static void Pump(IntPtr handle, MemoryStream destination, OwnedRun run)
    {
        try
        {
            using var borrowed = new SafeFileHandle(handle, ownsHandle: false);
            using var stream = new FileStream(borrowed, FileAccess.Read, bufferSize: 4096, isAsync: false);
            var buffer = new byte[4096];
            while (true)
            {
                var read = stream.Read(buffer, 0, buffer.Length);
                if (read == 0) return;
                if (!run.Capture.Append(destination, buffer.AsSpan(0, read))) return;
            }
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or UnauthorizedAccessException)
        {
            run.PumpFailure = error;
        }
    }

    public static string QuoteArgument(string argument)
    {
        ArgumentNullException.ThrowIfNull(argument);
        var text = new StringBuilder("\"");
        int slashes = 0;
        foreach (var character in argument)
        {
            if (character == '\\') { slashes++; continue; }
            if (character == '"') text.Append('\\', slashes * 2 + 1).Append('"');
            else text.Append('\\', slashes).Append(character);
            slashes = 0;
        }
        return text.Append('\\', slashes * 2).Append('"').ToString();
    }

    private static void Validate(ToolRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!OperatingSystem.IsWindows()) throw new InvalidDataException("A execução supervisionada requer Windows.");
        if (!IsLocalAbsolute(request.ExecutablePath) || !request.ExecutablePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Selecione um executável local Windows por caminho absoluto.");
        if (!File.Exists(request.ExecutablePath)) throw new FileNotFoundException("A ferramenta selecionada não está disponível.", request.ExecutablePath);
        if ((File.GetAttributes(request.ExecutablePath) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("O executável selecionado é um redirecionamento de identidade não admitido.");
        if (!IsLocalAbsolute(request.WorkingDirectory) || !Directory.Exists(request.WorkingDirectory)) throw new InvalidDataException("A pasta de trabalho deve existir e ser local e absoluta.");
        if (request.Arguments is null || request.Arguments.Any(argument => argument is null || argument.Length > 32760 || argument.Contains('\0')))
            throw new InvalidDataException("Argumentos inválidos para a operação nomeada.");
        if (request.Timeout <= TimeSpan.Zero || request.Timeout > TimeSpan.FromHours(2) || request.MaximumOutputBytes is < 256 or > 16_777_216)
            throw new InvalidDataException("Limites de tempo ou saída fora do intervalo suportado.");
        if (!Enum.IsDefined(request.OutputEncoding)) throw new InvalidDataException("Codificação de transporte não reconhecida.");
        if (request.MaximumMemoryBytes is < 16_777_216 || request.ExpectedExecutableSha256 is { } hash && (hash.Length != 64 || !hash.All(Uri.IsHexDigit)))
            throw new InvalidDataException("Limite de memória ou digest do executável inválido.");
        if (request.EnvironmentOverrides is not null && request.EnvironmentOverrides.Any(entry => string.IsNullOrWhiteSpace(entry.Key)
            || entry.Key.Contains('=') || entry.Key.Contains('\0') || entry.Value is null || entry.Value.Contains('\0')))
            throw new InvalidDataException("Substituições de ambiente inválidas.");
        if (request.EnvironmentVariablesToRemove is not null && request.EnvironmentVariablesToRemove.Any(name => string.IsNullOrWhiteSpace(name)
            || name.Contains('=') || name.Contains('\0') || name.Contains('*') || name.Contains('?')))
            throw new InvalidDataException("Use nomes exatos de variáveis para remoção, sem NUL, igualdade ou curingas.");
        long commandLength = QuoteArgument(request.ExecutablePath).Length;
        foreach (var argument in request.Arguments) commandLength += 1L + QuoteArgument(argument).Length;
        if (commandLength >= 32767) throw new InvalidDataException("A linha de comando excede o limite do protocolo Windows.");
    }
    private static bool IsLocalAbsolute(string? path) => !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path)
        && path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && (path[2] is '\\' or '/');
    private static Win32Exception NativeError(string message) { var code = Marshal.GetLastWin32Error(); return new(code, $"{message} (Win32 {code}: {new Win32Exception(code).Message})"); }
    private static ToolRunResult Failure(ToolRunStatus status, ErrorCode code, string message, TimeSpan elapsed) => new(status, null, "", "", new(elapsed), new(code, message));

    private sealed class CaptureBuffer(int maximum, ToolTextEncoding selectedEncoding)
    {
        private readonly Encoding _encoding = ResolveEncoding(selectedEncoding);
        private readonly object _gate = new();
        private int _retained;
        private volatile bool _limitReached;
        public bool InvalidEncoding { get; private set; }
        public int CodePage => _encoding.CodePage;
        public bool LimitReached => _limitReached;
        public bool Append(MemoryStream destination, ReadOnlySpan<byte> bytes)
        {
            lock (_gate)
            {
                var retained = Math.Min(bytes.Length, maximum - _retained);
                destination.Write(bytes[..retained]);
                _retained += retained;
                if (retained < bytes.Length) _limitReached = true;
                return !_limitReached;
            }
        }
        public (string Text, byte[]? Bytes) Snapshot(MemoryStream source, bool preserveBytes)
        {
            lock (_gate)
            {
                // The provider declares the encoding. No byte-sniffing or repair changes that choice.
                // flush=false keeps a resource-limited partial sequence from becoming a fabricated character.
                var decoder = _encoding.GetDecoder();
                var bytes = source.GetBuffer().AsSpan(0, checked((int)source.Length));
                byte[]? original = preserveBytes ? bytes.ToArray() : null;
                var characters = new char[_encoding.GetMaxCharCount(bytes.Length)];
                try
                {
                    decoder.Convert(bytes, characters, flush: !LimitReached, out _, out var count, out _);
                    return (new string(characters, 0, count), original);
                }
                catch (DecoderFallbackException)
                {
                    InvalidEncoding = true;
                    return (Encoding.GetEncoding(CodePage, EncoderFallback.ReplacementFallback,
                        DecoderFallback.ReplacementFallback).GetString(bytes), original);
                }
            }
        }

        private static Encoding ResolveEncoding(ToolTextEncoding selected)
        {
            if (selected == ToolTextEncoding.Utf8Strict) return new UTF8Encoding(false, true);
            if (selected == ToolTextEncoding.Utf16LittleEndian) return new UnicodeEncoding(false, false, true);
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(checked((int)GetOEMCP()), EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        }

        [DllImport("kernel32.dll")]
        private static extern uint GetOEMCP();
    }

    private sealed class OwnedRun(int maximum, ToolTextEncoding selectedEncoding)
    {
        public IntPtr Job, Process, Thread, OutputRead, OutputWrite, ErrorRead, ErrorWrite, InputRead, InputWrite;
        public bool Assigned, Resumed;
        public FileStream? ExecutableLease;
        public readonly MemoryStream Output = new(), ErrorOutput = new();
        public readonly CaptureBuffer Capture = new(maximum, selectedEncoding);
        public Task? OutputPump, ErrorPump;
        public volatile Exception? PumpFailure;
        public readonly SemaphoreSlim CleanupGate = new(1, 1);
        private readonly List<string> _closeErrors = [];
        private readonly HashSet<string> _unknownHandleIdentities = new(StringComparer.Ordinal);
        public void CloseChildPipeEnds()
        {
            Close(ref OutputWrite, nameof(OutputWrite)); Close(ref ErrorWrite, nameof(ErrorWrite));
            Close(ref InputRead, nameof(InputRead)); Close(ref InputWrite, nameof(InputWrite));
        }
        public IReadOnlyList<string> CloseAll()
        {
            _closeErrors.Clear();
            CloseChildPipeEnds();
            Close(ref OutputRead, nameof(OutputRead)); Close(ref ErrorRead, nameof(ErrorRead));
            Close(ref Thread, nameof(Thread)); Close(ref Process, nameof(Process)); Close(ref Job, nameof(Job));
            try { ExecutableLease?.Dispose(); ExecutableLease = null; }
            catch (IOException error) { _closeErrors.Add(error.Message); }
            if (_closeErrors.Count == 0) { Output.Dispose(); ErrorOutput.Dispose(); }
            return _closeErrors.ToArray();
        }
        private void Close(ref IntPtr handle, string name)
        {
            if (handle == IntPtr.Zero) return;
            if (_unknownHandleIdentities.Contains(name)) { _closeErrors.Add($"Identidade do handle {name} permanece desconhecida; retry não executado."); return; }
            if (WindowsNative.CloseHandle(handle)) { handle = IntPtr.Zero; return; }
            var code = Marshal.GetLastWin32Error();
            if (code == 6) _unknownHandleIdentities.Add(name);
            _closeErrors.Add($"Falha ao fechar handle próprio {name}: Win32 {code}.");
        }
    }

    private sealed class StartupAllocation : IDisposable
    {
        private IntPtr _attributes, _handles;
        private bool _initialized;
        public IntPtr Environment { get; private set; }
        public WindowsNative.StartupInfoEx Information;
        public StartupAllocation(OwnedRun run, IReadOnlyDictionary<string, string>? overrides, IReadOnlyList<string>? removals)
        {
            try
            {
                UIntPtr size = UIntPtr.Zero;
                WindowsNative.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
                if (size.ToUInt64() is 0 or > 65536) throw new InvalidDataException("Tamanho inválido de atributos nativos.");
                _attributes = Marshal.AllocHGlobal(checked((int)size.ToUInt64()));
                if (!WindowsNative.InitializeProcThreadAttributeList(_attributes, 1, 0, ref size)) throw NativeError("Não foi possível reservar a lista de handles herdados");
                _initialized = true;
                _handles = Marshal.AllocHGlobal(IntPtr.Size * 3);
                Marshal.WriteIntPtr(_handles, 0, run.InputRead);
                Marshal.WriteIntPtr(_handles, IntPtr.Size, run.OutputWrite);
                Marshal.WriteIntPtr(_handles, IntPtr.Size * 2, run.ErrorWrite);
                if (!WindowsNative.UpdateProcThreadAttribute(_attributes, 0, new UIntPtr(0x20002), _handles, new UIntPtr((uint)(IntPtr.Size * 3)), IntPtr.Zero, IntPtr.Zero))
                    throw NativeError("Não foi possível restringir a lista de handles herdados");
                Information.StartupInfo.Size = (uint)Marshal.SizeOf<WindowsNative.StartupInfoEx>();
                Information.StartupInfo.Flags = 0x101;
                Information.StartupInfo.StandardInput = run.InputRead;
                Information.StartupInfo.StandardOutput = run.OutputWrite;
                Information.StartupInfo.StandardError = run.ErrorWrite;
                Information.AttributeList = _attributes;
                if (overrides is { Count: > 0 } || removals is { Count: > 0 })
                {
                    var values = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (DictionaryEntry value in System.Environment.GetEnvironmentVariables()) values[(string)value.Key] = (string?)value.Value ?? "";
                    if (removals is not null) foreach (var name in removals) values.Remove(name);
                    if (overrides is not null) foreach (var value in overrides) values[value.Key] = value.Value;
                    var block = string.Join('\0', values.Select(entry => entry.Key + "=" + entry.Value)) + '\0';
                    if (block.Length > 1_048_576) throw new InvalidDataException("O ambiente do processo excede o limite admitido.");
                    Environment = Marshal.StringToHGlobalUni(block);
                }
            }
            catch { Dispose(); throw; }
        }
        public void Dispose()
        {
            if (_initialized) { WindowsNative.DeleteProcThreadAttributeList(_attributes); _initialized = false; }
            if (_attributes != IntPtr.Zero) { Marshal.FreeHGlobal(_attributes); _attributes = IntPtr.Zero; }
            if (_handles != IntPtr.Zero) { Marshal.FreeHGlobal(_handles); _handles = IntPtr.Zero; }
            if (Environment != IntPtr.Zero) { Marshal.FreeHGlobal(Environment); Environment = IntPtr.Zero; }
        }
    }
}
