using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Optimus.Application;
using Optimus.Domain;
using Optimus.Infrastructure.Files;

namespace Optimus.Infrastructure.SystemTools;

/// <summary>Runs a compiled query catalog. Neither scripts nor arguments are caller-supplied.</summary>
public sealed class WindowsSystemQueryRunner(IExternalToolRunner runner, Func<string>? systemDirectory = null) : IWindowsSystemQueryRunner
{
    public Task<ToolRunResult> RunAsync(WindowsSystemQuery query, CancellationToken cancellationToken) =>
        Task.Run(async () =>
        {
            var timer = Stopwatch.StartNew();
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                string scriptName = query switch
                {
                    WindowsSystemQuery.DefenderStatus => "DefenderStatus.ps1",
                    WindowsSystemQuery.DeviceInventory => "DeviceInventory.ps1",
                    WindowsSystemQuery.ServiceInventory => "ServiceInventory.ps1",
                    WindowsSystemQuery.UpdateHistory => "UpdateHistory.ps1",
                    WindowsSystemQuery.ProcessInventory => "ProcessInventory.ps1",
                    WindowsSystemQuery.TaskInventory => "TaskInventory.ps1",
                    WindowsSystemQuery.StorageInventory => "StorageInventory.ps1",
                    WindowsSystemQuery.NetworkConnections => "NetworkConnections.ps1",
                    WindowsSystemQuery.FirewallProfiles => "FirewallProfiles.ps1",
                    WindowsSystemQuery.RestorePoints => "RestorePoints.ps1",
                    WindowsSystemQuery.BitLockerStatus => "BitLockerStatus.ps1",
                    WindowsSystemQuery.HardwareProfile => "HardwareProfile.ps1",
                    _ => throw new InvalidDataException("A consulta não pertence ao catálogo de ferramentas do Windows.")
                };
                if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess)
                    return Failure(ErrorCode.InvalidConfiguration, "As consultas nativas exigem Windows com processo de 64 bits.");
                var directory = LocalPathGuard.LocalPath((systemDirectory ?? (() => Environment.GetFolderPath(Environment.SpecialFolder.System)))());
                using var directoryLease = LocalPathGuard.HoldDirectory(directory);
                var executablePath = LocalPathGuard.LocalPath(Path.Combine(directory, "WindowsPowerShell", "v1.0", "powershell.exe"));
                using var executable = new FileStream(executablePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(executable, cancellationToken).ConfigureAwait(false));
                var resource = "Optimus.Infrastructure.SystemTools.Scripts." + scriptName;
                using var content = typeof(WindowsSystemQueryRunner).Assembly.GetManifestResourceStream(resource)
                    ?? throw new InvalidDataException("A consulta compilada não está disponível no pacote.");
                if (content.Length > 65_536) throw new InvalidDataException("A consulta excede o limite do catálogo embutido.");
                using var reader = new StreamReader(content, new UTF8Encoding(false, true));
                var body = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
                var literalDirectory = directory.Replace("'", "''", StringComparison.Ordinal);
                var prefix = "[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)\n"
                    + "$OutputEncoding = [Console]::OutputEncoding\n$ErrorActionPreference = 'Stop'\n$ProgressPreference = 'SilentlyContinue'\n"
                    + "$optimusSystemDirectory = '" + literalDirectory + "'\ntry {\n";
                var script = prefix + body + "\n} catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }\n";
                // Base64(UTF-16) expansion must fit CreateProcess's 32767-character command line.
                if (script.Length > 10_000) throw new InvalidDataException("A consulta não cabe no orçamento de argumentos do Windows.");
                var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
                var moduleDirectory = Path.Combine(directory, "WindowsPowerShell", "v1.0", "Modules");
                return await runner.RunAsync(new(executablePath,
                    ["-NoLogo", "-NoProfile", "-NonInteractive", "-InputFormat", "Text", "-OutputFormat", "Text", "-EncodedCommand", encoded],
                    directory, TimeSpan.FromSeconds(query is WindowsSystemQuery.DefenderStatus or WindowsSystemQuery.FirewallProfiles ? 60 : 120),
                    query switch { WindowsSystemQuery.DefenderStatus or WindowsSystemQuery.FirewallProfiles => 65_536, WindowsSystemQuery.DeviceInventory => 1_048_576, _ => 262_144 },
                    EnvironmentOverrides: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    { ["PSModulePath"] = moduleDirectory, ["PATH"] = directory },
                    ExpectedExecutableSha256: hash, MaximumMemoryBytes: 512L * 1024 * 1024), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return new(ToolRunStatus.Cancelled, null, "", "", new(timer.Elapsed), new(ErrorCode.Cancelled, "Consulta cancelada antes de iniciar a ferramenta."));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException
                or NotSupportedException or System.Security.SecurityException)
            {
                return Failure(error is FileNotFoundException ? ErrorCode.DependencyUnavailable
                    : error is UnauthorizedAccessException or System.Security.SecurityException ? ErrorCode.PermissionDenied : ErrorCode.InvalidConfiguration, error.Message);
            }

            ToolRunResult Failure(ErrorCode code, string message) => new(ToolRunStatus.StartFailed, null, "", "", new(timer.Elapsed), new(code, message));
        }, CancellationToken.None);
}
