namespace SH.ELF;

public sealed class ElfHeader
{
    public byte Class { get; }
    public byte Data { get; }
    public byte Version { get; }

    public ushort Type { get; }
    public ushort Machine { get; }

    public uint EntryPoint { get; }
    public uint ProgramHeaderOffset { get; }
    public uint SectionHeaderOffset { get; }
    public uint Flags { get; }

    public ushort HeaderSize { get; }
    public ushort ProgramHeaderEntrySize { get; }
    public ushort ProgramHeaderCount { get; }
    public ushort SectionHeaderEntrySize { get; }
    public ushort SectionHeaderCount { get; }
    public ushort SectionNameTableIndex { get; }

    private ElfHeader(
        byte @class,
        byte data,
        byte version,
        ushort type,
        ushort machine,
        uint entryPoint,
        uint programHeaderOffset,
        uint sectionHeaderOffset,
        uint flags,
        ushort headerSize,
        ushort programHeaderEntrySize,
        ushort programHeaderCount,
        ushort sectionHeaderEntrySize,
        ushort sectionHeaderCount,
        ushort sectionNameTableIndex)
    {
        Class = @class;
        Data = data;
        Version = version;
        Type = type;
        Machine = machine;
        EntryPoint = entryPoint;
        ProgramHeaderOffset = programHeaderOffset;
        SectionHeaderOffset = sectionHeaderOffset;
        Flags = flags;
        HeaderSize = headerSize;
        ProgramHeaderEntrySize = programHeaderEntrySize;
        ProgramHeaderCount = programHeaderCount;
        SectionHeaderEntrySize = sectionHeaderEntrySize;
        SectionHeaderCount = sectionHeaderCount;
        SectionNameTableIndex = sectionNameTableIndex;
    }

    public static ElfHeader Parse(byte[] data)
    {
        if (data.Length < 52)
            throw new ArgumentException(
                "Um ELF32 precisa ter pelo menos 52 bytes.");

        if (data[0] != 0x7F ||
            data[1] != 0x45 ||
            data[2] != 0x4C ||
            data[3] != 0x46)
        {
            throw new InvalidDataException(
                "O arquivo não possui uma assinatura ELF válida.");
        }

        byte elfClass = data[4];
        byte elfData = data[5];
        byte version = data[6];

        if (elfClass != 1)
            throw new NotSupportedException(
                "Este leitor suporta apenas ELF32.");

        if (elfData != 1)
            throw new NotSupportedException(
                "Este leitor suporta apenas ELF little-endian.");

        ushort type =
            BitConverter.ToUInt16(data, 16);

        ushort machine =
            BitConverter.ToUInt16(data, 18);

        uint entryPoint =
            BitConverter.ToUInt32(data, 24);

        uint programHeaderOffset =
            BitConverter.ToUInt32(data, 28);

        uint sectionHeaderOffset =
            BitConverter.ToUInt32(data, 32);

        uint flags =
            BitConverter.ToUInt32(data, 36);

        ushort headerSize =
            BitConverter.ToUInt16(data, 40);

        ushort programHeaderEntrySize =
            BitConverter.ToUInt16(data, 42);

        ushort programHeaderCount =
            BitConverter.ToUInt16(data, 44);

        ushort sectionHeaderEntrySize =
            BitConverter.ToUInt16(data, 46);

        ushort sectionHeaderCount =
            BitConverter.ToUInt16(data, 48);

        ushort sectionNameTableIndex =
            BitConverter.ToUInt16(data, 50);

        return new ElfHeader(
            elfClass,
            elfData,
            version,
            type,
            machine,
            entryPoint,
            programHeaderOffset,
            sectionHeaderOffset,
            flags,
            headerSize,
            programHeaderEntrySize,
            programHeaderCount,
            sectionHeaderEntrySize,
            sectionHeaderCount,
            sectionNameTableIndex);
    }
}