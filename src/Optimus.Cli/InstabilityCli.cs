using System.Globalization;
using System.Text.Json;
using Optimus.Application;
using Optimus.Domain;
using Optimus.Infrastructure;

public sealed record InstabilityArguments(string Command, string? DataDirectory, string? ReportPath, string? ProfilePath,
    string? InputPath, string? DumpPath, string? MemoryReportPath, Guid? SessionId, int Days, string Question, int Steps, bool UseAi,
    string? Url = null, string? Manufacturer = null);

/// <summary>CLI boundary for named consultative workflows. Argument validation is pure and completes before any I/O.</summary>
public static class InstabilityCli
{
    public static bool Handles(string command) => command is "memory-import" or "dump-analyze" or "investigate" or "firmware-notes";

    public static InstabilityArguments ParseArguments(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Length == 0 || !Handles(args[0])) throw new ArgumentException("Comando de instabilidade desconhecido.");
        string[] allowed = args[0] switch
        {
            "memory-import" => ["--input", "--data-dir", "--report"],
            "dump-analyze" => ["--dump", "--profile", "--data-dir", "--report"],
            "firmware-notes" => ["--url", "--manufacturer", "--data-dir", "--report"],
            _ => ["--days", "--question", "--steps", "--ai", "--profile", "--dump", "--memory-report", "--session", "--data-dir", "--report"]
        };
        Dictionary<string, string> flags = new(StringComparer.Ordinal);
        for (int index = 1; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length || !allowed.Contains(args[index], StringComparer.Ordinal)
                || string.IsNullOrWhiteSpace(args[index + 1]) || !flags.TryAdd(args[index], args[index + 1]))
                throw new ArgumentException("Use somente pares de opções permitidas e valores não vazios, sem duplicações. Nenhuma consulta foi iniciada.");
        }
        int days = Integer("--days", 30, 1, 365), steps = Integer("--steps", 4, 1, 4);
        bool ai = flags.GetValueOrDefault("--ai") switch { null or "false" => false, "true" => true, _ => throw new ArgumentException("--ai aceita somente true ou false.") };
        string question = flags.GetValueOrDefault("--question") ?? "Investigue os sinais de instabilidade e indique próximos passos consultivos, preservando as incertezas.";
        if (question.Length > 400) throw new ArgumentException("A pergunta deve conter até 400 caracteres.");
        Guid? session = null;
        if (flags.TryGetValue("--session", out var id))
        {
            if (!Guid.TryParse(id, out var parsed) || parsed == Guid.Empty) throw new ArgumentException("--session exige um UUID de sessão não vazio.");
            session = parsed;
        }
        if (args[0] == "memory-import" && !flags.ContainsKey("--input")) throw new ArgumentException("Informe --input com o relatório HTML escolhido.");
        if (args[0] == "dump-analyze" && !flags.ContainsKey("--dump")) throw new ArgumentException("Informe --dump com o arquivo local escolhido.");
        if (args[0] == "firmware-notes" && (!flags.TryGetValue("--url", out var url) || url.Length > 4096
            || !flags.TryGetValue("--manufacturer", out var manufacturer) || manufacturer.Length > 512))
            throw new ArgumentException("Informe --url e --manufacturer explícitos, dentro dos limites de 4096 e 512 caracteres.");
        bool needsProfile = ai || flags.ContainsKey("--dump");
        if (needsProfile && !flags.ContainsKey("--profile")) throw new ArgumentException("IA ou análise de dump exige --profile com ferramentas explicitamente selecionadas.");
        if (!needsProfile && flags.ContainsKey("--profile")) throw new ArgumentException("--profile somente é utilizado com --ai true ou --dump.");
        foreach (var key in new[] { "--data-dir", "--report", "--profile", "--input", "--dump", "--memory-report" })
            if (flags.TryGetValue(key, out var path) && (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal) || path.Length > 32_768))
                throw new ArgumentException(key + " exige um caminho absoluto local; caminhos UNC/dispositivo não são aceitos.");
        foreach (var key in new[] { "--input", "--memory-report" })
            if (flags.TryGetValue(key, out var path) && Path.GetExtension(path).ToLowerInvariant() is not (".html" or ".htm"))
                throw new ArgumentException(key + " exige um relatório HTML real do MemTest86 (.html ou .htm).");
        if (flags.TryGetValue("--report", out var report) && Path.GetExtension(report).ToLowerInvariant() is not (".html" or ".json"))
            throw new ArgumentException("--report exige um arquivo novo .html ou .json.");
        return new(args[0], flags.GetValueOrDefault("--data-dir"), flags.GetValueOrDefault("--report"), flags.GetValueOrDefault("--profile"),
            flags.GetValueOrDefault("--input"), flags.GetValueOrDefault("--dump"), flags.GetValueOrDefault("--memory-report"), session, days, question, steps, ai,
            flags.GetValueOrDefault("--url"), flags.GetValueOrDefault("--manufacturer"));

        int Integer(string key, int fallback, int minimum, int maximum)
        {
            if (!flags.TryGetValue(key, out var text)) return fallback;
            if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int number) || number < minimum || number > maximum)
                throw new ArgumentException(key + $" exige um inteiro entre {minimum} e {maximum}.");
            return number;
        }
    }

    public static async Task<int> RunAsync(string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, value) => { value.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += cancel;
        bool emitted = false;
        try
        {
            var options = ParseArguments(args); // No file, database, directory or provider touched before this returns.
            var token = cancellation.Token;
            string directory = options.DataDirectory ?? Composition.DefaultDataDirectory;
            RuntimeProfile? profile = null;
            if (options.ProfilePath is not null)
            {
                var configured = await RuntimeProfileFiles.ReadAsync(options.ProfilePath, token).ConfigureAwait(false);
                if (!configured.IsSuccess) return Failure(configured.Error!);
                profile = configured.Value!;
                if (options.UseAi && (string.IsNullOrWhiteSpace(profile.LlamaExecutablePath) || string.IsNullOrWhiteSpace(profile.ModelPath)))
                    return Failure(new(ErrorCode.InvalidConfiguration, "O perfil precisa informar executável local da IA e modelo para --ai true."));
                if (options.DumpPath is not null && (string.IsNullOrWhiteSpace(profile.CdbExecutablePath) || string.IsNullOrWhiteSpace(profile.SymbolDirectory)))
                    return Failure(new(ErrorCode.InvalidConfiguration, "O perfil precisa informar CdbExecutablePath e SymbolDirectory locais para analisar o dump."));
            }
            var store = Composition.CreateStore(directory);
            DiagnosticSession? seed = null;
            if (options.SessionId is { } id)
            {
                var loaded = await store.GetAsync(id, token).ConfigureAwait(false);
                if (!loaded.IsSuccess) return Failure(loaded.Error!);
                seed = loaded.Value!;
            }
            var services = Composition.CreateInstability(directory);
            var progress = new Progress<string>(message => Console.Error.WriteLine(message));
            if (options.Command == "firmware-notes")
            {
                var read = await services.FirmwareNotes!.ReadAsync(options.Url!, options.Manufacturer!, token).ConfigureAwait(false);
                if (read.Value is null) return Failure(read.Error ?? new(ErrorCode.InvalidResponse, "A consulta não entregou evidência."));
                return await PublishSessionAsync(Session(read.Value), read.Error).ConfigureAwait(false);
            }
            if (options.Command == "memory-import")
            {
                var imported = await services.MemoryReports.ImportAsync(options.InputPath!, token).ConfigureAwait(false);
                if (imported.Value is null) return Failure(imported.Error ?? new(ErrorCode.InvalidResponse, "Importação não retornou evidência."));
                return await PublishSessionAsync(Session(imported.Value), imported.Error).ConfigureAwait(false);
            }
            CrashDumpSettings? dump = options.DumpPath is null ? null : new(profile!.CdbExecutablePath!, options.DumpPath, profile.SymbolDirectory!, profile.CdbSha256);
            if (options.Command == "dump-analyze")
            {
                var collected = await services.AnalyzeDump(dump!).RunAsync(progress, token).ConfigureAwait(false);
                return await PublishSessionAsync(collected, null).ConfigureAwait(false);
            }
            if (options.MemoryReportPath is not null)
            {
                var imported = await services.MemoryReports.ImportAsync(options.MemoryReportPath, token).ConfigureAwait(false);
                if (imported.Value is null) return Failure(imported.Error ?? new(ErrorCode.InvalidResponse, "Importação não retornou evidência."));
                var merged = EvidenceSessionUpdates.ReplaceOwned(seed, Session(imported.Value), new HashSet<string>(["memory.test-report"], StringComparer.Ordinal));
                if (!merged.IsSuccess) return Failure(merged.Error!);
                seed = merged.Value!;
                if (!imported.IsSuccess)
                {
                    var partial = InstabilityAssessment.Build(seed, options.Question, [], "ImportFailed", imported.Error, DateTimeOffset.UtcNow);
                    return await PublishInvestigationAsync(Result<InvestigationRun>.FailureWithValue(partial, imported.Error!.Code, imported.Error.Message, imported.Error.PendingCleanupPaths)).ConfigureAwait(false);
                }
            }
            LocalAiSettings? ai = options.UseAi ? new(profile!.LlamaExecutablePath!, profile.ModelPath!, profile.LlamaSha256, profile.ModelSha256) : null;
            var investigation = services.CreateInvestigation(options.Days, dump, ai);
            var result = await investigation.RunAsync(seed, new(options.Question, options.Steps, options.UseAi), progress, token).ConfigureAwait(false);
            return await PublishInvestigationAsync(result).ConfigureAwait(false);

            async Task<int> PublishInvestigationAsync(Result<InvestigationRun> run)
            {
                Console.WriteLine(JsonSerializer.Serialize(run, DiagnosticJson.Options)); emitted = true;
                if (run.Error is { } error) Console.Error.WriteLine(error.Message);
                return run.Value is null ? Exit(run.Error, null) : await FinishAsync(run.Value.Session, run.Error).ConfigureAwait(false);
            }
            async Task<int> PublishSessionAsync(DiagnosticSession session, AppError? error)
            {
                Console.WriteLine(JsonSerializer.Serialize(session, DiagnosticJson.Options)); emitted = true;
                if (error is not null) Console.Error.WriteLine(error.Message);
                return await FinishAsync(session, error).ConfigureAwait(false);
            }
            async Task<int> FinishAsync(DiagnosticSession session, AppError? primary)
            {
                // Emitted evidence remains usable if history fails; an independent requested report is still attempted.
                var saved = await store.SaveAsync(session, token).ConfigureAwait(false);
                if (!saved.IsSuccess) Console.Error.WriteLine("Resultado emitido, mas não salvo no histórico: " + saved.Error!.Message);
                bool exported = true;
                if (options.ReportPath is { } reportPath)
                {
                    var report = await new ReportExporter().ExportAsync(session, reportPath,
                        Path.GetExtension(reportPath).Equals(".html", StringComparison.OrdinalIgnoreCase) ? ReportFormat.Html : ReportFormat.Json, token).ConfigureAwait(false);
                    exported = report.IsSuccess;
                    if (!report.IsSuccess) Console.Error.WriteLine("Resultado emitido, mas relatório não publicado: " + report.Error!.Message);
                }
                return primary is not null ? Exit(primary, session) : !saved.IsSuccess || !exported ? 1 : Exit(null, session);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (!emitted) Console.WriteLine(JsonSerializer.Serialize(Result<string>.Failure(ErrorCode.Cancelled, "Operação cancelada antes da publicação do resultado."), DiagnosticJson.Options));
            Console.Error.WriteLine("Operação cancelada. Os resultados já emitidos permanecem em stdout; não foi declarada coleta completa.");
            return 130;
        }
        catch (Exception error) when (error is ArgumentException or IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException or System.Security.SecurityException)
        {
            if (emitted) { Console.Error.WriteLine("Resultado emitido; finalização falhou: " + error.Message); return 1; }
            return Failure(new(error is UnauthorizedAccessException or System.Security.SecurityException ? ErrorCode.PermissionDenied : ErrorCode.InvalidConfiguration, error.Message));
        }
        finally { Console.CancelKeyPress -= cancel; }

        int Failure(AppError error)
        {
            Console.WriteLine(JsonSerializer.Serialize(Result<string>.Failure(error.Code, error.Message, error.PendingCleanupPaths), DiagnosticJson.Options)); emitted = true;
            Console.Error.WriteLine(error.Message);
            return error.Code == ErrorCode.Cancelled ? 130 : 1;
        }
    }

    private static int Exit(AppError? error, DiagnosticSession? session) => error is not null ? error.Code == ErrorCode.Cancelled ? 130 : 1 : session?.Status == SessionStatus.Complete ? 0 : 3;
    private static DiagnosticSession Session(DiagnosticEvidence evidence) => new(Guid.NewGuid(), evidence.CapturedAt, evidence.CapturedAt,
        evidence.Status == EvidenceStatus.Complete ? SessionStatus.Complete : SessionStatus.Partial, [evidence], DiagnosticRules.Evaluate([evidence]));
}
