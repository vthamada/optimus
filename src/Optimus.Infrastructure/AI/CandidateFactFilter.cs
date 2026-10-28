using System.Globalization;
using System.Text;
using Optimus.Domain;

namespace Optimus.Infrastructure.AI;

// This is a visible lexical candidate policy, not a semantic classifier. It never
// matches observed values, subjects, source names or inferred synonyms/causes.
internal sealed record CandidateFactFilter(string Strategy, IReadOnlyList<string> Terms,
    IReadOnlySet<(Guid EvidenceId, string FactKey)>? MatchingFacts, long ExcludedFacts)
{
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "qual", "quais", "que", "como", "onde", "quando", "porque", "por", "para", "com", "sem", "sobre", "entre",
        "mais", "menos", "foi", "foram", "sao", "ser", "esta", "estao", "esse", "essa", "esses", "essas", "este",
        "estes", "estas", "meu", "minha", "meus", "minhas", "voce", "voces", "nos", "nossa", "nosso", "uma", "umas",
        "uns", "dos", "das", "pelo", "pela", "pelos", "pelas", "tem", "ter", "teve", "quero", "quer", "preciso",
        "fato", "fatos", "dado", "dados", "valor", "valores", "sinal", "sinais", "coleta", "coletado", "coletados",
        "observado", "observados", "observada", "observadas", "exiba", "mostre", "mostrar", "resuma", "resumo",
        "explique", "explicar", "selecionar", "selecione", "relevante", "relevantes", "diagnostico", "computador",
        "sistema", "resultado", "resultados", "atual", "atuais", "informacao", "informacoes", "entender",
        "what", "which", "how", "when", "where", "why", "the", "and", "for", "with", "without", "about", "from",
        "these", "those", "this", "that", "are", "was", "were", "have", "has", "had", "show", "select", "please",
        "fact", "facts", "value", "values", "data", "result", "results", "computer", "system", "observed", "summary",
        "summarize", "explain"
    };

    public bool Includes(Guid evidenceId, string factKey) => MatchingFacts is null || MatchingFacts.Contains((evidenceId, factKey));

    public static CandidateFactFilter Create(DiagnosticSession session, string question)
    {
        var terms = Words(question).Where(word => !StopWords.Contains(word)).Distinct(StringComparer.Ordinal).ToArray();
        if (terms.Length == 0) return new("broad", terms, null, 0);
        var matching = new HashSet<(Guid, string)>();
        long eligible = 0;
        foreach (var evidence in session.Evidence)
        {
            foreach (var fact in evidence.Facts)
            {
                if (fact.Detailed) continue;
                eligible++;
                var words = new HashSet<string>(Words(fact.Key), StringComparer.Ordinal);
                words.UnionWith(Words(SplitCase(fact.Key)));
                words.UnionWith(Words(fact.Label));
                words.UnionWith(Words(SplitCase(fact.Label)));
                if (terms.Any(words.Contains)) matching.Add((evidence.Id, fact.Key));
            }
        }
        return matching.Count == 0 ? new("broad", terms, null, 0)
            : new("lexical", terms, matching, eligible - matching.Count);
    }

    private static IEnumerable<string> Words(string text)
    {
        var token = new StringBuilder();
        foreach (var character in text.Normalize(NormalizationForm.FormD))
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(character);
            if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark) continue;
            if (char.IsLetterOrDigit(character)) { token.Append(char.ToLowerInvariant(character)); continue; }
            if (token.Length >= 3) yield return token.ToString();
            token.Clear();
        }
        if (token.Length >= 3) yield return token.ToString();
    }

    private static string SplitCase(string text)
    {
        var result = new StringBuilder(text.Length);
        for (int index = 0; index < text.Length; index++)
        {
            var current = text[index];
            if (index > 0 && char.IsUpper(current)
                && (char.IsLower(text[index - 1]) || char.IsDigit(text[index - 1])
                    || (char.IsUpper(text[index - 1]) && index + 1 < text.Length && char.IsLower(text[index + 1]))))
                result.Append(' ');
            result.Append(current);
        }
        return result.ToString();
    }
}
