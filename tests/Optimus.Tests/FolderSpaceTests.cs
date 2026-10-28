using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Optimus.Application;
using Optimus.Domain;
using Optimus.Infrastructure.Storage;

namespace Optimus.Tests;

public sealed class FolderSpaceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "optimus-folder-space-tests", Guid.NewGuid().ToString("N"));
    private readonly List<OwnedJunction> _junctions = [];
    private string Selected => Path.Combine(_root, "pasta escolhida Ω");

    public FolderSpaceTests() => Directory.CreateDirectory(Selected);

    [Fact]
    public async Task EmptyFolderHasZeroObservedLogicalBytesWithExplicitScope()
    {
        var evidence = await Collect();

        Assert.Equal(MaintenanceCollectors.FolderSpace, evidence.CollectorId);
        Assert.Equal(EvidenceStatus.Complete, evidence.Status);
        Assert.Equal(0m, Fact(evidence, "logicalBytesObserved").NumberValue);
        Assert.Equal("bytes", Fact(evidence, "logicalBytesObserved").Unit);
        Assert.Equal(0m, Fact(evidence, "filesObserved").NumberValue);
        Assert.Equal(1m, Fact(evidence, "directoriesBound").NumberValue);
        Assert.Equal("false", Fact(evidence, "contentReadRequested").TextValue);
        Assert.Equal("false", Fact(evidence, "physicalSpaceMeasured").TextValue);
        Assert.Equal("false", Fact(evidence, "recoverableSpaceEstimated").TextValue);
        Assert.Equal("false", Fact(evidence, "snapshotAtomic").TextValue);
    }

    [Fact]
    public async Task UnicodePathsSizesAndLastWriteMetadataComeFromOriginalFiles()
    {
        var child = Directory.CreateDirectory(Path.Combine(Selected, "subpasta d'água"));
        var largest = Path.Combine(child.FullName, "arquivo maior Ω.bin");
        File.WriteAllBytes(Path.Combine(Selected, "small.bin"), new byte[7]);
        File.WriteAllBytes(largest, new byte[41]);
        var stamp = new DateTime(2026, 10, 20, 10, 20, 30, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(largest, stamp);
        string requested = Selected + Path.DirectorySeparatorChar;

        var evidence = Assert.Single(await new FolderSpaceCollector(requested).CollectAsync(CancellationToken.None));

        Assert.Equal(EvidenceStatus.Complete, evidence.Status);
        Assert.Equal(requested, Fact(evidence, "requestedPath").TextValue);
        Assert.Equal(Selected, Fact(evidence, "resolvedRoot").TextValue);
        Assert.Equal(48m, Fact(evidence, "logicalBytesObserved").NumberValue);
        Assert.Equal(2m, Fact(evidence, "filesObserved").NumberValue);
        Assert.Equal(largest, Fact(evidence, "largest.0.path").TextValue);
        Assert.Equal(41m, Fact(evidence, "largest.0.logicalBytes").NumberValue);
        Assert.Equal(new DateTimeOffset(stamp).ToString("O"), Fact(evidence, "largest.0.lastWriteUtc").TextValue);
        Assert.Equal(2m, Fact(evidence, "largestFilesRetained").NumberValue);
        Assert.Equal(0m, Fact(evidence, "largestFileDetailsOmitted").NumberValue);
        Assert.Equal(41, new FileInfo(largest).Length);
    }

    [Fact]
    public async Task TopTwentyAreTheLargestObservedFilesWithoutRetainingEveryPath()
    {
        for (int index = 1; index <= 25; index++) File.WriteAllBytes(Path.Combine(Selected, $"file-{index:00}.bin"), new byte[index]);

        var evidence = await Collect();

        Assert.Equal(EvidenceStatus.Complete, evidence.Status);
        Assert.Equal(325m, Fact(evidence, "logicalBytesObserved").NumberValue);
        Assert.Equal(25m, Fact(evidence, "filesObserved").NumberValue);
        Assert.Equal(20m, Fact(evidence, "largestFilesRetained").NumberValue);
        var sizes = evidence.Facts.Where(item => item.Key.StartsWith("largest.", StringComparison.Ordinal) && item.Key.EndsWith(".logicalBytes", StringComparison.Ordinal)).Select(item => item.NumberValue).ToArray();
        Assert.Equal(Enumerable.Range(6, 20).Reverse().Select(value => (decimal?)value).ToArray(), sizes);
        Assert.DoesNotContain(evidence.Facts, item => item.TextValue == Path.Combine(Selected, "file-01.bin"));
    }

    [Fact]
    public async Task ExclusiveContentHandleDoesNotRequireFileDataReadAccess()
    {
        var path = Path.Combine(Selected, "content-is-not-opened.bin");
        File.WriteAllBytes(path, new byte[123]);
        using var heldContent = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var evidence = await Collect();

        Assert.Equal(EvidenceStatus.Complete, evidence.Status);
        Assert.Equal(123m, Fact(evidence, "logicalBytesObserved").NumberValue);
        Assert.Equal(1m, Fact(evidence, "filesObserved").NumberValue);
    }

    [Fact]
    public async Task EntryBudgetRetainsExactObservedCountersAndUnknownRemainder()
    {
        for (int index = 0; index < 5; index++) File.WriteAllBytes(Path.Combine(Selected, $"file-{index}.bin"), new byte[3]);

        var evidence = await Collect(new() { MaximumEntries = 2 });

        Assert.Equal(EvidenceStatus.Partial, evidence.Status);
        Assert.True(evidence.Coverage.LimitReached);
        Assert.Equal(2, evidence.Coverage.ExaminedItems);
        Assert.Equal(2m, Fact(evidence, "processedEntries").NumberValue);
        Assert.Equal(3m, Fact(evidence, "enumeratedEntries").NumberValue);
        Assert.Equal(6m, Fact(evidence, "logicalBytesObserved").NumberValue);
        Assert.Equal(1m, Fact(evidence, "knownOmittedEntries").NumberValue);
        Assert.Null(Fact(evidence, "unexaminedRemainderCount").NumberValue);
        Assert.Equal("true", Fact(evidence, "entryLimitReached").TextValue);
    }

    [Fact]
    public async Task DepthBudgetNeverTraversesTheOmittedChild()
    {
        File.WriteAllBytes(Path.Combine(Selected, "local.bin"), new byte[5]);
        var nested = Directory.CreateDirectory(Path.Combine(Selected, "not-traversed"));
        File.WriteAllBytes(Path.Combine(nested.FullName, "omitted.bin"), new byte[1000]);

        var evidence = await Collect(new() { MaximumDepth = 1 });

        Assert.Equal(EvidenceStatus.Partial, evidence.Status);
        Assert.Equal(5m, Fact(evidence, "logicalBytesObserved").NumberValue);
        Assert.Equal(1m, Fact(evidence, "skippedDepthEntries").NumberValue);
        Assert.Equal(1m, Fact(evidence, "directoriesBound").NumberValue);
        Assert.DoesNotContain(evidence.Facts, item => item.TextValue == Path.Combine(nested.FullName, "omitted.bin"));
    }

    [Fact]
    public async Task JunctionIsRecordedAsOmittedAndItsTargetIsNotTraversed()
    {
        File.WriteAllBytes(Path.Combine(Selected, "local.bin"), new byte[5]);
        var outside = Directory.CreateDirectory(Path.Combine(_root, "target-outside-selection"));
        File.WriteAllBytes(Path.Combine(outside.FullName, "keep.bin"), new byte[999]);
        CreateJunction(Path.Combine(Selected, "junction"), outside.FullName);

        var evidence = await Collect();

        Assert.Equal(EvidenceStatus.Partial, evidence.Status);
        Assert.Equal(5m, Fact(evidence, "logicalBytesObserved").NumberValue);
        Assert.Equal(1m, Fact(evidence, "skippedReparseEntries").NumberValue);
        Assert.Equal(1m, Fact(evidence, "knownOmittedEntries").NumberValue);
        Assert.Equal(999, new FileInfo(Path.Combine(outside.FullName, "keep.bin")).Length);
    }

    [Fact]
    public async Task SelectedJunctionRootIsRejectedInsteadOfBeingResolvedToItsTarget()
    {
        var link = Path.Combine(_root, "selected-junction");
        CreateJunction(link, Selected);

        var evidence = Assert.Single(await new FolderSpaceCollector(link).CollectAsync(CancellationToken.None));

        Assert.Equal(EvidenceStatus.Unavailable, evidence.Status);
        Assert.Equal(link, Fact(evidence, "requestedPath").TextValue);
        Assert.Null(Fact(evidence, "logicalBytesObserved").NumberValue);
        Assert.NotEmpty(evidence.Issues);
    }

    [Fact]
    public async Task HardlinksAreCountedPerEntryRatherThanAsRecoverablePhysicalSpace()
    {
        var source = Path.Combine(Selected, "first.bin");
        var alias = Path.Combine(Selected, "second.bin");
        File.WriteAllBytes(source, new byte[37]);
        if (!CreateHardLink(alias, source, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error());

        var evidence = await Collect();

        Assert.Equal(EvidenceStatus.Complete, evidence.Status);
        Assert.Equal(74m, Fact(evidence, "logicalBytesObserved").NumberValue);
        Assert.Equal(2m, Fact(evidence, "filesObserved").NumberValue);
        Assert.Equal(2m, Fact(evidence, "hardLinkedEntriesObserved").NumberValue);
        Assert.Equal(Fact(evidence, "largest.0.fileId").NumberValue, Fact(evidence, "largest.1.fileId").NumberValue);
        Assert.Equal("false", Fact(evidence, "recoverableSpaceEstimated").TextValue);
    }

    [Fact]
    public async Task BoundedIssueDetailsRetainTheExactOmissionCount()
    {
        var outside = Directory.CreateDirectory(Path.Combine(_root, "outside"));
        for (int index = 0; index < 36; index++) CreateJunction(Path.Combine(Selected, "link-" + index), outside.FullName);

        var evidence = await Collect();

        Assert.Equal(EvidenceStatus.Partial, evidence.Status);
        Assert.Equal(36m, Fact(evidence, "skippedReparseEntries").NumberValue);
        Assert.Equal(36m, Fact(evidence, "issueCount").NumberValue);
        Assert.Equal(4m, Fact(evidence, "issueDetailsOmitted").NumberValue);
        Assert.Equal(33, evidence.Issues.Count); // 32 original details and one fixed summary.
    }

    [Fact]
    public async Task CooperativeDeadlineProducesPartialOrUnavailableCoverageNotACompleteZero()
    {
        File.WriteAllBytes(Path.Combine(Selected, "not-assumed-empty.bin"), new byte[100]);
        var evidence = Assert.Single(await new FolderSpaceCollector(Selected, new AdvancingClock(), new() { MaximumDuration = TimeSpan.FromMilliseconds(2) }).CollectAsync(CancellationToken.None));

        Assert.NotEqual(EvidenceStatus.Complete, evidence.Status);
        Assert.Equal("true", Fact(evidence, "durationLimitReached").TextValue);
        Assert.True(evidence.Coverage.LimitReached);
        Assert.Null(Fact(evidence, "unexaminedRemainderCount").NumberValue);
    }

    [Theory]
    [InlineData(0, 32)]
    [InlineData(20_001, 32)]
    [InlineData(20_000, 0)]
    [InlineData(20_000, 33)]
    public async Task InvalidLimitsAreReportedByCollectionRatherThanConstructor(int entries, int depth)
    {
        var collector = new FolderSpaceCollector(Selected, limits: new() { MaximumEntries = entries, MaximumDepth = depth });

        var evidence = Assert.Single(await collector.CollectAsync(CancellationToken.None));

        Assert.Equal(EvidenceStatus.Unavailable, evidence.Status);
        Assert.Null(Fact(evidence, "logicalBytesObserved").NumberValue);
        Assert.NotEmpty(evidence.Issues);
    }

    [Fact]
    public async Task MissingFolderReturnsUnavailableWithOriginalSelectionAndUnknownSize()
    {
        var selected = Path.Combine(_root, "missing");
        var collector = new FolderSpaceCollector(selected);
        var evidence = Assert.Single(await collector.CollectAsync(CancellationToken.None));

        Assert.Equal(EvidenceStatus.Unavailable, evidence.Status);
        Assert.Equal(selected, Fact(evidence, "requestedPath").TextValue);
        Assert.Null(Fact(evidence, "logicalBytesObserved").NumberValue);
        Assert.False(Directory.Exists(selected));
    }

    [Fact]
    public async Task CancellationPropagatesWithoutPublishingAnIncompleteSnapshot()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new FolderSpaceCollector(Selected).CollectAsync(cancellation.Token));
    }

    private async Task<DiagnosticEvidence> Collect(FolderSpaceLimits? limits = null) =>
        Assert.Single(await new FolderSpaceCollector(Selected, limits: limits).CollectAsync(CancellationToken.None));
    private static DiagnosticFact Fact(DiagnosticEvidence evidence, string key) => evidence.Facts.Single(item => item.Key == key);
    private sealed class AdvancingClock : TimeProvider
    {
        private long _tick;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Interlocked.Increment(ref _tick);
    }

    private void CreateJunction(string path, string target)
    {
        Directory.CreateDirectory(path);
        using var handle = OpenJunction(path, 0x40000000 | 0x80, 7, IntPtr.Zero, 3, 0x02000000 | 0x00200000, IntPtr.Zero);
        if (handle.IsInvalid || !GetInformation(handle, out var identity)) throw new Win32Exception(Marshal.GetLastWin32Error());
        _junctions.Add(new(path, identity.VolumeSerialNumber, ((ulong)identity.FileIndexHigh << 32) | identity.FileIndexLow));
        var substitute = Encoding.Unicode.GetBytes(@"\??\" + target);
        var printed = Encoding.Unicode.GetBytes(target);
        using var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(buffer, Encoding.Unicode, leaveOpen: true))
        {
            writer.Write(0xA0000003u);
            writer.Write(checked((ushort)(8 + substitute.Length + 2 + printed.Length + 2)));
            writer.Write((ushort)0); writer.Write((ushort)0); writer.Write(checked((ushort)substitute.Length));
            writer.Write(checked((ushort)(substitute.Length + 2))); writer.Write(checked((ushort)printed.Length));
            writer.Write(substitute); writer.Write((ushort)0); writer.Write(printed); writer.Write((ushort)0);
        }
        var bytes = buffer.ToArray();
        if (!Reparse(handle, 0x000900A4, bytes, (uint)bytes.Length, IntPtr.Zero, 0, out _, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string link, string existing, IntPtr security);
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle OpenJunction(string path, uint access, uint sharing, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Reparse(SafeFileHandle file, uint code, byte[] input, uint bytes, IntPtr output, uint outputBytes, out uint returned, IntPtr overlapped);
    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetInformation(SafeFileHandle file, out FileInformation information);
    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
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
                if (handle.IsInvalid || !GetInformation(handle, out var identity)) throw new Win32Exception(Marshal.GetLastWin32Error());
                if (identity.VolumeSerialNumber != owned.Volume || (((ulong)identity.FileIndexHigh << 32) | identity.FileIndexLow) != owned.FileId)
                    throw new InvalidOperationException("Fixture junction identity changed; preserved instead of deleting unknown content.");
                if ((identity.FileAttributes & (uint)FileAttributes.ReparsePoint) != 0)
                {
                    byte[] deletion = new byte[8];
                    BinaryPrimitives.WriteUInt32LittleEndian(deletion, 0xA0000003u);
                    if (!Reparse(handle, 0x000900AC, deletion, (uint)deletion.Length, IntPtr.Zero, 0, out _, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error());
                }
            }
            Directory.Delete(owned.Path, recursive: false);
        }
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
