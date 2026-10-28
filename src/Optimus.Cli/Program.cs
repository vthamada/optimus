using System.Text.Json;
using Optimus.Application;
using Optimus.Domain;
using Optimus.Infrastructure;

Console.OutputEncoding = System.Text.Encoding.UTF8;
if (args.Length == 0 || args.Contains("--help"))
{
    Console.WriteLine("opTImus 0.9 · diagnóstico e ferramentas locais para Windows\nUso: Optimus.Cli diagnose|inventory|defender-status|device-inventory|services|update-history|processes|scheduled-tasks|storage|connections|firewall|application-failures|pending-restart|restore-points|bitlocker-status|hardware-profile [--data-dir PASTA] [--report ARQUIVO.html|.json]\n     Optimus.Cli restart-history|memory-results [--days 1..365] [--data-dir PASTA] [--report ARQUIVO.html|.json]\n     Optimus.Cli folder-space --path PASTA [--data-dir PASTA] [--report ARQUIVO.html|.json]\n     Optimus.Cli history [--data-dir PASTA]\n     Optimus.Cli integrity --operation sfc-verify|dism-check|dism-scan [--data-dir PASTA] [--execute true]\nConsultas não alteram configurações. Integrity sem --execute true apenas prepara uma prévia; não solicita elevação nem reparos.");
    Console.WriteLine("Módulos: assist --profile PERFIL [--data-dir PASTA] [--session ID] [--question TEXTO]\n         backup --profile PERFIL --source PASTA --repository NOVO --password-file ARQUIVO [--execute true]\n         restore --profile PERFIL --repository REPO --password-file ARQUIVO --snapshot ID --destination NOVO [--execute true]\n         smart-read --profile PERFIL --target DISPOSITIVO\n         smart-import --input SAIDA.json\n         profile-check --profile PERFIL\nBackup/restauração sem --execute true apenas preparam uma prévia.");
    Console.WriteLine("Instabilidade: firmware-notes --url URL_HTTPS --manufacturer FABRICANTE [--data-dir PASTA] [--report NOVO.html|.json]\n               memory-import --input RELATORIO.html [--data-dir PASTA] [--report NOVO.html|.json]\n               dump-analyze --dump ARQUIVO.dmp --profile PERFIL [--data-dir PASTA] [--report NOVO.html|.json]\n               investigate [--days 1..365] [--question TEXTO_ATÉ_400] [--steps 1..4] [--ai true|false] [--profile PERFIL] [--dump ARQUIVO] [--memory-report RELATORIO.html] [--session UUID] [--data-dir PASTA] [--report NOVO.html|.json]\nIA é opcional e desativada por padrão; perfil é exigido para IA ou dump. Importa somente o arquivo escolhido. Nenhum teste físico, reinício, instalação ou alteração é iniciado.");
    return 0;
}

if (args[0] == "integrity") return await MaintenanceCli.RunIntegrityAsync(args);
if (ToolModulesCli.Handles(args[0])) return await ToolModulesCli.RunAsync(args);
if (InstabilityCli.Handles(args[0])) return await InstabilityCli.RunAsync(args);

var values = new Dictionary<string, string>(StringComparer.Ordinal);
for (int i = 1; i < args.Length; i += 2)
{
    if (i + 1 >= args.Length || args[i] is not ("--data-dir" or "--report" or "--path" or "--days") || string.IsNullOrWhiteSpace(args[i + 1]) || !values.TryAdd(args[i], args[i + 1]))
    {
        Console.Error.WriteLine("Argumentos inválidos. Use --help.");
        return 1;
    }
}
bool isMaintenanceQuery = MaintenanceQueries.TryParseCommand(args[0], out var maintenanceQuery);
int instabilityDays = 30;
if (values.TryGetValue("--days", out var daysText) && (args[0] is not ("restart-history" or "memory-results")
    || !int.TryParse(daysText, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out instabilityDays) || instabilityDays is < 1 or > 365))
{
    Console.Error.WriteLine("--days aceita somente 1..365 em restart-history ou memory-results. Nenhuma coleta foi iniciada.");
    return 1;
}
if ((!isMaintenanceQuery && args[0] is not ("diagnose" or "inventory" or "history" or "folder-space"))
    || (args[0] == "history" && values.ContainsKey("--report"))
    || (args[0] == "folder-space") != values.ContainsKey("--path"))
{
    Console.Error.WriteLine("Comando inválido. Use --help.");
    return 1;
}
if (values.TryGetValue("--report", out var reportPath) && Path.GetExtension(reportPath) is not (".html" or ".json"))
{
    Console.Error.WriteLine("Use extensão .html ou .json. Nenhuma coleta foi iniciada.");
    return 1;
}

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };
try
{
    var store = Composition.CreateStore(values.GetValueOrDefault("--data-dir"));
    if (args[0] == "history")
    {
        var history = await store.ListAsync(cancellation.Token);
        if (!history.IsSuccess) { Console.Error.WriteLine(history.Error!.Message); return 1; }
        Console.WriteLine(JsonSerializer.Serialize(history.Value, DiagnosticJson.Options));
        return 0;
    }

    var progress = new Progress<string>(message => Console.Error.WriteLine(message));
    DiagnosticSession session;
    AppError? collectionFailure = null;
    if (isMaintenanceQuery)
    {
        var binding = Composition.CreateMaintenance(values.GetValueOrDefault("--data-dir"), instabilityDays).ResolveQuery(maintenanceQuery);
        if (!binding.IsSuccess) { Console.Error.WriteLine(binding.Error!.Message); return 1; }
        var collected = await binding.Value!.RunAsync(progress, cancellation.Token);
        if (!collected.IsSuccess)
        {
            Console.Error.WriteLine(collected.Error!.Message);
            if (collected.Value is null)
            {
                Console.WriteLine(JsonSerializer.Serialize(collected, DiagnosticJson.Options));
                return collected.Error.Code == ErrorCode.Cancelled ? 130 : 1;
            }
            collectionFailure = collected.Error;
        }
        session = collected.Value!;
    }
    else
    {
        var service = args[0] switch
        {
            "inventory" => Composition.CreateInventory(),
            "folder-space" => Composition.CreateFolderSpace(values["--path"]),
            _ => Composition.CreateDiagnostics()
        };
        session = await service.RunAsync(progress, cancellation.Token);
    }
    // A completed collection remains available even when history/report publication fails.
    Console.WriteLine(JsonSerializer.Serialize(session, DiagnosticJson.Options));
    var saved = await store.SaveAsync(session, cancellation.Token);
    if (!saved.IsSuccess) Console.Error.WriteLine($"Diagnóstico coletado e emitido em stdout, mas não salvo no histórico: {saved.Error!.Message}");
    if (values.TryGetValue("--report", out var path))
    {
        var extension = Path.GetExtension(path);
        if (extension is not (".html" or ".json")) { Console.Error.WriteLine("Use extensão .html ou .json."); return 1; }
        var exported = await new ReportExporter().ExportAsync(session, path, extension == ".html" ? ReportFormat.Html : ReportFormat.Json, cancellation.Token);
        if (!exported.IsSuccess) { Console.Error.WriteLine(exported.Error!.Message); return 1; }
    }

    return collectionFailure is not null ? collectionFailure.Code == ErrorCode.Cancelled ? 130 : 1
        : !saved.IsSuccess ? 1 : session.Status == SessionStatus.Complete ? 0 : 3;
}
catch (OperationCanceledException error) when (cancellation.IsCancellationRequested)
{
    Console.Error.WriteLine($"Coleta cancelada. Não foi publicada uma sessão incompleta como concluída. {error.Message}");
    return 130;
}
catch (Exception error)
{
    Console.Error.WriteLine(error.Message);
    return 1;
}
