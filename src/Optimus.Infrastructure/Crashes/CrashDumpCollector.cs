using System.Collections;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Optimus.Application;
using Optimus.Domain;
using Optimus.Infrastructure.Files;

namespace Optimus.Infrastructure.Crashes;

/// <summary>Owns the selected dump snapshot, fixed debugger request and conservative text projection.</summary>
public sealed class CrashDumpCollector(IExternalToolRunner runner, CrashDumpSettings settings, string workingRoot, Action? prepareParent = null) : IDiagnosticCollector
{
    public const long MaximumDumpBytes = 2L * 1024 * 1024 * 1024;
    public const int MaximumOutputBytes = 262_144;
    public const int MaximumRetainedRuns = 50;
    public const string AnalysisCommand = "!analyze -v; q";
    // CDB defaults plus EXACT_SYMBOLS, IGNORE_NT_SYMPATH, IGNORE_CVREC and DISABLE_SYMSRV_AUTODETECT.
    // SECURE is kernel-only: it is deliberately not asserted for an MDMP of unknown target mode.
    private const string SymbolOptions = "0x020B16A7";
    private static readonly byte[] CapacityMarker = "optimus.crash-dump-capacity.v1\n"u8.ToArray();
    private static readonly SemaphoreSlim Slots = new(2, 2);
    private sealed record PendingOwner(IExternalToolRunner Runner, IReadOnlyList<IDisposable> Leases);
    private static readonly ConcurrentDictionary<string, PendingOwner> PendingOwners = new(StringComparer.Ordinal);
    public static IReadOnlyList<string> PendingOwnershipIds => PendingOwners.Keys.ToArray();
    public string Id => "windows.crash-dump";
    public string DisplayName => "Análise local de dump selecionado";

    public Task<IReadOnlyList<DiagnosticEvidence>> CollectAsync(CancellationToken cancellationToken) =>
        Task.Run(() => CollectCoreAsync(cancellationToken), CancellationToken.None);

    private async Task<IReadOnlyList<DiagnosticEvidence>> CollectCoreAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        List<DiagnosticFact> facts = [
            new("liveProcessRequested", "Attach ou processo ativo solicitado", "false"),
            new("symbolNetworkRequested", "Servidor/rede de símbolos habilitado", "false"),
            new("sourceCopied", "Dump copiado pelo aplicativo", "false"),
            new("debuggerDependenciesVerified", "DLLs/extensões da instalação do CDB verificadas integralmente", "false", Detailed: true),
            new("causalConclusion", "Conclusão causal", "Não determinada; nomes reportados pelo depurador não comprovam a causa ou defeito físico."),
            new("symbolCompleteness", "Completude dos símbolos", "unknown")
        ];
        List<CollectionIssue> issues = [];
        List<IDisposable> leases = [];
        bool slot = false, retainLeases = false;
        string? operationDirectory = null;
        EvidenceStatus status = EvidenceStatus.Unavailable;
        int examined = 0;
        try
        {
            if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess)
                throw new InvalidDataException("A análise exige processo Windows de 64 bits.");
            if (settings is null || !ValidDigest(settings.ExpectedDebuggerSha256))
                throw new InvalidDataException("Configuração ausente ou SHA-256 esperado inválido.");
            string executablePath = SelectedPath(settings.DebuggerExecutablePath);
            string dumpPath = SelectedPath(settings.DumpPath);
            string symbols = SelectedPath(settings.SymbolDirectory);
            string root = SelectedPath(workingRoot);
            if (!Path.GetFileName(executablePath).Equals("cdb.exe", StringComparison.OrdinalIgnoreCase)
                || !Path.GetExtension(dumpPath).Equals(".dmp", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Selecione explicitamente cdb.exe e um arquivo .dmp local.");
            if (symbols.Contains(';') || symbols.Contains('*'))
                throw new InvalidDataException("A pasta de símbolos deve ser única, local e literal, sem lista ou sintaxe de symbol server.");
            if (LocalPathGuard.Related(root, symbols) || LocalPathGuard.IsWithin(executablePath, root) || LocalPathGuard.IsWithin(dumpPath, root))
                throw new InvalidDataException("A raiz de recibos deve estar separada do depurador, dump e pasta de símbolos selecionados.");
            if (!File.Exists(executablePath) || !File.Exists(dumpPath) || !Directory.Exists(symbols))
                throw new FileNotFoundException("CDB, dump ou pasta local de símbolos indisponível. Nenhuma ferramenta ou símbolo será baixado.");
            facts.AddRange([
                new("debuggerPath", "CDB selecionado", executablePath),
                new("dumpPath", "Dump selecionado", dumpPath),
                new("symbolDirectory", "Pasta local de símbolos selecionada", symbols),
                new("configuredDebuggerSha256", "SHA-256 esperado do CDB", settings.ExpectedDebuggerSha256, Detailed: true)
            ]);
            if (!Slots.Wait(0, cancellationToken))
                throw new IOException("As duas vagas locais de análise estão ocupadas ou retidas por finalização pendente; não haverá repetição automática.");
            slot = true;
            leases.Add(LocalPathGuard.HoldDirectory(Path.GetDirectoryName(executablePath)!));
            leases.Add(LocalPathGuard.HoldDirectory(Path.GetDirectoryName(dumpPath)!));
            leases.Add(LocalPathGuard.HoldDirectory(symbols));
            var executable = HoldReadFile(executablePath, leases);
            var dump = HoldReadFile(dumpPath, leases);
            long length = dump.Length;
            facts.Add(new("dumpBytes", "Tamanho original do dump", NumberValue: length, Unit: "bytes"));
            if (length < 32 || length > MaximumDumpBytes)
            {
                if (length > MaximumDumpBytes) issues.Add(new(IssueCode.LimitReached, "Dump acima de 2 GiB; hashing e depurador não iniciados. Limite do piloto para custo de leitura e recursos do Job."));
                throw new InvalidDataException("Dump fora do orçamento de 32 bytes a 2 GiB; nenhum depurador iniciado.");
            }
            byte[] header = new byte[8];
            await dump.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
            string? kind = header.AsSpan(0, 4).SequenceEqual("MDMP"u8) ? "MDMP"
                : header.AsSpan().SequenceEqual("PAGEDUMP"u8) ? "PAGEDUMP"
                : header.AsSpan().SequenceEqual("PAGEDU64"u8) ? "PAGEDU64" : null;
            facts.Add(new("dumpHeaderHex", "Oito bytes originais do cabeçalho", Convert.ToHexString(header), Detailed: true));
            if (kind is null) throw new InvalidDataException("Assinatura do dump não reconhecida; não foi tratado como um dump válido.");
            facts.Add(new("dumpKind", "Assinatura observada (não valida a estrutura completa)", kind));
            examined = 1;
            dump.Position = 0;
            string dumpHash = Convert.ToHexString(await SHA256.HashDataAsync(dump, cancellationToken).ConfigureAwait(false));
            string debuggerHash = Convert.ToHexString(await SHA256.HashDataAsync(executable, cancellationToken).ConfigureAwait(false));
            facts.Add(new("dumpSha256", "SHA-256 dos bytes selecionados", dumpHash));
            facts.Add(new("debuggerSha256", "SHA-256 observado do CDB", debuggerHash, Detailed: true));
            if (settings.ExpectedDebuggerSha256 is { } expected && !expected.Equals(debuggerHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("O SHA-256 observado do CDB diverge do esperado; nenhum processo iniciado.");
            cancellationToken.ThrowIfCancellationRequested();
            string parent = Path.GetDirectoryName(root) ?? throw new InvalidDataException("Raiz de recibos sem pasta pai.");
            prepareParent?.Invoke();
            if (!Directory.Exists(parent)) throw new DirectoryNotFoundException("Prepare a pasta pai local dos recibos antes da análise: " + parent);
            leases.Add(LocalPathGuard.HoldDirectory(parent));
            if (!Directory.Exists(root)) LocalPathGuard.CreateNewDirectory(root);
            _ = LocalPathGuard.LocalPath(root);
            leases.Add(LocalPathGuard.HoldDirectory(root));
            operationDirectory = ReserveRun(root);
            leases.Add(LocalPathGuard.HoldDirectory(operationDirectory));
            facts.Add(new("operationDirectory", "Diretório exclusivo de recibos", operationDirectory));
            string emptyStartup = Path.Combine(operationDirectory, "startup.txt");
            WriteNew(emptyStartup, [], 0, leases);
            IReadOnlyList<string> arguments = ["-sins", "-noshell", "-nosqm", "-noinh", "-failinc", "-netsyms:no",
                "-sflags", SymbolOptions, "-cf", emptyStartup, "-i", operationDirectory, "-srcpath", operationDirectory,
                "-y", symbols, "-z", dumpPath, "-c", AnalysisCommand];
            var environmentNames = EnvironmentNamesToRemove();
            var overrides = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["PATH"] = Path.GetDirectoryName(executablePath)!,
                ["TEMP"] = operationDirectory,
                ["TMP"] = operationDirectory,
                ["DBGENG_NO_DEBUG_PRIVILEGE"] = "1"
            };
            var request = new ToolRequest(executablePath, arguments, operationDirectory, TimeSpan.FromSeconds(120), MaximumOutputBytes,
                EnvironmentOverrides: overrides, ExpectedExecutableSha256: debuggerHash, MaximumMemoryBytes: 1024L * 1024 * 1024,
                EnvironmentVariablesToRemove: environmentNames, OutputEncoding: ToolTextEncoding.WindowsOem, PreserveOutputBytes: true);
            facts.Add(new("arguments", "Argumentos fixos da análise", JsonSerializer.Serialize(arguments), Detailed: true));
            WriteNew(Path.Combine(operationDirectory, "request.json"), JsonSerializer.SerializeToUtf8Bytes(new
            {
                kind = "optimus.crash-dump-request",
                version = 1,
                capturedAt = DateTimeOffset.UtcNow,
                dumpPath,
                dumpHash,
                dumpBytes = length,
                dumpKind = kind,
                debuggerHash,
                executablePath,
                symbols,
                request.Arguments,
                request.Timeout,
                request.MaximumOutputBytes,
                request.MaximumMemoryBytes,
                outputEncoding = request.OutputEncoding.ToString(),
                environmentNamesRemoved = environmentNames,
                networkSandbox = false,
                sourceCopy = false
            }), 65_536, leases);
            cancellationToken.ThrowIfCancellationRequested();
            // Until the runner returns a finalization result, ownership may already have crossed the process boundary.
            retainLeases = true;
            var execution = await runner.RunAsync(request, cancellationToken).ConfigureAwait(false);
            retainLeases = execution.Status == ToolRunStatus.CleanupFailed || execution.Error?.PendingCleanupPaths?.Count > 0;
            RetainExecution(execution, facts, issues);
            // Write the same bounded capture even on cancellation or transport failure; never retry an uncertain execution.
            WriteNew(Path.Combine(operationDirectory, "result.json"), JsonSerializer.SerializeToUtf8Bytes(new
            {
                kind = "optimus.crash-dump-result",
                version = 1,
                completedAt = DateTimeOffset.UtcNow,
                execution.Status,
                execution.ExitCode,
                execution.Metrics,
                execution.OutputCodePage,
                errorCode = execution.Error?.Code.ToString(),
                errorMessage = BoundedText(execution.Error?.Message, 8192),
                pendingCleanup = BoundedPending(execution.Error?.PendingCleanupPaths),
                stdout = BoundedText(execution.StandardOutput, MaximumOutputBytes),
                stderr = BoundedText(execution.StandardError, MaximumOutputBytes),
                stdoutBase64 = BoundedBase64(execution.StandardOutputBytes),
                stderrBase64 = BoundedBase64(execution.StandardErrorBytes)
            }), 4_194_304, leases);
            cancellationToken.ThrowIfCancellationRequested();
            if (!execution.ExitedSuccessfully)
            {
                issues.Add(new(execution.Error?.Code == ErrorCode.PermissionDenied ? IssueCode.PermissionDenied : IssueCode.ReadFailed,
                    "O executor não confirmou término bem-sucedido. Saída parcial e erro primário retidos, sem conclusão sobre o dump."));
                status = execution.StandardOutput.Length > 0 ? EvidenceStatus.Partial : EvidenceStatus.Unavailable;
            }
            else if (Encoding.UTF8.GetByteCount(execution.StandardOutput) <= MaximumOutputBytes)
            {
                ParseOutput(execution.StandardOutput, facts, issues);
                status = EvidenceStatus.Partial;
            }
            else status = EvidenceStatus.Partial; // raw bytes/metadata remain available even when text projection exceeds its budget.
            // Text markers are not a proof of symbol completeness, including when no warning is printed.
            issues.Add(new(IssueCode.NotSupported, "A completude dos símbolos e da análise não é comprovada pelo texto ou código de saída. Nomes/memory_corruption são observações do depurador, sem diagnóstico causal ou condenação de RAM."));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException
            or Win32Exception or System.Security.SecurityException or CryptographicException or InvalidDataException)
        {
            issues.Add(new(error is UnauthorizedAccessException or System.Security.SecurityException ? IssueCode.PermissionDenied : IssueCode.ReadFailed,
                BoundMessage(error.Message)));
            if (operationDirectory is not null) facts.Add(new("retainedOperationDirectory", "Operação parcial preservada sem repetição/limpeza automática", operationDirectory));
        }
        finally
        {
            if (!retainLeases)
            {
                for (int index = leases.Count - 1; index >= 0; index--)
                {
                    try { leases[index].Dispose(); leases.RemoveAt(index); }
                    catch (Exception error) { retainLeases = true; issues.Add(new(IssueCode.ReadFailed, "Falha ao liberar lease próprio: " + BoundMessage(error.Message))); }
                }
            }
            if (retainLeases)
            {
                string ownership = "crash-dump-leases:" + Guid.NewGuid().ToString("N");
                PendingOwners[ownership] = new(runner, leases.ToArray());
                facts.Add(new("pendingOwnershipId", "Leases e executor retidos até encerramento do aplicativo", ownership, Detailed: true));
                issues.Add(new(IssueCode.ReadFailed, "Finalização não confirmada: leases próprios e referência ao executor permanecem retidos. Nenhum retry ou exclusão automática."));
            }
            else if (slot) Slots.Release();
        }
        return [new(Guid.NewGuid(), Id, DisplayName, "Dump explicitamente selecionado; análise CDB local", DateTimeOffset.UtcNow, status,
            new("Um dump até 2 GiB (limite de leitura/hash do piloto), assinatura e SHA-256 com leases de leitura; sem cópia. CDB selecionado com SHA-256/lease; DLLs/extensões da instalação não verificadas integralmente. Pasta literal de símbolos local. Job de 120 s/1 GiB, captura combinada até 256 KiB, OEM declarada e bytes originais quando entregues pelo executor; codificação real do CDB ainda requer piloto. Quatro campos textuais exatos, até 1024 caracteres por valor, duplicatas desconhecidas; saída original separada. Até 50 diretórios por raiz e dois Jobs/ownerships locais; recibos novos de até 4 MiB, sem limpeza automática. Símbolos não são congelados ou validados integralmente. Nenhuma garantia de sandbox de rede, análise causal ou integridade completa do formato. Não instala, baixa, anexa a processos ou executa comandos do usuário.",
                examined, 1, issues.Any(issue => issue.Code == IssueCode.LimitReached)), facts.ToArray(), issues.ToArray())];
    }

    private static FileStream HoldReadFile(string path, List<IDisposable> leases)
    {
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65_536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        leases.Add(file);
        _ = LocalPathGuard.Identity(file.SafeFileHandle);
        return file;
    }
    private static string SelectedPath(string? path)
    {
        if (path is null || path.Length > 1024) throw new InvalidDataException("Caminho local ausente ou acima de 1024 caracteres.");
        return LocalPathGuard.LocalPath(path);
    }
    private static bool ValidDigest(string? value) => value is null || value.Length == 64 && value.All(char.IsAsciiHexDigit);
    private static string[] EnvironmentNamesToRemove()
    {
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase)
        { "INIT", "_NT_SYMBOL_PATH", "_NT_ALT_SYMBOL_PATH", "_NT_EXECUTABLE_IMAGE_PATH", "_NT_SOURCE_PATH", "_NT_DEBUGGER_EXTENSION_PATH", "_NT_DEBUG_LOG_FILE_OPEN", "_NT_DEBUG_LOG_FILE_APPEND", "_NT_SYMBOL_PROXY", "DBGHELP_HOMEDIR", "SRCSRV_INI_FILE", "DBGENG_NO_BUGCHECK_ANALYSIS" };
        foreach (DictionaryEntry item in Environment.GetEnvironmentVariables())
            if (item.Key is string name && new[] { "_NT_", "DBGHELP_", "DBGENG_", "SYMSRV_", "SRCSRV_", "WINDBG_" }.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))) names.Add(name);
        return names.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }
    private static string ReserveRun(string root)
    {
        string lockPath = LocalPathGuard.LocalPath(Path.Combine(root, ".capacity.lock"));
        FileStream capacity;
        bool created;
        try { capacity = new(lockPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None); created = true; }
        catch (IOException) when (File.Exists(lockPath)) { capacity = new(lockPath, FileMode.Open, FileAccess.Read, FileShare.None); created = false; }
        using (capacity)
        {
            _ = LocalPathGuard.Identity(capacity.SafeFileHandle);
            if (created) { capacity.Write(CapacityMarker); capacity.Flush(flushToDisk: true); }
            else
            {
                if (capacity.Length != CapacityMarker.Length) throw new InvalidDataException("Reserva de capacidade desconhecida; arquivo preservado.");
                Span<byte> bytes = stackalloc byte[CapacityMarker.Length]; capacity.ReadExactly(bytes);
                if (!bytes.SequenceEqual(CapacityMarker)) throw new InvalidDataException("Reserva de capacidade desconhecida; arquivo preservado.");
            }
            if (Directory.EnumerateDirectories(root, "run-*", SearchOption.TopDirectoryOnly).Take(MaximumRetainedRuns).Count() >= MaximumRetainedRuns)
                throw new InvalidDataException("Limite de 50 operações retidas atingido; arquive os recibos antes de nova análise. Nenhum dado foi excluído.");
            string directory = Path.Combine(root, "run-" + Guid.NewGuid().ToString("N"));
            LocalPathGuard.CreateNewDirectory(directory);
            return directory;
        }
    }
    private static void WriteNew(string path, byte[] bytes, int maximum, List<IDisposable> leases)
    {
        if (bytes.Length > maximum) throw new InvalidDataException("Recibo acima do limite local; nenhum destino existente foi substituído.");
        PathIdentity identity;
        using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        { identity = LocalPathGuard.Identity(file.SafeFileHandle); file.Write(bytes); file.Flush(flushToDisk: true); }
        var read = HoldReadFile(path, leases);
        if (LocalPathGuard.Identity(read.SafeFileHandle) != identity) throw new InvalidDataException("Recibo substituído entre criação e lease; substituto não alterado.");
    }
    private static void RetainExecution(ToolRunResult execution, List<DiagnosticFact> facts, List<CollectionIssue> issues)
    {
        facts.AddRange([
            new("toolStatus", "Estado do executor", execution.Status.ToString()),
            new("toolExitCode", "Código de saída", NumberValue: execution.ExitCode),
            new("toolErrorCode", "Erro primário do executor", execution.Error?.Code.ToString(), Detailed: true),
            new("toolOutputCodePage", "Página de código declarada pelo executor", NumberValue: execution.OutputCodePage, Detailed: true),
            new("elapsedSeconds", "Tempo do Job", NumberValue: execution.Metrics.Elapsed.Ticks / (decimal)TimeSpan.TicksPerSecond, Unit: "s", Detailed: true),
            new("cpuSeconds", "CPU da família do Job", NumberValue: execution.Metrics.CpuTime is { } cpu ? cpu.Ticks / (decimal)TimeSpan.TicksPerSecond : null, Unit: "s", Detailed: true),
            new("peakProcessFamilyMemoryBytes", "Pico de commit da família do Job", NumberValue: execution.Metrics.PeakProcessFamilyMemoryBytes, Unit: "bytes", Detailed: true)
        ]);
        Text("sourceOutput", "Stdout original do depurador", execution.StandardOutput, MaximumOutputBytes);
        Text("sourceError", "Stderr original do depurador", execution.StandardError, MaximumOutputBytes);
        Text("toolErrorMessage", "Mensagem primária original", execution.Error?.Message, 8192);
        var pending = execution.Error?.PendingCleanupPaths;
        facts.Add(new("toolPendingCleanupCount", "Recursos pendentes do executor", NumberValue: pending?.Count, Detailed: true));
        if (pending is null) Text("toolPendingCleanupPaths", "Recursos pendentes do executor", null, 16_384);
        else if (pending.Count <= 128 && pending.Sum(path => (long)(path?.Length ?? 0)) <= 8192)
            Text("toolPendingCleanupPaths", "Recursos pendentes do executor", JsonSerializer.Serialize(pending), 65_536);
        else { issues.Add(new(IssueCode.LimitReached, "Metadados pendentes excedem 128 nomes/8192 caracteres; ownership do executor permanece retido.")); }
        Bytes("sourceOutput", execution.StandardOutputBytes);
        Bytes("sourceError", execution.StandardErrorBytes);
        if (execution.Status == ToolRunStatus.OutputLimitExceeded) issues.Add(new(IssueCode.LimitReached, "Captura atingiu o orçamento combinado; prefixo original retido, sem projeção de campos incompletos."));
        void Text(string key, string label, string? value, int maximum)
        {
            int? count = value is null ? null : Encoding.UTF8.GetByteCount(value);
            facts.Add(new(key + "Utf8Bytes", label + ": bytes UTF-8 da representação", NumberValue: count, Unit: "bytes", Detailed: true));
            facts.Add(new(key, label, count is null || count <= maximum ? value : null, Detailed: true));
            if (count > maximum) issues.Add(new(IssueCode.LimitReached, label + " excedeu o orçamento; conteúdo não truncado ou tratado como vazio."));
        }
        void Bytes(string key, byte[]? value)
        {
            facts.Add(new(key + "OriginalBytes", "Quantidade original de bytes capturados", NumberValue: value?.Length, Unit: "bytes", Detailed: true));
            facts.Add(new(key + "BytesBase64", "Bytes originais capturados (base64)", BoundedBase64(value), Detailed: true));
            if (value?.Length > MaximumOutputBytes) issues.Add(new(IssueCode.LimitReached, "Bytes acima do orçamento; executor conserva a captura, tamanho conhecido preservado."));
        }
    }
    private static string? BoundedBase64(byte[]? value) => value is { Length: <= MaximumOutputBytes } ? Convert.ToBase64String(value) : null;
    private static IReadOnlyList<string>? BoundedPending(IReadOnlyList<string>? value) => value is not null && value.Count <= 128
        && value.Sum(path => (long)(path?.Length ?? 0)) <= 8192 ? value : null;
    private static string? BoundedText(string? value, int maximum) => value is not null && Encoding.UTF8.GetByteCount(value) <= maximum ? value : null;
    private static string BoundMessage(string text) => text.Length <= 1024 ? text : "Erro excedeu 1024 caracteres; conteúdo integral omitido, sem truncamento apresentado como original.";
    private static void ParseOutput(string text, List<DiagnosticFact> facts, List<CollectionIssue> issues)
    {
        var fields = new Dictionary<string, (string Key, string Label)>(StringComparer.Ordinal)
        {
            ["BUGCHECK_CODE"] = ("bugcheckCode", "BUGCHECK_CODE reportado pelo CDB"),
            ["MODULE_NAME"] = ("moduleName", "MODULE_NAME reportado pelo CDB"),
            ["IMAGE_NAME"] = ("imageName", "IMAGE_NAME reportado pelo CDB"),
            ["FAILURE_BUCKET_ID"] = ("failureBucketId", "FAILURE_BUCKET_ID reportado pelo CDB")
        };
        Dictionary<string, List<string>> selected = fields.Keys.ToDictionary(key => key, _ => new List<string>(), StringComparer.Ordinal);
        using var lines = new StringReader(text);
        while (lines.ReadLine() is { } line)
        {
            int colon = line.IndexOf(':');
            if (colon < 0 || !selected.TryGetValue(line[..colon].Trim(), out var values)) continue;
            if (values.Count < 2) values.Add(line[(colon + 1)..]); // two are enough to prove ambiguity; full source remains separate.
        }
        foreach (var (field, descriptor) in fields)
        {
            var values = selected[field];
            string? value = values.Count == 1 && values[0] is { Length: > 0 and <= 1024 } original && !string.IsNullOrWhiteSpace(original) ? original : null;
            facts.Add(new(descriptor.Key, descriptor.Label, value));
            facts.Add(new(descriptor.Key + ".matchCount", "Ocorrências exatas (2 significa duas ou mais)", NumberValue: values.Count, Detailed: true));
            if (value is null) issues.Add(new(values.Any(item => item.Length > 1024) ? IssueCode.LimitReached : IssueCode.ReadFailed,
                field + ": ausente, duplicado ou fora do orçamento; valor desconhecido, fonte preservada."));
            if (field == "BUGCHECK_CODE" && value is not null)
            {
                string numeric = value.Trim();
                string digits = numeric.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? numeric[2..] : numeric;
                uint? code = digits.Length <= 8 && uint.TryParse(digits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint parsed) ? parsed : null;
                facts.Add(new("bugcheckCode.numeric", "Código hexadecimal reportado (sem interpretação causal)", NumberValue: code, Detailed: true));
                if (code is null) issues.Add(new(IssueCode.ReadFailed, "BUGCHECK_CODE não é uint32 hexadecimal inequívoco; texto original preservado."));
            }
        }
    }
}
