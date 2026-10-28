using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Optimus.Application;
using Optimus.Domain;
using Optimus.Infrastructure.Files;

namespace Optimus.Infrastructure.Storage;

public sealed record FolderSpaceLimits
{
    public int MaximumEntries { get; init; } = 20_000;
    public int MaximumDepth { get; init; } = 32;
    public TimeSpan MaximumDuration { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>Reads metadata for one explicit local folder. Never reads, hashes or deletes file contents.</summary>
public sealed class FolderSpaceCollector(string selectedPath, TimeProvider? clock = null, FolderSpaceLimits? limits = null) : IDiagnosticCollector
{
    public string Id => MaintenanceCollectors.FolderSpace;
    public string DisplayName => "Espaço lógico da pasta selecionada";

    public Task<IReadOnlyList<DiagnosticEvidence>> CollectAsync(CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<DiagnosticEvidence>>(() => new Scan(selectedPath, clock ?? TimeProvider.System,
            limits ?? new FolderSpaceLimits(), cancellationToken).Run(), cancellationToken);

    private sealed class Scan(string requested, TimeProvider clock, FolderSpaceLimits limits, CancellationToken token)
    {
        private const uint Reparse = 0x400;
        private const uint OfflineOrRecall = 0x1000 | 0x400000;
        private const int TopCount = 20;
        private const int MaximumIssueDetails = 32;
        private readonly List<CollectionIssue> _issues = [];
        private readonly List<FileSnapshot> _top = [];
        private long _issueBudget = 128 * 1024;
        private int _issueCount, _issueDetailsOmitted;
        private int _processed, _enumerated, _files, _directoryEntries, _directoriesBound;
        private int _skippedReparse, _skippedOffline, _skippedDepth, _metadataFailures, _enumerationFailures, _hardLinkedEntries;
        private int _knownOmitted;
        private decimal _logicalBytes;
        private string? _resolved;
        private bool _rootBound, _stop, _entryLimit, _timeLimit;
        private long _started;

        public IReadOnlyList<DiagnosticEvidence> Run()
        {
            token.ThrowIfCancellationRequested();
            var ancestors = new List<SafeFileHandle>();
            try
            {
                if (limits.MaximumEntries is < 1 or > 20_000 || limits.MaximumDepth is < 1 or > 32
                    || limits.MaximumDuration <= TimeSpan.Zero || limits.MaximumDuration > TimeSpan.FromSeconds(30))
                    throw new InvalidDataException("Limites devem ser positivos e não exceder 20.000 entradas, 32 níveis e 30 segundos.");
                if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("A consulta vinculada de metadados requer Windows.");
                if (requested is null || requested.Length > 32767) throw new InvalidDataException("O caminho selecionado deve caber no limite de 32.767 caracteres do caminho Windows.");
                _started = clock.GetTimestamp();
                _resolved = LocalPathGuard.LocalPath(requested);
                var drive = Path.GetPathRoot(_resolved)!;
                var components = _resolved[drive.Length..].Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
                if (components.Length + 1 > 32) throw new InvalidDataException("A vinculação da raiz selecionada está limitada a 32 componentes de diretório.");
                var current = drive;
                if (Continue()) ancestors.Add(OpenDirectory(current));
                foreach (var component in components)
                {
                    if (!Continue()) break;
                    current = Path.Combine(current, component);
                    ancestors.Add(OpenDirectory(current));
                }
                if (ancestors.Count == components.Length + 1 && !_stop)
                {
                    _rootBound = true;
                    _directoriesBound = 1;
                    Visit(_resolved, 1);
                }
            }
            catch (Exception error) when (Expected(error)) { AddIssue(Code(error), $"Raiz selecionada: {error.Message}"); }
            finally { for (int index = ancestors.Count - 1; index >= 0; index--) ancestors[index].Dispose(); }
            token.ThrowIfCancellationRequested();
            return [Build()];
        }

        private bool Continue()
        {
            token.ThrowIfCancellationRequested();
            if (_stop) return false;
            var elapsed = clock.GetElapsedTime(_started, clock.GetTimestamp());
            if (elapsed < TimeSpan.Zero)
            {
                _stop = true;
                AddIssue(IssueCode.ReadFailed, "O relógio de duração retornou intervalo negativo; a cobertura restante é desconhecida.");
            }
            else if (elapsed >= limits.MaximumDuration)
            {
                _stop = _timeLimit = true;
                AddIssue(IssueCode.LimitReached, "Prazo cooperativo atingido. Metadados ainda não examinados não foram tratados como zero.");
            }
            return !_stop;
        }

        private void Visit(string directory, int depth)
        {
            try
            {
                var options = new EnumerationOptions
                {
                    RecurseSubdirectories = false,
                    IgnoreInaccessible = false,
                    ReturnSpecialDirectories = false,
                    AttributesToSkip = 0,
                    MatchType = MatchType.Simple
                };
                using var entries = new DirectoryInfo(directory).EnumerateFileSystemInfos("*", options).GetEnumerator();
                while (Continue())
                {
                    if (!entries.MoveNext()) break;
                    _enumerated++;
                    if (_processed == limits.MaximumEntries)
                    {
                        _entryLimit = _stop = true;
                        _knownOmitted++;
                        AddIssue(IssueCode.LimitReached, "Há pelo menos uma entrada além do orçamento. Uma sonda adicional confirmou a omissão; o restante não foi contado.");
                        break;
                    }
                    _processed++;
                    Process(entries.Current, depth);
                }
            }
            catch (Exception error) when (Expected(error))
            {
                _enumerationFailures++;
                AddIssue(Code(error), $"Enumeração de {directory}: {error.Message}. O conteúdo restante deste ramo é desconhecido.");
            }
        }

        private void Process(FileSystemInfo entry, int depth)
        {
            try
            {
                var attributes = (uint)entry.Attributes;
                if (Skip(attributes, entry.FullName)) return;
                if ((attributes & (uint)FileAttributes.Directory) != 0)
                {
                    _directoryEntries++;
                    if (depth >= limits.MaximumDepth)
                    {
                        _skippedDepth++; _knownOmitted++;
                        AddIssue(IssueCode.LimitReached, $"Profundidade máxima: {entry.FullName}. O ramo não foi percorrido.");
                        return;
                    }
                    if (!Continue()) return;
                    using var child = OpenDirectory(entry.FullName);
                    _directoriesBound++;
                    Visit(entry.FullName, depth + 1);
                    return;
                }
                var path = LocalPathGuard.LocalPath(entry.FullName);
                using var handle = OpenMetadata(path, directory: false);
                var raw = Information(handle);
                if (Skip(raw.FileAttributes, path, fromEnumeration: false)) return;
                if ((raw.FileAttributes & (uint)FileAttributes.Directory) != 0)
                    throw new InvalidDataException("O tipo da entrada mudou durante a consulta; tamanho de diretório não foi contado como arquivo.");
                var identity = LocalPathGuard.Identity(handle);
                var length = ((ulong)raw.FileSizeHigh << 32) | raw.FileSizeLow;
                var fileTime = ((ulong)(uint)raw.LastWriteTime.dwHighDateTime << 32) | (uint)raw.LastWriteTime.dwLowDateTime;
                string? written = null;
                try { written = new DateTimeOffset(DateTime.FromFileTimeUtc(checked((long)fileTime))).ToString("O", CultureInfo.InvariantCulture); }
                catch (ArgumentOutOfRangeException) { AddIssue(IssueCode.NotSupported, $"LastWriteTime inválido em {path}; o valor FILETIME foi preservado nos maiores arquivos retidos."); }
                catch (OverflowException) { AddIssue(IssueCode.NotSupported, $"LastWriteTime fora do formato temporal em {path}; FILETIME foi preservado nos maiores arquivos retidos."); }
                _files++;
                _logicalBytes += length;
                if (raw.NumberOfLinks > 1) _hardLinkedEntries++;
                _top.Add(new(path, length, written, fileTime, raw.FileAttributes, raw.NumberOfLinks, identity.Volume, identity.FileId));
                _top.Sort((left, right) =>
                {
                    int byLength = right.Length.CompareTo(left.Length);
                    return byLength != 0 ? byLength : string.CompareOrdinal(left.Path, right.Path);
                });
                if (_top.Count > TopCount) _top.RemoveAt(TopCount);
            }
            catch (Exception error) when (Expected(error))
            {
                _metadataFailures++; _knownOmitted++;
                AddIssue(Code(error), $"Metadados de {entry.FullName}: {error.Message}. A entrada não foi somada.");
            }
        }

        private bool Skip(uint attributes, string path, bool fromEnumeration = true)
        {
            if ((attributes & Reparse) != 0)
            {
                _skippedReparse++; _knownOmitted++;
                AddIssue(IssueCode.NotSupported, $"Ponto de reanálise omitido sem seguir o destino: {path}. Inclui links, junctions, mounts e provedores de nuvem.");
                return true;
            }
            // 0x40000 is RECALL_ON_OPEN only in enumeration; handle metadata uses that bit for EA.
            if ((attributes & OfflineOrRecall) != 0 || fromEnumeration && (attributes & 0x40000) != 0)
            {
                _skippedOffline++; _knownOmitted++;
                AddIssue(IssueCode.NotSupported, $"Entrada offline ou com indicador de recall omitida sem leitura de conteúdo: {path}.");
                return true;
            }
            return false;
        }

        private void AddIssue(IssueCode code, string text)
        {
            _issueCount++;
            long cost = 512 + 6L * text.Length;
            if (_issues.Count < MaximumIssueDetails && cost <= _issueBudget)
            {
                _issueBudget -= cost;
                _issues.Add(new(code, text));
            }
            else _issueDetailsOmitted++;
        }

        private DiagnosticEvidence Build()
        {
            List<DiagnosticFact> facts =
            [
                new("requestedPath", "Caminho selecionado original", requested is { Length: <= 32767 } ? requested : null),
                new("requestedPathCharacters", "Caracteres do caminho recebido", NumberValue: requested?.Length),
                new("resolvedRoot", "Raiz local validada", _resolved),
                new("logicalBytesObserved", "Bytes lógicos dos arquivos examinados (não é espaço recuperável)", NumberValue: _rootBound ? _logicalBytes : null, Unit: "bytes"),
                new("filesObserved", "Arquivos com metadados de tamanho observados", NumberValue: _files),
                new("directoryEntriesObserved", "Entradas de diretório enumeradas", NumberValue: _directoryEntries),
                new("directoriesBound", "Diretórios da seleção vinculados durante a leitura", NumberValue: _directoriesBound),
                new("processedEntries", "Entradas processadas no orçamento", NumberValue: _processed),
                new("enumeratedEntries", "Entradas enumeradas, incluindo sonda de limite", NumberValue: _enumerated),
                new("knownOmittedEntries", "Entradas explicitamente omitidas (não conta descendentes desconhecidos)", NumberValue: _knownOmitted),
                new("unexaminedRemainderCount", "Quantidade restante não examinada", NumberValue: _issueCount == 0 ? 0 : null),
                new("skippedReparseEntries", "Pontos de reanálise omitidos", NumberValue: _skippedReparse),
                new("skippedOfflineEntries", "Entradas offline/recall omitidas", NumberValue: _skippedOffline),
                new("skippedDepthEntries", "Ramos omitidos por profundidade", NumberValue: _skippedDepth),
                new("metadataFailures", "Falhas ao consultar metadados", NumberValue: _metadataFailures),
                new("enumerationFailures", "Falhas de enumeração", NumberValue: _enumerationFailures),
                new("hardLinkedEntriesObserved", "Entradas cujo número de hardlinks é maior que um", NumberValue: _hardLinkedEntries),
                new("entryLimitReached", "Limite de entradas atingido", _entryLimit ? "true" : "false"),
                new("durationLimitReached", "Prazo cooperativo atingido", _timeLimit ? "true" : "false"),
                new("maximumEntries", "Orçamento de entradas processadas", NumberValue: limits.MaximumEntries),
                new("maximumDepth", "Orçamento de níveis, incluindo a pasta selecionada", NumberValue: limits.MaximumDepth),
                new("maximumSeconds", "Prazo cooperativo em segundos", NumberValue: (decimal)limits.MaximumDuration.TotalSeconds, Unit: "seconds"),
                new("contentReadRequested", "Leitura ou hash de conteúdo solicitado", "false"),
                new("physicalSpaceMeasured", "Espaço físico, ADS, compressão e sparse medidos", "false"),
                new("recoverableSpaceEstimated", "Espaço recuperável para limpeza estimado", "false"),
                new("snapshotAtomic", "Snapshot atômico do conteúdo da pasta", "false")
            ];
            long topBudget = 512 * 1024;
            int retained = 0;
            foreach (var file in _top)
            {
                var prefix = "largest." + retained;
                DiagnosticFact[] item = [
                    new(prefix + ".path", "Maior arquivo lógico nº " + (retained + 1), file.Path),
                    new(prefix + ".logicalBytes", "Tamanho lógico observado", NumberValue: file.Length, Unit: "bytes"),
                    new(prefix + ".lastWriteUtc", "Última gravação reportada em UTC", file.LastWriteUtc),
                    new(prefix + ".lastWriteFileTime", "FILETIME original da gravação", NumberValue: file.LastWriteFileTime, Detailed: true),
                    new(prefix + ".attributes", "Atributos originais", NumberValue: file.Attributes, Detailed: true),
                    new(prefix + ".links", "Número de links reportado", NumberValue: file.Links, Detailed: true),
                    new(prefix + ".volume", "Identificador de volume", NumberValue: file.Volume, Detailed: true),
                    new(prefix + ".fileId", "Identificador de arquivo", NumberValue: file.FileId, Detailed: true)
                ];
                long cost = item.Sum(fact => 512 + 6L * (fact.Key.Length + fact.Label.Length + (fact.TextValue?.Length ?? 0) + (fact.Unit?.Length ?? 0)));
                if (cost > topBudget) break;
                topBudget -= cost;
                facts.AddRange(item);
                retained++;
            }
            if (retained < _top.Count) AddIssue(IssueCode.LimitReached, "Detalhes dos maiores arquivos excederam o orçamento de 512 KiB. Nenhum caminho foi truncado ou substituído; os tamanhos totais já observados permanecem preservados.");
            facts.Add(new("largestFilesRetained", "Detalhes dos maiores arquivos retidos (até vinte)", NumberValue: retained));
            facts.Add(new("largestFileDetailsOmitted", "Detalhes de candidatos maiores omitidos por orçamento", NumberValue: _top.Count - retained));
            facts.Add(new("issueCount", "Ocorrências de limitação/falha observadas", NumberValue: _issueCount));
            facts.Add(new("issueDetailsOmitted", "Detalhes de ocorrências não retidos", NumberValue: _issueDetailsOmitted));
            if (_issueDetailsOmitted > 0) _issues.Add(new(IssueCode.LimitReached, $"{_issueDetailsOmitted} detalhe(s) de ocorrências omitido(s); contadores preservados. Até 32 detalhes/128 KiB são retidos, sem truncar mensagens."));
            bool limited = _entryLimit || _timeLimit || _skippedDepth > 0 || retained < _top.Count;
            return new(Guid.NewGuid(), MaintenanceCollectors.FolderSpace, "Espaço lógico da pasta selecionada", "Pasta local explicitamente selecionada", clock.GetUtcNow(),
                !_rootBound ? EvidenceStatus.Unavailable : _issueCount > 0 ? EvidenceStatus.Partial : EvidenceStatus.Complete,
                new("Somente metadados, sem conteúdo, hash ou exclusão. Soma lógica por entrada: hardlinks podem duplicar bytes; não estima espaço físico ou recuperável. Links/junctions/mounts/cloud/offline não são seguidos. Snapshot não atômico: arquivos podem mudar. Prazo e cancelamento são cooperativos entre chamadas de metadados que não são preemptíveis. Raiz vinculada a até 32 ancestrais e DFS limitado, com detalhes top20 e ocorrências limitadas", _processed,
                    limits.MaximumEntries is >= 1 and <= 20_000 ? limits.MaximumEntries : null, limited), facts, _issues.ToArray());
        }

        private static SafeFileHandle OpenDirectory(string path)
        {
            _ = LocalPathGuard.LocalPath(path);
            using var initial = LocalPathGuard.HoldDirectory(path);
            var expected = LocalPathGuard.Identity(initial);
            var handle = OpenMetadata(path, directory: true);
            try
            {
                var raw = Information(handle);
                if ((raw.FileAttributes & (uint)FileAttributes.Directory) == 0 || (raw.FileAttributes & (Reparse | OfflineOrRecall)) != 0)
                    throw new InvalidDataException("O componente não é diretório local direto disponível; reparse/offline/recall não é percorrido.");
                if (LocalPathGuard.Identity(handle) != expected) throw new InvalidDataException("A identidade do diretório mudou durante sua vinculação.");
                return handle;
            }
            catch { handle.Dispose(); throw; }
        }

        private static SafeFileHandle OpenMetadata(string path, bool directory)
        {
            // READ_ATTRIBUTES only: no FILE_READ_DATA/GENERIC_READ. No recall and no traversal of leaf reparse points.
            // Directory leases also deny write handles, preventing reparse substitution while descendants are read.
            var handle = CreateFile(path, 0x80, directory ? 0x1u : 0x1u | 0x2u, IntPtr.Zero, 3,
                0x02000000 | 0x00200000 | 0x00100000, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw new Win32Exception(error);
            }
            return handle;
        }
        private static FileInformation Information(SafeFileHandle handle) => GetFileInformationByHandle(handle, out var information)
            ? information : throw new Win32Exception(Marshal.GetLastWin32Error());
        private static bool Expected(Exception error) => error is IOException or InvalidDataException or UnauthorizedAccessException
            or System.Security.SecurityException or NotSupportedException or ArgumentException or Win32Exception;
        private static IssueCode Code(Exception error) => error is UnauthorizedAccessException or System.Security.SecurityException
            || error is Win32Exception { NativeErrorCode: 5 or 1314 } ? IssueCode.PermissionDenied
            : error is NotSupportedException or InvalidDataException or ArgumentException ? IssueCode.NotSupported : IssueCode.ReadFailed;
        private sealed record FileSnapshot(string Path, ulong Length, string? LastWriteUtc, ulong LastWriteFileTime, uint Attributes, uint Links, uint Volume, ulong FileId);

        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation information);
        [StructLayout(LayoutKind.Sequential)]
        private struct FileInformation
        {
            public uint FileAttributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, LastAccessTime, LastWriteTime;
            public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
        }
    }
}
