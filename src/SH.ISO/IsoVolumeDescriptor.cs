using System.Text;

namespace SH.ISO;

public sealed class IsoVolumeDescriptor
{
    public string SystemIdentifier { get; }
    public string VolumeIdentifier { get; }
    public uint VolumeSpaceSize { get; }
    public ushort LogicalBlockSize { get; }
    public uint RootDirectorySector { get; }

    // Tamanho em bytes do diretório raiz (pode ocupar mais de um setor).
    public uint RootDirectorySize { get; }

    private IsoVolumeDescriptor(
        string systemIdentifier,
        string volumeIdentifier,
        uint volumeSpaceSize,
        ushort logicalBlockSize,
        uint rootDirectorySector,
        uint rootDirectorySize)
    {
        SystemIdentifier = systemIdentifier;
        VolumeIdentifier = volumeIdentifier;
        VolumeSpaceSize = volumeSpaceSize;
        LogicalBlockSize = logicalBlockSize;
        RootDirectorySector = rootDirectorySector;
        RootDirectorySize = rootDirectorySize;
    }

    public static IsoVolumeDescriptor Parse(byte[] sector)
    {
        if (sector.Length != 2048)
            throw new ArgumentException("O setor deve possuir 2048 bytes.");

        if (sector[0] != 0x01)
            throw new InvalidDataException("O setor não é um Primary Volume Descriptor.");

        string identifier = Encoding.ASCII.GetString(sector, 1, 5);

        if (identifier != "CD001")
            throw new InvalidDataException("Identificador ISO-9660 inválido.");

        string systemIdentifier =
            Encoding.ASCII.GetString(sector, 8, 32).Trim();

        string volumeIdentifier =
            Encoding.ASCII.GetString(sector, 40, 32).Trim();

        uint volumeSpaceSize =
            BitConverter.ToUInt32(sector, 80);

        ushort logicalBlockSize =
            BitConverter.ToUInt16(sector, 128);

        // Registro do diretório raiz começa no offset 156:
        // +2 = extent (LE), +10 = tamanho dos dados (LE).
        uint rootDirectorySector =
            BitConverter.ToUInt32(sector, 158);

        uint rootDirectorySize =
            BitConverter.ToUInt32(sector, 166);

        return new IsoVolumeDescriptor(
            systemIdentifier,
            volumeIdentifier,
            volumeSpaceSize,
            logicalBlockSize,
            rootDirectorySector,
            rootDirectorySize);
    }
}
