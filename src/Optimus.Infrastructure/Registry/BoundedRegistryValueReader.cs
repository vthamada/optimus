using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Optimus.Domain;

[assembly: InternalsVisibleTo("Optimus.Tests")]

namespace Optimus.Infrastructure.RegistryData;

internal sealed record BoundedRegistryValue(
    bool? Exists, uint? Kind, uint? ReportedBytes, byte[]? RawBytes, CollectionIssue? Issue,
    int NativeErrorCode, uint? ProbedKind = null, uint? ProbedBytes = null,
    bool ReadAttempted = false, bool ChangedDuringRead = false);

/// <summary>Owns size probing and a single bounded RAW read. It never expands strings or retries a changing value.</summary>
internal static class BoundedRegistryValueReader
{
    internal const int MaximumValueBytes = 16_384;
    internal delegate int QueryValue(SafeRegistryHandle key, string name, out uint kind, byte[]? bytes, ref uint size);

    internal static BoundedRegistryValue Read(SafeRegistryHandle key, string name, QueryValue? query = null)
    {
        query ??= NativeQuery;
        uint requested = 0;
        int result = query(key, name, out uint kind, null, ref requested);
        if (result == 2) return new(false, null, null, null, null, result);
        if (result is not (0 or 234)) return new(null, null, null, null, Failure(result, "Sondagem do valor"), result);
        uint probedKind = kind, probedBytes = requested;
        if (requested > MaximumValueBytes)
            return new(true, kind, requested, null,
                new(IssueCode.LimitReached, $"O valor excede {MaximumValueBytes} bytes e não foi alocado nem interpretado."), result, kind, requested);

        var data = new byte[checked((int)requested)];
        uint actual = requested;
        result = query(key, name, out kind, data, ref actual);
        if (result == 2)
            return new(false, null, null, null,
                new(IssueCode.ReadFailed, "O valor observado na sondagem não estava presente na leitura seguinte; nenhum dado indefinido do buffer foi retido."),
                result, probedKind, probedBytes, true, true);
        if (result == 234)
            return new(true, kind, actual, null,
                new(actual > MaximumValueBytes ? IssueCode.LimitReached : IssueCode.ReadFailed,
                    "O tamanho do valor mudou durante a leitura limitada (ERROR_MORE_DATA); o buffer é indefinido e não foi interpretado, retido ou repetido."),
                result, probedKind, probedBytes, true, true);
        if (result != 0)
            return new(null, null, null, null, Failure(result, "Leitura do valor"), result, probedKind, probedBytes, true);
        if (actual > data.Length || actual != probedBytes || kind != probedKind)
            return new(true, kind, actual, null,
                new(actual > MaximumValueBytes ? IssueCode.LimitReached : IssueCode.ReadFailed,
                    "O tipo ou o tamanho do valor mudou entre sondagem e leitura; nenhum snapshot consistente foi interpretado ou repetido."),
                result, probedKind, probedBytes, true, true);
        return new(true, kind, actual, data, null, result, probedKind, probedBytes, true);
    }

    private static CollectionIssue Failure(int result, string stage) => new(result == 5 ? IssueCode.PermissionDenied : IssueCode.ReadFailed,
        stage + ": " + new Win32Exception(result).Message + $" (código Win32 {result}).");
    private static int NativeQuery(SafeRegistryHandle key, string name, out uint kind, byte[]? data, ref uint size) =>
        RegQueryValueEx(key, name, IntPtr.Zero, out kind, data, ref size);
    [DllImport("advapi32.dll", EntryPoint = "RegQueryValueExW", CharSet = CharSet.Unicode)]
    private static extern int RegQueryValueEx(SafeRegistryHandle key, string name, IntPtr reserved, out uint kind, byte[]? data, ref uint size);
}
