using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Optimus.Infrastructure.Files;

internal sealed record PathIdentity(uint Volume, ulong FileId);

internal static class LocalPathGuard
{
    public static string LocalPath(string selected)
    {
        if (string.IsNullOrWhiteSpace(selected) || !Path.IsPathFullyQualified(selected)
            || selected.StartsWith(@"\\", StringComparison.Ordinal))
            throw new InvalidDataException("Selecione um caminho absoluto local, sem URI, UNC ou caminho de dispositivo.");
        RejectAmbiguousComponents(selected);
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(selected));
        var drive = new DriveInfo(Path.GetPathRoot(path)!);
        if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable))
            throw new InvalidDataException("O caminho deve estar em armazenamento local fixo ou removível.");
        RejectReparseAncestors(path);
        RejectExistingAliases(path);
        return path;
    }

    private static void RejectAmbiguousComponents(string path)
    {
        foreach (var part in path[Path.GetPathRoot(path)!.Length..].Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (part is "." or "..") continue;
            if (part.EndsWith('.') || part.EndsWith(' ') || part.Any(character => character < 32 || "<>:\"|?*".Contains(character)))
                throw new InvalidDataException("Componente de caminho ambíguo ou não suportado (ponto/espaço final, fluxo alternativo ou caractere reservado). Selecione o caminho direto.");
            var deviceName = part.Split('.')[0];
            if (deviceName.Equals("CON", StringComparison.OrdinalIgnoreCase) || deviceName.Equals("PRN", StringComparison.OrdinalIgnoreCase)
                || deviceName.Equals("AUX", StringComparison.OrdinalIgnoreCase) || deviceName.Equals("NUL", StringComparison.OrdinalIgnoreCase)
                || deviceName.Equals("CONIN$", StringComparison.OrdinalIgnoreCase) || deviceName.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase)
                || ((deviceName.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || deviceName.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
                    && deviceName.Length == 4 && "123456789¹²³".Contains(deviceName[3])))
                throw new InvalidDataException("O caminho contém um nome de dispositivo DOS reservado; selecione um arquivo ou pasta local comum.");
        }
    }

    private static void RejectExistingAliases(string path)
    {
        string? current = path;
        while (current is not null)
        {
            try
            {
                _ = File.GetAttributes(current);
                using var handle = CreateFile(current, 0x80, 0x1 | 0x2 | 0x4, IntPtr.Zero, 3, 0x02000000 | 0x00200000, IntPtr.Zero);
                if (handle.IsInvalid) throw new IOException("Não foi possível resolver a identidade do caminho selecionado.", new Win32Exception(Marshal.GetLastWin32Error()));
                _ = Identity(handle);
                var buffer = new StringBuilder(32_768);
                var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
                if (length == 0 || length >= buffer.Capacity) throw new IOException("Não foi possível obter uma representação final limitada do caminho.", new Win32Exception(Marshal.GetLastWin32Error()));
                var resolved = buffer.ToString();
                if (!resolved.StartsWith(@"\\?\", StringComparison.Ordinal)
                    || !string.Equals(Path.TrimEndingDirectorySeparator(resolved[4..]), Path.TrimEndingDirectorySeparator(current), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("O caminho usa alias ou representação divergente do destino real. Selecione seu caminho longo e direto; nenhum alias foi corrigido automaticamente.");
                return;
            }
            catch (FileNotFoundException) { current = Path.GetDirectoryName(current); }
            catch (DirectoryNotFoundException) { current = Path.GetDirectoryName(current); }
        }
        throw new InvalidDataException("Nenhum ancestral local existente pôde ser identificado para o caminho.");
    }

    public static bool Related(string first, string second) => IsWithin(first, second) || IsWithin(second, first);
    public static bool IsWithin(string child, string parent) => string.Equals(child, parent, StringComparison.OrdinalIgnoreCase)
        || child.StartsWith(Path.TrimEndingDirectorySeparator(parent) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    public static void CreateNewDirectory(string directory)
    {
        if (!CreateDirectory(directory, IntPtr.Zero))
            throw new IOException("Não foi possível reservar exclusivamente a nova pasta: " + directory, new Win32Exception(Marshal.GetLastWin32Error()));
    }

    public static SafeFileHandle HoldDirectory(string directory)
    {
        RejectReparseAncestors(directory);
        var handle = CreateFile(directory, 0x80, 0x1 | 0x2, IntPtr.Zero, 3, 0x02000000 | 0x00200000, IntPtr.Zero);
        if (handle.IsInvalid) { var error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new IOException("Não foi possível vincular a pasta selecionada.", new Win32Exception(error)); }
        return handle;
    }

    public static PathIdentity Identity(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var data)) throw new IOException("Não foi possível ler a identidade do arquivo/pasta.", new Win32Exception(Marshal.GetLastWin32Error()));
        if ((data.FileAttributes & (uint)FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Pontos de reanálise não são suportados nesta operação local.");
        return new(data.VolumeSerialNumber, ((ulong)data.FileIndexHigh << 32) | data.FileIndexLow);
    }

    public static PathIdentity DirectoryIdentity(string directory)
    {
        using var handle = HoldDirectory(directory);
        return Identity(handle);
    }

    public static string HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    public static string HashStream(FileStream stream)
    {
        stream.Position = 0;
        var hash = Convert.ToHexString(SHA256.HashData(stream));
        stream.Position = 0;
        return hash;
    }

    public static void RejectReparseAncestors(string path)
    {
        var current = path;
        while (!string.IsNullOrEmpty(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("O caminho contém ponto de reanálise/junction; selecione uma localização local direta.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            current = Path.GetDirectoryName(current);
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateDirectoryW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectory(string path, IntPtr securityAttributes);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr securityAttributes, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation information);

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint size, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }
}
