using System.Text.Json;
using Optimus.Application;
using Optimus.Domain;
using Optimus.Infrastructure;
using Optimus.Infrastructure.Smart;

internal static class ToolModulesCli
{
    public static bool Handles(string command) => command is "assist" or "backup" or "restore" or "smart-read" or "smart-import" or "profile-check";

    public static async Task<int> RunAsync(string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };
        try
        {
            var flags = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int index = 1; index < args.Length; index += 2)
                if (index + 1 >= args.Length || !args[index].StartsWith("--", StringComparison.Ordinal) || !flags.TryAdd(args[index], args[index + 1]))
                    throw new ArgumentException("Use pares --opção valor, sem opções duplicadas.");
            string[] allowed = args[0] switch
            {
                "assist" => ["--profile", "--data-dir", "--session", "--question"],
                "backup" => ["--profile", "--data-dir", "--source", "--repository", "--password-file", "--execute"],
                "restore" => ["--profile", "--data-dir", "--repository", "--password-file", "--snapshot", "--destination", "--execute"],
                "smart-read" => ["--profile", "--data-dir", "--target"],
                "smart-import" => ["--input"],
                "profile-check" => ["--profile"],
                _ => []
            };
            if (flags.Keys.Any(key => !allowed.Contains(key, StringComparer.Ordinal))) throw new ArgumentException("Opção não reconhecida para este comando.");
            var token = cancellation.Token;
            if (args[0] == "smart-import")
            {
                var imported = await RuntimeProfileFiles.ReadUtf8Async(Required(flags, "--input"), 1_048_576, token);
                if (!imported.IsSuccess) return Print(Result<DiagnosticEvidence>.Failure(imported.Error!.Code, imported.Error.Message));
                return Print(SmartJsonParser.Parse(imported.Value!));
            }

            var profile = await RuntimeProfileFiles.ReadAsync(Required(flags, "--profile"), token);
            if (!profile.IsSuccess) return Print(profile);
            var configured = profile.Value!;
            if (args[0] == "profile-check")
            {
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    checkedAt = DateTimeOffset.UtcNow,
                    profile = configured,
                    filesFound = new
                    {
                        llama = File.Exists(configured.LlamaExecutablePath),
                        model = File.Exists(configured.ModelPath),
                        restic = File.Exists(configured.ResticExecutablePath),
                        smartctl = File.Exists(configured.SmartctlExecutablePath),
                        cdb = File.Exists(configured.CdbExecutablePath),
                        symbols = Directory.Exists(configured.SymbolDirectory)
                    },
                    note = "Arquivo encontrado não comprova homologação. Nenhuma ferramenta foi executada."
                }, DiagnosticJson.Options));
                return 0;
            }

            var directory = flags.GetValueOrDefault("--data-dir") ?? Composition.DefaultDataDirectory;
            var modules = Composition.CreateModules(directory);
            if (args[0] == "assist")
            {
                var store = Composition.CreateStore(directory);
                DiagnosticSession session;
                if (flags.TryGetValue("--session", out var idText))
                {
                    if (!Guid.TryParse(idText, out var id)) throw new ArgumentException("ID de sessão inválido.");
                    var loaded = await store.GetAsync(id, token);
                    if (!loaded.IsSuccess) return Print(loaded);
                    session = loaded.Value!;
                }
                else
                {
                    Console.Error.WriteLine("Coletando diagnóstico consultivo para a análise local…");
                    session = await Composition.CreateDiagnostics().RunAsync(null, token);
                    var saved = await store.SaveAsync(session, token);
                    if (!saved.IsSuccess) return Print(saved);
                }
                return Print(await modules.Assistant.AnalyzeAsync(session,
                    flags.GetValueOrDefault("--question") ?? "Resuma os sinais observados e as limitações. Não atribua causas sem evidência.",
                    new(configured.LlamaExecutablePath ?? "", configured.ModelPath ?? "", configured.LlamaSha256, configured.ModelSha256), token));
            }
            if (args[0] == "smart-read") return Print(await modules.Smart.ReadAsync(configured.SmartctlExecutablePath ?? "", Required(flags, "--target"), token));

            var execute = flags.GetValueOrDefault("--execute") switch
            {
                null or "false" => false,
                "true" => true,
                _ => throw new ArgumentException("--execute aceita somente true ou false; sem essa opção o comando apenas prepara uma prévia.")
            };
            var settings = new BackupSettings(configured.ResticExecutablePath ?? "", flags.GetValueOrDefault("--source") ?? "",
                Required(flags, "--repository"), Required(flags, "--password-file"), configured.ResticSha256);
            if (args[0] == "backup")
            {
                _ = Required(flags, "--source");
                var plan = await modules.Backup.PlanAsync(settings, token);
                if (!plan.IsSuccess || !execute) return Print(plan);
                Console.Error.WriteLine($"Plano {plan.Value!.Id}: copiar {plan.Value.SourceDirectory} para repositório novo {plan.Value.RepositoryDirectory} e verificar integridade. Autorização explícita: --execute true.");
                return Print(await modules.Backup.ExecuteAsync(plan.Value, plan.Value.Id, token));
            }
            var restore = await modules.Backup.PlanRestoreAsync(settings, Required(flags, "--snapshot"), Required(flags, "--destination"), token);
            if (!restore.IsSuccess || !execute) return Print(restore);
            Console.Error.WriteLine($"Plano {restore.Value!.Id}: restaurar snapshot {restore.Value.SnapshotId} para destino novo {restore.Value.DestinationDirectory} com verificação. Autorização explícita: --execute true.");
            return Print(await modules.Backup.RestoreAsync(restore.Value, restore.Value.Id, token));
        }
        catch (OperationCanceledException error) when (cancellation.IsCancellationRequested)
        {
            return Print(Result<string>.Failure(ErrorCode.Cancelled, $"Operação cancelada. {error.Message}"));
        }
        catch (Exception error) when (error is ArgumentException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return Print(Result<string>.Failure(ErrorCode.InvalidConfiguration, error.Message));
        }
    }

    private static string Required(IReadOnlyDictionary<string, string> flags, string key) =>
        flags.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : throw new ArgumentException($"Informe {key}.");

    private static int Print<T>(Result<T> result)
    {
        Console.WriteLine(JsonSerializer.Serialize(result, DiagnosticJson.Options));
        return result.IsSuccess ? 0 : result.Error!.Code == ErrorCode.Cancelled ? 130 : 1;
    }
}
