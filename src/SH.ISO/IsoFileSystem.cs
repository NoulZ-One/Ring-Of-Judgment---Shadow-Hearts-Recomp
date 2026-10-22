namespace SH.ISO;

// Navegação por caminho em um volume ISO-9660.
public sealed class IsoFileSystem
{
    private readonly IsoReader _reader;
    private readonly IsoDirectory _directory;
    private readonly int _rootSector;
    private readonly uint _rootSize;

    public IsoVolumeDescriptor Volume { get; }

    public IsoFileSystem(IsoReader reader)
    {
        _reader = reader;
        _directory = new IsoDirectory(reader);

        Volume = IsoVolumeDescriptor.Parse(reader.ReadSector(16));

        _rootSector = checked((int)Volume.RootDirectorySector);
        _rootSize = Volume.RootDirectorySize;
    }

    public List<IsoDirectoryRecord> ListRoot()
    {
        return _directory.Read(_rootSector, _rootSize);
    }

    // Aceita "SLUS_203.47", "\SLUS_203.47;1", "DIR/ARQ.BIN" etc.
    public IsoDirectoryRecord? Find(string path)
    {
        string[] parts = path
            .Replace('\\', '/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length == 0)
            return null;

        int sector = _rootSector;
        uint size = _rootSize;

        for (int i = 0; i < parts.Length; i++)
        {
            IsoDirectoryRecord? match =
                FindEntry(_directory.Read(sector, size), parts[i]);

            if (match == null)
                return null;

            if (i == parts.Length - 1)
                return match;

            if (!match.IsDirectory)
                return null;

            sector = checked((int)match.ExtentLocation);
            size = match.DataLength;
        }

        return null;
    }

    public byte[] ReadFile(IsoDirectoryRecord record)
    {
        if (record.IsDirectory)
            throw new InvalidOperationException(
                "O registro é um diretório, não um arquivo.");

        return _reader.ReadBytes(
            checked((int)record.ExtentLocation),
            checked((int)record.DataLength));
    }

    public byte[] ReadFile(string path)
    {
        IsoDirectoryRecord record =
            Find(path) ??
            throw new FileNotFoundException(
                $"Arquivo não encontrado na ISO: {path}",
                path);

        return ReadFile(record);
    }

    public SystemConfig ReadSystemConfig()
    {
        return SystemConfig.Parse(ReadFile("SYSTEM.CNF"));
    }

    // Remove a versão ISO9660 (";1") e o ponto final de nomes sem extensão.
    public static string NormalizeName(string identifier)
    {
        int version = identifier.IndexOf(';');

        if (version >= 0)
            identifier = identifier[..version];

        return identifier.TrimEnd('.');
    }

    private static IsoDirectoryRecord? FindEntry(
        IEnumerable<IsoDirectoryRecord> records,
        string name)
    {
        string wanted = NormalizeName(name);

        foreach (IsoDirectoryRecord record in records)
        {
            if (IsSelfOrParent(record))
                continue;

            if (string.Equals(
                    NormalizeName(record.FileIdentifier),
                    wanted,
                    StringComparison.OrdinalIgnoreCase))
            {
                return record;
            }
        }

        return null;
    }

    // Entradas "." e ".." usam os identificadores 0x00 e 0x01.
    private static bool IsSelfOrParent(IsoDirectoryRecord record)
    {
        return record.FileIdentifier.Length == 1 &&
               (record.FileIdentifier[0] == '\0' ||
                record.FileIdentifier[0] == '\u0001');
    }
}
