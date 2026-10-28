using Optimus.Domain;

namespace Optimus.Application;

/// <summary>Only an explicit user-selected official page may be requested; never an automatic investigation step.</summary>
public interface IFirmwareNotesReader
{
    Task<Result<DiagnosticEvidence>> ReadAsync(string url, string manufacturer, CancellationToken cancellationToken);
}
