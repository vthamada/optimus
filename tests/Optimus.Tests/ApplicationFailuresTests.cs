using Optimus.Application;
using System.Globalization;
using System.Security;
using Optimus.Domain;
using Optimus.Infrastructure;
using Optimus.Infrastructure.Events;

namespace Optimus.Tests;

public sealed class ApplicationFailuresTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-28T12:00:00Z", CultureInfo.InvariantCulture);

    [Fact]
    public async Task SelectedProviderPairsRetainOriginalXmlRecordIdentityTimeAndData()
    {
        var original = Xml(data: "<Data Name='AppName'>d'água Ω.exe</Data><Data Name='ExceptionCode'>c0000005</Data>", systemTime: "2026-10-28T09:00:00.0000000-03:00", recordId: ulong.MaxValue.ToString(CultureInfo.InvariantCulture));
        var evidence = await Collect([new("Application Error", 1000, DateTimeOffset.UnixEpoch, original)]);
        Assert.Equal(MaintenanceCollectors.ApplicationFailures, evidence.CollectorId);
        Assert.Equal(EvidenceStatus.Complete, evidence.Status);
        Assert.Equal(1m, Fact(evidence, "retainedEvents").NumberValue);
        Assert.Equal("Application Error", Fact(evidence, "event.0.provider").TextValue);
        Assert.Equal(1000m, Fact(evidence, "event.0.id").NumberValue);
        Assert.Equal((decimal)ulong.MaxValue, Fact(evidence, "event.0.recordId").NumberValue);
        Assert.Equal("2026-10-28T09:00:00.0000000-03:00", Fact(evidence, "event.0.time").TextValue);
        Assert.Equal("2026-10-28T12:00:00.0000000+00:00", Fact(evidence, "event.0.timeParsedUtc").TextValue);
        Assert.Equal(original, Fact(evidence, "event.0.xml").TextValue);
        Assert.True(Fact(evidence, "event.0.xml").Detailed);
        Assert.Equal("AppName", Fact(evidence, "event.0.data.0.name").TextValue);
        Assert.Equal("d'água Ω.exe", Fact(evidence, "event.0.data.0.value").TextValue);
        Assert.False(Fact(evidence, "event.0.data.0.value").Detailed);
        Assert.Equal("false", Fact(evidence, "causalDiagnosisMade").TextValue);
        Assert.Equal("false", Fact(evidence, "localizedMessagesParsed").TextValue);
        Assert.True(SessionValidation.IsValid(new(Guid.NewGuid(), Now, Now, SessionStatus.Complete, [evidence], [])));
    }

    [Theory]
    [InlineData("Application Error", 1000)]
    [InlineData("Windows Error Reporting", 1001)]
    [InlineData("Application Hang", 1002)]
    public async Task EachAdvertisedPairIsSelectedWithoutInferringCause(string provider, int id)
    {
        var evidence = await Collect([Observation(provider, id)]);
        Assert.Equal(EvidenceStatus.Complete, evidence.Status);
        Assert.Equal(provider + " / " + id, Fact(evidence, "event.0.selection").TextValue);
        Assert.DoesNotContain(evidence.Facts, fact => fact.Key.Contains("rootCause", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task TheRequestedWindowIsExactlySevenDaysAndEmptyCoverageIsNotHealth()
    {
        DateTimeOffset? supplied = null;
        var evidence = Assert.Single(await new ApplicationFailuresCollector((cutoff, _) => { supplied = cutoff; return []; }, new Clock()).CollectAsync(CancellationToken.None));
        Assert.Equal(Now.AddDays(-7), supplied);
        Assert.Equal(EvidenceStatus.Complete, evidence.Status);
        Assert.Equal(0m, Fact(evidence, "examinedEvents").NumberValue);
        Assert.Equal(0m, Fact(evidence, "retainedEvents").NumberValue);
        Assert.Contains("ausência de eventos não comprova ausência de falhas", evidence.Coverage.Scope);
        Assert.Empty(evidence.Issues);
    }

    [Theory]
    [InlineData(20, false)]
    [InlineData(21, true)]
    public async Task PresenceProbeDeterminesCompletenessIndependentlyOfRetainedCount(int available, bool limited)
    {
        var records = Enumerable.Range(0, available).Select(index => Observation(recordId: (index + 1).ToString(CultureInfo.InvariantCulture))).ToArray();
        var evidence = await Collect(records);
        Assert.Equal(20m, Fact(evidence, "examinedEvents").NumberValue);
        Assert.Equal(20m, Fact(evidence, "retainedEvents").NumberValue);
        Assert.Equal(limited, evidence.Coverage.LimitReached);
        Assert.Equal(limited ? EvidenceStatus.Partial : EvidenceStatus.Complete, evidence.Status);
    }

    [Fact]
    public async Task SourceFailureAfterARecordKeepsEarlierFactsAndTheOriginalXml()
    {
        static IEnumerable<SystemEventObservation> Source(DateTimeOffset _, CancellationToken __)
        {
            yield return Observation();
            throw new IOException("fixture late source failure");
        }
        var evidence = Assert.Single(await new ApplicationFailuresCollector(Source, new Clock()).CollectAsync(CancellationToken.None));
        Assert.Equal(EvidenceStatus.Partial, evidence.Status);
        Assert.Equal(1m, Fact(evidence, "retainedEvents").NumberValue);
        Assert.Equal(Observation().OriginalXml, Fact(evidence, "event.0.xml").TextValue);
        Assert.Contains(evidence.Issues, item => item.Message.Contains("fixture late source failure"));
    }

    [Fact]
    public async Task PermissionFailureDoesNotBecomeAnEmptySuccessfulLog()
    {
        var evidence = Assert.Single(await new ApplicationFailuresCollector((_, _) => throw new UnauthorizedAccessException("fixture denied"), new Clock()).CollectAsync(CancellationToken.None));
        Assert.Equal(EvidenceStatus.Unavailable, evidence.Status);
        Assert.Equal(0m, Fact(evidence, "retainedEvents").NumberValue);
        Assert.Contains(evidence.Issues, issue => issue.Code == IssueCode.PermissionDenied);
    }

    [Theory]
    [InlineData("Other Provider", 1000)]
    [InlineData("Application Error", 1001)]
    [InlineData("Microsoft-Windows-WER-SystemErrorReporting", 1001)]
    public async Task AnEventIdAloneCannotSelectOrClassifyAnUnadvertisedProviderPair(string provider, int id)
    {
        var evidence = await Collect([Observation(provider, id)]);
        Assert.Equal(EvidenceStatus.Unavailable, evidence.Status);
        Assert.Equal(1m, Fact(evidence, "rejectedEvents").NumberValue);
        Assert.Equal(0m, Fact(evidence, "retainedEvents").NumberValue);
        Assert.DoesNotContain(evidence.Facts, fact => fact.Key == "event.0.selection");
        Assert.Equal(Observation(provider, id).OriginalXml, Fact(evidence, "rejected.0.xml").TextValue);
    }

    [Theory]
    [InlineData("2026-10-20T12:00:00Z", "Application")]
    [InlineData("2026-10-29T12:00:00Z", "Application")]
    [InlineData("2026-10-28T12:00:00Z", "System")]
    public async Task KnownTimestampOrChannelOutsideTheSelectionIsRejectedWithoutHidingItsXml(string instant, string channel)
    {
        var raw = Xml(systemTime: instant, channel: channel);
        var evidence = await Collect([new("Application Error", 1000, null, raw)]);
        Assert.Equal(0m, Fact(evidence, "retainedEvents").NumberValue);
        Assert.Equal(1m, Fact(evidence, "rejectedEvents").NumberValue);
        Assert.Equal(raw, Fact(evidence, "rejected.0.xml").TextValue);
    }

    [Fact]
    public async Task RepeatedAndUnnamedDataFieldsKeepTheirPositionsAndSourceNames()
    {
        var raw = Xml(data: "<Data Name='P1'>first</Data><Data Name='P1'>second</Data><Data> unnamed </Data>");
        var evidence = await Collect([new("Application Error", 1000, null, raw)]);
        Assert.Equal(EvidenceStatus.Complete, evidence.Status);
        Assert.Equal("P1", Fact(evidence, "event.0.data.0.name").TextValue);
        Assert.Equal("P1", Fact(evidence, "event.0.data.1.name").TextValue);
        Assert.Equal("first", Fact(evidence, "event.0.data.0.value").TextValue);
        Assert.Equal("second", Fact(evidence, "event.0.data.1.value").TextValue);
        Assert.Null(Fact(evidence, "event.0.data.2.name").TextValue);
        Assert.Equal(" unnamed ", Fact(evidence, "event.0.data.2.value").TextValue);
    }

    [Fact]
    public async Task MissingTimezoneDoesNotUseTheInjectedTimestampOrAssumeLocalTime()
    {
        var raw = Xml(systemTime: "2026-10-28T12:00:00");
        var evidence = await Collect([new("Application Error", 1000, Now, raw)]);
        Assert.Equal(EvidenceStatus.Partial, evidence.Status);
        Assert.Equal("2026-10-28T12:00:00", Fact(evidence, "event.0.time").TextValue);
        Assert.Null(Fact(evidence, "event.0.timeParsedUtc").TextValue);
        Assert.Contains(evidence.Issues, issue => issue.Message.Contains("nenhum horário local"));
    }

    [Theory]
    [InlineData("<Event><broken>")]
    [InlineData("<!DOCTYPE Event [<!ENTITY xxe SYSTEM 'file:///C:/not-read-by-this-test'>]><Event>&xxe;</Event>")]
    public async Task MalformedOrDtdXmlRemainsRawEvidenceWithoutParsedDataOrInferredTime(string raw)
    {
        var evidence = await Collect([new("Application Error", 1000, Now, raw)]);
        Assert.Equal(EvidenceStatus.Partial, evidence.Status);
        Assert.Equal(raw, Fact(evidence, "event.0.xml").TextValue);
        Assert.Null(Fact(evidence, "event.0.timeParsedUtc").TextValue);
        Assert.DoesNotContain(evidence.Facts, fact => fact.Key.StartsWith("event.0.data.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DuplicateSystemTimestampIsAmbiguousAndCannotBecomeARecognizedInstant()
    {
        var raw = Xml().Replace("</System>", "<TimeCreated SystemTime='2026-10-28T12:00:00Z'/></System>", StringComparison.Ordinal);
        var evidence = await Collect([new("Application Error", 1000, Now, raw)]);
        Assert.Equal(EvidenceStatus.Partial, evidence.Status);
        Assert.Null(Fact(evidence, "event.0.timeParsedUtc").TextValue);
        Assert.Equal(raw, Fact(evidence, "event.0.xml").TextValue);
    }

    [Fact]
    public async Task OversizedXmlIsNotTruncatedOrPassedToTheParser()
    {
        var raw = new string('Ω', WindowsEventReader.MaximumXmlBytes / 2 + 1);
        var evidence = await Collect([new("Application Error", 1000, Now, raw)]);
        Assert.Equal(EvidenceStatus.Partial, evidence.Status);
        Assert.Null(Fact(evidence, "event.0.xml").TextValue);
        Assert.Equal(32770m, Fact(evidence, "event.0.sourceXmlUtf8Bytes").NumberValue);
        Assert.True(evidence.Coverage.LimitReached);
        Assert.Null(Fact(evidence, "event.0.timeParsedUtc").TextValue);
    }

    [Fact]
    public async Task LargeDataFieldIsUnknownAndNestedDataIsNotFlattened()
    {
        var raw = Xml(data: "<Data Name='large'>" + new string('x', 2049) + "</Data><Data Name='structured'><Inner>value</Inner></Data>");
        var evidence = await Collect([new("Application Error", 1000, null, raw)]);
        Assert.Equal(EvidenceStatus.Partial, evidence.Status);
        Assert.Null(Fact(evidence, "event.0.data.0.value").TextValue);
        Assert.Null(Fact(evidence, "event.0.data.1.value").TextValue);
        Assert.Equal(raw, Fact(evidence, "event.0.xml").TextValue);
        Assert.Contains(evidence.Issues, issue => issue.Code == IssueCode.NotSupported);
    }

    [Fact]
    public async Task DataFieldLimitKeepsTheRemainingValuesInOriginalXml()
    {
        var raw = Xml(data: string.Concat(Enumerable.Range(0, 33).Select(index => $"<Data Name='field-{index}'>value-{index}</Data>")));
        var evidence = await Collect([new("Application Error", 1000, null, raw)]);
        Assert.Equal(EvidenceStatus.Partial, evidence.Status);
        Assert.Equal(32m, Fact(evidence, "event.0.eventDataFieldsRetained").NumberValue);
        Assert.Equal(raw, Fact(evidence, "event.0.xml").TextValue);
        Assert.DoesNotContain(evidence.Facts, fact => fact.Key == "event.0.data.32.value");
    }

    [Fact]
    public async Task TotalRetentionBudgetPreservesEarlierEventsAndDeclaresRemainderUnknown()
    {
        string value = string.Concat(Enumerable.Repeat("&amp;&lt;&gt;&quot;", 100));
        string data = string.Concat(Enumerable.Range(0, 10).Select(index => $"<Data Name='P{index}'>{value}</Data>"));
        var records = Enumerable.Range(0, 20).Select(index => new SystemEventObservation("Application Error", 1000, null, Xml(data: data, recordId: index.ToString(CultureInfo.InvariantCulture)))).ToArray();
        var evidence = await Collect(records);
        Assert.Equal(EvidenceStatus.Partial, evidence.Status);
        Assert.True(evidence.Coverage.LimitReached);
        Assert.InRange(Fact(evidence, "retainedEvents").NumberValue!.Value, 1m, 19m);
        Assert.NotNull(Fact(evidence, "event.0.xml").TextValue);
    }

    [Fact]
    public void NativeXmlInterpretationHasOneOwnerAndCacheDoesNotAcceptAChangedTimestamp()
    {
        var initial = WindowsEventReader.Interpret(Observation());
        var again = WindowsEventReader.Interpret(initial);
        Assert.Same(initial.Xml, again.Xml);
        var changed = WindowsEventReader.Interpret(initial with { OccurredAt = Now.AddYears(-20) });
        Assert.Equal(Now, changed.OccurredAt);
        Assert.Same(initial.Xml, changed.Xml);
    }

    [Fact]
    public async Task CancellationPropagatesBeforeAndDuringSourceEnumeration()
    {
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ApplicationFailuresCollector((_, _) => throw new InvalidOperationException("Not called"), new Clock()).CollectAsync(cancelled.Token));
        using var during = new CancellationTokenSource();
        IEnumerable<SystemEventObservation> Source(DateTimeOffset _, CancellationToken __)
        { yield return Observation(); during.Cancel(); yield return Observation(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ApplicationFailuresCollector(Source, new Clock()).CollectAsync(during.Token));
    }

    private static SystemEventObservation Observation(string provider = "Application Error", int id = 1000, string recordId = "42") => new(provider, id, null, Xml(provider, id, recordId: recordId));
    private static string Xml(string provider = "Application Error", int id = 1000, string systemTime = "2026-10-28T12:00:00Z", string recordId = "42", string channel = "Application", string data = "") =>
        $"<Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'><System><Provider Name='{SecurityElement.Escape(provider)}'/><EventID>{id}</EventID><TimeCreated SystemTime='{systemTime}'/><EventRecordID>{recordId}</EventRecordID><Channel>{channel}</Channel></System><EventData>{data}</EventData></Event>";
    private static DiagnosticFact Fact(DiagnosticEvidence evidence, string key) => evidence.Facts.Single(item => item.Key == key);
    private static async Task<DiagnosticEvidence> Collect(IReadOnlyList<SystemEventObservation> observations) => Assert.Single(await new ApplicationFailuresCollector((_, _) => observations, new Clock()).CollectAsync(CancellationToken.None));
    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
}
