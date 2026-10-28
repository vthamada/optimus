using System.Text.Json;
using Optimus.Application;
using Optimus.Domain;
using Optimus.Infrastructure.Hardware;

namespace Optimus.Tests;

public sealed class HardwareProfileTests
{
    [Fact]
    public async Task SelectedMetadataPreservesVendorWhitespaceUInt64AndUnzonedDate()
    {
        var document = Document();
        document["system"] = Query([new Dictionary<string, object?> { ["Manufacturer"] = "  Vendor Ω  ", ["Model"] = "Original model" }]);
        document["memory"] = Query([Memory(ulong.MaxValue)]);
        document["bios"] = Query([new Dictionary<string, object?> { ["Manufacturer"] = "Vendor", ["SMBIOSBIOSVersion"] = "1.23", ["ReleaseDate"] = new { sourceType = "System.DateTime", value = "2025-02-03T00:00:00.0000000", kind = "Unspecified" } }]);
        string raw = JsonSerializer.Serialize(document); var host = new Host(_ => Completed(raw));
        var evidence = Assert.Single(await new HardwareProfileCollector(host).CollectAsync(CancellationToken.None));
        Assert.Equal(WindowsSystemQuery.HardwareProfile, host.Query);
        Assert.Equal("windows.hardware-profile", evidence.CollectorId);
        Assert.Equal(EvidenceStatus.Complete, evidence.Status);
        Assert.Equal(raw, Fact(evidence, "sourceJson").TextValue);
        Assert.Equal("  Vendor Ω  ", Fact(evidence, "system.0.Manufacturer").TextValue);
        Assert.Equal((decimal)ulong.MaxValue, Fact(evidence, "memory.0.Capacity").NumberValue);
        Assert.Equal("2025-02-03T00:00:00.0000000", Fact(evidence, "bios.0.ReleaseDate").TextValue);
        Assert.Equal("Unspecified", Fact(evidence, "bios.0.ReleaseDate.kind").TextValue);
        Assert.Equal("false", Fact(evidence, "xmpStateInferred").TextValue);
        Assert.Null(Fact(evidence, "memory.0.Speed").Unit);
        Assert.Equal("MHz", Fact(evidence, "memory.0.ConfiguredClockSpeed").Unit);
        Assert.DoesNotContain(evidence.Facts, fact => fact.Key.Contains("Serial", StringComparison.Ordinal) || fact.Key.Contains("UUID", StringComparison.Ordinal));
        Assert.True(SessionValidation.IsValid(new(Guid.NewGuid(), evidence.CapturedAt, evidence.CapturedAt, SessionStatus.Complete, [evidence], [])));
    }

    [Fact]
    public async Task FailedAndMalformedClassesCannotDiscardIndependentMemoryMetadata()
    {
        var document = Document(); document["baseboards"] = Query([], false, notes: [new { code = "PermissionDenied", message = "denied" }]);
        document["bios"] = new { succeeded = true, examined = -1, limitReached = false, records = Array.Empty<object>(), issues = Array.Empty<object>() };
        document["memory"] = Query([Memory(16_384)]);
        var evidence = await Collect(document);
        Assert.Equal(EvidenceStatus.Partial, evidence.Status);
        Assert.Equal(16_384m, Fact(evidence, "memory.0.Capacity").NumberValue);
        Assert.Contains(evidence.Issues, issue => issue.Code == IssueCode.PermissionDenied);
        Assert.Contains(evidence.Issues, issue => issue.Message.Contains("bios", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("16384")]
    [InlineData(-1)]
    public async Task MissingNullAndWrongCapacityNeverBecomeZeroOrAnInventedValue(object? capacity)
    {
        var memory = Memory(1); memory["Capacity"] = capacity;
        var document = Document(); document["memory"] = Query([memory]);
        var evidence = await Collect(document);
        Assert.Equal(EvidenceStatus.Partial, evidence.Status);
        Assert.Null(Fact(evidence, "memory.0.Capacity").NumberValue);
    }

    [Fact]
    public async Task ZeroSpeedAndUnknownMemoryTypeRemainSourceNumbersWithoutXmpOrDdrGuess()
    {
        var memory = Memory(0); memory["ConfiguredClockSpeed"] = 0u; memory["SMBIOSMemoryType"] = uint.MaxValue;
        var document = Document(); document["memory"] = Query([memory]); var evidence = await Collect(document);
        Assert.Equal(EvidenceStatus.Partial, evidence.Status);
        Assert.Equal(0m, Fact(evidence, "memory.0.Capacity").NumberValue);
        Assert.Equal(0m, Fact(evidence, "memory.0.ConfiguredClockSpeed").NumberValue);
        Assert.Equal((decimal)uint.MaxValue, Fact(evidence, "memory.0.SMBIOSMemoryType").NumberValue);
        Assert.DoesNotContain(evidence.Facts, fact => fact.Key.EndsWith("SMBIOSMemoryType.name", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ModuleProbeAndProjectionBudgetsStayIndependentFromSystemFacts()
    {
        var document = Document(); var modules = Enumerable.Range(0, 32).Select(index =>
        {
            var memory = Memory(123); memory["DeviceLocator"] = new string('Ω', 512); memory["Manufacturer"] = new string('X', 512); return memory;
        }).Cast<object>().ToArray();
        document["memory"] = Query(modules, examined: 33, limited: true);
        document["system"] = Query([new { Manufacturer = "Vendor", Model = "Model" }]);
        var evidence = await Collect(document);
        Assert.Equal(EvidenceStatus.Partial, evidence.Status);
        Assert.True(evidence.Coverage.LimitReached);
        Assert.Equal(33m, Fact(evidence, "memory.examined").NumberValue);
        Assert.Equal(32m, Fact(evidence, "memory.retained").NumberValue);
        Assert.True(Fact(evidence, "memory.projected").NumberValue < 32);
        Assert.Equal("Model", Fact(evidence, "system.0.Model").TextValue);
    }

    [Theory]
    [InlineData("{\"kind\":\"optimus.hardware-profile\",\"kind\":\"duplicate\"}")]
    [InlineData("[]")]
    [InlineData("not json")]
    public async Task MalformedEnvelopePreservesRawAndPublishesNoHardware(string raw)
    {
        var evidence = Assert.Single(await new HardwareProfileCollector(new Host(_ => Completed(raw))).CollectAsync(CancellationToken.None));
        Assert.Equal(EvidenceStatus.Unavailable, evidence.Status);
        Assert.Equal(raw, Fact(evidence, "sourceJson").TextValue);
        Assert.DoesNotContain(evidence.Facts, fact => fact.Key == "system.0.Model");
    }

    [Fact]
    public async Task OversizeAndBadFieldTypesAreBoundedWithoutTruncationOrDefault()
    {
        var document = Document(); document["system"] = Query([new { Manufacturer = new string('X', 513), Model = "retained" }]);
        var evidence = await Collect(document);
        Assert.Null(Fact(evidence, "system.0.Manufacturer").TextValue);
        Assert.Equal("retained", Fact(evidence, "system.0.Model").TextValue);
        evidence = Assert.Single(await new HardwareProfileCollector(new Host(_ => Completed(new string('X', 262_145)))).CollectAsync(CancellationToken.None));
        Assert.Equal(EvidenceStatus.Unavailable, evidence.Status);
        Assert.DoesNotContain(evidence.Facts, fact => fact.Key == "sourceJson");
    }

    [Fact]
    public async Task FailedTransportPreservesPrimaryErrorRawBytesCleanupAndUnknownMetrics()
    {
        var execution = new ToolRunResult(ToolRunStatus.TimedOut, null, "partial", "stderr", new(TimeSpan.FromSeconds(120)),
            new(ErrorCode.Timeout, "primary", ["owned pending"]), OutputCodePage: 65001, StandardOutputBytes: [0xFF], StandardErrorBytes: [0xFE]);
        var evidence = Assert.Single(await new HardwareProfileCollector(new Host(_ => execution)).CollectAsync(CancellationToken.None));
        Assert.Equal(EvidenceStatus.Unavailable, evidence.Status);
        Assert.Equal("primary", Fact(evidence, "toolErrorMessage").TextValue);
        Assert.Contains("owned pending", Fact(evidence, "toolPendingCleanupPaths").TextValue!);
        Assert.Equal("/w==", Fact(evidence, "toolStdoutBytesBase64").TextValue);
        Assert.Null(Fact(evidence, "cpuSeconds").NumberValue);
    }

    [Fact]
    public async Task CancellationBeforeOrAfterHostNeverPublishesInventory()
    {
        using var before = new CancellationTokenSource(); before.Cancel(); var host = new Host(_ => Completed(JsonSerializer.Serialize(Document())));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new HardwareProfileCollector(host).CollectAsync(before.Token)); Assert.Equal(0, host.Calls);
        using var after = new CancellationTokenSource(); host = new Host(_ => { after.Cancel(); return Completed(JsonSerializer.Serialize(Document())); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new HardwareProfileCollector(host).CollectAsync(after.Token)); Assert.Equal(1, host.Calls);
    }

    private static Dictionary<string, object?> Memory(ulong capacity) => new() { ["DeviceLocator"] = "DIMM A", ["BankLabel"] = "BANK0", ["Manufacturer"] = "Vendor", ["PartNumber"] = " Part original ", ["Capacity"] = capacity, ["Speed"] = 3200u, ["ConfiguredClockSpeed"] = 2666u, ["SMBIOSMemoryType"] = 26u };
    private static Dictionary<string, object?> Document() => new() { ["kind"] = "optimus.hardware-profile", ["version"] = 1, ["observedAt"] = "2026-10-28T10:00:00.0000000-03:00", ["baseboards"] = Query([]), ["system"] = Query([]), ["bios"] = Query([]), ["processors"] = Query([]), ["memory"] = Query([]) };
    private static object Query(object[] records, bool succeeded = true, int? examined = null, bool limited = false, object[]? notes = null) => new { succeeded, examined = examined ?? records.Length, limitReached = limited, records, issues = notes ?? [] };
    private static DiagnosticFact Fact(DiagnosticEvidence evidence, string key) => evidence.Facts.Single(fact => fact.Key == key);
    private static ToolRunResult Completed(string raw) => new(ToolRunStatus.Exited, 0, raw, "", new(TimeSpan.Zero));
    private static async Task<DiagnosticEvidence> Collect(Dictionary<string, object?> document) => Assert.Single(await new HardwareProfileCollector(new Host(_ => Completed(JsonSerializer.Serialize(document)))).CollectAsync(CancellationToken.None));
    private sealed class Host(Func<CancellationToken, ToolRunResult> run) : IWindowsSystemQueryRunner
    {
        public int Calls { get; private set; }
        public WindowsSystemQuery? Query { get; private set; }
        public Task<ToolRunResult> RunAsync(WindowsSystemQuery query, CancellationToken token) { Calls++; Query = query; return Task.FromResult(run(token)); }
    }
}
