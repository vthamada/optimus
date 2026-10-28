namespace Optimus.Application;

/// <summary>Explicit local inputs. No debugger discovery, symbol server, or live-process target.</summary>
public sealed record CrashDumpSettings(
    string DebuggerExecutablePath,
    string DumpPath,
    string SymbolDirectory,
    string? ExpectedDebuggerSha256 = null);
