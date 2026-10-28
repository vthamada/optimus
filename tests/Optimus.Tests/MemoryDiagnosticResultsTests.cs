using Optimus.Domain;
using Optimus.Infrastructure;
using Optimus.Infrastructure.Events;

namespace Optimus.Tests;

public sealed class MemoryDiagnosticResultsTests
{
    [Theory]
    [InlineData(1201, "Fail", 1, 0)]
    [InlineData(1202, "Pass", 0, 1)]
    [InlineData(1101, "Fail", 1, 0)]
    [InlineData(2001, "Pass", 0, 1)]
    public async Task ExplicitCompletionTypeSelectsOutcomeIndependentlyOfId(int id, string value, int fail, int pass)
    {
        var observation = Observation(id: id, data: $"<Data Name='CompletionType'>{value}</Data>");
        var evidence = await Collect([observation]);
        Assert.Equal("windows.memory-diagnostic-results", evidence.CollectorId);
        Assert.Equal(EvidenceStatus.Complete, evidence.Status);
        Assert.Equal((decimal)fail, Fact(evidence, MemoryDiagnosticResultsCollector.ReportedFailEvents).NumberValue);
        Assert.Equal((decimal)pass, Fact(evidence, MemoryDiagnosticResultsCollector.ReportedPassEvents).NumberValue);
        Assert.Equal(observation.OriginalXml, Fact(evidence, "event.0.xml").TextValue);
        Assert.Equal(value, Fact(evidence, "event.0.reportedCompletion").TextValue);
        Assert.True(SessionValidation.IsValid(new(Guid.NewGuid(), SystemEventFixture.Now, SystemEventFixture.Now, SessionStatus.Complete, [evidence], [])));
    }

    [Theory]
    [InlineData("Pass")]
    [InlineData("Fail")]
    public async Task ExplicitResultsNamespaceIsInterpretedOnceByTheReaderWithSourcePath(string value)
    {
        var raw = SystemEventFixture.Xml(MemoryDiagnosticResultsCollector.ProviderName, 1201, extra:
            $"<UserData><Results xmlns='{WindowsEventReader.MemoryResultsNamespace}'><CompletionType>{value}</CompletionType><NumBadPages>17</NumBadPages></Results></UserData>");
        var evidence = await Collect([new(MemoryDiagnosticResultsCollector.ProviderName, 1201, null, raw)]);
        Assert.Equal(value, Fact(evidence, "event.0.reportedCompletion").TextValue);
        Assert.Equal(WindowsEventReader.MemoryResultsPath + "/{" + WindowsEventReader.MemoryResultsNamespace + "}CompletionType", Fact(evidence, "event.0.data.0.sourcePath").TextValue);
        Assert.Equal("17", Fact(evidence, "event.0.data.1.value").TextValue);
        Assert.Equal(raw, Fact(evidence, "event.0.xml").TextValue);
    }

    [Theory]
    [InlineData(1201, "")]
    [InlineData(1202, "<Data Name='NumBadPages'>999</Data>")]
    [InlineData(1201, "<Data Name='CompletionType'>pass</Data>")]
    [InlineData(1201, "<Data Name='CompletionType'> Pass </Data>")]
    [InlineData(1201, "<Data Name='CompletionType'>0</Data>")]
    [InlineData(1201, "<Data Name='CompletionType'>Fail</Data><Data Name='CompletionType'>Pass</Data>")]
    [InlineData(1201, "<Data Name='CompletionType'>Fail</Data><Data Name='CompletionType'>Fail</Data>")]
    public async Task MissingUnknownOrDuplicateOutcomeCannotBeInferredFromIdOrCounters(int id, string data)
    {
        var evidence = await Collect([Observation(id: id, data: data)]);
        Assert.Equal(0m, Fact(evidence, MemoryDiagnosticResultsCollector.ReportedFailEvents).NumberValue);
        Assert.Equal(0m, Fact(evidence, MemoryDiagnosticResultsCollector.ReportedPassEvents).NumberValue);
        Assert.Equal(1m, Fact(evidence, MemoryDiagnosticResultsCollector.UnknownResultEvents).NumberValue);
        Assert.Equal("unknown", Fact(evidence, "event.0.reportedCompletion").TextValue);
    }

    [Theory]
    [InlineData("<UserData><Results xmlns='urn:foreign'><CompletionType>Fail</CompletionType></Results></UserData>")]
    [InlineData("<UserData><Other><CompletionType>Fail</CompletionType></Other></UserData>")]
    public async Task UnknownUserDataFormatKeepsRawXmlAndDoesNotFlattenTextIntoAResult(string userData)
    {
        var raw = SystemEventFixture.Xml(MemoryDiagnosticResultsCollector.ProviderName, 1201, extra: userData);
        var evidence = await Collect([new(MemoryDiagnosticResultsCollector.ProviderName, 1201, null, raw)]);
        Assert.Equal(EvidenceStatus.Partial, evidence.Status);
        Assert.Equal(raw, Fact(evidence, "event.0.xml").TextValue);
        Assert.Equal(1m, Fact(evidence, MemoryDiagnosticResultsCollector.UnknownResultEvents).NumberValue);
        Assert.Contains(evidence.Issues, issue => issue.Code == IssueCode.NotSupported);
    }

    [Fact]
    public async Task IncompleteDataCannotHideAConflictingCompletionAfterTheFieldLimit()
    {
        string data = "<Data Name='CompletionType'>Pass</Data>" + string.Concat(Enumerable.Range(0, 31).Select(index => $"<Data Name='field{index}'>x</Data>")) + "<Data Name='CompletionType'>Fail</Data>";
        var evidence = await Collect([Observation(data: data)]);
        Assert.Equal(EvidenceStatus.Partial, evidence.Status);
        Assert.True(evidence.Coverage.LimitReached);
        Assert.Equal(0m, Fact(evidence, MemoryDiagnosticResultsCollector.ReportedPassEvents).NumberValue);
        Assert.Equal(1m, Fact(evidence, MemoryDiagnosticResultsCollector.UnknownResultEvents).NumberValue);
        Assert.Equal("false", Fact(evidence, "event.0.dataComplete").TextValue);
    }

    [Fact]
    public async Task OversizedXmlAndStructuredScalarsAreUnknownWithoutTruncation()
    {
        string raw = new('x', WindowsEventReader.MaximumXmlBytes + 1);
        var oversized = await Collect([new(MemoryDiagnosticResultsCollector.ProviderName, 1201, null, raw)]);
        Assert.Equal(EvidenceStatus.Partial, oversized.Status);
        Assert.True(oversized.Coverage.LimitReached);
        Assert.Null(Fact(oversized, "event.0.xml").TextValue);
        Assert.Equal(0m, Fact(oversized, MemoryDiagnosticResultsCollector.ReportedPassEvents).NumberValue);
        Assert.Equal("false", Fact(oversized, "event.0.dataComplete").TextValue);
        var scalar = Observation(extra: $"<UserData><Results xmlns='{WindowsEventReader.MemoryResultsNamespace}'><CompletionType>Pass</CompletionType><Extra>{new string('x', 2049)}</Extra></Results></UserData>");
        var partial = await Collect([scalar]);
        Assert.True(partial.Coverage.LimitReached);
        Assert.Equal(1m, Fact(partial, MemoryDiagnosticResultsCollector.UnknownResultEvents).NumberValue);
        Assert.Equal(scalar.OriginalXml, Fact(partial, "event.0.xml").TextValue);
    }

    [Fact]
    public async Task NestedDuplicatedResultsAndCrossContainerOutcomesAreUnknown()
    {
        string ns = WindowsEventReader.MemoryResultsNamespace;
        var variants = new[]
        {
            $"<UserData><Results xmlns='{ns}'><CompletionType><Inner>Pass</Inner></CompletionType></Results></UserData>",
            $"<UserData><Results xmlns='{ns}'><CompletionType>Pass</CompletionType></Results><Results xmlns='{ns}'><CompletionType>Pass</CompletionType></Results></UserData>",
            $"<UserData><Results xmlns='{ns}'><CompletionType>Pass</CompletionType><CompletionType>Pass</CompletionType></Results></UserData>"
        };
        foreach (var variant in variants)
        {
            var raw = SystemEventFixture.Xml(MemoryDiagnosticResultsCollector.ProviderName, 1201, extra: variant);
            var evidence = await Collect([new(MemoryDiagnosticResultsCollector.ProviderName, 1201, null, raw)]);
            Assert.Equal(1m, Fact(evidence, MemoryDiagnosticResultsCollector.UnknownResultEvents).NumberValue);
            Assert.Equal(raw, Fact(evidence, "event.0.xml").TextValue);
        }
        var both = Observation(data: "<Data Name='CompletionType'>Pass</Data>", extra: $"<UserData><Results xmlns='{ns}'><CompletionType>Pass</CompletionType></Results></UserData>");
        Assert.Equal(1m, Fact(await Collect([both]), MemoryDiagnosticResultsCollector.UnknownResultEvents).NumberValue);
    }

    [Theory]
    [InlineData(64, false)]
    [InlineData(65, true)]
    public async Task MemoryHasItsOwnSixtyFourRecordPresenceProbe(int available, bool limited)
    {
        var evidence = await Collect(Enumerable.Range(0, available).Select(index => Observation(recordId: (ulong)index + 1, data: "<Data Name='CompletionType'>Fail</Data>")));
        Assert.Equal(64m, Fact(evidence, "examinedEvents").NumberValue);
        Assert.Equal(64m, Fact(evidence, MemoryDiagnosticResultsCollector.ReportedFailEvents).NumberValue);
        Assert.Equal(limited, evidence.Coverage.LimitReached);
        Assert.Equal(limited ? EvidenceStatus.Partial : EvidenceStatus.Complete, evidence.Status);
    }

    [Fact]
    public async Task MemoryXmlBudgetDoesNotAffectOriginalsAlreadyRetained()
    {
        var evidence = await Collect(Enumerable.Range(0, 64).Select(index => Observation(recordId: (ulong)index + 1,
            data: "<Data Name='CompletionType'>Pass</Data>", extra: "<!--" + new string('x', 30000) + "-->")));
        Assert.Equal(EvidenceStatus.Partial, evidence.Status);
        Assert.True(evidence.Coverage.LimitReached);
        Assert.InRange(Fact(evidence, "retainedEvents").NumberValue!.Value, 1m, 63m);
        Assert.InRange(Fact(evidence, "retainedXmlBytes").NumberValue!.Value, 1m, MemoryDiagnosticResultsCollector.MaximumRetainedXmlBytes);
        Assert.NotNull(Fact(evidence, "event.0.xml").TextValue);
    }

    [Fact]
    public async Task InvalidMetadataAndOtherProviderDoNotProduceReportedPassOrFail()
    {
        var record = Observation(data: "<Data Name='CompletionType'>Fail</Data>");
        var raw = record.OriginalXml.Replace("<Channel>System</Channel>", "<Channel>Application</Channel>", StringComparison.Ordinal);
        var evidence = await Collect([record with { OriginalXml = raw }, SystemEventFixture.Observation("Other", 1201, 43, data: "<Data Name='CompletionType'>Fail</Data>")]);
        Assert.Equal(0m, Fact(evidence, "classifiedEvents").NumberValue);
        Assert.Equal(0m, Fact(evidence, MemoryDiagnosticResultsCollector.ReportedFailEvents).NumberValue);
        Assert.Equal(EvidenceStatus.Partial, evidence.Status);
    }

    [Fact]
    public async Task SourceFailureIsUnknownAndLateFailurePreservesEarlierReportedResult()
    {
        var unavailable = Assert.Single(await new MemoryDiagnosticResultsCollector(source: (_, _, _) => throw new UnauthorizedAccessException("fixture denied"), clock: new SystemEventFixture.Clock()).CollectAsync(CancellationToken.None));
        Assert.Equal(EvidenceStatus.Unavailable, unavailable.Status);
        Assert.Null(Fact(unavailable, MemoryDiagnosticResultsCollector.ReportedFailEvents).NumberValue);
        Assert.Contains(unavailable.Issues, issue => issue.Code == IssueCode.PermissionDenied);
        static IEnumerable<SystemEventObservation> Source(DateTimeOffset _, DateTimeOffset __, CancellationToken ___)
        { yield return Observation(data: "<Data Name='CompletionType'>Fail</Data>"); throw new IOException("fixture late read"); }
        var partial = Assert.Single(await new MemoryDiagnosticResultsCollector(source: Source, clock: new SystemEventFixture.Clock()).CollectAsync(CancellationToken.None));
        Assert.Equal(EvidenceStatus.Partial, partial.Status);
        Assert.Equal(1m, Fact(partial, MemoryDiagnosticResultsCollector.ReportedFailEvents).NumberValue);
        Assert.Contains(partial.Issues, issue => issue.Message.Contains("fixture late read", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EmptyCoverageAndWindowAreExplicitWithoutClaimingSuccessfulMemoryTest()
    {
        DateTimeOffset? from = null, to = null;
        var evidence = Assert.Single(await new MemoryDiagnosticResultsCollector(365, (start, end, _) => { from = start; to = end; return []; }, new SystemEventFixture.Clock()).CollectAsync(CancellationToken.None));
        Assert.Equal(SystemEventFixture.Now.AddDays(-365), from);
        Assert.Equal(SystemEventFixture.Now, to);
        Assert.Equal(0m, Fact(evidence, MemoryDiagnosticResultsCollector.ReportedPassEvents).NumberValue);
        Assert.Contains("um Pass histórico não atesta a memória atual", evidence.Coverage.Scope);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(366)]
    public void InvalidMemoryWindowFailsBeforeEnumeration(int days) => Assert.Throws<ArgumentOutOfRangeException>(() => new MemoryDiagnosticResultsCollector(days));

    [Fact]
    public async Task MemoryCancellationPropagatesIncludingCancellationAtEndOfEnumeration()
    {
        using var before = new CancellationTokenSource(); before.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new MemoryDiagnosticResultsCollector().CollectAsync(before.Token));
        using var final = new CancellationTokenSource();
        IEnumerable<SystemEventObservation> Source(DateTimeOffset _, DateTimeOffset __, CancellationToken ___)
        { yield return Observation(); final.Cancel(); yield break; }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new MemoryDiagnosticResultsCollector(source: Source).CollectAsync(final.Token));
    }

    private static SystemEventObservation Observation(int id = 1201, ulong recordId = 42, string data = "", string extra = "") => SystemEventFixture.Observation(MemoryDiagnosticResultsCollector.ProviderName, id, recordId, data: data, extra: extra);
    private static DiagnosticFact Fact(DiagnosticEvidence evidence, string key) => evidence.Facts.Single(item => item.Key == key);
    private static async Task<DiagnosticEvidence> Collect(IEnumerable<SystemEventObservation> records) => Assert.Single(await new MemoryDiagnosticResultsCollector(source: (_, _, _) => records, clock: new SystemEventFixture.Clock()).CollectAsync(CancellationToken.None));
}
