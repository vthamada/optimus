using Optimus.Domain;

namespace Optimus.Application;

/// <summary>Imports only a report explicitly selected by the caller; never starts a physical memory test.</summary>
public interface IMemoryTestReportImporter
{
    Task<Result<DiagnosticEvidence>> ImportAsync(string reportPath, CancellationToken cancellationToken);
}
