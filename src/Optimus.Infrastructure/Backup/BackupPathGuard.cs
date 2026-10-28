using Optimus.Infrastructure.Files;

namespace Optimus.Infrastructure.Backup;

internal static class BackupPathGuard
{
    public static void RequireSeparated(string source, string repository, string password, string journal)
    {
        if (LocalPathGuard.Related(source, repository)) throw new InvalidDataException("Origem e repositório não podem conter um ao outro.");
        if (LocalPathGuard.IsWithin(password, source) || LocalPathGuard.IsWithin(password, repository))
            throw new InvalidDataException("O arquivo de senha precisa ficar fora da origem e do repositório.");
        if (LocalPathGuard.Related(journal, source) || LocalPathGuard.Related(journal, repository))
            throw new InvalidDataException("O diário operacional deve ficar separado da origem e do repositório.");
    }

    public static void RequireEmptyDirectory(string directory)
    {
        if (File.Exists(directory)) throw new InvalidDataException("O destino é um arquivo, não uma pasta.");
        if (Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any())
            throw new InvalidDataException("O repositório inicial deve ser uma pasta nova ou vazia; conteúdo existente foi preservado.");
        var parent = Path.GetDirectoryName(directory);
        if (parent is null || !Directory.Exists(parent)) throw new InvalidDataException("A pasta pai do destino deve existir.");
    }

}
