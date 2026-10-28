namespace Optimus.Tests;

public sealed class InstabilityCliTests
{
    [Fact]
    public void InvestigationDefaultsAreFiniteDeterministicAndOffline()
    {
        var parsed = InstabilityCli.ParseArguments(["investigate"]);
        Assert.Equal(30, parsed.Days); Assert.Equal(4, parsed.Steps); Assert.False(parsed.UseAi);
        Assert.Null(parsed.ProfilePath); Assert.Null(parsed.DumpPath); Assert.Null(parsed.MemoryReportPath); Assert.Null(parsed.SessionId);
        Assert.InRange(parsed.Question.Length, 1, 400);
    }

    [Fact]
    public void AllInputsAreCapturedBeforeAnyFactoryOrFileRead()
    {
        var id = Guid.NewGuid();
        var parsed = InstabilityCli.ParseArguments(["investigate", "--days", "365", "--steps", "1", "--ai", "true", "--question", "  Pergunta original Ω  ",
            "--profile", @"C:\missing\profile.json", "--dump", @"D:\missing\chosen.dmp", "--memory-report", @"E:\missing\report.HTML", "--session", id.ToString(),
            "--data-dir", @"C:\missing\data", "--report", @"C:\missing\new.json"]);
        Assert.Equal(365, parsed.Days); Assert.Equal(1, parsed.Steps); Assert.True(parsed.UseAi); Assert.Equal(id, parsed.SessionId);
        Assert.Equal("  Pergunta original Ω  ", parsed.Question);
        Assert.Equal(@"E:\missing\report.HTML", parsed.MemoryReportPath);
    }

    [Theory]
    [InlineData("memory-import", "--input", @"C:\missing\report.html")]
    [InlineData("memory-import", "--input", @"C:\missing\report.HTM")]
    [InlineData("dump-analyze", "--dump", @"C:\missing\chosen.mdmp", "--profile", @"C:\missing\profile.json")]
    public void NamedCommandsDoNotProbeMissingInputPaths(params string[] args)
    {
        var parsed = InstabilityCli.ParseArguments(args);
        Assert.Equal(args[0], parsed.Command);
    }

    [Theory]
    [InlineData("investigate", "--days", "0")]
    [InlineData("investigate", "--days", "366")]
    [InlineData("investigate", "--days", "+1")]
    [InlineData("investigate", "--days", " 1")]
    [InlineData("investigate", "--steps", "0")]
    [InlineData("investigate", "--steps", "5")]
    [InlineData("investigate", "--ai", "True")]
    [InlineData("investigate", "--ai", "yes")]
    [InlineData("investigate", "--execute", "true")]
    [InlineData("investigate", "--unknown", "value")]
    [InlineData("memory-import", "--input", @"C:\missing\report.html", "--ai", "false")]
    [InlineData("dump-analyze", "--dump", @"C:\missing\dump.dmp", "--profile", @"C:\missing\profile.json", "--days", "30")]
    public void UnknownOrOutOfRangeFlagsFailAtThePureBoundary(params string[] args) => Assert.Throws<ArgumentException>(() => InstabilityCli.ParseArguments(args));

    [Theory]
    [InlineData("memory-import")]
    [InlineData("memory-import", "--input", @"C:\missing\report.json")]
    [InlineData("dump-analyze", "--profile", @"C:\missing\profile.json")]
    [InlineData("dump-analyze", "--dump", @"C:\missing\dump.dmp")]
    [InlineData("investigate", "--ai", "true")]
    [InlineData("investigate", "--dump", @"C:\missing\dump.dmp")]
    [InlineData("investigate", "--profile", @"C:\missing\profile.json")]
    public void RequiredSelectedInputsAndProfileUseAreExplicit(params string[] args) => Assert.Throws<ArgumentException>(() => InstabilityCli.ParseArguments(args));

    [Theory]
    [InlineData("investigate", "--steps")]
    [InlineData("investigate", "--days", "1", "--days", "2")]
    [InlineData("investigate", "--question", " ")]
    [InlineData("investigate", "--session", "not a UUID")]
    [InlineData("investigate", "--session", "00000000-0000-0000-0000-000000000000")]
    [InlineData("investigate", "--memory-report", @"C:\missing\fake.csv")]
    [InlineData("investigate", "--report", @"C:\missing\new.txt")]
    [InlineData("memory-import", "--input", "relative.html")]
    [InlineData("memory-import", "--input", @"\\server\share\report.html")]
    [InlineData("memory-import", "--input", @"\\?\C:\report.html")]
    public void DuplicatesMalformedIdsAndUnsupportedPathsFailBeforeIo(params string[] args) => Assert.Throws<ArgumentException>(() => InstabilityCli.ParseArguments(args));

    [Fact]
    public void LongQuestionsAreRejectedAndAllowedLengthIsPreserved()
    {
        Assert.Throws<ArgumentException>(() => InstabilityCli.ParseArguments(["investigate", "--question", new string('X', 401)]));
        Assert.Equal(new string('X', 400), InstabilityCli.ParseArguments(["investigate", "--question", new string('X', 400)]).Question);
    }
}
