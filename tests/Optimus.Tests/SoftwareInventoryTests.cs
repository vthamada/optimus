using System.Text;
using Microsoft.Win32;
using Optimus.Application;
using Optimus.Domain;
using Optimus.Infrastructure.Inventory;

namespace Optimus.Tests;

public sealed class SoftwareInventoryTests
{
    [Fact]
    public async Task InstalledRegistryIdentitiesDoNotCollapseEqualDisplayNamesAcrossHiveAndView()
    {
        static IEnumerable<SoftwareRegistryObservation> Read(SoftwareRegistryLocation _, CancellationToken __)
        {
            yield return new SoftwareRegistryScopeObservation(true);
            yield return Program("entry-key", "Same display name");
        }
        var evidence = Assert.Single(await new InstalledApplicationsCollector(Read).CollectAsync(CancellationToken.None));

        Assert.Equal(InventoryCollectors.InstalledApplications, evidence.CollectorId);
        Assert.Equal(EvidenceStatus.Complete, evidence.Status);
        Assert.Equal(4, evidence.Coverage.ExaminedItems);
        Assert.Equal(4, evidence.Facts.Count(item => item.Key.EndsWith(".DisplayName", StringComparison.Ordinal)));
        Assert.Equal(4, evidence.Facts.Single(item => item.Key == "retainedEntries").NumberValue);
        Assert.Contains(evidence.Facts, item => item.Key.EndsWith(".source", StringComparison.Ordinal) && item.TextValue!.StartsWith("CurrentUser\\", StringComparison.Ordinal));
        Assert.Contains(evidence.Facts, item => item.Key.EndsWith(".source", StringComparison.Ordinal) && item.TextValue!.StartsWith("LocalMachine\\", StringComparison.Ordinal));
        Assert.Contains(evidence.Facts, item => item.Key.EndsWith(".view", StringComparison.Ordinal) && item.TextValue == "Registry32");
        Assert.Contains(evidence.Facts, item => item.Key.EndsWith(".view", StringComparison.Ordinal) && item.TextValue == "Registry64");
    }

    [Fact]
    public async Task StartupPreservesUnexpandedLiteralCommandsWithoutInferringEnabledState()
    {
        const string command = "%LOCALAPPDATA%\\program.exe --argument \"$(literal) & value\"";
        static IEnumerable<SoftwareRegistryObservation> Read(SoftwareRegistryLocation _, CancellationToken __)
        {
            yield return new SoftwareRegistryScopeObservation(true);
            yield return new SoftwareRegistryEntryObservation("Example", [Text("Example", command, 2)]);
        }
        var evidence = Assert.Single(await new StartupCollector(Read).CollectAsync(CancellationToken.None));

        Assert.Equal(InventoryCollectors.Startup, evidence.CollectorId);
        Assert.Equal(8, evidence.Coverage.ExaminedItems);
        Assert.All(evidence.Facts.Where(item => item.Key.EndsWith(".command", StringComparison.Ordinal)), item => Assert.Equal(command, item.TextValue));
        Assert.Contains("não comprova habilitação", evidence.Coverage.Scope);
        Assert.Contains("pastas Startup", evidence.Coverage.Scope);
        Assert.DoesNotContain(evidence.Facts, item => item.Key.Contains("enabled", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task MissingScopesAreExplicitAndDoNotBecomeFailedReads()
    {
        var evidence = Assert.Single(await new InstalledApplicationsCollector((_, _) => [new SoftwareRegistryScopeObservation(false)])
            .CollectAsync(CancellationToken.None));

        Assert.Equal(EvidenceStatus.Complete, evidence.Status);
        Assert.Equal(0, evidence.Coverage.ExaminedItems);
        Assert.Equal(4, evidence.Facts.Count(item => item.Key.EndsWith(".exists", StringComparison.Ordinal) && item.TextValue == "false"));
        Assert.Empty(evidence.Issues);
    }

    [Fact]
    public async Task LateReadFailureRetainsFactsAlreadyObservedAndMarksPartial()
    {
        static IEnumerable<SoftwareRegistryObservation> Read(SoftwareRegistryLocation _, CancellationToken __)
        {
            yield return new SoftwareRegistryScopeObservation(true);
            yield return Program("preserved", "Original");
            throw new UnauthorizedAccessException("Fixture denied after one record");
        }
        var evidence = Assert.Single(await new InstalledApplicationsCollector(Read).CollectAsync(CancellationToken.None));

        Assert.Equal(EvidenceStatus.Partial, evidence.Status);
        Assert.Equal(4, evidence.Coverage.ExaminedItems);
        Assert.Contains(evidence.Facts, item => item.TextValue == "Original");
        Assert.All(evidence.Issues, item => Assert.Equal(IssueCode.PermissionDenied, item.Code));
    }

    [Fact]
    public async Task UnreadableScopesRemainUnavailableRatherThanEmptyCompleteInventory()
    {
        var evidence = Assert.Single(await new InstalledApplicationsCollector((_, _) => throw new IOException("Fixture unavailable"))
            .CollectAsync(CancellationToken.None));

        Assert.Equal(EvidenceStatus.Unavailable, evidence.Status);
        Assert.Equal(0, evidence.Coverage.ExaminedItems);
        Assert.Equal(4, evidence.Issues.Count);
    }

    [Fact]
    public async Task EntryLimitCountsTheExaminedProbeAndPreservesAtMost500Records()
    {
        static IEnumerable<SoftwareRegistryObservation> Read(SoftwareRegistryLocation _, CancellationToken __)
        {
            yield return new SoftwareRegistryScopeObservation(true);
            for (int index = 0; index < 501; index++) yield return new SoftwareRegistryEntryObservation(index.ToString(), []);
        }
        var evidence = Assert.Single(await new InstalledApplicationsCollector(Read).CollectAsync(CancellationToken.None));

        Assert.Equal(EvidenceStatus.Partial, evidence.Status);
        Assert.True(evidence.Coverage.LimitReached);
        Assert.Equal(501, evidence.Coverage.ExaminedItems);
        Assert.Equal(500, evidence.Facts.Single(item => item.Key == "retainedEntries").NumberValue);
        Assert.Equal(500, evidence.Facts.Count(item => item.Key.EndsWith(".source", StringComparison.Ordinal)));
        Assert.Contains(evidence.Issues, item => item.Code == IssueCode.LimitReached);
    }

    [Fact]
    public async Task OversizedEntryIsOmittedAsAWholeInsteadOfSilentlyTruncatingItsSource()
    {
        var original = new string('X', 200_000);
        IEnumerable<SoftwareRegistryObservation> Read(SoftwareRegistryLocation _, CancellationToken __)
        {
            yield return new SoftwareRegistryScopeObservation(true);
            yield return Program("too-large", original);
        }
        var evidence = Assert.Single(await new InstalledApplicationsCollector(Read).CollectAsync(CancellationToken.None));

        Assert.True(evidence.Coverage.LimitReached);
        Assert.Equal(1, evidence.Coverage.ExaminedItems);
        Assert.Equal(0, evidence.Facts.Single(item => item.Key == "retainedEntries").NumberValue);
        Assert.DoesNotContain(evidence.Facts, item => item.Key.StartsWith("entry.", StringComparison.Ordinal));
        Assert.Equal(200_000, original.Length);
    }

    [Fact]
    public async Task MissingAndNonTextValuesRetainUnknownAndRawTypeFacts()
    {
        static IEnumerable<SoftwareRegistryObservation> Read(SoftwareRegistryLocation _, CancellationToken __)
        {
            yield return new SoftwareRegistryScopeObservation(true);
            yield return new SoftwareRegistryEntryObservation("entry", [new("DisplayName", 0, 0, Exists: false),
                SoftwareRegistrySource.Decode("DisplayVersion", 4, [42, 0, 0, 0])]);
        }
        var evidence = Assert.Single(await new InstalledApplicationsCollector(Read).CollectAsync(CancellationToken.None));

        Assert.Equal(EvidenceStatus.Partial, evidence.Status);
        Assert.Null(evidence.Facts.Single(item => item.Key == "entry.1.DisplayName").TextValue);
        Assert.Equal("false", evidence.Facts.Single(item => item.Key == "entry.1.DisplayName.exists").TextValue);
        Assert.Null(evidence.Facts.Single(item => item.Key == "entry.1.DisplayVersion").TextValue);
        Assert.Equal(42, evidence.Facts.Single(item => item.Key == "entry.1.DisplayVersion.number").NumberValue);
        Assert.Equal("KgAAAA==", evidence.Facts.Single(item => item.Key == "entry.1.DisplayVersion.raw").TextValue);
    }

    [Fact]
    public async Task DuplicateOrUnconfirmedBoundaryEntriesCannotPublishAmbiguousFacts()
    {
        static IEnumerable<SoftwareRegistryObservation> Read(SoftwareRegistryLocation _, CancellationToken __)
        {
            yield return new SoftwareRegistryScopeObservation(true);
            yield return new SoftwareRegistryEntryObservation("entry", [Text("DisplayName", "first"), Text("DisplayName", "second")]);
        }
        var evidence = Assert.Single(await new InstalledApplicationsCollector(Read).CollectAsync(CancellationToken.None));

        Assert.Equal(EvidenceStatus.Partial, evidence.Status);
        Assert.DoesNotContain(evidence.Facts, item => item.TextValue is "first" or "second");
        Assert.Contains(evidence.Issues, item => item.Message.Contains("duplicados", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CancellationDuringEnumerationDoesNotReturnACompletedInventory()
    {
        using var cancellation = new CancellationTokenSource();
        IEnumerable<SoftwareRegistryObservation> Read(SoftwareRegistryLocation _, CancellationToken token)
        {
            yield return new SoftwareRegistryScopeObservation(true);
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
        }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new InstalledApplicationsCollector(Read).CollectAsync(cancellation.Token));
    }

    [Fact]
    public void RegistryDecodingPreservesExpansionSyntaxEmptyValuesAndEmbeddedNulls()
    {
        var value = SoftwareRegistrySource.Decode("command", 2, Encoding.Unicode.GetBytes("%USERPROFILE%\\app.exe\0inside\0"));
        Assert.Equal("%USERPROFILE%\\app.exe\0inside", value.Text);
        Assert.Null(value.Issue);
        Assert.Equal("", SoftwareRegistrySource.Decode("empty", 1, [0, 0]).Text);
    }

    [Fact]
    public void MalformedUtf16AndUnknownKindRetainBytesInsteadOfInventingText()
    {
        var malformed = SoftwareRegistrySource.Decode("broken", 1, [0x00, 0xD8]);
        Assert.Null(malformed.Text);
        Assert.Equal("ANg=", malformed.RawBase64);
        Assert.NotNull(malformed.Issue);
        var unknown = SoftwareRegistrySource.Decode("unknown", 1234, [1, 2]);
        Assert.Equal((uint)1234, unknown.Kind);
        Assert.Equal("AQI=", unknown.RawBase64);
        Assert.Null(unknown.Text);
    }

    [Fact]
    public void ValuesAboveReadLimitAreNotDecodedOrRetainedAsAnExpandedPayload()
    {
        var value = SoftwareRegistrySource.Decode("large", 1, new byte[SoftwareRegistrySource.MaximumValueBytes + 1]);
        Assert.Null(value.Text);
        Assert.Null(value.RawBase64);
        Assert.Equal(IssueCode.LimitReached, value.Issue!.Code);
        Assert.Equal((uint)(SoftwareRegistrySource.MaximumValueBytes + 1), value.ReportedBytes);
    }

    private static SoftwareRegistryEntryObservation Program(string key, string name) => new(key, [Text("DisplayName", name)]);
    private static SoftwareRegistryValue Text(string name, string text, uint kind = 1) => new(name, kind, (uint)((text.Length + 1) * 2), Text: text);
}
