namespace Optimus.Domain;

public static class DiagnosticRules
{
    public static IReadOnlyList<DiagnosticFinding> Evaluate(IReadOnlyList<DiagnosticEvidence> evidence)
    {
        var findings = new List<DiagnosticFinding>();
        foreach (var item in evidence)
        {
            if (item.Status != EvidenceStatus.Complete)
            {
                findings.Add(new($"coverage-{item.Id}", FindingSeverity.Information,
                    $"Cobertura limitada: {item.Title}",
                    "Parte dos dados não foi obtida. Isso não estabelece que o componente esteja saudável ou com defeito.",
                    "Consulte as limitações da coleta antes de concluir o diagnóstico.", [item.Id]));
            }

            // Only the volume collector owns these facts and the meaning of their units.
            if (item.CollectorId == "windows.volumes")
            {
                var total = Number(item, "totalBytes", "bytes");
                var available = Number(item, "availableBytes", "bytes");
                if (total > 0 && available >= 0 && available < total * 0.10m)
                {
                    findings.Add(new($"disk-space-{item.Id}", FindingSeverity.Warning,
                        $"Pouco espaço disponível em {item.Subject}",
                        "O volume tem menos de 10% de espaço disponível ao usuário nesta medição. Esse é um limite de triagem do opTImus.",
                        "Revise o uso de armazenamento e preserve seus arquivos antes de excluir qualquer item.", [item.Id]));
                }
            }

            if (item.CollectorId == "windows.system")
            {
                var total = Number(item, "physicalMemoryBytes", "bytes");
                var available = Number(item, "availableMemoryBytes", "bytes");
                if (total > 0 && available >= 0 && available < total * 0.10m && available < 1_073_741_824m)
                {
                    findings.Add(new($"memory-pressure-{item.Id}", FindingSeverity.Warning,
                        "Pouca memória física disponível nesta amostra",
                        "Menos de 10% e menos de 1 GiB de memória física estavam disponíveis no momento da coleta. Uma amostra não estabelece a causa de travamentos.",
                        "Observe o uso de memória durante o problema e compare outra medição sob carga equivalente.", [item.Id]));
                }
            }
        }

        return findings;
    }

    private static decimal? Number(DiagnosticEvidence evidence, string key, string unit) =>
        evidence.Facts.FirstOrDefault(fact => fact.Key == key && fact.Unit == unit)?.NumberValue;
}
