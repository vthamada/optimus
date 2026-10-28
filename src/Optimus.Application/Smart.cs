using Optimus.Domain;

namespace Optimus.Application;

public interface ISmartService
{
    Task<Result<DiagnosticEvidence>> ReadAsync(string executablePath, string target, CancellationToken cancellationToken);
}
