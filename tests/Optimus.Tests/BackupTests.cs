using System.Text.Json;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Optimus.Application;
using Optimus.Domain;
using Optimus.Infrastructure.Backup;

namespace Optimus.Tests;

public sealed class BackupTests : IDisposable
{
    private static readonly string Snapshot = new('b', 64);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "optimus-backup-tests", Guid.NewGuid().ToString("N"));
    private readonly BackupSettings _settings;
    private readonly string _journals;
    private readonly List<OwnedJunction> _junctions = [];

    public BackupTests()
    {
        Directory.CreateDirectory(_root);
        var source = Path.Combine(_root, "source");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "selected.txt"), "selected fixture contents");
        var executable = Path.Combine(_root, "restic-fixture.exe");
        File.WriteAllText(executable, "Fixture path is hashed but never executed by the fake runner");
        var password = Path.Combine(_root, "recovery-password.txt");
        File.WriteAllText(password, "fixture-secret-not-for-journal");
        _settings = new(executable, source, Path.Combine(_root, "repository"), password);
        _journals = Path.Combine(_root, "journals");
    }

    [Fact]
    public async Task PreviewDoesNotCreateRepositoryOrJournalOrExecuteTools()
    {
        var runner = new FakeRunner();
        var service = new ResticBackupService(runner, _journals);

        var preview = await service.PlanAsync(_settings, CancellationToken.None);

        Assert.True(preview.IsSuccess, preview.Error?.Message);
        Assert.Empty(runner.Requests);
        Assert.False(Directory.Exists(_settings.RepositoryDirectory));
        Assert.False(Directory.Exists(_journals));
        Assert.Contains(preview.Value!.Limitations, text => text.Contains("VSS"));
    }

    [Fact]
    public async Task ConstructorIsInertAndUnsupportedJournalFailsOnlyTheBackupOperation()
    {
        var runner = new FakeRunner();
        var service = new ResticBackupService(runner, @"\\unreachable-host\unsupported-share");

        var result = await service.PlanAsync(_settings, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCode.InvalidConfiguration, result.Error!.Code);
        Assert.Empty(runner.Requests);
        Assert.False(Directory.Exists(_settings.RepositoryDirectory));
    }

    [Fact]
    public async Task BackupWritesDurableJournalBeforeEachCommandAndSeparatesIntegrityFromRestore()
    {
        var runner = new FakeRunner(request =>
        {
            var files = Directory.GetFiles(_journals, "*.json", SearchOption.AllDirectories)
                .Where(path => Path.GetFileName(path).Contains('-')).ToArray();
            Assert.NotEmpty(files);
            var last = files.Order(StringComparer.Ordinal).Last();
            using var document = JsonDocument.Parse(File.ReadAllText(last));
            var expected = Command(request) switch
            {
                "init" => "before-initialize",
                "cat" => "before-repository-readback",
                "backup" => "before-backup",
                "check" => "before-integrity-check",
                _ => "unexpected"
            };
            Assert.Equal(expected, document.RootElement.GetProperty("phase").GetString());
            return Success(request);
        });
        var service = new ResticBackupService(runner, _journals);
        var plan = (await service.PlanAsync(_settings, CancellationToken.None)).Value!;

        var result = await service.ExecuteAsync(plan, plan.Id, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(BackupState.IntegrityVerified, result.Value!.State);
        Assert.Equal(Snapshot, result.Value.SnapshotId);
        Assert.True(result.Value.IntegrityVerified);
        Assert.False(result.Value.RestorationVerified);
        Assert.Equal(new[] { "initialize", "repository-readback", "backup", "integrity-check" }, result.Value.Commands.Select(item => item.Stage));
        var journals = Directory.GetFiles(_journals, "*.json", SearchOption.AllDirectories).Select(File.ReadAllText);
        Assert.All(journals, text => Assert.DoesNotContain("fixture-secret-not-for-journal", text));
        Assert.All(runner.Requests, request => Assert.Contains("--password-file", request.Arguments));
        Assert.All(runner.Requests, request => Assert.DoesNotContain("fixture-secret-not-for-journal", request.Arguments));
        Assert.All(runner.Requests, request => Assert.Contains("RESTIC_PASSWORD_COMMAND", request.EnvironmentVariablesToRemove!));
        Assert.All(runner.Requests, request => Assert.Null(request.EnvironmentOverrides));
    }

    [Fact]
    public async Task WrongConfirmationAndForgedPlanCannotExecuteOrConsumeTheValidPlan()
    {
        var runner = new FakeRunner();
        var service = new ResticBackupService(runner, _journals);
        var plan = (await service.PlanAsync(_settings, CancellationToken.None)).Value!;

        Assert.False((await service.ExecuteAsync(plan, Guid.NewGuid(), CancellationToken.None)).IsSuccess);
        Assert.False((await service.ExecuteAsync(plan with { SourceDirectory = _root }, plan.Id, CancellationToken.None)).IsSuccess);
        Assert.Empty(runner.Requests);
        Assert.True((await service.ExecuteAsync(plan, plan.Id, CancellationToken.None)).IsSuccess);
        var count = runner.Requests.Count;
        Assert.False((await service.ExecuteAsync(plan, plan.Id, CancellationToken.None)).IsSuccess);
        Assert.Equal(count, runner.Requests.Count);
    }

    [Fact]
    public async Task RelatedPathsAndExistingRepositoryContentsAreRejectedWithoutChanges()
    {
        var runner = new FakeRunner();
        var service = new ResticBackupService(runner, _journals);
        Assert.False((await service.PlanAsync(_settings with { RepositoryDirectory = Path.Combine(_settings.SourceDirectory, "backup") }, CancellationToken.None)).IsSuccess);
        Directory.CreateDirectory(_settings.RepositoryDirectory);
        var existing = Path.Combine(_settings.RepositoryDirectory, "user.txt");
        File.WriteAllText(existing, "preserve this");

        var result = await service.PlanAsync(_settings, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("preserve this", File.ReadAllText(existing));
        Assert.Empty(runner.Requests);
    }

    [Theory]
    [InlineData("source.")]
    [InlineData("source ")]
    [InlineData("source:alternate")]
    [InlineData("NUL.txt")]
    [InlineData("COM¹")]
    [InlineData("LPT9.data")]
    public async Task AmbiguousOrDevicePathIsRejectedWithoutCorrectingTheCallerInput(string component)
    {
        var runner = new FakeRunner();
        var service = new ResticBackupService(runner, _journals);

        var result = await service.PlanAsync(_settings with { SourceDirectory = Path.Combine(_root, component) }, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCode.InvalidConfiguration, result.Error!.Code);
        Assert.Empty(runner.Requests);
        Assert.False(Directory.Exists(_settings.RepositoryDirectory));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task JunctionInIntermediateComponentIsRejectedForSourceOrRepository(bool repositoryPath)
    {
        var actual = Path.Combine(_root, "actual");
        Directory.CreateDirectory(Path.Combine(actual, "inner"));
        var junction = Path.Combine(_root, "junction");
        CreateJunction(junction, actual);
        var settings = repositoryPath
            ? _settings with { RepositoryDirectory = Path.Combine(junction, "inner", "new-repo") }
            : _settings with { SourceDirectory = Path.Combine(junction, "inner") };
        var runner = new FakeRunner();
        var service = new ResticBackupService(runner, _journals);

        var result = await service.PlanAsync(settings, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains("reanálise", result.Error!.Message);
        Assert.Empty(runner.Requests);
        Assert.False(Directory.Exists(Path.Combine(actual, "inner", "new-repo")));
    }

    [Fact]
    public async Task PlanExpiringBehindAnotherOperationIsRevalidatedAfterTheGate()
    {
        var clock = new ManualClock();
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var initialCheck = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int first = 0;
        var runner = new AsyncRunner(async request =>
        {
            if (Command(request) == "init" && Interlocked.CompareExchange(ref first, 1, 0) == 0)
            {
                entered.TrySetResult(true);
                await release.Task;
            }
            return Success(request);
        });
        var service = new ResticBackupService(runner, _journals, clock);
        var one = (await service.PlanAsync(_settings, CancellationToken.None)).Value!;
        var two = (await service.PlanAsync(_settings with { RepositoryDirectory = Path.Combine(_root, "second-repo") }, CancellationToken.None)).Value!;
        var running = service.ExecuteAsync(one, one.Id, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        clock.OnRead = () => initialCheck.TrySetResult(true);
        var waiting = service.ExecuteAsync(two, two.Id, CancellationToken.None);
        try
        {
            await initialCheck.Task.WaitAsync(TimeSpan.FromSeconds(5));
            clock.Advance(TimeSpan.FromMinutes(16));
        }
        finally { release.TrySetResult(true); }

        Assert.True((await running).IsSuccess);
        var expired = await waiting;
        Assert.False(expired.IsSuccess);
        Assert.Equal(ErrorCode.InvalidConfiguration, expired.Error!.Code);
        Assert.False(Directory.Exists(two.RepositoryDirectory));
    }

    [Fact]
    public async Task PasswordChangeAfterPreviewIsRejectedBeforeAnyToolEffect()
    {
        var runner = new FakeRunner();
        var service = new ResticBackupService(runner, _journals);
        var plan = (await service.PlanAsync(_settings, CancellationToken.None)).Value!;
        File.WriteAllText(_settings.PasswordFilePath, "changed fixture key");

        var result = await service.ExecuteAsync(plan, plan.Id, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Empty(runner.Requests);
        Assert.False(Directory.Exists(_settings.RepositoryDirectory));
    }

    [Fact]
    public async Task SourceReplacementAfterPreviewIsRejectedByIdentity()
    {
        var runner = new FakeRunner();
        var service = new ResticBackupService(runner, _journals);
        var plan = (await service.PlanAsync(_settings, CancellationToken.None)).Value!;
        Directory.Move(_settings.SourceDirectory, Path.Combine(_root, "original-source"));
        Directory.CreateDirectory(_settings.SourceDirectory);

        var result = await service.ExecuteAsync(plan, plan.Id, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Empty(runner.Requests);
        Assert.True(File.Exists(Path.Combine(_root, "original-source", "selected.txt")));
    }

    [Fact]
    public async Task RepositoryAppearingAfterPreviewIsNotAdopted()
    {
        var runner = new FakeRunner();
        var service = new ResticBackupService(runner, _journals);
        var plan = (await service.PlanAsync(_settings, CancellationToken.None)).Value!;
        Directory.CreateDirectory(_settings.RepositoryDirectory);

        var result = await service.ExecuteAsync(plan, plan.Id, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Empty(runner.Requests);
        Assert.Empty(Directory.GetFileSystemEntries(_settings.RepositoryDirectory));
    }

    [Fact]
    public async Task ExistingEmptyRepositoryReplacementIsRejectedByFileIdentityBeforeEffects()
    {
        Directory.CreateDirectory(_settings.RepositoryDirectory);
        var runner = new FakeRunner();
        var service = new ResticBackupService(runner, _journals);
        var plan = (await service.PlanAsync(_settings, CancellationToken.None)).Value!;
        Directory.Move(_settings.RepositoryDirectory, Path.Combine(_root, "previous-empty-repo"));
        Directory.CreateDirectory(_settings.RepositoryDirectory);

        var result = await service.ExecuteAsync(plan, plan.Id, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(BackupState.Failed, result.Value!.State);
        Assert.Empty(runner.Requests);
        Assert.Empty(Directory.GetFileSystemEntries(_settings.RepositoryDirectory));
    }

    [Fact]
    public async Task PartialBackupRetainsSnapshotAndNeverClaimsIntegrityOrRestore()
    {
        var runner = new FakeRunner(request => Command(request) == "backup"
            ? Output(3, "{\"message_type\":\"summary\",\"snapshot_id\":\"" + Snapshot + "\"}\n", "some files could not be read")
            : Success(request));
        var service = new ResticBackupService(runner, _journals);
        var plan = (await service.PlanAsync(_settings, CancellationToken.None)).Value!;

        var result = await service.ExecuteAsync(plan, plan.Id, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(BackupState.Partial, result.Value!.State);
        Assert.Equal(Snapshot, result.Value.SnapshotId);
        Assert.False(result.Value.IntegrityVerified);
        Assert.False(result.Value.RestorationVerified);
        Assert.DoesNotContain(runner.Requests, request => Command(request) == "check");
    }

    [Fact]
    public async Task InvalidBackupJsonDoesNotFabricateSnapshotOrRunCheck()
    {
        var runner = new FakeRunner(request => Command(request) == "backup" ? Output(0, "not JSON") : Success(request));
        var service = new ResticBackupService(runner, _journals);
        var plan = (await service.PlanAsync(_settings, CancellationToken.None)).Value!;

        var result = await service.ExecuteAsync(plan, plan.Id, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCode.InvalidResponse, result.Error!.Code);
        Assert.Null(result.Value!.SnapshotId);
        Assert.Contains(result.Value.Commands, command => command.StandardOutput == "not JSON");
        Assert.DoesNotContain(runner.Requests, request => Command(request) == "check");
    }

    [Fact]
    public async Task IntegrityFailurePreservesKnownSnapshotAndFailureReason()
    {
        var runner = new FakeRunner(request => Command(request) == "check" ? Output(1, "", "damaged fixture blob") : Success(request));
        var service = new ResticBackupService(runner, _journals);
        var plan = (await service.PlanAsync(_settings, CancellationToken.None)).Value!;

        var result = await service.ExecuteAsync(plan, plan.Id, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCode.VerificationFailed, result.Error!.Code);
        Assert.Equal(Snapshot, result.Value!.SnapshotId);
        Assert.False(result.Value.IntegrityVerified);
        Assert.Equal("damaged fixture blob", result.Value.Commands.Last().StandardError);
    }

    [Fact]
    public async Task ErrorAccompanyingExitZeroDoesNotAuthorizeFurtherStages()
    {
        var runner = new FakeRunner(request => Command(request) == "backup"
            ? Success(request) with { Error = new(ErrorCode.ToolFailed, "fixture completion not confirmed") }
            : Success(request));
        var service = new ResticBackupService(runner, _journals);
        var plan = (await service.PlanAsync(_settings, CancellationToken.None)).Value!;

        var result = await service.ExecuteAsync(plan, plan.Id, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.False(result.Value!.IntegrityVerified);
        Assert.DoesNotContain(runner.Requests, request => Command(request) == "check");
    }

    [Fact]
    public async Task CancellationAfterVerificationIsReportedAfterDurableFinalizationWithoutLosingTheFact()
    {
        using var cancellation = new CancellationTokenSource();
        var runner = new FakeRunner(request =>
        {
            var result = Success(request);
            if (Command(request) == "check") cancellation.Cancel();
            return result;
        });
        var service = new ResticBackupService(runner, _journals);
        var plan = (await service.PlanAsync(_settings, CancellationToken.None)).Value!;

        var result = await service.ExecuteAsync(plan, plan.Id, cancellation.Token);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCode.Cancelled, result.Error!.Code);
        Assert.True(result.Value!.IntegrityVerified);
        Assert.Equal(BackupState.IntegrityVerified, result.Value.State);
        Assert.NotEmpty(Directory.GetFiles(result.Value.JournalDirectory, "*-completed-cancelled.json"));
    }

    [Theory]
    [InlineData(ToolRunStatus.Cancelled, ErrorCode.Cancelled)]
    [InlineData(ToolRunStatus.TimedOut, ErrorCode.Timeout)]
    [InlineData(ToolRunStatus.OutputLimitExceeded, ErrorCode.OutputLimitExceeded)]
    public async Task InterruptedToolKeepsUnknownOutcomeJournalAndCleanupReason(ToolRunStatus status, ErrorCode expected)
    {
        const string ownedPath = "fixture-owned-resource";
        var runner = new FakeRunner(request => Command(request) == "backup"
            ? new(status, null, "", "partial fixture output", new(TimeSpan.Zero), new(expected, "fixture interrupted", [ownedPath]))
            : Success(request));
        var service = new ResticBackupService(runner, _journals);
        var plan = (await service.PlanAsync(_settings, CancellationToken.None)).Value!;

        var result = await service.ExecuteAsync(plan, plan.Id, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(expected, result.Error!.Code);
        Assert.Equal(BackupState.OutcomeUnknown, result.Value!.State);
        Assert.Contains(ownedPath, result.Error.PendingCleanupPaths!);
        Assert.Contains("não repita", result.Value.RecoveryInstruction!);
        Assert.NotEmpty(Directory.GetFiles(result.Value.JournalDirectory, "*-stopped.json"));
    }

    [Fact]
    public async Task CancellationAfterInitDoesNotRepeatOrContinueWithoutRepositoryReadback()
    {
        var runner = new FakeRunner(request => Command(request) == "cat"
            ? new(ToolRunStatus.Cancelled, null, "", "cancelled fixture", new(TimeSpan.Zero), new(ErrorCode.Cancelled, "cancelled"))
            : Success(request));
        var service = new ResticBackupService(runner, _journals);
        var plan = (await service.PlanAsync(_settings, CancellationToken.None)).Value!;

        var result = await service.ExecuteAsync(plan, plan.Id, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal(BackupState.OutcomeUnknown, result.Value!.State);
        Assert.True(File.Exists(Path.Combine(_settings.RepositoryDirectory, "config")));
        Assert.DoesNotContain(runner.Requests, request => Command(request) == "backup");
        Assert.False((await service.PlanAsync(_settings, CancellationToken.None)).IsSuccess);
        Assert.Equal(2, runner.Requests.Count);
    }

    [Fact]
    public async Task CancelledInitWithoutConfigCannotBeReplayedByANewInstance()
    {
        var interrupted = new FakeRunner(_ => new(ToolRunStatus.Cancelled, null, "", "cancelled before config", new(TimeSpan.Zero), new(ErrorCode.Cancelled, "fixture cancelled")));
        var service = new ResticBackupService(interrupted, _journals);
        var plan = (await service.PlanAsync(_settings, CancellationToken.None)).Value!;
        var first = await service.ExecuteAsync(plan, plan.Id, CancellationToken.None);
        Assert.False(first.IsSuccess);
        Assert.True(Directory.Exists(_settings.RepositoryDirectory));
        Assert.Empty(Directory.GetFileSystemEntries(_settings.RepositoryDirectory));
        var resumedRunner = new FakeRunner();
        var resumed = new ResticBackupService(resumedRunner, _journals);

        var replay = await resumed.PlanAsync(_settings, CancellationToken.None);

        Assert.False(replay.IsSuccess);
        Assert.Contains("reserv", replay.Error!.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(first.Value!.JournalDirectory, replay.Error.Message);
        Assert.Empty(resumedRunner.Requests);
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(_journals, "destination-reservations"), "*.json"));
    }

    [Fact]
    public async Task MalformedRetainedReservationIsNotTreatedAsMissing()
    {
        var runner = new FakeRunner(_ => new(ToolRunStatus.TimedOut, null, "", "timed out", new(TimeSpan.Zero), new(ErrorCode.Timeout, "fixture timeout")));
        var service = new ResticBackupService(runner, _journals);
        var plan = (await service.PlanAsync(_settings, CancellationToken.None)).Value!;
        Assert.False((await service.ExecuteAsync(plan, plan.Id, CancellationToken.None)).IsSuccess);
        var marker = Assert.Single(Directory.GetFiles(Path.Combine(_journals, "destination-reservations"), "*.json"));
        File.WriteAllText(marker, "{invalid fixture record");
        var resumed = new ResticBackupService(new FakeRunner(), _journals);

        var replay = await resumed.PlanAsync(_settings, CancellationToken.None);

        Assert.False(replay.IsSuccess);
        Assert.Contains("malformada", replay.Error!.Message);
        Assert.Equal("{invalid fixture record", File.ReadAllText(marker));
    }

    [Fact]
    public async Task SuccessfulReservationIsRetainedWithoutBeingReportedAsPending()
    {
        var service = new ResticBackupService(new FakeRunner(), _journals);
        var plan = (await service.PlanAsync(_settings, CancellationToken.None)).Value!;
        var completed = await service.ExecuteAsync(plan, plan.Id, CancellationToken.None);
        Assert.True(completed.IsSuccess);

        var replay = await new ResticBackupService(new FakeRunner(), _journals).PlanAsync(_settings, CancellationToken.None);

        Assert.False(replay.IsSuccess);
        Assert.Contains("não significa, por si, estado pendente", replay.Error!.Message);
        Assert.Contains(completed.Value!.JournalDirectory, replay.Error.Message);
    }

    [Fact]
    public async Task InterruptedRestoreDestinationCannotBeReusedAfterRestartEvenWhenEmpty()
    {
        Directory.CreateDirectory(_settings.RepositoryDirectory);
        File.WriteAllText(Path.Combine(_settings.RepositoryDirectory, "config"), "fixture config");
        var runner = new FakeRunner(_ => new(ToolRunStatus.Cancelled, null, "", "cancelled restore", new(TimeSpan.Zero), new(ErrorCode.Cancelled, "fixture cancelled")));
        var service = new ResticBackupService(runner, _journals);
        var destination = Path.Combine(_root, "restored");
        var plan = (await service.PlanRestoreAsync(_settings, Snapshot, destination, CancellationToken.None)).Value!;
        Assert.False((await service.RestoreAsync(plan, plan.Id, CancellationToken.None)).IsSuccess);
        Assert.Empty(Directory.GetFileSystemEntries(destination));
        Directory.Delete(destination, recursive: false);

        var replay = await new ResticBackupService(new FakeRunner(), _journals).PlanRestoreAsync(_settings, Snapshot, destination, CancellationToken.None);

        Assert.False(replay.IsSuccess);
        Assert.Contains("reserv", replay.Error!.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(destination));
    }

    [Fact]
    public async Task RestorePreviewAndExecutionRequireNewDestinationAndExplicitConfirmation()
    {
        Directory.CreateDirectory(_settings.RepositoryDirectory);
        File.WriteAllText(Path.Combine(_settings.RepositoryDirectory, "config"), "fixture config");
        var runner = new FakeRunner();
        var service = new ResticBackupService(runner, _journals);
        var destination = Path.Combine(_root, "restored");
        var plan = (await service.PlanRestoreAsync(_settings, Snapshot, destination, CancellationToken.None)).Value!;
        Assert.False(Directory.Exists(destination));
        Assert.False((await service.RestoreAsync(plan, Guid.NewGuid(), CancellationToken.None)).IsSuccess);
        Assert.Empty(runner.Requests);

        var result = await service.RestoreAsync(plan, plan.Id, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(RestoreState.RestoredAndVerified, result.Value!.State);
        Assert.True(result.Value.RestorationVerified);
        var request = Assert.Single(runner.Requests);
        Assert.Contains("--verify", request.Arguments);
        Assert.Contains("never", request.Arguments);
        Assert.DoesNotContain("--delete", request.Arguments);
        Assert.True(Directory.Exists(destination));
    }

    [Fact]
    public async Task DestinationAppearingAfterRestorePreviewIsPreserved()
    {
        Directory.CreateDirectory(_settings.RepositoryDirectory);
        File.WriteAllText(Path.Combine(_settings.RepositoryDirectory, "config"), "fixture config");
        var runner = new FakeRunner();
        var service = new ResticBackupService(runner, _journals);
        var destination = Path.Combine(_root, "restored");
        var plan = (await service.PlanRestoreAsync(_settings, Snapshot, destination, CancellationToken.None)).Value!;
        Directory.CreateDirectory(destination);
        File.WriteAllText(Path.Combine(destination, "user.txt"), "preserve");

        var result = await service.RestoreAsync(plan, plan.Id, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Empty(runner.Requests);
        Assert.Equal("preserve", File.ReadAllText(Path.Combine(destination, "user.txt")));
    }

    [Fact]
    public async Task SnapshotAliasIsRejectedInsteadOfSelectingUnknownLatest()
    {
        var service = new ResticBackupService(new FakeRunner(), _journals);
        var result = await service.PlanRestoreAsync(_settings, "latest", Path.Combine(_root, "restored"), CancellationToken.None);
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task RestoreDoesNotWriteJournalsInsideTheSelectedRepository()
    {
        Directory.CreateDirectory(_settings.RepositoryDirectory);
        File.WriteAllText(Path.Combine(_settings.RepositoryDirectory, "config"), "fixture config");
        var journal = Path.Combine(_settings.RepositoryDirectory, "journals");
        var runner = new FakeRunner();
        var service = new ResticBackupService(runner, journal);

        var result = await service.PlanRestoreAsync(_settings, Snapshot, Path.Combine(_root, "restored"), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Empty(runner.Requests);
        Assert.False(Directory.Exists(journal));
    }

    [Fact]
    public async Task RestoreDoesNotRequireApplicabilityOfTheDisconnectedOriginalDrive()
    {
        Directory.CreateDirectory(_settings.RepositoryDirectory);
        File.WriteAllText(Path.Combine(_settings.RepositoryDirectory, "config"), "fixture config");
        var service = new ResticBackupService(new FakeRunner(), _journals);
        var settings = _settings with { SourceDirectory = @"Z:\disconnected-original\files" };

        var result = await service.PlanRestoreAsync(settings, Snapshot, Path.Combine(_root, "restored"), CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(settings.SourceDirectory, result.Value!.Settings.SourceDirectory);
    }

    [Fact]
    public async Task RestoreLateCancellationPreservesVerifiedContentsAndReportsCancellation()
    {
        Directory.CreateDirectory(_settings.RepositoryDirectory);
        File.WriteAllText(Path.Combine(_settings.RepositoryDirectory, "config"), "fixture config");
        using var cancellation = new CancellationTokenSource();
        var runner = new FakeRunner(request =>
        {
            var result = Success(request);
            if (Command(request) == "restore") cancellation.Cancel();
            return result;
        });
        var service = new ResticBackupService(runner, _journals);
        var plan = (await service.PlanRestoreAsync(_settings, Snapshot, Path.Combine(_root, "restored"), CancellationToken.None)).Value!;

        var result = await service.RestoreAsync(plan, plan.Id, cancellation.Token);

        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCode.Cancelled, result.Error!.Code);
        Assert.True(result.Value!.RestorationVerified);
        Assert.Equal(RestoreState.RestoredAndVerified, result.Value.State);
        Assert.NotEmpty(Directory.GetFiles(result.Value.JournalDirectory, "*-completed-cancelled.json"));
    }

    private static string Command(ToolRequest request) => request.Arguments.First(argument => argument is "init" or "cat" or "backup" or "check" or "restore");
    private static ToolRunResult Output(int exitCode, string stdout, string stderr = "") => new(ToolRunStatus.Exited, exitCode, stdout, stderr, new(TimeSpan.FromMilliseconds(1)));
    private static ToolRunResult Success(ToolRequest request)
    {
        var command = Command(request);
        if (command == "init")
        {
            var repoIndex = request.Arguments.ToList().IndexOf("--repo") + 1;
            File.WriteAllText(Path.Combine(request.Arguments[repoIndex], "config"), "fixture config");
        }
        return command switch
        {
            "cat" => Output(0, "{\"id\":\"" + new string('a', 64) + "\",\"version\":2}"),
            "backup" => Output(0, "{\"message_type\":\"summary\",\"snapshot_id\":\"" + Snapshot + "\"}\n"),
            _ => Output(0, "")
        };
    }
    private sealed class FakeRunner(Func<ToolRequest, ToolRunResult>? run = null) : IExternalToolRunner
    {
        public List<ToolRequest> Requests { get; } = [];
        public Task<ToolRunResult> RunAsync(ToolRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.FromResult((run ?? Success)(request));
        }
    }
    private sealed class AsyncRunner(Func<ToolRequest, Task<ToolRunResult>> run) : IExternalToolRunner
    {
        public Task<ToolRunResult> RunAsync(ToolRequest request, CancellationToken cancellationToken) => run(request);
    }
    private sealed class ManualClock : TimeProvider
    {
        private readonly object _sync = new();
        private DateTimeOffset _value = DateTimeOffset.UnixEpoch;
        public Action? OnRead { get; set; }
        public override DateTimeOffset GetUtcNow()
        {
            DateTimeOffset value;
            lock (_sync) value = _value;
            OnRead?.Invoke();
            return value;
        }
        public void Advance(TimeSpan duration) { lock (_sync) _value += duration; }
    }
    private void CreateJunction(string junction, string target)
    {
        Directory.CreateDirectory(junction);
        using var handle = OpenJunction(junction, 0x40000000 | 0x80, 7, IntPtr.Zero, 3, 0x02000000 | 0x00200000, IntPtr.Zero);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (!JunctionInformation(handle, out var identity)) throw new Win32Exception(Marshal.GetLastWin32Error());
        _junctions.Add(new(junction, identity.VolumeSerialNumber, ((ulong)identity.FileIndexHigh << 32) | identity.FileIndexLow));
        var substitute = System.Text.Encoding.Unicode.GetBytes(@"\??\" + target);
        var printed = System.Text.Encoding.Unicode.GetBytes(target);
        using var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(buffer, System.Text.Encoding.Unicode, leaveOpen: true))
        {
            writer.Write(0xA0000003u);
            writer.Write(checked((ushort)(8 + substitute.Length + 2 + printed.Length + 2)));
            writer.Write((ushort)0);
            writer.Write((ushort)0);
            writer.Write(checked((ushort)substitute.Length));
            writer.Write(checked((ushort)(substitute.Length + 2)));
            writer.Write(checked((ushort)printed.Length));
            writer.Write(substitute); writer.Write((ushort)0); writer.Write(printed); writer.Write((ushort)0);
        }
        var bytes = buffer.ToArray();
        if (!SetJunction(handle, 0x000900A4, bytes, (uint)bytes.Length, IntPtr.Zero, 0, out _, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle OpenJunction(string path, uint access, uint sharing, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetJunction(SafeFileHandle file, uint code, byte[] input, uint inputBytes, IntPtr output, uint outputBytes, out uint returned, IntPtr overlapped);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool JunctionInformation(SafeFileHandle file, out JunctionFileInformation information);
    [StructLayout(LayoutKind.Sequential)]
    private struct JunctionFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }
    private sealed record OwnedJunction(string Path, uint Volume, ulong FileId);
    public void Dispose()
    {
        foreach (var owned in _junctions)
        {
            if (!Path.GetFullPath(owned.Path).StartsWith(Path.GetFullPath(_root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Fixture junction escaped its private root; preserved for inspection.");
            using (var handle = OpenJunction(owned.Path, 0x40000000 | 0x80, 7, IntPtr.Zero, 3, 0x02000000 | 0x00200000, IntPtr.Zero))
            {
                if (handle.IsInvalid || !JunctionInformation(handle, out var identity)) throw new Win32Exception(Marshal.GetLastWin32Error());
                if (identity.VolumeSerialNumber != owned.Volume || (((ulong)identity.FileIndexHigh << 32) | identity.FileIndexLow) != owned.FileId)
                    throw new InvalidOperationException("Fixture junction identity changed; preserved instead of deleting unknown content.");
                if ((identity.FileAttributes & (uint)FileAttributes.ReparsePoint) != 0)
                {
                    byte[] deletion = new byte[8];
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(deletion, 0xA0000003u);
                    if (!SetJunction(handle, 0x000900AC, deletion, (uint)deletion.Length, IntPtr.Zero, 0, out _, IntPtr.Zero))
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                }
            }
            // The original empty directory is now a normal, identity-verified fixture directory.
            Directory.Delete(owned.Path, recursive: false);
        }
        // The fixture owns this exact random directory; no user data is placed here.
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
