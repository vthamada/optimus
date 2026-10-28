using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Optimus.Application;
using Optimus.Domain;
using Optimus.Infrastructure.Crashes;

namespace Optimus.Tests;

// Fake executor exercises the production collector/path boundary, not a real CDB or dump analysis.
public sealed class CrashDumpTests
{
    private const string Output = "BUGCHECK_CODE:  1a\r\nMODULE_NAME: memory_corruption\r\nIMAGE_NAME:  memory_corruption\r\nFAILURE_BUCKET_ID:  fixture!unknown\r\n";

    [Fact]
    public async Task SelectedSnapshotUsesFixedArgumentsBoundedJobAndRetainsOriginalObservation()
    {
        using var fixture = new Fixture();
        var host = new Host(_ => Completed(Output));
        var evidence = await fixture.Collect(host);
        var request = Assert.IsType<ToolRequest>(host.Request);
        Assert.Equal(fixture.Debugger, request.ExecutablePath);
        Assert.Equal(fixture.Dump, Argument(request, "-z"));
        Assert.Equal(fixture.Symbols, Argument(request, "-y"));
        Assert.Equal("!analyze -v; q", Argument(request, "-c"));
        Assert.Contains("-sins", request.Arguments);
        Assert.Contains("-noshell", request.Arguments);
        Assert.Contains("-nosqm", request.Arguments);
        Assert.Contains("-netsyms:no", request.Arguments);
        Assert.Contains("-failinc", request.Arguments);
        Assert.DoesNotContain("-secure", request.Arguments);
        Assert.DoesNotContain("-p", request.Arguments);
        Assert.DoesNotContain("-pn", request.Arguments);
        Assert.DoesNotContain("-ia", request.Arguments);
        Assert.DoesNotContain(fixture.Dump, Argument(request, "-c"));
        Assert.Equal(120, request.Timeout.TotalSeconds);
        Assert.Equal(262144, request.MaximumOutputBytes);
        Assert.Equal(1073741824L, request.MaximumMemoryBytes);
        Assert.True(request.PreserveOutputBytes);
        Assert.Equal(ToolTextEncoding.WindowsOem, request.OutputEncoding);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(fixture.Debugger))), request.ExpectedExecutableSha256);
        Assert.Equal(Output, Fact(evidence, "sourceOutput").TextValue);
        Assert.Equal("  1a", Fact(evidence, "bugcheckCode").TextValue);
        Assert.Equal(26m, Fact(evidence, "bugcheckCode.numeric").NumberValue);
        Assert.Equal(" memory_corruption", Fact(evidence, "moduleName").TextValue);
        Assert.Equal("unknown", Fact(evidence, "symbolCompleteness").TextValue);
        Assert.Equal(EvidenceStatus.Partial, evidence.Status);
        Assert.Equal("false", Fact(evidence, "liveProcessRequested").TextValue);
        Assert.Equal("false", Fact(evidence, "sourceCopied").TextValue);
        Assert.DoesNotContain(evidence.Facts, fact => fact.Key is "ramFailure" or "faultyDriver" or "rootCause");
        Assert.Contains("não comprovam a causa", Fact(evidence, "causalConclusion").TextValue);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(fixture.Dump))), Fact(evidence, "dumpSha256").TextValue);
        Assert.Equal(1, evidence.Coverage.ExaminedItems);
        Assert.True(SessionValidation.IsValid(new(Guid.NewGuid(), evidence.CapturedAt, evidence.CapturedAt, SessionStatus.Partial, [evidence], [])));
        var operation = Fact(evidence, "operationDirectory").TextValue!;
        Assert.Equal(operation, request.WorkingDirectory);
        Assert.Equal(0L, new FileInfo(Argument(request, "-cf")).Length);
        Assert.Equal(new[] { "request.json", "result.json", "startup.txt" }, Directory.GetFiles(operation).Select(Path.GetFileName).Order().ToArray());
        using var result = JsonDocument.Parse(File.ReadAllText(Path.Combine(operation, "result.json")));
        Assert.Equal(Output, result.RootElement.GetProperty("stdout").GetString());
        Assert.Equal(32, new FileInfo(fixture.Dump).Length);
    }

    [Fact]
    public async Task InputFileAndDebuggerLeasesBlockWritesRenamesAndStartupInjectionDuringTheCall()
    {
        using var fixture = new Fixture();
        var host = new Host(request =>
        {
            Assert.ThrowsAny<IOException>(() => File.WriteAllText(fixture.Dump, "replacement"));
            Assert.ThrowsAny<IOException>(() => File.Move(fixture.Dump, fixture.Dump + ".moved"));
            Assert.ThrowsAny<IOException>(() => File.WriteAllText(fixture.Debugger, "replacement"));
            Assert.ThrowsAny<IOException>(() => File.WriteAllText(Argument(request, "-cf"), ".shell bad"));
            return Completed(Output);
        });
        await fixture.Collect(host);
        Assert.Equal(1, host.Calls);
        // Successful finalization releases the read leases; the caller retains its original source.
        using var dump = new FileStream(fixture.Dump, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var debugger = new FileStream(fixture.Debugger, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public async Task SymbolAndInitializationEnvironmentCannotSelectServersScriptsOrExternalLogs()
    {
        using var fixture = new Fixture();
        var host = new Host(_ => Completed(Output));
        await fixture.Collect(host);
        var request = host.Request!;
        var removed = request.EnvironmentVariablesToRemove!;
        foreach (var name in new[] { "INIT", "_NT_SYMBOL_PATH", "_NT_ALT_SYMBOL_PATH", "_NT_EXECUTABLE_IMAGE_PATH", "_NT_DEBUGGER_EXTENSION_PATH", "_NT_DEBUG_LOG_FILE_OPEN", "_NT_DEBUG_LOG_FILE_APPEND", "SRCSRV_INI_FILE", "DBGHELP_HOMEDIR" })
            Assert.Contains(name, removed);
        Assert.Equal(Path.GetDirectoryName(fixture.Debugger), request.EnvironmentOverrides!["PATH"]);
        Assert.Equal(request.WorkingDirectory, request.EnvironmentOverrides["TEMP"]);
        Assert.Equal(request.WorkingDirectory, request.EnvironmentOverrides["TMP"]);
        Assert.Equal(request.WorkingDirectory, Argument(request, "-srcpath"));
        Assert.Equal(request.WorkingDirectory, Argument(request, "-i"));
        Assert.NotEqual(fixture.Symbols, request.WorkingDirectory);
        Assert.Contains("0x020B16A7", request.Arguments);
    }

    [Theory]
    [InlineData("MDMP")]
    [InlineData("PAGEDUMP")]
    [InlineData("PAGEDU64")]
    public async Task HeaderKindsAreSignaturesAndNotClaimsOfACompleteValidDump(string signature)
    {
        using var fixture = new Fixture(signature);
        var evidence = await fixture.Collect(new Host(_ => Completed(Output)));
        Assert.Equal(signature, Fact(evidence, "dumpKind").TextValue);
        Assert.Contains("não valida", Fact(evidence, "dumpKind").Label);
    }

    [Theory]
    [InlineData("BAD!")]
    [InlineData("PAGEBAD!")]
    public async Task UnrecognizedHeaderCannotStartTheDebugger(string header)
    {
        using var fixture = new Fixture(header);
        var host = new Host(_ => throw new InvalidOperationException("must not run"));
        var evidence = await fixture.Collect(host);
        Assert.Equal(0, host.Calls);
        Assert.Equal(EvidenceStatus.Unavailable, evidence.Status);
        Assert.DoesNotContain(evidence.Facts, fact => fact.Key == "operationDirectory");
    }

    [Fact]
    public async Task OversizedAndUndersizedDumpsStopBeforeHashingOrExecution()
    {
        using var fixture = new Fixture();
        using (var stream = new FileStream(fixture.Dump, FileMode.Open, FileAccess.Write))
        {
            // Only this fixture is marked sparse: exercise the size boundary without allocating 2 GiB.
            Assert.True(DeviceIoControl(stream.SafeFileHandle, 0x000900C4, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero));
            stream.SetLength(CrashDumpCollector.MaximumDumpBytes + 1);
        }
        var host = new Host(_ => Completed(Output));
        var evidence = await fixture.Collect(host);
        Assert.Equal(EvidenceStatus.Unavailable, evidence.Status);
        Assert.True(evidence.Coverage.LimitReached);
        Assert.DoesNotContain(evidence.Facts, fact => fact.Key == "dumpSha256");
        Assert.Equal(0, host.Calls);
        using (var stream = new FileStream(fixture.Dump, FileMode.Open, FileAccess.Write)) stream.SetLength(8);
        var shortDump = await fixture.Collect(host);
        Assert.Equal(EvidenceStatus.Unavailable, shortDump.Status);
        Assert.Equal(0, host.Calls);
    }

    [Fact]
    public async Task ExpectedDebuggerDigestMismatchDoesNotReachTheRunnerOrCreateAnOperation()
    {
        using var fixture = new Fixture();
        var host = new Host(_ => Completed(Output));
        var evidence = await fixture.Collect(host, fixture.Settings with { ExpectedDebuggerSha256 = new string('A', 64) });
        Assert.Equal(0, host.Calls);
        Assert.Equal(EvidenceStatus.Unavailable, evidence.Status);
        Assert.DoesNotContain(evidence.Facts, fact => fact.Key == "operationDirectory");
    }

    [Theory]
    [InlineData("MODULE_NAME", "moduleName")]
    [InlineData("IMAGE_NAME", "imageName")]
    [InlineData("BUGCHECK_CODE", "bugcheckCode")]
    [InlineData("FAILURE_BUCKET_ID", "failureBucketId")]
    public async Task EvenIdenticalDuplicateFieldsRemainAmbiguous(string sourceKey, string factKey)
    {
        using var fixture = new Fixture();
        string duplicate = Output + Output.Split("\r\n").Single(line => line.StartsWith(sourceKey + ":", StringComparison.Ordinal)) + "\r\n";
        var evidence = await fixture.Collect(new Host(_ => Completed(duplicate)));
        Assert.Null(Fact(evidence, factKey).TextValue);
        Assert.Equal(2m, Fact(evidence, factKey + ".matchCount").NumberValue);
        Assert.Equal(duplicate, Fact(evidence, "sourceOutput").TextValue);
        Assert.Contains(evidence.Issues, issue => issue.Message.Contains(sourceKey, StringComparison.Ordinal));
    }

    [Fact]
    public async Task AbsentMalformedAndOversizedFieldsDoNotBecomeInventedResults()
    {
        using var fixture = new Fixture();
        string source = "BUGCHECK_CODE: not-hex\nMODULE_NAME: " + new string('X', 1025) + "\nIMAGE_NAME: \n";
        var evidence = await fixture.Collect(new Host(_ => Completed(source)));
        Assert.Equal(" not-hex", Fact(evidence, "bugcheckCode").TextValue);
        Assert.Null(Fact(evidence, "bugcheckCode.numeric").NumberValue);
        Assert.Null(Fact(evidence, "moduleName").TextValue);
        Assert.Null(Fact(evidence, "imageName").TextValue);
        Assert.Null(Fact(evidence, "failureBucketId").TextValue);
        Assert.Equal(source, Fact(evidence, "sourceOutput").TextValue);
        Assert.True(evidence.Coverage.LimitReached);
    }

    [Fact]
    public async Task MissingSymbolsAndMemoryCorruptionAreObservationsWithoutARamVerdict()
    {
        using var fixture = new Fixture();
        string source = "*** ERROR: Module load completed but symbols could not be loaded\n" + Output;
        var evidence = await fixture.Collect(new Host(_ => Completed(source)));
        Assert.Equal(EvidenceStatus.Partial, evidence.Status);
        Assert.Equal("unknown", Fact(evidence, "symbolCompleteness").TextValue);
        Assert.Equal(source, Fact(evidence, "sourceOutput").TextValue);
        Assert.Contains(evidence.Issues, issue => issue.Message.Contains("sem diagnóstico causal", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(ToolRunStatus.TimedOut)]
    [InlineData(ToolRunStatus.OutputLimitExceeded)]
    [InlineData(ToolRunStatus.Failed)]
    public async Task FailedCaptureRetainsBytesPrimaryErrorMetricsAndReceiptWithoutParsingPartialFields(ToolRunStatus status)
    {
        using var fixture = new Fixture();
        var execution = new ToolRunResult(status, null, Output, "original stderr Ω", new(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(1), 1048576),
            new(ErrorCode.ToolFailed, "original primary reason"), [1, 2], [3, 4], 437);
        var evidence = await fixture.Collect(new Host(_ => execution));
        Assert.Equal(EvidenceStatus.Partial, evidence.Status);
        Assert.Equal(Output, Fact(evidence, "sourceOutput").TextValue);
        Assert.Equal("AQI=", Fact(evidence, "sourceOutputBytesBase64").TextValue);
        Assert.Equal("AwQ=", Fact(evidence, "sourceErrorBytesBase64").TextValue);
        Assert.Equal("original primary reason", Fact(evidence, "toolErrorMessage").TextValue);
        Assert.Equal(437m, Fact(evidence, "toolOutputCodePage").NumberValue);
        Assert.Equal(3m, Fact(evidence, "elapsedSeconds").NumberValue);
        Assert.Equal(1m, Fact(evidence, "cpuSeconds").NumberValue);
        Assert.DoesNotContain(evidence.Facts, fact => fact.Key == "moduleName");
        Assert.True(File.Exists(Path.Combine(Fact(evidence, "operationDirectory").TextValue!, "result.json")));
        if (status == ToolRunStatus.OutputLimitExceeded) Assert.True(evidence.Coverage.LimitReached);
    }

    [Fact]
    public async Task ExcessiveTransportMetadataCannotExpandTheRetainedReceiptWithoutBound()
    {
        using var fixture = new Fixture();
        var result = Completed(Output) with
        {
            Error = new(ErrorCode.ToolFailed, new string('X', 9000)),
            StandardOutput = new string('Ω', 140000),
            StandardOutputBytes = new byte[262145]
        };
        var evidence = await fixture.Collect(new Host(_ => result));
        Assert.Null(Fact(evidence, "toolErrorMessage").TextValue);
        Assert.Null(Fact(evidence, "sourceOutput").TextValue);
        Assert.Null(Fact(evidence, "sourceOutputBytesBase64").TextValue);
        Assert.True(evidence.Coverage.LimitReached);
        Assert.InRange(new FileInfo(Path.Combine(Fact(evidence, "operationDirectory").TextValue!, "result.json")).Length, 1, 4194304);
    }

    [Fact]
    public async Task UnavailableSelectedInputsAndSymbolSyntaxCannotFallBackToAnotherToolOrServer()
    {
        using var fixture = new Fixture();
        var host = new Host(_ => Completed(Output));
        var missing = await fixture.Collect(host, fixture.Settings with { DebuggerExecutablePath = Path.Combine(Path.GetDirectoryName(fixture.Debugger)!, "missing", "cdb.exe") });
        Assert.Equal(EvidenceStatus.Unavailable, missing.Status);
        Directory.CreateDirectory(fixture.Symbols + ";srv");
        var list = await fixture.Collect(host, fixture.Settings with { SymbolDirectory = fixture.Symbols + ";srv" });
        Assert.Equal(EvidenceStatus.Unavailable, list.Status);
        var remote = await fixture.Collect(host, fixture.Settings with { SymbolDirectory = @"\\server\symbols" });
        Assert.Equal(EvidenceStatus.Unavailable, remote.Status);
        var wrongTool = await fixture.Collect(host, fixture.Settings with { DebuggerExecutablePath = fixture.Dump });
        Assert.Equal(EvidenceStatus.Unavailable, wrongTool.Status);
        Assert.Equal(0, host.Calls);
    }

    [Fact]
    public async Task ExclusiveCapacityDoesNotAdoptUnknownFilesOrRemovePriorRuns()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(fixture.Work);
        string marker = Path.Combine(fixture.Work, ".capacity.lock");
        File.WriteAllText(marker, "unknown owner");
        var host = new Host(_ => Completed(Output));
        var evidence = await fixture.Collect(host);
        Assert.Equal(0, host.Calls);
        Assert.Equal("unknown owner", File.ReadAllText(marker));
        Assert.Equal(EvidenceStatus.Unavailable, evidence.Status);
        File.Delete(marker); // this test created and still owns the unknown fixture, never a product cleanup.
        for (int index = 0; index < 50; index++) Directory.CreateDirectory(Path.Combine(fixture.Work, "run-" + index));
        var full = await fixture.Collect(host);
        Assert.Equal(EvidenceStatus.Unavailable, full.Status);
        Assert.Equal(50, Directory.GetDirectories(fixture.Work, "run-*").Length);
        Assert.Equal(0, host.Calls);
    }

    [Fact]
    public async Task CancellationBeforeAndAfterExecutionCannotPublishAnAnalysisAndRetainsCompletedReceipt()
    {
        using var fixture = new Fixture();
        using var before = new CancellationTokenSource(); before.Cancel();
        var host = new Host(_ => Completed(Output));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Collector(host).CollectAsync(before.Token));
        Assert.Equal(0, host.Calls);
        using var after = new CancellationTokenSource();
        host = new Host(_ => { after.Cancel(); return Completed(Output); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Collector(host).CollectAsync(after.Token));
        Assert.Equal(1, host.Calls);
        var operation = Assert.Single(Directory.GetDirectories(fixture.Work, "run-*"));
        Assert.True(File.Exists(Path.Combine(operation, "result.json")));
        using var readable = new FileStream(fixture.Dump, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public async Task ReplacementBetweenSelectionsIsCapturedAsNewBytesRatherThanReusingAStaleDigest()
    {
        using var fixture = new Fixture();
        var host = new Host(_ => Completed(Output));
        var first = await fixture.Collect(host);
        byte[] next = File.ReadAllBytes(fixture.Dump); next[31] = 7; File.WriteAllBytes(fixture.Dump, next);
        var second = await fixture.Collect(host);
        Assert.NotEqual(Fact(first, "dumpSha256").TextValue, Fact(second, "dumpSha256").TextValue);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(next)), Fact(second, "dumpSha256").TextValue);
        Assert.NotEqual(Fact(first, "operationDirectory").TextValue, Fact(second, "operationDirectory").TextValue);
        Assert.Equal(2, host.Calls);
    }

    [Fact]
    public async Task CleanupFailureRetainsOwnershipSourceLeasesAndPrimaryFailureWithoutRetry()
    {
        // Intentionally retained tiny fixture: releasing input leases while the fake reports an owned
        // unfinished Job would invalidate the production invariant. The process-exit cleanup owns it.
        using var fixture = new Fixture { PreserveForPendingOwner = true };
        var before = CrashDumpCollector.PendingOwnershipIds.ToHashSet(StringComparer.Ordinal);
        var host = new Host(_ => new(ToolRunStatus.CleanupFailed, null, "partial prefix", "", new(TimeSpan.Zero),
            new(ErrorCode.Timeout, "primary timeout; owned finalization pending", ["native-job:fixture"])));
        var evidence = await fixture.Collect(host);
        Assert.Equal(1, host.Calls);
        Assert.Equal("primary timeout; owned finalization pending", Fact(evidence, "toolErrorMessage").TextValue);
        Assert.Equal("[\"native-job:fixture\"]", Fact(evidence, "toolPendingCleanupPaths").TextValue);
        string ownership = Fact(evidence, "pendingOwnershipId").TextValue!;
        Assert.DoesNotContain(ownership, before);
        Assert.Contains(ownership, CrashDumpCollector.PendingOwnershipIds);
        Assert.ThrowsAny<IOException>(() => File.WriteAllText(fixture.Dump, "replacement"));
        Assert.Equal("partial prefix", Fact(evidence, "sourceOutput").TextValue);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(Microsoft.Win32.SafeHandles.SafeFileHandle handle, uint code,
        IntPtr input, uint inputLength, IntPtr output, uint outputLength, out uint returned, IntPtr overlapped);

    private static string Argument(ToolRequest request, string name)
    { var arguments = request.Arguments.ToList(); return arguments[arguments.IndexOf(name) + 1]; }
    private static DiagnosticFact Fact(DiagnosticEvidence evidence, string key) => Assert.Single(evidence.Facts, fact => fact.Key == key);
    private static ToolRunResult Completed(string output) => new(ToolRunStatus.Exited, 0, output, "", new(TimeSpan.FromSeconds(1)), StandardOutputBytes: Encoding.ASCII.GetBytes(output), StandardErrorBytes: [], OutputCodePage: 437);
    private sealed class Host(Func<ToolRequest, ToolRunResult> run) : IExternalToolRunner
    {
        public ToolRequest? Request { get; private set; }
        public int Calls { get; private set; }
        public Task<ToolRunResult> RunAsync(ToolRequest request, CancellationToken cancellationToken)
        { Request = request; Calls++; return Task.FromResult(run(request)); }
    }
    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "optimus-dump-fixtures", Guid.NewGuid().ToString("N"));
        public string Debugger { get; }
        public string Dump { get; }
        public string Symbols { get; }
        public string Work { get; }
        public bool PreserveForPendingOwner { get; init; }
        public CrashDumpSettings Settings => new(Debugger, Dump, Symbols);
        public Fixture(string signature = "MDMP")
        {
            Debugger = Path.Combine(_root, "tools", "cdb.exe");
            Dump = Path.Combine(_root, "source", "dump Ω; q;.DMP");
            Symbols = Path.Combine(_root, "symbols local");
            Work = Path.Combine(_root, "receipts");
            Directory.CreateDirectory(Path.GetDirectoryName(Debugger)!);
            Directory.CreateDirectory(Path.GetDirectoryName(Dump)!);
            Directory.CreateDirectory(Symbols);
            File.WriteAllBytes(Debugger, [1, 2, 3, 4]);
            var content = new byte[32]; Encoding.ASCII.GetBytes(signature).CopyTo(content, 0); File.WriteAllBytes(Dump, content);
        }
        public CrashDumpCollector Collector(IExternalToolRunner host, CrashDumpSettings? settings = null) => new(host, settings ?? Settings, Work);
        public async Task<DiagnosticEvidence> Collect(IExternalToolRunner host, CrashDumpSettings? settings = null) => Assert.Single(await Collector(host, settings).CollectAsync(CancellationToken.None));
        public void Dispose() { if (!PreserveForPendingOwner && Directory.Exists(_root)) Directory.Delete(_root, true); }
    }
}
