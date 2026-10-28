using Optimus.Infrastructure;
using Optimus.Infrastructure.Events;
using System.Xml.Linq;

namespace Optimus.Tests;

public sealed class WindowsEventReaderSelectionTests
{
    [Fact]
    public void NativeRestartQueryUsesSeparateStructuredSelectorsWithTheSameCapturedWindow()
    {
        var from = SystemEventFixture.Now.AddDays(-30);
        var query = XDocument.Parse(WindowsEventReader.BuildRestartQuery(from, SystemEventFixture.Now));
        Assert.Equal("QueryList", query.Root!.Name.LocalName);
        var selects = query.Root.Element("Query")!.Elements("Select").ToArray();
        Assert.Equal(5, selects.Length);
        Assert.All(selects, select =>
        {
            Assert.Equal("System", select.Attribute("Path")!.Value);
            Assert.Contains("@SystemTime >= '2026-09-28T12:00:00.0000000Z'", select.Value);
            Assert.Contains("@SystemTime <= '2026-10-28T12:00:00.0000000Z'", select.Value);
            Assert.StartsWith("*[System[", select.Value);
        });
        Assert.Contains(selects, select => select.Value.Contains("WHEA-Logger", StringComparison.Ordinal) && select.Value.Contains("(Level=2 or Level=3)", StringComparison.Ordinal));
        Assert.Contains(selects, select => select.Value.Contains("BugCheck", StringComparison.Ordinal) && select.Value.Contains("EventID=1001", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("<Level>2</Level>", "<Level>2</Level><Level>3</Level>")]
    [InlineData("<Level>2</Level>", "<Level>256</Level>")]
    [InlineData("<Level>2</Level>", "<Level><Inner>2</Inner></Level>")]
    [InlineData("<EventID>41</EventID>", "<EventID>65536</EventID>")]
    [InlineData("<Channel>System</Channel>", "<Channel><Inner>System</Inner></Channel>")]
    [InlineData("<EventRecordID>42</EventRecordID>", "<EventRecordID>18446744073709551616</EventRecordID>")]
    public void InvalidScalarOrDuplicateSystemMetadataCannotBecomeValidSelectedFacts(string original, string changed)
    {
        var source = SystemEventFixture.Observation();
        var raw = source.OriginalXml.Replace(original, changed, StringComparison.Ordinal);
        var parsed = WindowsEventReader.Interpret(source with { OriginalXml = raw });
        Assert.False(parsed.Xml!.MetadataValid);
        Assert.NotEmpty(parsed.Xml.Issues);
        Assert.Equal(raw, parsed.OriginalXml);
    }

    [Fact]
    public void DataIncompleteCannotBeReusedAsACompletedProjectionOrUseAnInjectedTime()
    {
        var source = SystemEventFixture.Observation(MemoryDiagnosticResultsCollector.ProviderName, 1201, data: "<Data Name='CompletionType'><Inner>Pass</Inner></Data>");
        var initial = WindowsEventReader.Interpret(source);
        Assert.True(initial.Xml!.MetadataValid);
        Assert.False(initial.Xml.DataComplete);
        Assert.Null(Assert.Single(initial.Xml.Data).Value);
        var changed = WindowsEventReader.Interpret(initial with { OccurredAt = DateTimeOffset.UnixEpoch });
        Assert.Same(initial.Xml, changed.Xml);
        Assert.Equal(SystemEventFixture.Now, changed.OccurredAt);
    }

    [Fact]
    public void OriginalEventDataConstructorAndDeconstructionRemainCompatible()
    {
        var original = new WindowsEventDataValue(4, "name", "value");
        var (index, name, value) = original;
        Assert.Equal(4, index);
        Assert.Equal("name", name);
        Assert.Equal("value", value);
        Assert.Equal("EventData/Data", original.SourcePath);
    }

    [Fact]
    public void ResultsAreNotParsedForAnUnrelatedProvider()
    {
        var source = SystemEventFixture.Observation("User32", 1074, extra: $"<UserData><Results xmlns='{WindowsEventReader.MemoryResultsNamespace}'><CompletionType>Pass</CompletionType></Results></UserData>");
        var parsed = WindowsEventReader.Interpret(source);
        Assert.Empty(parsed.Xml!.Data);
        Assert.True(parsed.Xml.MetadataValid);
    }
}
