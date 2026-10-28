namespace Optimus.Application;

public sealed record RuntimeProfile(
    string? LlamaExecutablePath = null,
    string? ModelPath = null,
    string? ResticExecutablePath = null,
    string? SmartctlExecutablePath = null,
    string? LlamaSha256 = null,
    string? ModelSha256 = null,
    string? ResticSha256 = null,
    string? CdbExecutablePath = null,
    string? SymbolDirectory = null,
    string? CdbSha256 = null);
