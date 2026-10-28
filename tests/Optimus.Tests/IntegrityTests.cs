using System.Text;
using System.Text.Json;
using Optimus.Application;
using Optimus.Domain;
using Optimus.Infrastructure.Integrity;

namespace Optimus.Tests;

public sealed class IntegrityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "optimus-integrity-tests", Guid.NewGuid().ToString("N"));
    private readonly FixtureRunner _runner = new();
    private readonly FakeClock _clock = new();
    private bool _elevated = true;
    private string SystemDirectory => Path.Combine(_root, "system");
    private string JournalRoot => Path.Combine(_root, "journal");

    public IntegrityTests()
    {
        Directory.CreateDirectory(SystemDirectory);
        File.WriteAllBytes(Path.Combine(SystemDirectory, "sfc.exe"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(SystemDirectory, "dism.exe"), [4, 5, 6]);
    }

    [Fact]
    public async Task PreviewIsReadOnlyAndReportsElevationWithoutStartingACommand()
    {
        _elevated = false;
        var result = await Service().PlanAsync(IntegrityOperation.SfcVerifyOnly, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.False(result.Value!.IsElevated);
        Assert.Equal(new[] { "/verifyonly" }, result.Value.Arguments);
        Assert.Equal(ToolTextEncoding.Utf16LittleEndian, result.Value.OutputEncoding);
        Assert.Equal(SystemDirectory, result.Value.WorkingDirectory);
        Assert.Equal(64, result.Value.ExecutableSha256.Length);
        Assert.Equal(0, _runner.Calls);
        Assert.False(Directory.Exists(JournalRoot));
    }

    [Theory]
    [InlineData(IntegrityOperation.SfcVerifyOnly)]
    [InlineData(IntegrityOperation.DismCheckHealth)]
    [InlineData(IntegrityOperation.DismScanHealth)]
    public async Task NamedCommandsAreBoundToFixedArgumentsDigestSystemWorkingDirectoryAndDurableJournal(IntegrityOperation operation)
    {
        var service = Service();
        var plan = await Plan(service, operation);
        _runner.Handler = (request, _) =>
        {
            var operationDirectory = OperationDirectory(plan);
            Assert.True(File.Exists(Path.Combine(operationDirectory, "01-prepared.json")));
            Assert.True(File.Exists(Path.Combine(operationDirectory, "02-before-command.json")));
            using var prepared = JsonDocument.Parse(File.ReadAllText(Path.Combine(operationDirectory, "01-prepared.json")));
            Assert.False(prepared.RootElement.GetProperty("repairRequested").GetBoolean());
            Assert.Equal(plan.Id, prepared.RootElement.GetProperty("plan").GetProperty("id").GetGuid());
            Assert.Equal(plan.ExecutablePath, request.ExecutablePath);
            Assert.Equal(plan.ExecutableSha256, request.ExpectedExecutableSha256);
            Assert.Equal(SystemDirectory, request.WorkingDirectory);
            Assert.Equal(plan.OutputEncoding, request.OutputEncoding);
            Assert.True(request.PreserveOutputBytes);
            Assert.Equal(262_144, request.MaximumOutputBytes);
            Assert.Equal(plan.Timeout, request.Timeout);
            Assert.DoesNotContain(request.Arguments, argument => new[] { "/scannow", "/RestoreHealth", "/StartComponentCleanup", "/ResetBase" }.Contains(argument, StringComparer.OrdinalIgnoreCase));
            Assert.Equal(operation == IntegrityOperation.SfcVerifyOnly ? new[] { "/verifyonly" }
                : new[] { "/Online", "/Cleanup-Image", operation == IntegrityOperation.DismCheckHealth ? "/CheckHealth" : "/ScanHealth", "/English" }, request.Arguments);
            Assert.ThrowsAny<IOException>(() => File.WriteAllBytes(request.ExecutablePath, [9]));
            return Task.FromResult(Completed(request));
        };

        var result = await service.ExecuteAsync(plan, plan.Id, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(IntegrityState.CommandCompleted, result.Value!.State);
        Assert.False(result.Value.RepairPerformed);
        Assert.Equal("false", Fact(result.Value, "healthConclusionMade").TextValue);
        Assert.Equal("false", Fact(result.Value, "repairRequested").TextValue);
        Assert.NotNull(result.Value.Command!.StandardOutputBase64);
        Assert.True(File.Exists(Path.Combine(OperationDirectory(plan), "03-result.json")));
        Assert.Single(result.Value.Evidence.Facts, item => item.Key == "stdoutBytes" && item.Detailed);
        Assert.Equal(1, _runner.Calls);
    }

    [Fact]
    public async Task ForgedCloneOrWrongConfirmationCannotConsumeTheIssuedPlan()
    {
        var service = Service();
        var plan = await Plan(service);
        Assert.False((await service.ExecuteAsync(plan with { }, plan.Id, CancellationToken.None)).IsSuccess);
        Assert.False((await service.ExecuteAsync(plan, Guid.NewGuid(), CancellationToken.None)).IsSuccess);
        Assert.Equal(0, _runner.Calls);
        Assert.False(Directory.Exists(JournalRoot));

        Assert.True((await service.ExecuteAsync(plan, plan.Id, CancellationToken.None)).IsSuccess);
        Assert.False((await service.ExecuteAsync(plan, plan.Id, CancellationToken.None)).IsSuccess);
        Assert.Equal(1, _runner.Calls);
    }

    [Fact]
    public async Task ConcurrentPreviewRegistrationAndConsumptionReturnKnownResultsWithoutEvictionRaces()
    {
        var service = Service();
        var work = Enumerable.Range(0, 24).Select(async _ =>
        {
            var planned = await service.PlanAsync(IntegrityOperation.SfcVerifyOnly, CancellationToken.None);
            Assert.True(planned.IsSuccess, planned.Error?.Message);
            var plan = planned.Value!;
            return await service.ExecuteAsync(plan, plan.Id, CancellationToken.None);
        });

        var results = await Task.WhenAll(work);

        Assert.All(results, result => Assert.True(result.IsSuccess || result.Error?.Code == ErrorCode.InvalidConfiguration,
            result.Error?.Message));
        Assert.Equal(results.Count(result => result.IsSuccess), _runner.Calls);
        Assert.Equal(_runner.Calls, Directory.GetDirectories(JournalRoot, "operation-*").Length);
    }

    [Fact]
    public async Task MissingElevationPreventsAllJournalAndToolEffects()
    {
        var service = Service();
        var plan = await Plan(service);
        _elevated = false;

        var result = await service.ExecuteAsync(plan, plan.Id, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCode.PermissionDenied, result.Error!.Code);
        Assert.Equal(0, _runner.Calls);
        Assert.False(Directory.Exists(JournalRoot));
    }

    [Fact]
    public async Task ExpiredPlanDoesNotStartTheCommand()
    {
        var service = Service();
        var plan = await Plan(service);
        _clock.Advance(TimeSpan.FromMinutes(5));

        var result = await service.ExecuteAsync(plan, plan.Id, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCode.InvalidConfiguration, result.Error!.Code);
        Assert.Equal(0, _runner.Calls);
        Assert.False(Directory.Exists(JournalRoot));
    }

    [Fact]
    public async Task PlanValidityIsRecheckedAfterWaitingForAnotherOperation()
    {
        var service = Service();
        var first = await Plan(service);
        var second = await Plan(service);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _runner.Handler = async (request, _) => { entered.SetResult(); await release.Task; return Completed(request); };
        var active = service.ExecuteAsync(first, first.Id, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var waiting = service.ExecuteAsync(second, second.Id, CancellationToken.None);
        _clock.Advance(TimeSpan.FromMinutes(6));
        release.SetResult();

        Assert.True((await active).IsSuccess);
        var result = await waiting;
        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCode.InvalidConfiguration, result.Error!.Code);
        Assert.Equal(1, _runner.Calls);
        Assert.False(Directory.Exists(OperationDirectory(second)));
    }

    [Fact]
    public async Task ChangedExecutableContentIsRejectedBeforeAnyEffects()
    {
        var service = Service();
        var plan = await Plan(service);
        File.WriteAllBytes(plan.ExecutablePath, [7, 8, 9]);

        var result = await service.ExecuteAsync(plan, plan.Id, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCode.InvalidConfiguration, result.Error!.Code);
        Assert.Equal(0, _runner.Calls);
        Assert.False(Directory.Exists(JournalRoot));
    }

    [Fact]
    public async Task UnknownReservationMarkerIsPreservedAndPreventsExecution()
    {
        Directory.CreateDirectory(JournalRoot);
        var marker = Path.Combine(JournalRoot, ".capacity.lock");
        File.WriteAllText(marker, "foreign data");
        var service = Service();
        var plan = await Plan(service);

        var result = await service.ExecuteAsync(plan, plan.Id, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCode.InvalidData, result.Error!.Code);
        Assert.Equal("foreign data", File.ReadAllText(marker));
        Assert.Equal(0, _runner.Calls);
        Assert.Empty(Directory.GetDirectories(JournalRoot));
    }

    [Fact]
    public async Task ExistingOperationDirectoryIsNotAdoptedOrOverwritten()
    {
        var service = Service();
        var plan = await Plan(service);
        Directory.CreateDirectory(OperationDirectory(plan));
        var existing = Path.Combine(OperationDirectory(plan), "keep.txt");
        File.WriteAllText(existing, "foreign preserved");

        var result = await service.ExecuteAsync(plan, plan.Id, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(0, _runner.Calls);
        Assert.Equal("foreign preserved", File.ReadAllText(existing));
        Assert.False(File.Exists(Path.Combine(OperationDirectory(plan), "01-prepared.json")));
    }

    [Fact]
    public async Task RetentionCapacityPreventsAnotherCommandWithoutRemovingEvidence()
    {
        Directory.CreateDirectory(JournalRoot);
        for (int index = 0; index < 100; index++) Directory.CreateDirectory(Path.Combine(JournalRoot, "operation-" + index));
        var service = Service();
        var plan = await Plan(service);

        var result = await service.ExecuteAsync(plan, plan.Id, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCode.StorageFailure, result.Error!.Code);
        Assert.Equal(100, Directory.GetDirectories(JournalRoot).Length);
        Assert.Equal(0, _runner.Calls);
    }

    [Fact]
    public async Task LastCapacitySlotIsReservedAcrossServiceInstances()
    {
        Directory.CreateDirectory(JournalRoot);
        for (int index = 0; index < 99; index++) Directory.CreateDirectory(Path.Combine(JournalRoot, "operation-" + index));
        var first = Service();
        var second = Service();
        var firstPlan = await Plan(first);
        var secondPlan = await Plan(second);

        var results = await Task.WhenAll(first.ExecuteAsync(firstPlan, firstPlan.Id, CancellationToken.None),
            second.ExecuteAsync(secondPlan, secondPlan.Id, CancellationToken.None));

        Assert.Single(results, result => result.IsSuccess);
        Assert.Equal(100, Directory.GetDirectories(JournalRoot).Length);
        Assert.Equal(1, _runner.Calls);
    }

    [Theory]
    [InlineData(ToolRunStatus.TimedOut, ErrorCode.Timeout, IntegrityState.OutcomeUnknown)]
    [InlineData(ToolRunStatus.Cancelled, ErrorCode.Cancelled, IntegrityState.Cancelled)]
    [InlineData(ToolRunStatus.OutputLimitExceeded, ErrorCode.OutputLimitExceeded, IntegrityState.OutcomeUnknown)]
    public async Task InterruptedExecutionKeepsRawEvidenceFailureReasonAndNeverRetries(ToolRunStatus status, ErrorCode code, IntegrityState state)
    {
        var service = Service();
        var plan = await Plan(service);
        _runner.Handler = (request, _) => Task.FromResult(Completed(request) with
        {
            Status = status,
            ExitCode = 123,
            Error = new(code, "fixture interruption", ["owned-job:fixture"])
        });

        var result = await service.ExecuteAsync(plan, plan.Id, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(code, result.Error!.Code);
        Assert.Contains("owned-job:fixture", result.Error.PendingCleanupPaths!);
        Assert.Equal(state, result.Value!.State);
        Assert.NotNull(result.Value.Command!.StandardOutputBase64);
        Assert.Equal("fixture interruption", result.Value.Command.FailureReason);
        Assert.Equal(1, _runner.Calls);
        Assert.True(File.Exists(Path.Combine(OperationDirectory(plan), "03-result.json")));
    }

    [Fact]
    public async Task NonzeroExitPreservesTheReportedFailureAndDoesNotClaimHealth()
    {
        var service = Service();
        var plan = await Plan(service);
        _runner.Handler = (request, _) => Task.FromResult(Completed(request) with { ExitCode = 1 });

        var result = await service.ExecuteAsync(plan, plan.Id, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(IntegrityState.CommandReportedFailure, result.Value!.State);
        Assert.Equal(1, result.Value.Command!.ExitCode);
        Assert.False(result.Value.RepairPerformed);
        Assert.Equal("false", Fact(result.Value, "healthConclusionMade").TextValue);
    }

    [Fact]
    public async Task ExitZeroWithTransportErrorNeverBecomesCompleted()
    {
        var service = Service();
        var plan = await Plan(service);
        _runner.Handler = (request, _) => Task.FromResult(Completed(request) with
        {
            Error = new(ErrorCode.ToolFailed, "cleanup not confirmed", ["owned-job:pending"])
        });

        var result = await service.ExecuteAsync(plan, plan.Id, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(IntegrityState.OutcomeUnknown, result.Value!.State);
        Assert.Equal("cleanup not confirmed", result.Value.Command!.FailureReason);
        Assert.Contains("owned-job:pending", result.Error!.PendingCleanupPaths!);
    }

    [Fact]
    public async Task MissingRequestedRawBytesMakesTheTransportUnconfirmed()
    {
        var service = Service();
        var plan = await Plan(service);
        _runner.Handler = (request, _) => Task.FromResult(Completed(request) with { StandardOutputBytes = null });

        var result = await service.ExecuteAsync(plan, plan.Id, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCode.InvalidResponse, result.Error!.Code);
        Assert.Equal(IntegrityState.OutcomeUnknown, result.Value!.State);
    }

    [Fact]
    public async Task FailureBeforeOutputCapturePreservesThePrimaryDependencyReason()
    {
        var service = Service();
        var plan = await Plan(service);
        _runner.Handler = (_, _) => Task.FromResult(new ToolRunResult(ToolRunStatus.StartFailed, null, "", "",
            new(TimeSpan.Zero), new(ErrorCode.DependencyUnavailable, "fixture dependency missing")));

        var result = await service.ExecuteAsync(plan, plan.Id, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCode.DependencyUnavailable, result.Error!.Code);
        Assert.Equal("fixture dependency missing", result.Error.Message);
        Assert.Equal(IntegrityState.OutcomeUnknown, result.Value!.State);
        Assert.Equal("fixture dependency missing", result.Value.Command!.FailureReason);
        Assert.Null(result.Value.Command.StandardOutputBase64);
        Assert.Equal(1, _runner.Calls);
    }

    [Fact]
    public async Task CancellationBeforeTheGateHasNoJournalOrCommandEffects()
    {
        var service = Service();
        var plan = await Plan(service);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var result = await service.ExecuteAsync(plan, plan.Id, cancellation.Token);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCode.Cancelled, result.Error!.Code);
        Assert.Equal(0, _runner.Calls);
        Assert.False(Directory.Exists(JournalRoot));
    }

    [Fact]
    public async Task LateCancellationKeepsTheCompletedCommandAndDurableResult()
    {
        var service = Service();
        var plan = await Plan(service);
        using var cancellation = new CancellationTokenSource();
        _runner.Handler = (request, _) => { cancellation.Cancel(); return Task.FromResult(Completed(request)); };

        var result = await service.ExecuteAsync(plan, plan.Id, cancellation.Token);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCode.Cancelled, result.Error!.Code);
        Assert.Equal(IntegrityState.CommandCompleted, result.Value!.State);
        Assert.True(File.Exists(Path.Combine(OperationDirectory(plan), "03-result.json")));
        Assert.False(result.Value.RepairPerformed);
        Assert.Equal(1, _runner.Calls);
    }

    [Fact]
    public async Task FailureOfFinalJournalKeepsObservedCompletionWithoutOverwritingTheExistingFile()
    {
        var service = Service();
        var plan = await Plan(service);
        var path = Path.Combine(OperationDirectory(plan), "03-result.json");
        _runner.Handler = (request, _) => { File.WriteAllText(path, "foreign preserved"); return Task.FromResult(Completed(request)); };

        var result = await service.ExecuteAsync(plan, plan.Id, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCode.StorageFailure, result.Error!.Code);
        Assert.Equal(IntegrityState.CommandCompleted, result.Value!.State);
        Assert.Equal("foreign preserved", File.ReadAllText(path));
        Assert.True(File.Exists(Path.Combine(OperationDirectory(plan), "04-interrupted.json")));
        Assert.Equal(1, _runner.Calls);
    }

    [Fact]
    public async Task ExceptionAfterInvocationPreservesUnknownOutcomeAndPendingOwnership()
    {
        var service = Service();
        var plan = await Plan(service);
        _runner.Handler = (_, _) =>
        {
            var error = new IOException("fixture interrupted after request");
            error.Data["PendingCleanupPaths"] = new[] { "owned-job:exception" };
            throw error;
        };

        var result = await service.ExecuteAsync(plan, plan.Id, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(IntegrityState.OutcomeUnknown, result.Value!.State);
        Assert.Null(result.Value.Command);
        Assert.Contains("owned-job:exception", result.Error!.PendingCleanupPaths!);
        Assert.True(File.Exists(Path.Combine(OperationDirectory(plan), "04-interrupted.json")));
        Assert.Equal(1, _runner.Calls);
    }

    private WindowsIntegrityService Service() => new(_runner, JournalRoot, _clock, () => _elevated,
        operation => Path.Combine(SystemDirectory, operation == IntegrityOperation.SfcVerifyOnly ? "sfc.exe" : "dism.exe"), () => SystemDirectory);
    private static async Task<IntegrityPlan> Plan(WindowsIntegrityService service, IntegrityOperation operation = IntegrityOperation.SfcVerifyOnly)
    {
        var result = await service.PlanAsync(operation, CancellationToken.None);
        Assert.True(result.IsSuccess, result.Error?.Message);
        return result.Value!;
    }
    private string OperationDirectory(IntegrityPlan plan) => Path.Combine(JournalRoot, "operation-" + plan.Id.ToString("N"));
    private static DiagnosticFact Fact(IntegrityReceipt receipt, string key) => receipt.Evidence.Facts.Single(item => item.Key == key);
    private static ToolRunResult Completed(ToolRequest request)
    {
        const string text = "Saída original: conclusão humana pendente. Não é laudo.";
        var bytes = (request.OutputEncoding == ToolTextEncoding.Utf16LittleEndian ? Encoding.Unicode : Encoding.UTF8).GetBytes(text);
        return new(ToolRunStatus.Exited, 0, text, "", new(TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(10), 12345),
            StandardOutputBytes: bytes, StandardErrorBytes: [], OutputCodePage: request.OutputEncoding == ToolTextEncoding.Utf16LittleEndian ? 1200 : 65001);
    }

    private sealed class FixtureRunner : IExternalToolRunner
    {
        private int _calls;
        public int Calls => _calls;
        public Func<ToolRequest, CancellationToken, Task<ToolRunResult>> Handler { get; set; } = (request, _) => Task.FromResult(Completed(request));
        public Task<ToolRunResult> RunAsync(ToolRequest request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return Handler(request, cancellationToken);
        }
    }
    private sealed class FakeClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.Parse("2026-10-27T10:00:00Z");
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan elapsed) => _now += elapsed;
    }
    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
