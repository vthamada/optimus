using System.Text.RegularExpressions;
using Optimus.Application;
using Optimus.Domain;

namespace Optimus.Infrastructure.Smart;

public sealed class SmartService(IExternalToolRunner runner) : ISmartService
{
    public async Task<Result<DiagnosticEvidence>> ReadAsync(string executablePath, string target, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(target) || target.Length > 260 || !IsSupportedLocalTarget(target))
            return Result<DiagnosticEvidence>.Failure(ErrorCode.InvalidConfiguration,
                "Selecione explicitamente um disco local básico, por exemplo /dev/pd0, /dev/sda, /dev/nvme0 ou C:. Controladoras e helpers externos não são habilitados nesta versão.");
        var directory = Path.GetDirectoryName(executablePath);
        if (string.IsNullOrWhiteSpace(directory)) return Result<DiagnosticEvidence>.Failure(ErrorCode.InvalidConfiguration, "Selecione smartctl por caminho absoluto.");
        var execution = await runner.RunAsync(new(executablePath, ["-j", "-a", target], directory,
            TimeSpan.FromSeconds(30), SmartJsonParser.MaximumJsonBytes), cancellationToken).ConfigureAwait(false);
        var parsed = SmartJsonParser.Parse(execution.StandardOutput, execution.ExitCode);
        var evidence = parsed.Value;
        if (evidence is not null)
        {
            evidence = evidence with
            {
                Facts = evidence.Facts.Concat([
                new DiagnosticFact("selectedTarget", "Alvo selecionado pelo usuário", target),
                new DiagnosticFact("toolRunStatus", "Estado da execução supervisionada", execution.Status.ToString()),
                new DiagnosticFact("toolStderr", "Diagnóstico da ferramenta", execution.StandardError, Detailed: true)
            ]).ToArray()
            };
        }
        if (execution.Status != ToolRunStatus.Exited || execution.Error is not null)
        {
            var error = execution.Error ?? new AppError(ErrorCode.ToolFailed, "A execução SMART não confirmou saída completa.");
            if (evidence is not null)
            {
                evidence = evidence with
                {
                    Status = EvidenceStatus.Partial,
                    Issues = evidence.Issues.Concat([new CollectionIssue(IssueCode.ReadFailed, error.Message)]).ToArray()
                };
                return Result<DiagnosticEvidence>.FailureWithValue(evidence, error.Code, error.Message, error.PendingCleanupPaths);
            }
            return Result<DiagnosticEvidence>.Failure(error.Code, error.Message, error.PendingCleanupPaths);
        }
        return parsed.IsSuccess && evidence is not null ? Result<DiagnosticEvidence>.Success(evidence)
            : evidence is not null ? Result<DiagnosticEvidence>.FailureWithValue(evidence, parsed.Error!.Code, parsed.Error.Message)
            : parsed;
    }

    private static bool IsSupportedLocalTarget(string target) => Regex.IsMatch(target,
        @"^(?:(?:/dev/)?(?:pd[0-9]{1,3}|sd[a-z]{1,2}|hd[a-z]|nvmes?[0-9]+(?:n[1-9])?)|[A-Za-z]:\\?|\\\\\.\\PhysicalDrive[0-9]+)$",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
}
