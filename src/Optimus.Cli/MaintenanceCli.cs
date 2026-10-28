using System.Text.Json;
using Optimus.Application;
using Optimus.Domain;
using Optimus.Infrastructure;

internal static class MaintenanceCli
{
    public static async Task<int> RunIntegrityAsync(string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, value) => { value.Cancel = true; cancellation.Cancel(); };
        try
        {
            var flags = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int index = 1; index < args.Length; index += 2)
                if (index + 1 >= args.Length || args[index] is not ("--operation" or "--data-dir" or "--execute")
                    || string.IsNullOrWhiteSpace(args[index + 1]) || !flags.TryAdd(args[index], args[index + 1]))
                    throw new ArgumentException("Use somente pares --operation, --data-dir e --execute true, sem duplicações.");
            if (!flags.TryGetValue("--operation", out var name) || !MaintenanceOperations.TryParseIntegrity(name, out var operation))
                throw new ArgumentException("Escolha sfc-verify, dism-check ou dism-scan.");
            if (flags.TryGetValue("--execute", out var execute) && execute != "true")
                throw new ArgumentException("Para autorizar a execução use --execute true; para prévia omita essa opção.");
            var service = Composition.CreateMaintenance(flags.GetValueOrDefault("--data-dir")).Integrity;
            var result = await service.PlanAsync(operation, cancellation.Token);
            if (!result.IsSuccess || !flags.ContainsKey("--execute")) return Print(result);
            Console.Error.WriteLine(JsonSerializer.Serialize(result.Value, DiagnosticJson.Options));
            var receipt = await service.ExecuteAsync(result.Value!, result.Value!.Id, cancellation.Token);
            return Print(receipt);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return Print(Result<IntegrityReceipt>.Failure(ErrorCode.Cancelled, "Solicitação cancelada; preserve qualquer diário já criado antes de repetir."));
        }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return Print(Result<IntegrityReceipt>.Failure(ErrorCode.InvalidConfiguration, error.Message));
        }
    }

    private static int Print<T>(Result<T> result)
    {
        Console.WriteLine(JsonSerializer.Serialize(result, DiagnosticJson.Options));
        return result.IsSuccess ? 0 : result.Error!.Code == ErrorCode.Cancelled ? 130 : 1;
    }
}
