using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using Optimus.Domain;
using Optimus.Infrastructure.Inventory;
using Optimus.Infrastructure.RegistryData;
using Optimus.Infrastructure.Updates;

namespace Optimus.Tests;

public sealed class PendingRestartTests
{
    private static readonly DateTimeOffset Stamp = DateTimeOffset.Parse("2026-10-28T10:11:12.1234567-03:00");

    [Fact]
    public async Task FixedCatalogUsesOnlySelectedHklm64IndicatorsWithoutUniversalRestartConclusion()
    {
        var calls = new List<PendingRestartIndicator>();
        var evidence = Assert.Single(await new PendingRestartCollector((indicator, _) =>
        { calls.Add(indicator); return new(Stamp, PendingRestartRegistryReadState.KeyAbsent, false); }).CollectAsync(CancellationToken.None));
        Assert.Equal(new[] { PendingRestartIndicator.ComponentBasedServicing, PendingRestartIndicator.WindowsUpdate, PendingRestartIndicator.FileRenameOperations }, calls);
        Assert.All(calls, indicator => Assert.Equal(RegistryView.Registry64, PendingRestartRegistrySource.Describe(indicator).View));
        Assert.All(calls, indicator => Assert.Equal(RegistryHive.LocalMachine, PendingRestartRegistrySource.Describe(indicator).Hive));
        Assert.Equal(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending", Fact(evidence, "cbs.source").TextValue);
        Assert.EndsWith(@"WindowsUpdate\Auto Update\RebootRequired", Fact(evidence, "windowsUpdate.source").TextValue);
        Assert.EndsWith(@"Session Manager\PendingFileRenameOperations", Fact(evidence, "fileRename.source").TextValue);
        Assert.Equal("false", Fact(evidence, "cbs.keyExists").TextValue);
        Assert.Null(Fact(evidence, "fileRename.valueExists").TextValue);
        Assert.Equal("false", Fact(evidence, "universalRestartAssessed").TextValue);
        Assert.Equal("false", Fact(evidence, "wuaComRequeried").TextValue);
        Assert.Equal(EvidenceStatus.Complete, evidence.Status);
        Assert.DoesNotContain(evidence.Facts, fact => fact.Key == "restartRequired");
        Assert.Equal(Stamp.ToString("O"), Fact(evidence, "cbs.observedAt").TextValue);
    }

    [Fact]
    public async Task AccessDeniedIsUnknownRatherThanAbsentAndOtherIndicatorsSurvive()
    {
        var evidence = Assert.Single(await new PendingRestartCollector((indicator, _) => indicator switch
        {
            PendingRestartIndicator.ComponentBasedServicing => new(Stamp, PendingRestartRegistryReadState.KeyPresent, true),
            PendingRestartIndicator.WindowsUpdate => throw new UnauthorizedAccessException("fixture-denied"),
            _ => new(Stamp, PendingRestartRegistryReadState.ValueAbsent, true, false, NativeErrorCode: 2)
        }).CollectAsync(CancellationToken.None));
        Assert.Equal(EvidenceStatus.Partial, evidence.Status);
        Assert.Equal("true", Fact(evidence, "cbs.keyExists").TextValue);
        Assert.Null(Fact(evidence, "windowsUpdate.keyExists").TextValue);
        Assert.Equal("AccessDenied", Fact(evidence, "windowsUpdate.readState").TextValue);
        Assert.Equal("false", Fact(evidence, "fileRename.valueExists").TextValue);
        Assert.Contains(evidence.Issues, issue => issue.Code == IssueCode.PermissionDenied && issue.Message.Contains("fixture-denied", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AllUnavailableAndContradictoryObservationsNeverBecomeCompleteAbsence()
    {
        var evidence = Assert.Single(await new PendingRestartCollector((_, _) => throw new IOException("fixture unavailable"))
            .CollectAsync(CancellationToken.None));
        Assert.Equal(EvidenceStatus.Unavailable, evidence.Status);
        Assert.Null(Fact(evidence, "cbs.keyExists").TextValue);
        var malformed = Assert.Single(await new PendingRestartCollector((_, _) => new(Stamp, PendingRestartRegistryReadState.KeyAbsent, true))
            .CollectAsync(CancellationToken.None));
        Assert.Equal(EvidenceStatus.Unavailable, malformed.Status);
        Assert.Contains(malformed.Issues, issue => issue.Message.Contains("diverge", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MultiStringPreservesDeleteDestinationEmptiesAllDelimitersAndLiteralPaths()
    {
        const string deleted = @"\??\C:\%TEMP%\delete $(literal).tmp";
        const string moved = @"\??\C:\move.tmp";
        const string destination = @"\??\D:\target Ω.tmp";
        string original = deleted + "\0\0" + moved + "\0" + destination + "\0\0";
        var bytes = Encoding.Unicode.GetBytes(original);
        var evidence = await CollectValue(Present(bytes));
        Assert.Equal(EvidenceStatus.Complete, evidence.Status);
        Assert.Equal(Convert.ToBase64String(bytes), Fact(evidence, "fileRename.rawBase64").TextValue);
        Assert.Equal(original, JsonSerializer.Deserialize<string>(Fact(evidence, "fileRename.originalTextJson").TextValue!));
        Assert.Equal(deleted, Fact(evidence, "fileRename.segment.0").TextValue);
        Assert.Equal("", Fact(evidence, "fileRename.segment.1").TextValue);
        Assert.Equal(moved, Fact(evidence, "fileRename.segment.2").TextValue);
        Assert.Equal(destination, Fact(evidence, "fileRename.segment.3").TextValue);
        Assert.Equal("", Fact(evidence, "fileRename.segment.4").TextValue);
        Assert.Equal("", Fact(evidence, "fileRename.segment.5").TextValue);
        Assert.Equal(6m, Fact(evidence, "fileRename.segmentsObserved").NumberValue);
        Assert.Null(Fact(evidence, "fileRename.operationCount").NumberValue);
        Assert.Contains("vazio preservado", Fact(evidence, "fileRename.segment.1").Label);
    }

    [Theory]
    [InlineData(1U)]
    [InlineData(3U)]
    [InlineData(99U)]
    public async Task UnexpectedRegistryKindKeepsRawBytesWithoutConvertingToAQueue(uint kind)
    {
        var bytes = Encoding.Unicode.GetBytes("source\0\0");
        var evidence = await CollectValue(Present(bytes) with { Kind = kind });
        Assert.Equal(EvidenceStatus.Partial, evidence.Status);
        Assert.Equal((decimal)kind, Fact(evidence, "fileRename.kind").NumberValue);
        Assert.Equal("UnsupportedRegistryKind", Fact(evidence, "fileRename.parseState").TextValue);
        Assert.Equal(Convert.ToBase64String(bytes), Fact(evidence, "fileRename.rawBase64").TextValue);
        Assert.DoesNotContain(evidence.Facts, fact => fact.Key == "fileRename.segmentsObserved");
    }

    [Fact]
    public async Task MalformedUtf16AndMissingTerminatorsAreDistinctAndNotRepaired()
    {
        var odd = await CollectValue(Present([0x61]));
        var surrogate = await CollectValue(Present([0x00, 0xD8, 0x00, 0x00, 0x00, 0x00]));
        var unclosedBytes = Encoding.Unicode.GetBytes("source\0destination\0");
        var unclosed = await CollectValue(Present(unclosedBytes));
        Assert.Equal("MalformedUtf16", Fact(odd, "fileRename.parseState").TextValue);
        Assert.Equal("MalformedUtf16", Fact(surrogate, "fileRename.parseState").TextValue);
        Assert.Equal("MissingMultiStringTermination", Fact(unclosed, "fileRename.parseState").TextValue);
        Assert.Equal("false", Fact(unclosed, "fileRename.doubleNullTerminated").TextValue);
        Assert.Equal(Convert.ToBase64String(unclosedBytes), Fact(unclosed, "fileRename.rawBase64").TextValue);
        Assert.All(new[] { odd, surrogate, unclosed }, evidence => Assert.Equal(EvidenceStatus.Partial, evidence.Status));
    }

    [Fact]
    public async Task SampleLimitPreservesAllOriginalBytesAndExactSegmentCount()
    {
        string text = string.Concat(Enumerable.Range(0, 70).Select(index => "item-" + index + "\0")) + "\0";
        var bytes = Encoding.Unicode.GetBytes(text);
        var evidence = await CollectValue(Present(bytes));
        Assert.Equal(EvidenceStatus.Partial, evidence.Status);
        Assert.Equal(72m, Fact(evidence, "fileRename.segmentsObserved").NumberValue);
        Assert.Equal(64m, Fact(evidence, "fileRename.segmentsRetained").NumberValue);
        Assert.Equal(Convert.ToBase64String(bytes), Fact(evidence, "fileRename.rawBase64").TextValue);
        Assert.Equal(text, JsonSerializer.Deserialize<string>(Fact(evidence, "fileRename.originalTextJson").TextValue!));
        Assert.Contains(evidence.Issues, issue => issue.Code == IssueCode.LimitReached);
    }

    [Fact]
    public async Task OversizedOrInconsistentPayloadIsNotDecodedOrPresentedAsEmpty()
    {
        var oversized = await CollectValue(Present(new byte[PendingRestartCollector.MaximumValueBytes + 1]));
        Assert.Equal(EvidenceStatus.Partial, oversized.Status);
        Assert.DoesNotContain(oversized.Facts, fact => fact.Key == "fileRename.rawBase64");
        Assert.Contains(oversized.Issues, issue => issue.Code == IssueCode.LimitReached);
        var inconsistent = await CollectValue(Present([0, 0, 0, 0]) with { ReportedBytes = 6 });
        Assert.Equal("ReadFailed", Fact(inconsistent, "fileRename.readState").TextValue);
        Assert.Equal(6m, Fact(inconsistent, "fileRename.reportedByteCount").NumberValue);
        Assert.Null(Fact(inconsistent, "fileRename.valueExists").TextValue);
        Assert.DoesNotContain(inconsistent.Facts, fact => fact.Key == "fileRename.segmentsObserved");
    }

    [Fact]
    public async Task CancellationBeforeAndAfterSourceReadDoesNotPublishAPartialSnapshot()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new PendingRestartCollector((_, _) => throw new InvalidOperationException("must not read"))
            .CollectAsync(cancellation.Token));
        using var late = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new PendingRestartCollector((_, _) =>
        { late.Cancel(); return new(Stamp, PendingRestartRegistryReadState.KeyAbsent, false); }).CollectAsync(late.Token));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(5)]
    public void RawProbeDistinguishesMissingFromPermissionDeniedAndIgnoresUndefinedOutMetadata(int result)
    {
        using var handle = new SafeRegistryHandle(new IntPtr(1), ownsHandle: false);
        int calls = 0;
        int Query(SafeRegistryHandle _, string __, out uint kind, byte[]? buffer, ref uint size)
        { calls++; Assert.Null(buffer); kind = 7; size = 12345; return result; }
        var value = BoundedRegistryValueReader.Read(handle, "selected", Query);
        Assert.Equal(1, calls);
        Assert.Equal(result == 2 ? false : (bool?)null, value.Exists);
        Assert.Null(value.Kind);
        Assert.Null(value.ReportedBytes);
        Assert.Null(value.RawBytes);
        if (result == 5) Assert.Equal(IssueCode.PermissionDenied, value.Issue!.Code);
    }

    [Fact]
    public void RawProbeBoundsAllocationBeforeAnyDataRead()
    {
        using var handle = new SafeRegistryHandle(new IntPtr(1), ownsHandle: false);
        int calls = 0;
        int Query(SafeRegistryHandle _, string __, out uint kind, byte[]? buffer, ref uint size)
        { calls++; Assert.Null(buffer); kind = 7; size = 16385; return 0; }
        var value = BoundedRegistryValueReader.Read(handle, "selected", Query);
        Assert.Equal(1, calls);
        Assert.Equal(16385U, value.ProbedBytes);
        Assert.Null(value.RawBytes);
        Assert.False(value.ReadAttempted);
        Assert.Equal(IssueCode.LimitReached, value.Issue!.Code);
    }

    [Theory]
    [InlineData(234, 7U, 12U)]
    [InlineData(0, 1U, 8U)]
    [InlineData(0, 7U, 4U)]
    [InlineData(0, 7U, 9U)]
    public void RawGrowthShrinkageTypeChangeOrImpossibleByteCountNeverProducesDecodedBytes(int code, uint readKind, uint readBytes)
    {
        using var handle = new SafeRegistryHandle(new IntPtr(1), ownsHandle: false);
        int calls = 0;
        int Query(SafeRegistryHandle _, string __, out uint kind, byte[]? buffer, ref uint size)
        {
            calls++;
            if (buffer is null) { kind = 7; size = 8; return 0; }
            Assert.Equal(8, buffer.Length); kind = readKind; size = readBytes; return code;
        }
        var value = BoundedRegistryValueReader.Read(handle, "selected", Query);
        Assert.Equal(2, calls);
        Assert.True(value.ChangedDuringRead);
        Assert.Null(value.RawBytes);
        Assert.Equal(7U, value.ProbedKind);
        Assert.Equal(8U, value.ProbedBytes);
        Assert.NotNull(value.Issue);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(5)]
    public void RawDisappearanceOrDeniedReadPreservesProbeFactsAndDoesNotRetry(int result)
    {
        using var handle = new SafeRegistryHandle(new IntPtr(1), ownsHandle: false);
        int calls = 0;
        int Query(SafeRegistryHandle _, string __, out uint kind, byte[]? buffer, ref uint size)
        { calls++; kind = 7; size = 8; return buffer is null ? 0 : result; }
        var value = BoundedRegistryValueReader.Read(handle, "selected", Query);
        Assert.Equal(2, calls);
        Assert.Equal(result == 2 ? false : (bool?)null, value.Exists);
        Assert.Equal(8U, value.ProbedBytes);
        Assert.Null(value.Kind);
        Assert.Null(value.ReportedBytes);
        Assert.Null(value.RawBytes);
        Assert.Equal(result == 5 ? IssueCode.PermissionDenied : IssueCode.ReadFailed, value.Issue!.Code);
    }

    [Fact]
    public void RawSuccessfulReadPreservesExactBytesAndTypeOnce()
    {
        using var handle = new SafeRegistryHandle(new IntPtr(1), ownsHandle: false);
        byte[] expected = [1, 2, 3, 4];
        int calls = 0;
        int Query(SafeRegistryHandle _, string __, out uint kind, byte[]? buffer, ref uint size)
        { calls++; kind = 7; size = 4; if (buffer is not null) expected.CopyTo(buffer, 0); return 0; }
        var value = BoundedRegistryValueReader.Read(handle, "selected", Query);
        Assert.Equal(2, calls);
        Assert.True(value.Exists);
        Assert.Equal(7U, value.Kind);
        Assert.Equal(4U, value.ReportedBytes);
        Assert.Equal(expected, value.RawBytes);
        Assert.Null(value.Issue);
    }

    [Fact]
    public async Task SoftwareInventoryUsesUnknownPresenceWithoutInventingAbsentOrFoundValues()
    {
        static IEnumerable<SoftwareRegistryObservation> Read(SoftwareRegistryLocation _, CancellationToken __)
        {
            yield return new SoftwareRegistryScopeObservation(true);
            yield return new SoftwareRegistryEntryObservation("denied", [new("denied", 0, 0,
                Issue: new(IssueCode.PermissionDenied, "fixture denied"), Exists: null, NativeErrorCode: 5)]);
        }
        var evidence = Assert.Single(await new StartupCollector(Read).CollectAsync(CancellationToken.None));
        Assert.Equal(EvidenceStatus.Partial, evidence.Status);
        Assert.All(evidence.Facts.Where(fact => fact.Key.EndsWith(".command.exists", StringComparison.Ordinal)), fact => Assert.Null(fact.TextValue));
        Assert.DoesNotContain(evidence.Facts, fact => fact.Key.EndsWith(".command.kind", StringComparison.Ordinal));
        Assert.All(evidence.Issues, issue => Assert.Equal(IssueCode.PermissionDenied, issue.Code));
    }

    private static PendingRestartRegistryObservation Present(byte[] bytes) => new(Stamp, PendingRestartRegistryReadState.ValuePresent, true, true,
        7, (uint)bytes.Length, bytes, NativeErrorCode: 0, ProbedKind: 7, ProbedBytes: (uint)bytes.Length, ReadAttempted: true);
    private static async Task<DiagnosticEvidence> CollectValue(PendingRestartRegistryObservation value) => Assert.Single(await new PendingRestartCollector((indicator, _) =>
        indicator == PendingRestartIndicator.FileRenameOperations ? value : new(Stamp, PendingRestartRegistryReadState.KeyAbsent, false)).CollectAsync(CancellationToken.None));
    private static DiagnosticFact Fact(DiagnosticEvidence evidence, string key) => evidence.Facts.Single(fact => fact.Key == key);
}
