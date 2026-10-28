using Optimus.Domain;
using Optimus.Infrastructure;
using Optimus.Infrastructure.Events;

namespace Optimus.Tests;

public sealed class EventCoverageTests
{
    [Fact]
    public async Task SharedProducerKeepsSystemKeysAndUsesOnlyAnExplicitXmlTimestamp()
    {
        const string xml = "<Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'><System><Provider Name='Fixture.Provider'/><EventID>42</EventID><TimeCreated SystemTime='2026-10-28T09:00:00.0000000-03:00'/><Channel>System</Channel></System></Event>";
        var observed = WindowsEventReader.Interpret(new("Fixture.Provider", 42, DateTimeOffset.UnixEpoch, xml));
        var evidence = Assert.Single(await new SystemEventsCollector((_, _) => [observed]).CollectAsync(CancellationToken.None));
        Assert.Equal(EvidenceStatus.Complete, evidence.Status);
        Assert.Equal(10, evidence.Coverage.ItemLimit);
        Assert.Equal("Fixture.Provider", evidence.Facts.Single(item => item.Key == "event.0.provider").TextValue);
        Assert.Equal(42m, evidence.Facts.Single(item => item.Key == "event.0.id").NumberValue);
        Assert.Equal("2026-10-28T12:00:00.0000000+00:00", evidence.Facts.Single(item => item.Key == "event.0.time").TextValue);
        Assert.Equal(xml, evidence.Facts.Single(item => item.Key == "event.0.xml").TextValue);
    }

    [Fact]
    public async Task SharedXmlTimestampWithoutTimezoneStaysUnknownInsteadOfUsingLocalTime()
    {
        const string xml = "<Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'><System><Provider Name='Fixture.Provider'/><EventID>42</EventID><TimeCreated SystemTime='2026-10-28T09:00:00'/><Channel>System</Channel></System></Event>";
        var observed = WindowsEventReader.Interpret(new("Fixture.Provider", 42, DateTimeOffset.UnixEpoch, xml));
        var evidence = Assert.Single(await new SystemEventsCollector((_, _) => [observed]).CollectAsync(CancellationToken.None));
        Assert.Equal(EvidenceStatus.Partial, evidence.Status);
        Assert.Null(evidence.Facts.Single(item => item.Key == "event.0.time").TextValue);
        Assert.Equal(xml, evidence.Facts.Single(item => item.Key == "event.0.xml").TextValue);
    }

    [Fact]
    public async Task SharedOversizedXmlRetainsMetadataAndKnownSizeWithoutPublishingATruncatedDocument()
    {
        string xml = new('Ω', WindowsEventReader.MaximumXmlBytes / 2 + 1);
        var observed = WindowsEventReader.Interpret(new("Fixture.Provider", 42, DateTimeOffset.UnixEpoch, xml));
        var evidence = Assert.Single(await new SystemEventsCollector((_, _) => [observed]).CollectAsync(CancellationToken.None));
        Assert.Equal(EvidenceStatus.Partial, evidence.Status);
        Assert.Equal(42m, evidence.Facts.Single(item => item.Key == "event.0.id").NumberValue);
        Assert.Null(evidence.Facts.Single(item => item.Key == "event.0.xml").TextValue);
        Assert.Null(evidence.Facts.Single(item => item.Key == "event.0.time").TextValue);
        Assert.Equal(32770m, evidence.Facts.Single(item => item.Key == "event.0.sourceXmlUtf8Bytes").NumberValue);
        Assert.True(evidence.Coverage.LimitReached);
        Assert.DoesNotContain(evidence.Issues, issue => issue.Message.Contains("Existem mais eventos"));
    }

    [Fact]
    public async Task ReadTimeoutIsNotAnEmptyCompleteResultAndThreeDayWindowRemainsSelected()
    {
        DateTimeOffset? supplied = null;
        var before = DateTimeOffset.UtcNow.AddDays(-3);
        var evidence = Assert.Single(await new SystemEventsCollector((cutoff, _) => { supplied = cutoff; throw new TimeoutException("fixture read timeout"); }).CollectAsync(CancellationToken.None));
        var after = DateTimeOffset.UtcNow.AddDays(-3);
        Assert.InRange(supplied!.Value, before, after);
        Assert.Equal(EvidenceStatus.Unavailable, evidence.Status);
        Assert.Contains("fixture read timeout", Assert.Single(evidence.Issues).Message);
    }

    [Fact]
    public async Task LateSourceFailureRetainsAlreadyObservedEvents()
    {
        static IEnumerable<SystemEventObservation> Events(DateTimeOffset _, CancellationToken __)
        {
            yield return new("Fixture.Provider", 42, DateTimeOffset.UnixEpoch, "<Event>preserve source</Event>");
            throw new IOException("Fixture interrupted after one complete event");
        }
        var collector = new SystemEventsCollector(Events);

        var result = Assert.Single(await collector.CollectAsync(CancellationToken.None));

        Assert.Equal(EvidenceStatus.Partial, result.Status);
        Assert.Equal(1, result.Coverage.ExaminedItems);
        Assert.Equal("<Event>preserve source</Event>", result.Facts.Single(fact => fact.Key == "event.0.xml").TextValue);
        Assert.Contains("after one complete event", Assert.Single(result.Issues).Message);
    }

    [Theory]
    [InlineData(10, false, EvidenceStatus.Complete)]
    [InlineData(11, true, EvidenceStatus.Partial)]
    public async Task CompletenessDependsOnExaminedCoverageAndNotOnlyOutputCount(int available, bool limited, EvidenceStatus status)
    {
        var collector = new SystemEventsCollector((_, _) => Enumerable.Range(0, available)
            .Select(index => new SystemEventObservation("Fixture", index, null, $"<Event>{index}</Event>")));

        var result = Assert.Single(await collector.CollectAsync(CancellationToken.None));

        Assert.Equal(10, result.Coverage.ExaminedItems);
        Assert.Equal(limited, result.Coverage.LimitReached);
        Assert.Equal(status, result.Status);
    }

    [Fact]
    public async Task SourceFailureBeforeAnyRecordDoesNotBecomeAnEmptyHealthyLog()
    {
        var collector = new SystemEventsCollector((_, _) => throw new UnauthorizedAccessException("Fixture denied"));

        var result = Assert.Single(await collector.CollectAsync(CancellationToken.None));

        Assert.Equal(EvidenceStatus.Unavailable, result.Status);
        Assert.Equal(0, result.Coverage.ExaminedItems);
        Assert.Single(result.Issues);
    }
}
