using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Optimus.Application;
using Optimus.Domain;
using Optimus.Infrastructure.Tools;

namespace Optimus.Tests;

public sealed class ToolRuntimeTests
{
    [Fact]
    public async Task LiteralArgumentsIncludingQuotesUnicodeAndTrailingSlashReachTheOwnedProcess()
    {
        string[] values = ["", "plain", "with spaces", "a\"b", @"C:\folder with space\", "João & $(literal)"];
        var result = await new WindowsToolRunner().RunAsync(Request(["echo", .. values]), CancellationToken.None);

        Assert.True(result.ExitedSuccessfully, result.Error?.Message);
        Assert.Equal(values, JsonSerializer.Deserialize<string[]>(result.StandardOutput));
        Assert.NotNull(result.Metrics.CpuTime);
        Assert.NotNull(result.Metrics.PeakProcessFamilyMemoryBytes);
    }

    [Fact]
    public async Task CombinedOutputBudgetTerminatesTheOwnedJobWithoutRetainingUnboundedText()
    {
        var runner = new WindowsToolRunner();
        var result = await runner.RunAsync(Request(["flood"]) with { MaximumOutputBytes = 4096 }, CancellationToken.None);

        Assert.Equal(ToolRunStatus.OutputLimitExceeded, result.Status);
        Assert.True(Encoding.UTF8.GetByteCount(result.StandardOutput) + Encoding.UTF8.GetByteCount(result.StandardError) <= 4096);
        Assert.Empty(runner.PendingCleanupIds);
    }

    [Fact]
    public async Task DeadlineWaitsForOwnedProcessFinalization()
    {
        var runner = new WindowsToolRunner();
        var result = await runner.RunAsync(Request(["wait"]) with { Timeout = TimeSpan.FromMilliseconds(250) }, CancellationToken.None);

        Assert.Equal(ToolRunStatus.TimedOut, result.Status);
        Assert.NotNull(result.ExitCode);
        Assert.Empty(runner.PendingCleanupIds);
    }

    [Fact]
    public async Task CancellationTerminatesAndFinalizesOnlyTheOwnedJob()
    {
        using var cancellation = new CancellationTokenSource();
        var runner = new WindowsToolRunner();
        var directory = Path.Combine(Path.GetTempPath(), "optimus-runtime-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var marker = Path.Combine(directory, "started.txt");
        Task<ToolRunResult>? running = null;
        try
        {
            running = runner.RunAsync(Request(["wait", marker]), cancellation.Token);
            var deadline = Stopwatch.StartNew();
            while (!File.Exists(marker) && !running.IsCompleted && deadline.Elapsed < TimeSpan.FromSeconds(5))
                await Task.Delay(10);
            Assert.True(File.Exists(marker), "The fixture must complete its start handshake before cancellation.");
            cancellation.Cancel();
            var result = await running;
            Assert.Equal(ToolRunStatus.Cancelled, result.Status);
            Assert.Empty(runner.PendingCleanupIds);
        }
        finally
        {
            cancellation.Cancel();
            if (File.Exists(marker)) File.Delete(marker);
            Directory.Delete(directory, recursive: false);
        }
    }

    [Fact]
    public async Task DescendantRemainingAfterRootExitIsFinalizedBeforeSuccess()
    {
        var runner = new WindowsToolRunner();
        var result = await runner.RunAsync(Request(["child"]), CancellationToken.None);

        Assert.True(result.ExitedSuccessfully, result.Error?.Message);
        var line = result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries).Single(value => value.StartsWith("child:", StringComparison.Ordinal));
        var id = int.Parse(line[6..]);
        try
        {
            using var child = Process.GetProcessById(id);
            Assert.True(child.HasExited, "The known fixture descendant is still active after job finalization.");
        }
        catch (ArgumentException) { /* The process object is already gone. No PID-based termination is attempted. */ }
        Assert.Empty(runner.PendingCleanupIds);
    }

    [Fact]
    public async Task ExecutableDigestMismatchFailsBeforeExecution()
    {
        var result = await new WindowsToolRunner().RunAsync(Request(["echo", "must-not-run"]) with
        { ExpectedExecutableSha256 = new string('0', 64) }, CancellationToken.None);

        Assert.Equal(ToolRunStatus.StartFailed, result.Status);
        Assert.Equal("", result.StandardOutput);
    }

    [Fact]
    public async Task ExpectedDigestAndExplicitEnvironmentAreHonored()
    {
        var request = Request(["environment"]);
        var expected = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(request.ExecutablePath)));
        request = request with { ExpectedExecutableSha256 = expected, EnvironmentOverrides = new Dictionary<string, string> { ["OPTIMUS_FIXTURE_VALUE"] = "fixture-only" } };

        var result = await new WindowsToolRunner().RunAsync(request, CancellationToken.None);

        Assert.True(result.ExitedSuccessfully, result.Error?.Message);
        Assert.Equal("fixture-only", result.StandardOutput.Trim());
    }

    [Fact]
    public async Task ExplicitEnvironmentRemovalMeansAbsentAndOverridesApplyAfterRemoval()
    {
        var name = "OPTIMUS_FIXTURE_REMOVE_" + Guid.NewGuid().ToString("N").ToUpperInvariant();
        var before = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, "ambient-fixture-value");
        try
        {
            var request = Request(["environment-state", name]) with { EnvironmentVariablesToRemove = [name.ToLowerInvariant()] };
            var runner = new WindowsToolRunner();
            var absent = await runner.RunAsync(request, CancellationToken.None);
            Assert.True(absent.ExitedSuccessfully, absent.Error?.Message);
            using (var value = JsonDocument.Parse(absent.StandardOutput))
            {
                Assert.False(value.RootElement.GetProperty("exists").GetBoolean());
                Assert.Equal(JsonValueKind.Null, value.RootElement.GetProperty("value").ValueKind);
            }
            var overridden = await runner.RunAsync(request with
            {
                EnvironmentOverrides = new Dictionary<string, string> { [name] = "explicit-fixture-value" }
            }, CancellationToken.None);
            Assert.True(overridden.ExitedSuccessfully, overridden.Error?.Message);
            using (var value = JsonDocument.Parse(overridden.StandardOutput))
            {
                Assert.True(value.RootElement.GetProperty("exists").GetBoolean());
                Assert.Equal("explicit-fixture-value", value.RootElement.GetProperty("value").GetString());
            }
            Assert.Equal("ambient-fixture-value", Environment.GetEnvironmentVariable(name));
        }
        finally { Environment.SetEnvironmentVariable(name, before); }
    }

    [Theory]
    [InlineData("LLAMA_ARG_*")]
    [InlineData("BAD=NAME")]
    [InlineData("")]
    public async Task EnvironmentRemovalRejectsAmbiguousOrInvalidNamesBeforeExecution(string name)
    {
        var result = await new WindowsToolRunner().RunAsync(Request(["echo", "must-not-run"]) with
        { EnvironmentVariablesToRemove = [name] }, CancellationToken.None);
        Assert.Equal(ToolRunStatus.StartFailed, result.Status);
        Assert.Equal("", result.StandardOutput);
    }
    [Fact]
    public async Task InvalidUtf8NeverBecomesSuccessfulStructuredOutput()
    {
        var result = await new WindowsToolRunner().RunAsync(Request(["invalid-utf8"]), CancellationToken.None);

        Assert.Equal(ToolRunStatus.Failed, result.Status);
        Assert.Equal(ErrorCode.InvalidResponse, result.Error!.Code);
    }

    [Fact]
    public async Task NonzeroToolExitIsPreservedWithoutLosingStderr()
    {
        var result = await new WindowsToolRunner().RunAsync(Request(["exit"]), CancellationToken.None);

        Assert.Equal(ToolRunStatus.Exited, result.Status);
        Assert.Equal(7, result.ExitCode);
        Assert.Contains("fixture stderr", result.StandardError);
        Assert.False(result.ExitedSuccessfully);
    }

    [Fact]
    public async Task PreCancelledRequestDoesNotStartAProcess()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await new WindowsToolRunner().RunAsync(Request(["echo", "not-started"]), cancellation.Token);

        Assert.Equal(ToolRunStatus.Cancelled, result.Status);
        Assert.Null(result.ExitCode);
        Assert.Equal("", result.StandardOutput);
    }

    [Fact]
    public async Task DeclaredUtf16LittleEndianPreservesBothStreamsAndTheirExactBytes()
    {
        var result = await new WindowsToolRunner().RunAsync(Request(["utf16-output"]) with
        { OutputEncoding = ToolTextEncoding.Utf16LittleEndian, PreserveOutputBytes = true }, CancellationToken.None);

        Assert.True(result.ExitedSuccessfully, result.Error?.Message);
        Assert.Equal("João Ω", result.StandardOutput);
        Assert.Equal("erro João Ω", result.StandardError);
        Assert.Equal(1200, result.OutputCodePage);
        Assert.Equal(new UnicodeEncoding(false, false, true).GetBytes("João Ω"), result.StandardOutputBytes);
        Assert.Equal(new UnicodeEncoding(false, false, true).GetBytes("erro João Ω"), result.StandardErrorBytes);
    }

    [Fact]
    public async Task DeclaredOemPageUsesTheActualWindowsPageWithoutUtf8Guessing()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        int page = checked((int)GetOEMCP());
        var encoding = Encoding.GetEncoding(page);
        byte[] expectedOutput = encoding.GetBytes("João");
        byte[] expectedError = encoding.GetBytes("erro João");

        var result = await new WindowsToolRunner().RunAsync(Request(["oem-output"]) with
        { OutputEncoding = ToolTextEncoding.WindowsOem, PreserveOutputBytes = true }, CancellationToken.None);

        Assert.True(result.ExitedSuccessfully, result.Error?.Message);
        Assert.Equal(page, result.OutputCodePage);
        Assert.Equal(expectedOutput, result.StandardOutputBytes);
        Assert.Equal(expectedError, result.StandardErrorBytes);
        // Some OEM pages cannot encode ã. The fixture's producer bytes, not a guessed locale,
        // determine its observable text; on supported pages this is the literal João.
        Assert.Equal(encoding.GetString(expectedOutput), result.StandardOutput);
        Assert.Equal(encoding.GetString(expectedError), result.StandardError);
    }

    [Fact]
    public async Task CompleteOddUtf16IsInvalidEvenWhenTheProcessExitsZero()
    {
        var result = await new WindowsToolRunner().RunAsync(Request(["invalid-utf16"]) with
        { OutputEncoding = ToolTextEncoding.Utf16LittleEndian, PreserveOutputBytes = true }, CancellationToken.None);

        Assert.Equal(ToolRunStatus.Failed, result.Status);
        Assert.Equal(ErrorCode.InvalidResponse, result.Error!.Code);
        Assert.False(result.ExitedSuccessfully);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(1200, result.OutputCodePage);
        Assert.Equal(new byte[] { 0x4a, 0x00, 0x6f }, result.StandardOutputBytes);
    }

    [Fact]
    public async Task Utf16BudgetEndingInsideASurrogateRetainsOnlyTheValidTextPrefixAndExactRawBytes()
    {
        var request = Request(["utf16-limit-boundary"]) with
        { OutputEncoding = ToolTextEncoding.Utf16LittleEndian, PreserveOutputBytes = true, MaximumOutputBytes = 256 };
        var runner = new WindowsToolRunner();
        var result = await runner.RunAsync(request, CancellationToken.None);
        var complete = new UnicodeEncoding(false, false, true).GetBytes(new string('a', 127) + "🙂" + new string('b', 2048));

        Assert.Equal(ToolRunStatus.OutputLimitExceeded, result.Status);
        Assert.Equal(ErrorCode.OutputLimitExceeded, result.Error!.Code);
        Assert.Equal(new string('a', 127), result.StandardOutput);
        Assert.DoesNotContain('\ufffd', result.StandardOutput);
        Assert.Equal(complete[..256], result.StandardOutputBytes);
        Assert.Equal(1200, result.OutputCodePage);
        Assert.NotNull(result.StandardErrorBytes);
        Assert.True(result.StandardOutputBytes!.Length + result.StandardErrorBytes!.Length <= request.MaximumOutputBytes);
        Assert.Empty(runner.PendingCleanupIds);
    }

    [Fact]
    public async Task RawOutputRemainsOptionalAndDefaultUtf8SemanticsAreUnchanged()
    {
        var result = await new WindowsToolRunner().RunAsync(Request(["echo", "João Ω"]), CancellationToken.None);

        Assert.True(result.ExitedSuccessfully, result.Error?.Message);
        Assert.Equal(new[] { "João Ω" }, JsonSerializer.Deserialize<string[]>(result.StandardOutput));
        Assert.Equal(65001, result.OutputCodePage);
        Assert.Null(result.StandardOutputBytes);
        Assert.Null(result.StandardErrorBytes);
    }

    [Fact]
    public async Task InvalidEncodingEnumIsRejectedBeforeTheFixtureExecutes()
    {
        var result = await new WindowsToolRunner().RunAsync(Request(["echo", "must-not-run"]) with
        { OutputEncoding = (ToolTextEncoding)int.MaxValue, PreserveOutputBytes = true }, CancellationToken.None);

        Assert.Equal(ToolRunStatus.StartFailed, result.Status);
        Assert.Null(result.ExitCode);
        Assert.Equal("", result.StandardOutput);
        Assert.Equal("", result.StandardError);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task RawCaptureHonorsTheSharedBudgetAcrossBothStreams()
    {
        var request = Request(["utf16-both-flood"]) with
        { OutputEncoding = ToolTextEncoding.Utf16LittleEndian, PreserveOutputBytes = true, MaximumOutputBytes = 256 };
        var result = await new WindowsToolRunner().RunAsync(request, CancellationToken.None);

        Assert.Equal(ToolRunStatus.OutputLimitExceeded, result.Status);
        var stdout = Assert.IsType<byte[]>(result.StandardOutputBytes);
        var stderr = Assert.IsType<byte[]>(result.StandardErrorBytes);
        Assert.Equal(256, stdout.Length + stderr.Length);
        Assert.True(stdout.Length + stderr.Length <= request.MaximumOutputBytes);
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetOEMCP();

    private static ToolRequest Request(IReadOnlyList<string> arguments)
    {
        var projectRoot = FindProjectRoot();
        var configuration = AppContext.BaseDirectory.Contains(Path.DirectorySeparatorChar + "Release" + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? "Release" : "Debug";
        var executable = Path.Combine(projectRoot, "tests", "Optimus.ToolFixture", "bin", configuration, "net10.0-windows", "Optimus.ToolFixture.exe");
        return new(executable, arguments, Path.GetDirectoryName(executable)!, TimeSpan.FromSeconds(15));
    }

    private static string FindProjectRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Optimus.slnx"))) return directory.FullName;
        throw new InvalidOperationException("The tool fixture requires the source checkout.");
    }
}
