using System.Security.Cryptography;
using System.Text;
using Optimus.Application;
using Optimus.Domain;
using Optimus.Infrastructure.SystemTools;

namespace Optimus.Tests;

public sealed class SystemQueryRunnerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "optimus-system-query-tests", Guid.NewGuid().ToString("N"), "d'água Ω");
    private readonly string _executable;

    public SystemQueryRunnerTests()
    {
        var executableDirectory = Path.Combine(_root, "WindowsPowerShell", "v1.0");
        Directory.CreateDirectory(executableDirectory);
        _executable = Path.Combine(executableDirectory, "powershell.exe");
        File.WriteAllBytes(_executable, [3, 1, 4, 1, 5]);
    }

    [Theory]
    [InlineData(WindowsSystemQuery.DefenderStatus, "Defender\\Get-MpComputerStatus", 65_536, 60)]
    [InlineData(WindowsSystemQuery.DeviceInventory, "Get-CimInstance", 1_048_576, 120)]
    [InlineData(WindowsSystemQuery.ServiceInventory, "Win32_Service", 262_144, 120)]
    [InlineData(WindowsSystemQuery.UpdateHistory, "QueryHistory", 262_144, 120)]
    [InlineData(WindowsSystemQuery.ProcessInventory, "Win32_Process", 262_144, 120)]
    [InlineData(WindowsSystemQuery.TaskInventory, "Schedule.Service", 262_144, 120)]
    [InlineData(WindowsSystemQuery.StorageInventory, "Win32_DiskDrive", 262_144, 120)]
    [InlineData(WindowsSystemQuery.NetworkConnections, "Get-NetTCPConnection", 262_144, 120)]
    [InlineData(WindowsSystemQuery.FirewallProfiles, "Get-NetFirewallProfile", 65_536, 60)]
    [InlineData(WindowsSystemQuery.RestorePoints, "SystemRestore", 262_144, 120)]
    [InlineData(WindowsSystemQuery.BitLockerStatus, "Win32_EncryptableVolume", 262_144, 120)]
    [InlineData(WindowsSystemQuery.HardwareProfile, "Win32_PhysicalMemory", 262_144, 120)]
    public async Task CompiledQueriesBindTheSelectedExecutableAndConfineTheProcessEnvironment(
        WindowsSystemQuery query, string expectedCommand, int outputBudget, int timeoutSeconds)
    {
        bool writeBlocked = false;
        var runner = new ProbeRunner(request =>
        {
            try { using var file = new FileStream(_executable, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete); }
            catch (IOException) { writeBlocked = true; }
            catch (UnauthorizedAccessException) { writeBlocked = true; }
            return Completed();
        });

        var result = await new WindowsSystemQueryRunner(runner, () => _root).RunAsync(query, CancellationToken.None);

        Assert.True(result.ExitedSuccessfully, result.Error?.Message);
        var request = Assert.IsType<ToolRequest>(runner.Request);
        Assert.Equal(1, runner.Calls);
        Assert.Equal(_executable, request.ExecutablePath);
        Assert.Equal(_root, request.WorkingDirectory);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(_executable))), request.ExpectedExecutableSha256);
        Assert.True(writeBlocked);
        Assert.Equal(ToolTextEncoding.Utf8Strict, request.OutputEncoding);
        Assert.Contains("-NoProfile", request.Arguments);
        Assert.Contains("-NonInteractive", request.Arguments);
        Assert.DoesNotContain("-ExecutionPolicy", request.Arguments);
        Assert.DoesNotContain("Bypass", request.Arguments);
        Assert.DoesNotContain("-File", request.Arguments);
        Assert.Equal(outputBudget, request.MaximumOutputBytes);
        Assert.Equal(TimeSpan.FromSeconds(timeoutSeconds), request.Timeout);
        Assert.Equal(512L * 1024 * 1024, request.MaximumMemoryBytes);
        var environment = Assert.IsAssignableFrom<IReadOnlyDictionary<string, string>>(request.EnvironmentOverrides);
        Assert.Equal(2, environment.Count);
        Assert.Equal(_root, environment["PATH"]);
        Assert.Equal(Path.Combine(_root, "WindowsPowerShell", "v1.0", "Modules"), environment["PSModulePath"]);
        var arguments = request.Arguments.ToList();
        var script = Encoding.Unicode.GetString(Convert.FromBase64String(arguments[arguments.IndexOf("-EncodedCommand") + 1]));
        Assert.Contains("[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)", script);
        Assert.Contains("$optimusSystemDirectory = '" + _root.Replace("'", "''", StringComparison.Ordinal) + "'", script);
        Assert.Contains(expectedCommand, script);
        Assert.Contains("$ErrorActionPreference = 'Stop'", script);
        Assert.Contains("[Console]::Error.WriteLine($_.Exception.Message)", script);
    }

    [Fact]
    public async Task UnknownQueryCannotResolvePathsOrReachTheProcessRunner()
    {
        var runner = new ProbeRunner(_ => throw new InvalidOperationException("Not an allowed query."));
        var host = new WindowsSystemQueryRunner(runner, () => throw new InvalidOperationException("Must validate the catalog first."));

        var result = await host.RunAsync((WindowsSystemQuery)999, CancellationToken.None);

        Assert.Equal(ToolRunStatus.StartFailed, result.Status);
        Assert.Equal(ErrorCode.InvalidConfiguration, result.Error!.Code);
        Assert.Equal(0, runner.Calls);
    }

    [Fact]
    public async Task PreCancelledQueryDoesNotResolveFilesOrStartATool()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var runner = new ProbeRunner(_ => throw new InvalidOperationException("Must not execute."));
        var host = new WindowsSystemQueryRunner(runner, () => throw new InvalidOperationException("Must not inspect paths."));

        var result = await host.RunAsync(WindowsSystemQuery.DefenderStatus, cancelled.Token);

        Assert.Equal(ToolRunStatus.Cancelled, result.Status);
        Assert.Equal(ErrorCode.Cancelled, result.Error!.Code);
        Assert.Equal(0, runner.Calls);
    }

    private static ToolRunResult Completed() => new(ToolRunStatus.Exited, 0, "fixture output", "", new(TimeSpan.Zero));
    private sealed class ProbeRunner(Func<ToolRequest, ToolRunResult> run) : IExternalToolRunner
    {
        public ToolRequest? Request { get; private set; }
        public int Calls { get; private set; }
        public Task<ToolRunResult> RunAsync(ToolRequest request, CancellationToken cancellationToken)
        { Request = request; Calls++; return Task.FromResult(run(request)); }
    }

    public void Dispose()
    {
        // Only this fixture creates anything below the unpredictable directory.
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
