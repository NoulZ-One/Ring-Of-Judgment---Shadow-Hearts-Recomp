using System.Text;

namespace SH.ISO;

public sealed class IsoVolumeDescriptor
{
    public string SystemIdentifier { get; }
    public string VolumeIdentifier { get; }
    public uint VolumeSpaceSize { get; }
    public ushort LogicalBlockSize { get; }
    public uint RootDirectorySector { get; }

    private IsoVolumeDescriptor(
        string systemIdentifier,
        string volumeIdentifier,
        uint volumeSpaceSize,
        ushort logicalBlockSize,
        uint rootDirectorySector)
    {
        SystemIdentifier = systemIdentifier;
        VolumeIdentifier = volumeIdentifier;
        VolumeSpaceSize = volumeSpaceSize;
        LogicalBlockSize = logicalBlockSize;
        RootDirectorySector = rootDirectorySector;
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

        uint rootDirectorySector =
            BitConverter.ToUInt32(sector, 158);

        return new IsoVolumeDescriptor(
            systemIdentifier,
            volumeIdentifier,
            volumeSpaceSize,
            logicalBlockSize,
            rootDirectorySector);
    }
}