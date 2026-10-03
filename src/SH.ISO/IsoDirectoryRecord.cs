using System.Text;

namespace SH.ISO;

public sealed class IsoDirectoryRecord
{
    public byte Length { get; }
    public uint ExtentLocation { get; }
    public uint DataLength { get; }
    public byte FileFlags { get; }
    public string FileIdentifier { get; }

    public bool IsDirectory =>
        (FileFlags & 0x02) != 0;

    private IsoDirectoryRecord(
        byte length,
        uint extentLocation,
        uint dataLength,
        byte fileFlags,
        string fileIdentifier)
    {
        Length = length;
        ExtentLocation = extentLocation;
        DataLength = dataLength;
        FileFlags = fileFlags;
        FileIdentifier = fileIdentifier;
    }

    public static IsoDirectoryRecord Parse(byte[] data, int offset)
    {
        byte length = data[offset];

        if (length == 0)
            throw new InvalidDataException(
                "Registro de diretório vazio.");

        uint extentLocation =
            BitConverter.ToUInt32(data, offset + 2);

        uint dataLength =
            BitConverter.ToUInt32(data, offset + 10);

        byte fileFlags =
            data[offset + 25];

        byte fileIdentifierLength =
            data[offset + 32];

        string fileIdentifier =
            Encoding.ASCII.GetString(
                data,
                offset + 33,
                fileIdentifierLength);

        return new IsoDirectoryRecord(
            length,
            extentLocation,
            dataLength,
            fileFlags,
            fileIdentifier);
    }
}