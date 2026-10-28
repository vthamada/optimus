using Optimus.Domain;

namespace Optimus.Application;

public sealed record LocalAiSettings(
    string ExecutablePath,
    string ModelPath,
    string? ExpectedExecutableSha256 = null,
    string? ExpectedModelSha256 = null,
    int MaximumOutputTokens = 768,
    int ContextTokens = 8192,
    int Threads = 0,
    int TimeoutSeconds = 120,
    long MaximumMemoryBytes = 8L * 1024 * 1024 * 1024,
    int MaximumSelectedFacts = 6);

public interface IAssistantService
{
    Task<Result<AssistantAnswer>> AnalyzeAsync(
        DiagnosticSession session, string question, LocalAiSettings settings,
        CancellationToken cancellationToken);
}
