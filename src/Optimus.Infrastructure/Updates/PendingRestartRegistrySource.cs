using Microsoft.Win32;
using Optimus.Domain;
using Optimus.Infrastructure.RegistryData;

namespace Optimus.Infrastructure.Updates;

public enum PendingRestartIndicator { ComponentBasedServicing, WindowsUpdate, FileRenameOperations }
public enum PendingRestartRegistryReadState { KeyPresent, KeyAbsent, ValuePresent, ValueAbsent, AccessDenied, ValueTooLarge, ChangedDuringRead, ReadFailed }

public sealed record PendingRestartRegistryLocation(string Prefix, string Label, string KeyPath, string? ValueName = null)
{
    public RegistryHive Hive => RegistryHive.LocalMachine;
    public RegistryView View => RegistryView.Registry64;
    public string SourcePath => @"HKEY_LOCAL_MACHINE\" + KeyPath + (ValueName is null ? "" : "\\" + ValueName);
}

public sealed record PendingRestartRegistryObservation(
    DateTimeOffset ObservedAt, PendingRestartRegistryReadState State, bool? KeyExists, bool? ValueExists = null,
    uint? Kind = null, uint? ReportedBytes = null, byte[]? RawBytes = null, CollectionIssue? Issue = null,
    int? NativeErrorCode = null, uint? ProbedKind = null, uint? ProbedBytes = null,
    bool ReadAttempted = false, bool ChangedDuringRead = false);

/// <summary>Reads three fixed indicators in HKLM's 64-bit view; never enumerates or changes unrelated values.</summary>
public static class PendingRestartRegistrySource
{
    public static PendingRestartRegistryLocation Describe(PendingRestartIndicator indicator) => indicator switch
    {
        PendingRestartIndicator.ComponentBasedServicing => new("cbs", "Chave convencional CBS RebootPending",
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending"),
        PendingRestartIndicator.WindowsUpdate => new("windowsUpdate", "Chave convencional Windows Update RebootRequired",
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired"),
        PendingRestartIndicator.FileRenameOperations => new("fileRename", "Valor PendingFileRenameOperations",
            @"SYSTEM\CurrentControlSet\Control\Session Manager", "PendingFileRenameOperations"),
        _ => throw new ArgumentOutOfRangeException(nameof(indicator), "Indicador fora do catálogo fixo.")
    };

    public static PendingRestartRegistryObservation Read(PendingRestartIndicator indicator, CancellationToken token, TimeProvider? clock = null)
    {
        var location = Describe(indicator);
        var time = clock ?? TimeProvider.System;
        token.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitOperatingSystem)
            return new(time.GetUtcNow(), PendingRestartRegistryReadState.ReadFailed, null,
                Issue: new(IssueCode.NotSupported, "Os indicadores selecionados requerem Windows com visão de Registro de 64 bits."));
        try
        {
            using var hive = RegistryKey.OpenBaseKey(location.Hive, location.View);
            using var key = hive.OpenSubKey(location.KeyPath, writable: false);
            token.ThrowIfCancellationRequested();
            if (key is null) return new(time.GetUtcNow(), PendingRestartRegistryReadState.KeyAbsent, false);
            if (location.ValueName is null) return new(time.GetUtcNow(), PendingRestartRegistryReadState.KeyPresent, true);
            var value = BoundedRegistryValueReader.Read(key.Handle, location.ValueName);
            token.ThrowIfCancellationRequested();
            var state = value.ChangedDuringRead ? PendingRestartRegistryReadState.ChangedDuringRead
                : value.Issue?.Code == IssueCode.PermissionDenied ? PendingRestartRegistryReadState.AccessDenied
                : value.Issue?.Code == IssueCode.LimitReached ? PendingRestartRegistryReadState.ValueTooLarge
                : value.Issue is not null ? PendingRestartRegistryReadState.ReadFailed
                : value.Exists == false ? PendingRestartRegistryReadState.ValueAbsent : PendingRestartRegistryReadState.ValuePresent;
            return new(time.GetUtcNow(), state, true, value.Exists, value.Kind, value.ReportedBytes, value.RawBytes,
                value.Issue, value.NativeErrorCode, value.ProbedKind, value.ProbedBytes, value.ReadAttempted, value.ChangedDuringRead);
        }
        catch (Exception error) when (Expected(error))
        {
            bool denied = error is UnauthorizedAccessException or System.Security.SecurityException;
            return new(time.GetUtcNow(), denied ? PendingRestartRegistryReadState.AccessDenied : PendingRestartRegistryReadState.ReadFailed, null,
                Issue: new(denied ? IssueCode.PermissionDenied : IssueCode.ReadFailed, error.Message));
        }
    }

    internal static bool Expected(Exception error) => error is IOException or InvalidDataException or UnauthorizedAccessException
        or System.Security.SecurityException or NotSupportedException or ArgumentException;
}
