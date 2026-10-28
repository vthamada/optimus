using System.Text.Json;
using Optimus.Application;
using Optimus.Domain;
using Optimus.Infrastructure.Smart;

namespace Optimus.Tests;

public sealed class SmartTests
{
    [Fact]
    public void HealthStatusBitIsNotConfusedWithACommandFailure()
    {
        const string source = """{"smartctl":{"exit_status":8},"device":{"name":"/dev/pd0"},"smart_status":{"passed":false},"future_field":{"keep":"original"}}""";

        var result = SmartJsonParser.Parse(source, 8);

        Assert.True(result.IsSuccess);
        Assert.Equal(EvidenceStatus.Complete, result.Value!.Status);
        Assert.Equal("false", result.Value.Facts.Single(fact => fact.Key == "smartPassed").TextValue);
        Assert.Contains(result.Value.Facts, fact => fact.Key == "exitBit3");
        Assert.Equal(source, result.Value.Facts.Single(fact => fact.Key == "sourceJson").TextValue);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    public void CommandFailureBitsPreservePartialFactsAndReason(int code)
    {
        var source = JsonSerializer.Serialize(new { smartctl = new { exit_status = code }, model_name = "selected model", smart_status = new { passed = true } });
        var result = SmartJsonParser.Parse(source, code);

        Assert.True(result.IsSuccess);
        Assert.Equal(EvidenceStatus.Partial, result.Value!.Status);
        Assert.Contains(result.Value.Facts, fact => fact.Key == "modelName" && fact.TextValue == "selected model");
        Assert.NotEmpty(result.Value.Issues);
    }

    [Fact]
    public void MissingStatusAndMissingDeviceDataNeverBecomeHealthy()
    {
        var result = SmartJsonParser.Parse("{}");

        Assert.True(result.IsSuccess);
        Assert.Equal(EvidenceStatus.Unavailable, result.Value!.Status);
        Assert.Contains(result.Value.Issues, issue => issue.Message.Contains("código de saída", StringComparison.Ordinal));
    }

    [Fact]
    public void DuplicatePropertiesAreAmbiguousAndPreservedAsInvalidEvidence()
    {
        const string source = """{"smart_status":{"passed":true,"passed":false}}""";
        var result = SmartJsonParser.Parse(source);

        Assert.False(result.IsSuccess);
        Assert.Equal(EvidenceStatus.Unavailable, result.Value!.Status);
        Assert.Equal(source, Assert.Single(result.Value.Facts).TextValue);
    }

    [Fact]
    public void UnexpectedNumericRepresentationStaysUnknownInsteadOfZero()
    {
        var result = SmartJsonParser.Parse("""{"smartctl":{"exit_status":0},"model_name":"disk","temperature":{"current":"unknown"}}""", 0);

        Assert.True(result.IsSuccess);
        Assert.Null(result.Value!.Facts.Single(fact => fact.Key == "temperatureCelsius").NumberValue);
        Assert.Equal(EvidenceStatus.Partial, result.Value.Status);
    }

    [Fact]
    public void DivergentExitCodesAreRetainedAndMakeCoveragePartial()
    {
        var result = SmartJsonParser.Parse("""{"smartctl":{"exit_status":0},"smart_status":{"passed":true}}""", 4);

        Assert.Equal(EvidenceStatus.Partial, result.Value!.Status);
        Assert.Contains(result.Value.Facts, fact => fact.Key == "reportedExitStatus" && fact.TextValue == "0");
        Assert.Contains(result.Value.Facts, fact => fact.Key == "processExitStatus" && fact.NumberValue == 4);
    }

    [Fact]
    public async Task ServiceOnlyInvokesFixedReadArgumentsForExplicitLocalTarget()
    {
        var runner = new CapturingRunner();
        var result = await new SmartService(runner).ReadAsync(@"C:\tools\smartctl.exe", "/dev/pd0", CancellationToken.None);

        Assert.True(result.IsSuccess);
        Assert.Equal(new[] { "-j", "-a", "/dev/pd0" }, runner.Request!.Arguments);
        Assert.Contains(result.Value!.Facts, fact => fact.Key == "selectedTarget" && fact.TextValue == "/dev/pd0");
    }

    [Theory]
    [InlineData("--scan")]
    [InlineData("/dev/tw_cli/c0/p0")]
    [InlineData("https://example.com")]
    public async Task UnsupportedTargetCannotSelectOptionsOrExternalHelpers(string target)
    {
        var runner = new CapturingRunner();
        var result = await new SmartService(runner).ReadAsync(@"C:\tools\smartctl.exe", target, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Null(runner.Request);
    }

    private sealed class CapturingRunner : IExternalToolRunner
    {
        public ToolRequest? Request;
        public Task<ToolRunResult> RunAsync(ToolRequest request, CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(new ToolRunResult(ToolRunStatus.Exited, 0,
                """{"smartctl":{"exit_status":0},"device":{"name":"/dev/pd0"},"smart_status":{"passed":true}}""", "", new(TimeSpan.Zero)));
        }
    }
}
