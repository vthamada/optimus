using Optimus.Domain;

namespace Optimus.Application;

public enum ToolRunStatus { Exited, StartFailed, Failed, TimedOut, Cancelled, OutputLimitExceeded, CleanupFailed }
public enum ToolTextEncoding { Utf8Strict, Utf16LittleEndian, WindowsOem }
public sealed record ToolRequest(
    string ExecutablePath,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    TimeSpan Timeout,
    int MaximumOutputBytes = 1_048_576,
    IReadOnlyDictionary<string, string>? EnvironmentOverrides = null,
    string? ExpectedExecutableSha256 = null,
    long? MaximumMemoryBytes = null,
    IReadOnlyList<string>? EnvironmentVariablesToRemove = null,
    ToolTextEncoding OutputEncoding = ToolTextEncoding.Utf8Strict,
    bool PreserveOutputBytes = false);
public sealed record ToolMetrics(TimeSpan Elapsed, TimeSpan? CpuTime = null, long? PeakProcessFamilyMemoryBytes = null);
public sealed record ToolRunResult(
    ToolRunStatus Status, int? ExitCode, string StandardOutput, string StandardError,
    ToolMetrics Metrics, AppError? Error = null,
    byte[]? StandardOutputBytes = null, byte[]? StandardErrorBytes = null, int? OutputCodePage = null)
{
    public bool ExitedSuccessfully => Status == ToolRunStatus.Exited && ExitCode == 0 && Error is null;
}
public interface IExternalToolRunner
{
    Task<ToolRunResult> RunAsync(ToolRequest request, CancellationToken cancellationToken);
}
