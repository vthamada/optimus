using System.Globalization;
using System.Security;
using System.Text.Json;
using Optimus.Domain;
using Optimus.Infrastructure;
using Optimus.Infrastructure.Events;

namespace Optimus.Tests;

public sealed class RestartHistoryTests
{
    [Fact]
    public async Task CategoriesAreSeparateRecordsNotSummedPhysicalReboots()
    {
        var records = new[]
        {
            SystemEventFixture.Observation("Microsoft-Windows-Kernel-Power", 41, 1),
            SystemEventFixture.Observation("EventLog", 6008, 2),
            SystemEventFixture.Observation("User32", 1074, 3),
            SystemEventFixture.Observation("Microsoft-Windows-WER-SystemErrorReporting", 1001, 4),
            SystemEventFixture.Observation("BugCheck", 1001, 5),
            SystemEventFixture.Observation("Microsoft-Windows-WHEA-Logger", 17, 6, level: 3),
            SystemEventFixture.Observation("Microsoft-Windows-WHEA-Logger", 18, 7, level: 2)
        };
        var evidence = await Collect(records);
        Assert.Equal("windows.restart-history", evidence.CollectorId);
        Assert.Equal(EvidenceStatus.Complete, evidence.Status);
        Assert.Equal(1m, Fact(evidence, RestartHistoryCollector.KernelPower41Observed).NumberValue);
        Assert.Equal(1m, Fact(evidence, RestartHistoryCollector.UnexpectedShutdown6008Observed).NumberValue);
        Assert.Equal(1m, Fact(evidence, RestartHistoryCollector.ShutdownRequests1074Observed).NumberValue);
        Assert.Equal(2m, Fact(evidence, RestartHistoryCollector.BugcheckEventsObserved).NumberValue);
        Assert.Equal(2m, Fact(evidence, RestartHistoryCollector.WheaEventsObserved).NumberValue);
        Assert.Equal("false", Fact(evidence, "physicalRestartCountKnown").TextValue);
        Assert.Equal("unknown", Fact(evidence, "logRolloverCoverage").TextValue);
        Assert.Contains("não se somam 41 e 6008", evidence.Coverage.Scope);
        Assert.True(SessionValidation.IsValid(new(Guid.NewGuid(), SystemEventFixture.Now, SystemEventFixture.Now, SessionStatus.Complete, [evidence], [])));
    }

    [Fact]
    public async Task OriginalXmlUtcSourceTimeIdentityAndStructuredDataArePreservedInSourceOrder()
    {
        var raw = SystemEventFixture.Xml("User32", 1074, ulong.MaxValue.ToString(CultureInfo.InvariantCulture),
            systemTime: "2026-10-28T09:00:00.0000000-03:00", data: "<Data Name='param1'>d'água Ω.exe</Data><Data Name='param1'>second</Data>");
        var evidence = await Collect([new("User32", 1074, DateTimeOffset.UnixEpoch, raw)]);
        Assert.Equal(raw, Fact(evidence, "event.0.xml").TextValue);
        Assert.True(Fact(evidence, "event.0.xml").Detailed);
        Assert.Equal((decimal)ulong.MaxValue, Fact(evidence, "event.0.recordId").NumberValue);
        Assert.Equal("2026-10-28T09:00:00.0000000-03:00", Fact(evidence, "event.0.time").TextValue);
        Assert.Equal("2026-10-28T12:00:00.0000000+00:00", Fact(evidence, "event.0.timeParsedUtc").TextValue);
        Assert.Equal("EventData/Data", Fact(evidence, "event.0.data.0.sourcePath").TextValue);
        Assert.Equal("d'água Ω.exe", Fact(evidence, "event.0.data.0.value").TextValue);
        Assert.False(Fact(evidence, "event.0.data.0.value").Detailed);
        Assert.Equal("second", Fact(evidence, "event.0.data.1.value").TextValue);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(30)]
    [InlineData(365)]
    public async Task FixedFromToAreCapturedOnceAndEmptyLogDoesNotAssertHealth(int days)
    {
        DateTimeOffset? from = null, to = null;
        var evidence = Assert.Single(await new RestartHistoryCollector(days, (start, end, _) => { from = start; to = end; return []; }, new SystemEventFixture.Clock()).CollectAsync(CancellationToken.None));
        Assert.Equal(SystemEventFixture.Now.AddDays(-days), from);
        Assert.Equal(SystemEventFixture.Now, to);
        Assert.Equal(0m, Fact(evidence, RestartHistoryCollector.KernelPower41Observed).NumberValue);
        Assert.Equal(EvidenceStatus.Complete, evidence.Status);
        Assert.Contains("ausência de registros não comprova", evidence.Coverage.Scope);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(366)]
    public void InvalidWindowIsRejectedBeforeReading(int days) => Assert.Throws<ArgumentOutOfRangeException>(() => new RestartHistoryCollector(days));

    [Theory]
    [InlineData("Other", 41, 2)]
    [InlineData("Microsoft-Windows-Kernel-Power", 6008, 2)]
    [InlineData("Microsoft-Windows-WHEA-Logger", 17, 4)]
    [InlineData("Microsoft-Windows-WHEA-Logger", 17, 1)]
    public async Task ProviderIdAndWheaLevelMustMatchTheSelection(string provider, int id, int level)
    {
        var observation = SystemEventFixture.Observation(provider, id, level: level);
        var evidence = await Collect([observation]);
        Assert.Equal(EvidenceStatus.Partial, evidence.Status);
        Assert.Equal(0m, Fact(evidence, "classifiedEvents").NumberValue);
        Assert.Null(Fact(evidence, "event.0.category").TextValue);
        Assert.Equal(observation.OriginalXml, Fact(evidence, "event.0.xml").TextValue);
    }

    [Theory]
    [InlineData("2026-09-27T12:00:00Z", "System")]
    [InlineData("2026-10-29T12:00:00Z", "System")]
    [InlineData("2026-10-28T12:00:00", "System")]
    [InlineData("2026-10-28T12:00:00Z", "Application")]
    public async Task UnknownOrForeignTimeChannelCannotContributeToCounters(string stamp, string channel)
    {
        var raw = SystemEventFixture.Xml(systemTime: stamp, channel: channel);
        var evidence = await Collect([new("Microsoft-Windows-Kernel-Power", 41, SystemEventFixture.Now, raw)]);
        Assert.Equal(0m, Fact(evidence, RestartHistoryCollector.KernelPower41Observed).NumberValue);
        Assert.Equal("false", Fact(evidence, "event.0.metadataValid").TextValue);
        Assert.Equal(raw, Fact(evidence, "event.0.xml").TextValue);
    }

    [Fact]
    public async Task DivergentAndMalformedXmlKeepOriginalWithoutCountingTheMetadataClaim()
    {
        string malformed = "<broken>";
        string divergent = SystemEventFixture.Xml("Other", 41);
        var evidence = await Collect([new("Microsoft-Windows-Kernel-Power", 41, null, malformed), new("Microsoft-Windows-Kernel-Power", 41, null, divergent)]);
        Assert.Equal(EvidenceStatus.Partial, evidence.Status);
        Assert.Equal(0m, Fact(evidence, RestartHistoryCollector.KernelPower41Observed).NumberValue);
        Assert.Equal(malformed, Fact(evidence, "event.0.xml").TextValue);
        Assert.Equal(divergent, Fact(evidence, "event.1.xml").TextValue);
    }

    [Theory]
    [InlineData(512, false)]
    [InlineData(513, true)]
    public async Task PresenceOnlyProbeDoesNotInterpretOrCountTheExtraRecord(int available, bool limited)
    {
        var records = Enumerable.Range(0, available).Select(index => index == 512
            ? new SystemEventObservation(null, 0, null, "malformed probe must not be parsed")
            : SystemEventFixture.Observation(recordId: (ulong)index + 1));
        var evidence = await Collect(records);
        Assert.Equal(512m, Fact(evidence, "examinedEvents").NumberValue);
        Assert.Equal(512m, Fact(evidence, RestartHistoryCollector.KernelPower41Observed).NumberValue);
        Assert.Equal(limited, evidence.Coverage.LimitReached);
        Assert.Equal(limited ? EvidenceStatus.Partial : EvidenceStatus.Complete, evidence.Status);
        Assert.DoesNotContain(evidence.Facts, fact => fact.Key.StartsWith("event.512.", StringComparison.Ordinal));
        Assert.DoesNotContain(evidence.Issues, issue => issue.Message.Contains("XML de evento inválido", StringComparison.Ordinal));
    }

    [Fact]
    public async Task XmlBudgetStopsBeforeRetainingOrCountingTheNextRecord()
    {
        var records = Enumerable.Range(0, 100).Select(index => SystemEventFixture.Observation(recordId: (ulong)index + 1, extra: "<!--" + new string('x', 30000) + "-->"));
        var evidence = await Collect(records);
        Assert.Equal(EvidenceStatus.Partial, evidence.Status);
        Assert.True(evidence.Coverage.LimitReached);
        Assert.InRange(Fact(evidence, "retainedXmlBytes").NumberValue!.Value, 1m, RestartHistoryCollector.MaximumRetainedXmlBytes);
        Assert.InRange(Fact(evidence, "retainedEvents").NumberValue!.Value, 1m, 99m);
        Assert.Equal(Fact(evidence, "retainedEvents").NumberValue, Fact(evidence, RestartHistoryCollector.KernelPower41Observed).NumberValue);
    }

    [Fact]
    public async Task DuplicateRecordIdentityIsRetainedWithoutDoubleCounting()
    {
        var record = SystemEventFixture.Observation();
        var evidence = await Collect([record, record]);
        Assert.Equal(EvidenceStatus.Partial, evidence.Status);
        Assert.Equal(2m, Fact(evidence, "retainedEvents").NumberValue);
        Assert.Equal(1m, Fact(evidence, RestartHistoryCollector.KernelPower41Observed).NumberValue);
        Assert.Equal(record.OriginalXml, Fact(evidence, "event.1.xml").TextValue);
    }

    [Fact]
    public async Task UniqueTemporalCandidatesReferenceOriginalSourcePositionsAndIdsWithoutMergingCounters()
    {
        var power = Timed("Microsoft-Windows-Kernel-Power", 41, 100, SystemEventFixture.Now.AddMinutes(-2));
        var shutdown = Timed("EventLog", 6008, 101, SystemEventFixture.Now.AddMinutes(-1));
        var evidence = await Collect([shutdown, power]);
        string prefix = RestartHistoryCollector.TemporalCorrelationPrefix + ".pair.0";
        Assert.Equal(1m, Fact(evidence, RestartHistoryCollector.TemporalCorrelationPrefix + ".pairsPresented").NumberValue);
        Assert.Equal(1m, Fact(evidence, prefix + ".kernelPowerSourceIndex").NumberValue);
        Assert.Equal(0m, Fact(evidence, prefix + ".unexpectedShutdownSourceIndex").NumberValue);
        Assert.Equal(100m, Fact(evidence, prefix + ".kernelPowerRecordId").NumberValue);
        Assert.Equal(101m, Fact(evidence, prefix + ".unexpectedShutdownRecordId").NumberValue);
        Assert.Contains(RestartHistoryCollector.TemporalCorrelationPolicy, Fact(evidence, prefix + ".kernelPowerSourceIndex").Label);
        Assert.Equal(shutdown.OriginalXml, Fact(evidence, "event.0.xml").TextValue);
        Assert.Equal(power.OriginalXml, Fact(evidence, "event.1.xml").TextValue);
        Assert.Equal(1m, Fact(evidence, RestartHistoryCollector.KernelPower41Observed).NumberValue);
        Assert.Equal(1m, Fact(evidence, RestartHistoryCollector.UnexpectedShutdown6008Observed).NumberValue);
        Assert.Equal("false", Fact(evidence, "physicalRestartCountKnown").TextValue);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    public async Task TemporalWindowIsInclusiveAtExactlyOneHundredTwentySeconds(long extraTicks, int pairs)
    {
        var power = Timed("Microsoft-Windows-Kernel-Power", 41, 100, SystemEventFixture.Now.AddSeconds(-120).AddTicks(-extraTicks));
        var shutdown = Timed("EventLog", 6008, 101, SystemEventFixture.Now);
        var evidence = await Collect([power, shutdown]);
        Assert.Equal((decimal)pairs, Fact(evidence, RestartHistoryCollector.TemporalCorrelationPrefix + ".pairsPresented").NumberValue);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AmbiguityOnEitherSidePreventsPairing(bool twoPowerRecords)
    {
        var power = Timed("Microsoft-Windows-Kernel-Power", 41, 1, SystemEventFixture.Now.AddMinutes(-1));
        var shutdown = Timed("EventLog", 6008, 2, SystemEventFixture.Now);
        var extra = twoPowerRecords ? Timed("Microsoft-Windows-Kernel-Power", 41, 3, SystemEventFixture.Now.AddSeconds(-30))
            : Timed("EventLog", 6008, 3, SystemEventFixture.Now.AddSeconds(-30));
        var evidence = await Collect([power, shutdown, extra]);
        Assert.Equal(0m, Fact(evidence, RestartHistoryCollector.TemporalCorrelationPrefix + ".pairsPresented").NumberValue);
        Assert.Equal(twoPowerRecords ? 2m : 1m, Fact(evidence, RestartHistoryCollector.KernelPower41Observed).NumberValue);
        Assert.DoesNotContain(evidence.Facts, fact => fact.Key.StartsWith(RestartHistoryCollector.TemporalCorrelationPrefix + ".pair.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DuplicateIdentityDoesNotBecomeAnotherCandidateEvenWhenItsTimestampChanges()
    {
        var power = Timed("Microsoft-Windows-Kernel-Power", 41, 1, SystemEventFixture.Now.AddMinutes(-1));
        var duplicate = Timed("Microsoft-Windows-Kernel-Power", 41, 1, SystemEventFixture.Now.AddSeconds(-30));
        var shutdown = Timed("EventLog", 6008, 2, SystemEventFixture.Now);
        var evidence = await Collect([power, duplicate, shutdown]);
        Assert.Equal(EvidenceStatus.Partial, evidence.Status);
        Assert.Equal(1m, Fact(evidence, RestartHistoryCollector.KernelPower41Observed).NumberValue);
        Assert.Equal(1m, Fact(evidence, RestartHistoryCollector.TemporalCorrelationPrefix + ".pairsPresented").NumberValue);
        Assert.Equal(duplicate.OriginalXml, Fact(evidence, "event.1.xml").TextValue);
        Assert.Contains(evidence.Issues, issue => issue.Code == IssueCode.ReadFailed && issue.Message.Contains("repetidos", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NativeIdWithoutTheSameProviderAndEventIdIsNotTheDuplicateIdentity()
    {
        var power = Timed("Microsoft-Windows-Kernel-Power", 41, 42, SystemEventFixture.Now.AddMinutes(-1));
        var shutdown = Timed("EventLog", 6008, 42, SystemEventFixture.Now);
        var evidence = await Collect([power, shutdown]);
        Assert.Equal(EvidenceStatus.Complete, evidence.Status);
        Assert.Equal(1m, Fact(evidence, RestartHistoryCollector.KernelPower41Observed).NumberValue);
        Assert.Equal(1m, Fact(evidence, RestartHistoryCollector.UnexpectedShutdown6008Observed).NumberValue);
    }

    [Fact]
    public async Task MissingRecordIdDoesNotDeduplicateByTimeOrXmlAndAmbiguityRemainsUnpaired()
    {
        var power = Timed("Microsoft-Windows-Kernel-Power", 41, 1, SystemEventFixture.Now.AddMinutes(-1));
        power = power with { OriginalXml = power.OriginalXml.Replace("<EventRecordID>1</EventRecordID>", "", StringComparison.Ordinal) };
        var shutdown = Timed("EventLog", 6008, 2, SystemEventFixture.Now);
        var evidence = await Collect([power, power, shutdown]);
        Assert.Equal(EvidenceStatus.Complete, evidence.Status);
        Assert.Equal(2m, Fact(evidence, RestartHistoryCollector.KernelPower41Observed).NumberValue);
        Assert.Null(Fact(evidence, "event.0.recordId").NumberValue);
        Assert.Equal(0m, Fact(evidence, RestartHistoryCollector.TemporalCorrelationPrefix + ".pairsPresented").NumberValue);
    }

    [Fact]
    public async Task InvalidOrUnretainedObservationsCannotContributeToTemporalSuggestions()
    {
        var power = Timed("Microsoft-Windows-Kernel-Power", 41, 1, SystemEventFixture.Now.AddMinutes(-1));
        var shutdown = Timed("EventLog", 6008, 2, SystemEventFixture.Now);
        shutdown = shutdown with { OriginalXml = shutdown.OriginalXml.Replace("<Channel>System</Channel>", "<Channel>Application</Channel>", StringComparison.Ordinal) };
        var evidence = await Collect([power, shutdown]);
        Assert.Equal(0m, Fact(evidence, RestartHistoryCollector.TemporalCorrelationPrefix + ".pairsPresented").NumberValue);
        Assert.Equal(shutdown.OriginalXml, Fact(evidence, "event.1.xml").TextValue);
    }

    [Fact]
    public async Task TemporalPresentationHasItsOwnReservedBudgetAndDoesNotRemoveOriginalRecords()
    {
        var records = Enumerable.Range(0, 40).SelectMany(index => new[]
        {
            Timed("Microsoft-Windows-Kernel-Power", 41, (ulong)(index * 2 + 1), SystemEventFixture.Now.AddSeconds(-index * 500 - 60)),
            Timed("EventLog", 6008, (ulong)(index * 2 + 2), SystemEventFixture.Now.AddSeconds(-index * 500))
        });
        var evidence = await Collect(records);
        Assert.Equal(EvidenceStatus.Partial, evidence.Status);
        Assert.True(evidence.Coverage.LimitReached);
        Assert.InRange(Fact(evidence, RestartHistoryCollector.TemporalCorrelationPrefix + ".pairsPresented").NumberValue!.Value, 1m, RestartHistoryCollector.MaximumTemporalPairs);
        Assert.Equal(80m, Fact(evidence, "retainedEvents").NumberValue);
        Assert.Equal(40m, Fact(evidence, RestartHistoryCollector.KernelPower41Observed).NumberValue);
        Assert.Equal(40m, Fact(evidence, RestartHistoryCollector.UnexpectedShutdown6008Observed).NumberValue);
        var inferences = evidence.Facts.Where(fact => fact.Key.StartsWith(RestartHistoryCollector.TemporalCorrelationPrefix, StringComparison.Ordinal)).ToArray();
        Assert.InRange(JsonSerializer.SerializeToUtf8Bytes(inferences, DiagnosticJson.Options).Length, 1, RestartHistoryCollector.MaximumTemporalCorrelationBytes);
        Assert.Contains(evidence.Issues, issue => issue.Code == IssueCode.LimitReached && issue.Message.Contains("Sugestões temporais", StringComparison.Ordinal));
        Assert.NotNull(Fact(evidence, "event.79.xml").TextValue);
    }

    [Fact]
    public async Task LateReadFailureKeepsOriginalEarlierFactsAndPrimaryError()
    {
        static IEnumerable<SystemEventObservation> Source(DateTimeOffset _, DateTimeOffset __, CancellationToken ___)
        { yield return SystemEventFixture.Observation(); throw new IOException("fixture late failure"); }
        var evidence = Assert.Single(await new RestartHistoryCollector(source: Source, clock: new SystemEventFixture.Clock()).CollectAsync(CancellationToken.None));
        Assert.Equal(EvidenceStatus.Partial, evidence.Status);
        Assert.Equal(1m, Fact(evidence, RestartHistoryCollector.KernelPower41Observed).NumberValue);
        Assert.Equal(SystemEventFixture.Observation().OriginalXml, Fact(evidence, "event.0.xml").TextValue);
        Assert.Contains(evidence.Issues, issue => issue.Message.Contains("fixture late failure", StringComparison.Ordinal));
        Assert.Equal("false", Fact(evidence, "sourceReachedEnd").TextValue);
    }

    [Fact]
    public async Task AccessDeniedIsUnknownInsteadOfAnEmptyHealthyCatalog()
    {
        var evidence = Assert.Single(await new RestartHistoryCollector(source: (_, _, _) => throw new UnauthorizedAccessException("fixture denied"), clock: new SystemEventFixture.Clock()).CollectAsync(CancellationToken.None));
        Assert.Equal(EvidenceStatus.Unavailable, evidence.Status);
        Assert.Null(Fact(evidence, RestartHistoryCollector.KernelPower41Observed).NumberValue);
        Assert.Contains(evidence.Issues, issue => issue.Code == IssueCode.PermissionDenied);
    }

    [Fact]
    public async Task FinalPrimaryReadFailureSurvivesAnExhaustedMalformedDetailBudget()
    {
        static IEnumerable<SystemEventObservation> Source(DateTimeOffset _, DateTimeOffset __, CancellationToken ___)
        {
            for (int index = 0; index < 40; index++) yield return new("Microsoft-Windows-Kernel-Power", 41, null, "<broken>");
            throw new IOException("fixture primary failure after malformed records");
        }
        var evidence = Assert.Single(await new RestartHistoryCollector(source: Source, clock: new SystemEventFixture.Clock()).CollectAsync(CancellationToken.None));
        Assert.Equal(EvidenceStatus.Partial, evidence.Status);
        Assert.True(Fact(evidence, "issueDetailsOmitted").NumberValue > 0);
        Assert.Contains(evidence.Issues, issue => issue.Message.Contains("fixture primary failure after malformed records", StringComparison.Ordinal));
        Assert.Null(Fact(evidence, RestartHistoryCollector.KernelPower41Observed).NumberValue);
    }

    [Fact]
    public async Task CancellationBeforeDuringAndAtFinalEnumerationBoundaryPropagates()
    {
        using var before = new CancellationTokenSource(); before.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new RestartHistoryCollector(source: (_, _, _) => throw new InvalidOperationException("must not be called")).CollectAsync(before.Token));
        using var during = new CancellationTokenSource();
        IEnumerable<SystemEventObservation> During(DateTimeOffset _, DateTimeOffset __, CancellationToken ___)
        { yield return SystemEventFixture.Observation(); during.Cancel(); yield return SystemEventFixture.Observation(recordId: 2); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new RestartHistoryCollector(source: During).CollectAsync(during.Token));
        using var final = new CancellationTokenSource();
        IEnumerable<SystemEventObservation> Final(DateTimeOffset _, DateTimeOffset __, CancellationToken ___)
        { yield return SystemEventFixture.Observation(); final.Cancel(); yield break; }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new RestartHistoryCollector(source: Final).CollectAsync(final.Token));
    }

    private static DiagnosticFact Fact(DiagnosticEvidence evidence, string key) => evidence.Facts.Single(item => item.Key == key);
    private static SystemEventObservation Timed(string provider, int id, ulong recordId, DateTimeOffset stamp) =>
        new(provider, id, null, SystemEventFixture.Xml(provider, id, recordId.ToString(CultureInfo.InvariantCulture), stamp.ToString("O", CultureInfo.InvariantCulture)));
    private static async Task<DiagnosticEvidence> Collect(IEnumerable<SystemEventObservation> records) => Assert.Single(await new RestartHistoryCollector(source: (_, _, _) => records, clock: new SystemEventFixture.Clock()).CollectAsync(CancellationToken.None));
}

internal static class SystemEventFixture
{
    internal static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-28T12:00:00Z", CultureInfo.InvariantCulture);
    internal static SystemEventObservation Observation(string provider = "Microsoft-Windows-Kernel-Power", int id = 41, ulong recordId = 42, int level = 2, string data = "", string extra = "") =>
        new(provider, id, null, Xml(provider, id, recordId.ToString(CultureInfo.InvariantCulture), level: level, data: data, extra: extra));
    internal static string Xml(string provider = "Microsoft-Windows-Kernel-Power", int id = 41, string recordId = "42", string systemTime = "2026-10-28T12:00:00Z", string channel = "System", int level = 2, string data = "", string extra = "") =>
        $"<Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'><System><Provider Name='{SecurityElement.Escape(provider)}'/><EventID>{id}</EventID><Level>{level}</Level><TimeCreated SystemTime='{systemTime}'/><EventRecordID>{recordId}</EventRecordID><Channel>{channel}</Channel></System><EventData>{data}</EventData>{extra}</Event>";
    internal sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
}
