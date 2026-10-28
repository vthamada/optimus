using System.Security.Cryptography;
using System.Text;
using Optimus.Domain;
using Optimus.Infrastructure.Memory;

namespace Optimus.Tests;

public sealed class MemoryTestReportTests
{
    // Reduced structural fixture based on PassMark's official V10.5 sample:
    // https://www.memtest86.com/MemTest86Report.html (retrieved 2026-10-28, UTF-16LE BOM).
    // Synthetic values make the bit arithmetic independently checkable; this is not a physical test.
    private const string SingleBitRow = "2026-01-02 03:04:05 - [Data Error] Test: 3, CPU: 2, Address: FFFFFFFFFFFFFFFF/A1, Expected: FFFFFFFFFFFFFFFF, Actual: 7FFFFFFFFFFFFFFF";

    [Fact]
    public async Task RealHtmlStructurePreservesOriginalDigestProducerSelectionAndUInt64Bit63()
    {
        string html = Report(SingleBitRow); var bytes = Encoding.UTF8.GetBytes(html);
        var evidence = await Parse(bytes);
        Assert.Equal("memory.test-report", evidence.CollectorId);
        Assert.Equal(EvidenceStatus.Partial, evidence.Status);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), Fact(evidence, "sourceSha256").TextValue);
        Assert.Equal(bytes, Convert.FromBase64String(Fact(evidence, "sourceBytesBase64").TextValue!));
        Assert.Equal(html, Fact(evidence, "sourceHtml").TextValue);
        Assert.Equal("MemTest86", Fact(evidence, "producer.name").TextValue);
        Assert.Equal("10.5", Fact(evidence, "producer.version").TextValue);
        Assert.Equal(1m, Fact(evidence, "tests.errorRowsObserved").NumberValue);
        Assert.Equal(1m, Fact(evidence, "tests.singleBitRowsObserved").NumberValue);
        Assert.Equal((decimal)ulong.MaxValue, Fact(evidence, "tests.errorRows.0.address").NumberValue);
        Assert.Equal(9_223_372_036_854_775_808m, Fact(evidence, "tests.errorRows.0.xor").NumberValue);
        Assert.Equal(63m, Fact(evidence, "tests.errorRows.0.singleBitPosition").NumberValue);
        Assert.Null(Fact(evidence, "tests.reportedErrors").NumberValue);
        Assert.Contains("não identifica DIMM", Fact(evidence, "tests.errorRows.0.singleBitPosition").Label);
        Assert.True(SessionValidation.IsValid(new(Guid.NewGuid(), evidence.CapturedAt, evidence.CapturedAt, SessionStatus.Partial, [evidence], [])));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitUtf16BomIsDecodedStrictlyWithoutRewritingOriginalBytes(bool bigEndian)
    {
        var encoding = new UnicodeEncoding(bigEndian, true, true);
        string html = Report(SingleBitRow, charset: bigEndian ? "UTF-16BE" : "UTF-16LE");
        byte[] bytes = [.. encoding.GetPreamble(), .. encoding.GetBytes(html)];
        var evidence = await Parse(bytes);
        Assert.Equal(bigEndian ? "utf-16be" : "utf-16le", Fact(evidence, "sourceEncoding").TextValue);
        Assert.Equal(2m, Fact(evidence, "sourceBomBytes").NumberValue);
        Assert.Equal(html, Fact(evidence, "sourceHtml").TextValue);
        Assert.Equal(bytes, Convert.FromBase64String(Fact(evidence, "sourceBytesBase64").TextValue!));
        Assert.Equal(1m, Fact(evidence, "tests.errorRowsObserved").NumberValue);
    }

    [Fact]
    public async Task EncodingConflictIsAnIssueWhileBomDeterminesDecoder()
    {
        var encoding = new UnicodeEncoding(false, true, true); byte[] bytes = [.. encoding.GetPreamble(), .. encoding.GetBytes(Report(SingleBitRow, charset: "utf-8"))];
        var evidence = await Parse(bytes);
        Assert.Equal(1m, Fact(evidence, "tests.errorRowsObserved").NumberValue);
        Assert.Contains(evidence.Issues, issue => issue.Message.Contains("Meta charset", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("{\"result\":\"PASS\",\"errors\":0}")]
    [InlineData("test,errors\nMemTest86,0")]
    [InlineData("<html><title>MemTest86 PASS</title><p>0 errors</p></html>")]
    public async Task UnknownFormatsKeepRawAndUnknownCountsInsteadOfPass(string text)
    {
        var evidence = await Parse(Encoding.UTF8.GetBytes(text));
        Assert.Equal(EvidenceStatus.Partial, evidence.Status);
        Assert.Null(Fact(evidence, "tests.errorRowsObserved").NumberValue);
        Assert.Null(Fact(evidence, "tests.singleBitRowsObserved").NumberValue);
        Assert.Null(Fact(evidence, "tests.reportedErrors").NumberValue);
        Assert.DoesNotContain(evidence.Facts, fact => fact.Key == "tests.reportedResult");
        Assert.Equal(text, Fact(evidence, "sourceHtml").TextValue);
    }

    [Fact]
    public async Task DuplicateFieldsAndErrorRowsArePreservedWithoutDeduplicationOrChoosingAResult()
    {
        string html = Report(SingleBitRow + "</td></tr><tr><td>" + SingleBitRow, summaryExtra: "<tr><td>Result</td><td>PASS</td></tr><tr><td>Total Errors</td><td>999</td></tr><tr><td>Total Errors</td><td>1</td></tr>");
        var evidence = await Parse(Encoding.UTF8.GetBytes(html));
        Assert.Equal(2m, Fact(evidence, "tests.errorRowsObserved").NumberValue);
        Assert.Equal(2m, Fact(evidence, "tests.singleBitRowsObserved").NumberValue);
        Assert.Equal("FAIL", Fact(evidence, "reportedResult.0").TextValue);
        Assert.Equal("PASS", Fact(evidence, "reportedResult.1").TextValue);
        Assert.Null(Fact(evidence, "tests.reportedResult").TextValue);
        Assert.Null(Fact(evidence, "tests.reportedErrors").NumberValue);
        Assert.Equal("999", Fact(evidence, "tests.reportedErrorsSource.0").TextValue);
        Assert.Equal("1", Fact(evidence, "tests.reportedErrorsSource.1").TextValue);
    }

    [Fact]
    public async Task ExplicitTotalIsSeparateFromLastRowsAndTestTableCountsAreNotSummed()
    {
        string html = Report(SingleBitRow, summaryExtra: "<tr><td>Total Errors</td><td>670</td></tr>", resultExtra: "<table><tr><td>Test</td><td># Tests Passed</td><td>Errors</td></tr><tr><td>Test 1</td><td>0/1</td><td>7</td></tr><tr><td>Test 2</td><td>0/1</td><td>9</td></tr></table>");
        var evidence = await Parse(Encoding.UTF8.GetBytes(html));
        Assert.Equal(670m, Fact(evidence, "tests.reportedErrors").NumberValue);
        Assert.Equal(1m, Fact(evidence, "tests.errorRowsObserved").NumberValue);
    }

    [Theory]
    [InlineData("0000000000000000", "0000000000000001", 1, 0)]
    [InlineData("0000000000000000", "0000000000000003", 2, -1)]
    [InlineData("0000000000000001", "0000000000000001", 0, -1)]
    public async Task XorBitPositionsDescribeOnlyTheValue(string expected, string actual, int bits, int bit)
    {
        var evidence = await Parse(Encoding.UTF8.GetBytes(Report($"[Data Error] Test: 1, CPU: 0, Address: 00000000, Expected: {expected}, Actual: {actual}")));
        Assert.Equal((decimal)bits, Fact(evidence, "tests.errorRows.0.bitsDifferent").NumberValue);
        Assert.Equal(bit < 0 ? (decimal?)null : bit, Fact(evidence, "tests.errorRows.0.singleBitPosition").NumberValue);
        Assert.Equal(bits == 1 ? 1m : 0m, Fact(evidence, "tests.singleBitRowsObserved").NumberValue);
    }

    [Fact]
    public async Task EccAndUnknownRowsCountAsObservedButDoNotInventExpectedOrSingleBitData()
    {
        var evidence = await Parse(Encoding.UTF8.GetBytes(Report("[ECC Error] Test: 3, Address: 12345678, ECC Corrected: yes, Syndrome: N/A, Channel/Slot: N/A")));
        Assert.Equal(1m, Fact(evidence, "tests.errorRowsObserved").NumberValue);
        Assert.Equal(0m, Fact(evidence, "tests.singleBitRowsObserved").NumberValue);
        Assert.Equal(0m, Fact(evidence, "tests.dataErrorRowsParsed").NumberValue);
        Assert.DoesNotContain(evidence.Facts, fact => fact.Key == "tests.errorRows.0.expected");
    }

    [Fact]
    public async Task InertScriptsAndExternalResourcesDoNotCreateResultsOutsideSelectedTable()
    {
        string html = Report(SingleBitRow) + "<script>throw new Error('should never execute')</script><img src='https://invalid.example/memtest'><iframe src='file:///C:/private'></iframe><table><tr><td>Generated by</td><td>MemTest86 V99.9</td></tr></table>";
        var evidence = await Parse(Encoding.UTF8.GetBytes(html));
        Assert.Equal("10.5", Fact(evidence, "producer.version").TextValue);
        Assert.Equal(1m, Fact(evidence, "tests.errorRowsObserved").NumberValue);
        Assert.Contains("should never execute", Fact(evidence, "sourceHtml").TextValue!);
    }

    [Fact]
    public async Task InvalidEncodingRetainsOriginalBytesAndUnknownCounts()
    {
        foreach (byte[] bytes in new byte[][] { [0xFF], [0xFF, 0xFE, 0x41], Encoding.Unicode.GetBytes(Report(SingleBitRow)) })
        {
            var evidence = await Parse(bytes);
            Assert.Equal(bytes, Convert.FromBase64String(Fact(evidence, "sourceBytesBase64").TextValue!));
            Assert.Null(Fact(evidence, "tests.errorRowsObserved").NumberValue);
            Assert.Contains(evidence.Issues, issue => issue.Code == IssueCode.ReadFailed);
        }
    }

    [Fact]
    public async Task ErrorRowAndDomBudgetsRemainPartialWithOriginalRetained()
    {
        string rows = string.Join("</td></tr><tr><td>", Enumerable.Repeat(SingleBitRow, 257));
        var evidence = await Parse(Encoding.UTF8.GetBytes(Report(rows)));
        Assert.Equal(257m, Fact(evidence, "tests.errorRowsObserved").NumberValue);
        Assert.Equal(256m, Fact(evidence, "tests.dataErrorRowsParsed").NumberValue);
        Assert.True(evidence.Coverage.LimitReached);
        evidence = await Parse(Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("<i></i>", 20_001))));
        Assert.Contains(evidence.Issues, issue => issue.Message.Contains("20000", StringComparison.Ordinal));
        Assert.NotNull(Fact(evidence, "sourceHtml").TextValue);
    }

    [Fact]
    public async Task SelectedLocalFileIsReadWithoutOverwriteAndOversizeInputIsRefused()
    {
        string directory = Path.Combine(Path.GetTempPath(), "optimus-memtest-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Combine(directory, "chosen.html"); byte[] bytes = Encoding.UTF8.GetBytes(Report(SingleBitRow)); await File.WriteAllBytesAsync(path, bytes);
            var result = await new MemoryTestReportImporter().ImportAsync(path, CancellationToken.None);
            Assert.True(result.IsSuccess, result.Error?.Message); Assert.Equal(path, Fact(result.Value!, "selectedSourcePath").TextValue);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
            string large = Path.Combine(directory, "large.html"); using (var stream = new FileStream(large, FileMode.CreateNew)) stream.SetLength(MemoryTestReportImporter.MaximumReportBytes + 1L);
            result = await new MemoryTestReportImporter().ImportAsync(large, CancellationToken.None);
            Assert.False(result.IsSuccess); Assert.Null(result.Value); Assert.Equal(ErrorCode.InvalidData, result.Error!.Code);
            Assert.Equal(MemoryTestReportImporter.MaximumReportBytes + 1L, new FileInfo(large).Length);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task CancelledImportAndParserNeverReturnAPublishedResult()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new MemoryTestReportImporter().ImportAsync("missing.html", cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Parse(Encoding.UTF8.GetBytes(Report(SingleBitRow)), cancellation.Token));
        await Assert.ThrowsAsync<ArgumentException>(() => Parse(new byte[MemoryTestReportImporter.MaximumReportBytes + 1]));
    }

    private static string Report(string row, string charset = "utf-8", string summaryExtra = "", string resultExtra = "") =>
        $"<html><head><meta charset='{charset}'><title>MemTest86 - Sample Report</title></head><body><h2>Summary</h2><div class='summary'><table><tr><td>Report Date</td><td>2026-01-02 03:04:05</td></tr><tr><td>Generated by</td><td>MemTest86 V10.5 Site (64-bit)<br></td></tr><tr><td>Result</td><td>FAIL</td></tr>{summaryExtra}</table></div><h2>Result summary</h2><div class='results'><table><tr><td># Tests Passed</td><td>0/1 (0%)</td></tr></table>{resultExtra}<table><tr><td class='header'>Last 10 Errors</td></tr><tr><td>{row}</td></tr></table></div></body></html>";
    private static Task<DiagnosticEvidence> Parse(byte[] bytes, CancellationToken token = default) => MemoryTestReportImporter.ParseBytesAsync(bytes, "selected-report.html", token);
    private static DiagnosticFact Fact(DiagnosticEvidence evidence, string key) => evidence.Facts.Single(fact => fact.Key == key);
}
