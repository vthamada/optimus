using Optimus.Application;
using Optimus.Domain;
using Optimus.Infrastructure;

namespace Optimus.Tests;

public sealed class RuntimeProfileTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "optimus-profile-tests", Guid.NewGuid().ToString("N"));
    public RuntimeProfileTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task NewProfileRoundTripsOnlyExplicitConfiguration()
    {
        var profile = new RuntimeProfile(LlamaExecutablePath: @"C:\Ferramentas\llama-cli.exe", ModelPath: @"D:\Modelos\Qwen.gguf", ModelSha256: new string('a', 64),
            CdbExecutablePath: @"C:\Debuggers\cdb.exe", SymbolDirectory: @"D:\Symbols", CdbSha256: new string('b', 64));
        var path = Path.Combine(_directory, "profile.json");
        Assert.True((await RuntimeProfileFiles.WriteNewAsync(profile, path, CancellationToken.None)).IsSuccess);
        var read = await RuntimeProfileFiles.ReadAsync(path, CancellationToken.None);
        Assert.Equal(profile, read.Value);
        Assert.Null(read.Value!.ResticExecutablePath);
    }

    [Fact]
    public async Task ExistingProfileIsNeverReplaced()
    {
        var path = Path.Combine(_directory, "existing.json");
        await File.WriteAllTextAsync(path, "preserve existing settings");
        var result = await RuntimeProfileFiles.WriteNewAsync(new(), path, CancellationToken.None);
        Assert.Equal(ErrorCode.OutputExists, result.Error!.Code);
        Assert.Equal("preserve existing settings", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task ForeignOrUnknownConfigurationIsNotAdopted()
    {
        var path = Path.Combine(_directory, "foreign.json");
        await File.WriteAllTextAsync(path, "{\"kind\":\"another-product\",\"version\":1,\"profile\":{}}");
        var result = await RuntimeProfileFiles.ReadAsync(path, CancellationToken.None);
        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCode.InvalidConfiguration, result.Error!.Code);
    }

    [Fact]
    public async Task DuplicateConfigurationCannotSelectTheLastAuthoritySilently()
    {
        var path = Path.Combine(_directory, "ambiguous.json");
        await File.WriteAllTextAsync(path, "{\"kind\":\"foreign\",\"kind\":\"optimus.runtime-profile\",\"version\":1,\"profile\":{}}");
        var result = await RuntimeProfileFiles.ReadAsync(path, CancellationToken.None);
        Assert.False(result.IsSuccess);
        Assert.Equal(ErrorCode.InvalidConfiguration, result.Error!.Code);
    }

    [Fact]
    public async Task NetworkPathsAndInvalidDigestsAreRejected()
    {
        Assert.False((await RuntimeProfileFiles.WriteNewAsync(new(LlamaExecutablePath: @"\\server\tools\llama.exe"), Path.Combine(_directory, "network.json"), CancellationToken.None)).IsSuccess);
        Assert.False((await RuntimeProfileFiles.WriteNewAsync(new(ModelSha256: "not-a-sha256"), Path.Combine(_directory, "hash.json"), CancellationToken.None)).IsSuccess);
        Assert.Empty(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task BoundedReadRejectsExpansionBeforeLoadingTheDocument()
    {
        var path = Path.Combine(_directory, "large.json");
        await File.WriteAllBytesAsync(path, new byte[RuntimeProfileFiles.MaximumProfileBytes + 1]);
        var result = await RuntimeProfileFiles.ReadAsync(path, CancellationToken.None);
        Assert.Equal(ErrorCode.InvalidData, result.Error!.Code);
    }

    [Fact]
    public async Task CancelledWriteDoesNotPublishConfiguration()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RuntimeProfileFiles.WriteNewAsync(new(), Path.Combine(_directory, "cancelled.json"), cancellation.Token));
        Assert.Empty(Directory.GetFiles(_directory));
    }

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
}
