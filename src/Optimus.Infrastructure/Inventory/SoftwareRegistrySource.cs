using System.Buffers.Binary;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using Optimus.Domain;
using Optimus.Infrastructure.RegistryData;

namespace Optimus.Infrastructure.Inventory;

public sealed record SoftwareRegistryLocation(RegistryHive Hive, RegistryView View, string KeyPath, bool ValuesAreEntries)
{
    public string SourcePath => $"{Hive}\\{KeyPath}";
}

public abstract record SoftwareRegistryObservation;
public sealed record SoftwareRegistryScopeObservation(bool Exists) : SoftwareRegistryObservation;
public sealed record SoftwareRegistryEntryObservation(string EntryName, IReadOnlyList<SoftwareRegistryValue> Values,
    CollectionIssue? Issue = null) : SoftwareRegistryObservation;
public sealed record SoftwareRegistryValue(string Name, uint Kind, uint ReportedBytes, string? Text = null,
    decimal? Number = null, string? RawBase64 = null, CollectionIssue? Issue = null, bool? Exists = true,
    uint? ProbedKind = null, uint? ProbedBytes = null, int? NativeErrorCode = null,
    bool ReadAttempted = false, bool ChangedDuringRead = false);

/// <summary>Owns registry enumeration and typed, non-expanding value reads. Never opens keys for writing.</summary>
public static class SoftwareRegistrySource
{
    public const int MaximumValueBytes = BoundedRegistryValueReader.MaximumValueBytes;
    private const int MaximumSubkeys = 4096;
    private const int MaximumValues = 512;
    internal static readonly string[] ProgramFields = ["DisplayName", "DisplayVersion", "Publisher", "InstallDate", "InstallLocation"];
    private static readonly UnicodeEncoding Utf16 = new(false, false, true);

    public static IEnumerable<SoftwareRegistryObservation> Read(SoftwareRegistryLocation location, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var hive = RegistryKey.OpenBaseKey(location.Hive, location.View);
        using var key = hive.OpenSubKey(location.KeyPath, writable: false);
        yield return new SoftwareRegistryScopeObservation(key is not null);
        if (key is null) yield break;
        if (location.ValuesAreEntries)
        {
            if (key.ValueCount > MaximumValues) throw new RegistryCapacityException("A chave contém mais de 512 valores; seus nomes não foram materializados.");
            foreach (var name in key.GetValueNames().Order(StringComparer.Ordinal))
            {
                token.ThrowIfCancellationRequested();
                yield return new SoftwareRegistryEntryObservation(name, [ReadValue(key.Handle, name)]);
            }
        }
        else
        {
            if (key.SubKeyCount > MaximumSubkeys) throw new RegistryCapacityException("A chave contém mais de 4096 subchaves; seus nomes não foram materializados.");
            foreach (var name in key.GetSubKeyNames().Order(StringComparer.Ordinal))
            {
                token.ThrowIfCancellationRequested();
                SoftwareRegistryEntryObservation observation;
                try
                {
                    using var entry = key.OpenSubKey(name, writable: false);
                    observation = entry is null
                        ? new(name, [], new(IssueCode.ReadFailed, "A subchave enumerada não estava mais disponível no momento da leitura."))
                        : new(name, ProgramFields.Select(field => ReadValue(entry.Handle, field)).ToArray());
                }
                catch (Exception error) when (Expected(error))
                {
                    observation = new(name, [], new(error is UnauthorizedAccessException or System.Security.SecurityException
                        ? IssueCode.PermissionDenied : IssueCode.ReadFailed, error.Message));
                }
                yield return observation;
            }
        }
    }

    private static SoftwareRegistryValue ReadValue(SafeRegistryHandle key, string name)
    {
        var value = BoundedRegistryValueReader.Read(key, name);
        if (value.Issue is null && value.Exists == true && value.RawBytes is { } data && value.Kind is { } kind)
            return Decode(name, kind, data);
        if (value.Issue is null && value.Exists == false) return new(name, 0, 0, Exists: false);
        return new(name, value.Kind ?? value.ProbedKind ?? 0, value.ReportedBytes ?? value.ProbedBytes ?? 0,
            Issue: value.Issue, Exists: value.Exists, ProbedKind: value.ProbedKind, ProbedBytes: value.ProbedBytes,
            NativeErrorCode: value.NativeErrorCode, ReadAttempted: value.ReadAttempted, ChangedDuringRead: value.ChangedDuringRead);
    }

    public static SoftwareRegistryValue Decode(string name, uint kind, byte[] data)
    {
        if (data.Length > MaximumValueBytes)
            return new(name, kind, (uint)data.Length, Issue: new(IssueCode.LimitReached, "Valor acima do limite; conteúdo não interpretado."));
        try
        {
            if (kind is 1 or 2)
            {
                if ((data.Length & 1) != 0) throw new DecoderFallbackException("REG_SZ/REG_EXPAND_SZ com tamanho UTF-16 ímpar.");
                var text = Utf16.GetString(data);
                if (text.EndsWith('\0')) text = text[..^1]; // Remove only the format terminator; embedded NULs remain source data.
                return new(name, kind, (uint)data.Length, Text: text);
            }
            decimal? number = kind switch
            {
                4 when data.Length == 4 => BinaryPrimitives.ReadUInt32LittleEndian(data),
                11 when data.Length == 8 => BinaryPrimitives.ReadUInt64LittleEndian(data),
                _ => null
            };
            return new(name, kind, (uint)data.Length, Number: number, RawBase64: Convert.ToBase64String(data),
                Issue: new(IssueCode.ReadFailed, "Tipo de valor não textual; não foi convertido em nome, versão ou comando."));
        }
        catch (DecoderFallbackException error)
        {
            return new(name, kind, (uint)data.Length, RawBase64: Convert.ToBase64String(data), Issue: new(IssueCode.ReadFailed, error.Message));
        }
    }

    internal static bool Expected(Exception error) => error is IOException or InvalidDataException or UnauthorizedAccessException
        or System.Security.SecurityException or ArgumentException or NotSupportedException;
    internal sealed class RegistryCapacityException(string message) : IOException(message);
}

internal static class SoftwareRegistryEvidence
{
    internal const int MaximumEntries = 500;
    internal const long MaximumEstimatedJsonBytes = 1024 * 1024;

    internal static IReadOnlyList<DiagnosticEvidence> Collect(string id, string title, bool startup,
        Func<SoftwareRegistryLocation, CancellationToken, IEnumerable<SoftwareRegistryObservation>> source,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var facts = new List<DiagnosticFact>();
        var issues = new List<CollectionIssue>();
        var entries = 0;
        var retainedEntries = 0;
        var scopes = 0;
        var reached = false;
        long budget = MaximumEstimatedJsonBytes - 16_384;
        bool stop = false;
        foreach (var location in Locations(startup))
        {
            if (stop) break;
            token.ThrowIfCancellationRequested();
            bool sawScope = false;
            bool scopeExists = false;
            try
            {
                foreach (var observation in source(location, token))
                {
                    token.ThrowIfCancellationRequested();
                    if (observation is SoftwareRegistryScopeObservation scope)
                    {
                        if (sawScope) throw new InvalidDataException("A fonte repetiu a observação de uma chave.");
                        sawScope = true;
                        scopeExists = scope.Exists;
                        scopes++;
                        facts.Add(new($"scope.{scopes}.path", "Chave consultada", location.SourcePath));
                        facts.Add(new($"scope.{scopes}.view", "Visão do Registro", location.View.ToString()));
                        facts.Add(new($"scope.{scopes}.exists", "Chave encontrada", scope.Exists ? "true" : "false"));
                        continue;
                    }
                    if (!sawScope || !scopeExists || observation is not SoftwareRegistryEntryObservation entry)
                        throw new InvalidDataException("A fonte não confirmou a chave antes de entregar uma entrada.");
                    entries++;
                    if (retainedEntries == MaximumEntries) { reached = true; stop = true; break; }
                    ValidateEntry(entry, startup);
                    var prefix = $"entry.{entries}";
                    var candidate = BuildEntry(prefix, location, entry, startup);
                    var candidateIssues = new List<CollectionIssue>();
                    var context = $"{location.SourcePath} ({location.View}), entrada '{entry.EntryName}'";
                    if (entry.Issue is { } entryIssue) candidateIssues.Add(new(entryIssue.Code, $"{context}: {entryIssue.Message}"));
                    foreach (var value in entry.Values)
                        if (value.Issue is { } valueIssue) candidateIssues.Add(new(valueIssue.Code, $"{context}, valor '{value.Name}': {valueIssue.Message}"));
                    var cost = candidate.Sum(fact => 128L + 6L * (fact.Key.Length + fact.Label.Length + (fact.TextValue?.Length ?? 0)));
                    cost += candidateIssues.Sum(issue => 128L + 6L * issue.Message.Length);
                    if (cost > budget) { reached = true; stop = true; break; }
                    budget -= cost;
                    facts.AddRange(candidate);
                    retainedEntries++;
                    issues.AddRange(candidateIssues);
                    reached |= candidateIssues.Any(issue => issue.Code == IssueCode.LimitReached);
                }
                if (!sawScope) throw new InvalidDataException("A fonte encerrou sem informar a presença ou ausência da chave.");
            }
            catch (SoftwareRegistrySource.RegistryCapacityException error)
            {
                reached = true;
                issues.Add(new(IssueCode.LimitReached, $"{location.SourcePath} ({location.View}): {error.Message}"));
            }
            catch (Exception error) when (SoftwareRegistrySource.Expected(error))
            {
                issues.Add(new(error is UnauthorizedAccessException or System.Security.SecurityException ? IssueCode.PermissionDenied : IssueCode.ReadFailed,
                    $"{location.SourcePath} ({location.View}): {error.Message}"));
            }
        }
        if (reached) issues.Add(new(IssueCode.LimitReached, "Limites de recurso atingidos: até 500 entradas retidas, orçamento conservador de 1 MiB por coletor e 16 KiB por valor. Dados omitidos não foram avaliados."));
        facts.Insert(0, new("examinedEntries", "Entradas examinadas", NumberValue: entries));
        facts.Insert(1, new("examinedScopes", "Chaves consultadas", NumberValue: scopes));
        facts.Insert(2, new("retainedEntries", "Entradas preservadas no relatório", NumberValue: retainedEntries));
        var scopeText = startup
            ? "Somente Run/RunOnce, HKCU/HKLM, visões 32/64. Presença do registro não comprova habilitação ou execução; pastas Startup, tarefas e serviços não foram consultados. Comandos são dados originais, não executados."
            : "Somente registros Uninstall, HKCU/HKLM, visões 32/64. Registros não comprovam arquivos presentes ou abrangência de apps portáteis/Store. Visões são identidades distintas; nomes iguais não foram deduplicados.";
        return [new(Guid.NewGuid(), id, title, "Registro do Windows", DateTimeOffset.UtcNow,
            issues.Count == 0 ? EvidenceStatus.Complete : scopes > 0 ? EvidenceStatus.Partial : EvidenceStatus.Unavailable,
            new(scopeText, entries, MaximumEntries, reached), facts, issues)];
    }

    private static void ValidateEntry(SoftwareRegistryEntryObservation entry, bool startup)
    {
        if (entry.EntryName is null || entry.Values is null || (!startup && entry.EntryName.Length == 0))
            throw new InvalidDataException("Entrada do Registro sem identidade válida.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in entry.Values)
            if (value is null || value.Name is null || !names.Add(value.Name)
                || (!startup && !SoftwareRegistrySource.ProgramFields.Contains(value.Name, StringComparer.Ordinal))
                || (startup && value.Name != entry.EntryName))
                throw new InvalidDataException("Entrada do Registro com valores duplicados ou fora do contrato da fonte.");
        if (startup && entry.Values.Count > 1) throw new InvalidDataException("Registro de inicialização com mais de um comando para a mesma identidade.");
    }

    private static IReadOnlyList<DiagnosticFact> BuildEntry(string prefix, SoftwareRegistryLocation location,
        SoftwareRegistryEntryObservation entry, bool startup)
    {
        var facts = new List<DiagnosticFact>
        {
            new($"{prefix}.source", "Origem da entrada", location.SourcePath + (startup ? "" : "\\" + entry.EntryName)),
            new($"{prefix}.view", "Visão do Registro", location.View.ToString()),
            new($"{prefix}.name", startup ? "Nome do valor de inicialização" : "Nome da subchave", entry.EntryName)
        };
        foreach (var value in entry.Values)
        {
            var field = startup ? "command" : value.Name;
            var label = startup ? "Comando registrado (não executado)" : value.Name switch
            {
                "DisplayName" => "Nome registrado do programa",
                "DisplayVersion" => "Versão registrada",
                "Publisher" => "Editor registrado",
                "InstallDate" => "Data registrada (original)",
                "InstallLocation" => "Local registrado",
                _ => value.Name
            };
            facts.Add(new($"{prefix}.{field}", label, value.Text));
            facts.Add(new($"{prefix}.{field}.exists", "Valor encontrado", value.Exists is { } present ? present ? "true" : "false" : null, Detailed: true));
            if (value.NativeErrorCode is not null)
            {
                facts.Add(new($"{prefix}.{field}.nativeReturnCode", "Código Win32 original da última consulta", NumberValue: value.NativeErrorCode, Detailed: true));
                facts.Add(new($"{prefix}.{field}.probedKind", "Tipo observado na sondagem anterior à leitura", NumberValue: value.ProbedKind, Detailed: true));
                facts.Add(new($"{prefix}.{field}.probedBytes", "Bytes observados na sondagem anterior à leitura", NumberValue: value.ProbedBytes, Unit: "bytes", Detailed: true));
                facts.Add(new($"{prefix}.{field}.readAttempted", "Leitura limitada após sondagem solicitada", value.ReadAttempted ? "true" : "false", Detailed: true));
                facts.Add(new($"{prefix}.{field}.changedDuringRead", "Alteração de presença, tipo ou tamanho observada", value.ChangedDuringRead ? "true" : "false", Detailed: true));
            }
            if (value.Exists != true) continue;
            facts.Add(new($"{prefix}.{field}.kind", "Tipo original do Registro", NumberValue: value.Kind, Detailed: true));
            facts.Add(new($"{prefix}.{field}.bytes", "Tamanho original do valor", NumberValue: value.ReportedBytes, Unit: "bytes", Detailed: true));
            if (value.Number is { } number) facts.Add(new($"{prefix}.{field}.number", "Valor numérico original não textual", NumberValue: number, Detailed: true));
            if (value.RawBase64 is { } raw) facts.Add(new($"{prefix}.{field}.raw", "Bytes originais (base64)", raw, Detailed: true));
        }
        return facts;
    }

    private static IEnumerable<SoftwareRegistryLocation> Locations(bool startup)
    {
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                foreach (var path in startup
                    ? new[] { @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce" }
                    : new[] { @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall" })
                    yield return new(hive, view, path, startup);
    }
}
