using System.Net;
using System.Text;
using System.Text.Json;
using Optimus.Application;
using Optimus.Domain;
using Optimus.Infrastructure.Files;

namespace Optimus.Infrastructure;

public sealed class ReportExporter : IReportExporter
{
    public Task<Result<string>> ExportAsync(DiagnosticSession session, string path, ReportFormat format, CancellationToken cancellationToken) =>
        Task.Run(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Enum.IsDefined(format) || !WithinRenderingBudget(session, format))
                return Result<string>.Failure(ErrorCode.InvalidData, "A sessão ou seu orçamento de renderização não cabe no limite de 32 MiB. As evidências originais foram preservadas.");
            if (!SessionValidation.IsValid(session)) return Result<string>.Failure(ErrorCode.InvalidData, "Sessão de relatório inválida.");
            var content = format == ReportFormat.Json
                ? JsonSerializer.Serialize(session, DiagnosticJson.Options)
                : RenderHtml(session);
            return await AtomicNewFileWriter.PublishUtf8Async(path, content, cancellationToken).ConfigureAwait(false);
        }, CancellationToken.None);

    private static bool WithinRenderingBudget(DiagnosticSession? session, ReportFormat format)
    {
        if (session?.Evidence is null || session.Findings is null) return false;
        long remaining = AtomicNewFileWriter.MaximumSupportedUtf8Bytes - 16384L;
        int factor = format == ReportFormat.Json ? 6 : 12;
        bool Add(string? text) { remaining -= (long)(text?.Length ?? 0) * factor; return remaining >= 0; }
        foreach (var finding in session.Findings)
        {
            remaining -= 1024;
            if (finding is null || finding.EvidenceIds is null) return false;
            remaining -= finding.EvidenceIds.Count * 48L;
            if (!Add(finding.Id) || !Add(finding.Title) || !Add(finding.Explanation) || !Add(finding.Recommendation)) return false;
        }
        foreach (var evidence in session.Evidence)
        {
            remaining -= 4096;
            if (evidence?.Facts is null || evidence.Issues is null || evidence.Coverage is null) return false;
            remaining -= evidence.Facts.Count * 512L + evidence.Issues.Count * 512L;
            if (!Add(evidence.CollectorId) || !Add(evidence.Title) || !Add(evidence.Subject) || !Add(evidence.Coverage.Scope)) return false;
            foreach (var fact in evidence.Facts)
                if (fact is null || !Add(fact.Key) || !Add(fact.Label) || !Add(fact.TextValue) || !Add(fact.Unit)) return false;
            foreach (var issue in evidence.Issues) if (issue is null || !Add(issue.Message)) return false;
        }
        return remaining >= 0;
    }
    private static string RenderHtml(DiagnosticSession session)
    {
        static string E(string? text) => WebUtility.HtmlEncode(text ?? "Não disponível");
        var html = new StringBuilder("<!doctype html><html lang=\"pt-BR\"><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width\"><title>opTImus · Diagnóstico</title><style>body{font:16px/1.6 system-ui;max-width:1050px;margin:40px auto;padding:0 24px;color:#152232;background:#f5f7fb}section{background:white;padding:24px;margin:20px 0;border:1px solid #dce3ec;border-radius:12px}h1{font-size:38px}h2{font-size:23px}td,th{text-align:left;vertical-align:top;padding:8px;border-bottom:1px solid #e5e9ef}table{width:100%;border-collapse:collapse}pre{white-space:pre-wrap;overflow-wrap:anywhere}small{color:#53657b}</style><h1>opTImus</h1><p>Diagnóstico consultivo · protótipo 0.9</p>");
        html.Append($"<p>{E(session.StatusText)} · {session.StartedAt:O}</p><small>Sessão {session.Id}</small><p>As conclusões se limitam aos dados coletados. Este diagnóstico não executou limpeza, reparos, antivírus ou testes físicos.</p><section><h2>Achados</h2>");
        if (session.Findings.Count == 0) html.Append("<p>Nenhum alerta pelas regras implementadas. Isso não comprova a saúde completa do computador.</p>");
        foreach (var finding in session.Findings)
        {
            html.Append($"<h3>{E(finding.Title)}</h3><p>{E(finding.Explanation)}</p><p>{E(finding.Recommendation)}</p><small>Evidências: {E(string.Join(", ", finding.EvidenceIds))}</small>");
        }
        html.Append("</section>");
        foreach (var evidence in session.Evidence)
        {
            html.Append($"<section><h2>{E(evidence.Title)}</h2><p>{E(evidence.StatusText)}</p><p>{E(evidence.Coverage.Scope)}</p><small>ID {evidence.Id} · {E(evidence.CollectorId)} · {evidence.CapturedAt:O}</small><table><tr><th>Fato observado</th><th>Valor</th></tr>");
            foreach (var fact in evidence.SummaryFacts) html.Append($"<tr><td>{E(fact.Label)}</td><td>{E(fact.DisplayValue)}</td></tr>");
            html.Append("</table>");
            foreach (var fact in evidence.Facts.Where(fact => fact.Detailed)) html.Append($"<details><summary>{E(fact.Label)}</summary><pre>{E(fact.TextValue)}</pre></details>");
            foreach (var issue in evidence.Issues) html.Append($"<p><strong>Limitação:</strong> {E(issue.Message)}</p>");
            html.Append("</section>");
        }
        return html.Append("</html>").ToString();
    }
}
