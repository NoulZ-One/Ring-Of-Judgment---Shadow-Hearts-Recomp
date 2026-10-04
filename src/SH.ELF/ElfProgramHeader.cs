namespace SH.ELF;

public sealed class ElfProgramHeader
{
    public uint Type { get; }
    public uint Offset { get; }
    public uint VirtualAddress { get; }
    public uint PhysicalAddress { get; }
    public uint FileSize { get; }
    public uint MemorySize { get; }
    public uint Flags { get; }
    public uint Alignment { get; }

    private ElfProgramHeader(
        uint type,
        uint offset,
        uint virtualAddress,
        uint physicalAddress,
        uint fileSize,
        uint memorySize,
        uint flags,
        uint alignment)
    {
        Type = type;
        Offset = offset;
        VirtualAddress = virtualAddress;
        PhysicalAddress = physicalAddress;
        FileSize = fileSize;
        MemorySize = memorySize;
        Flags = flags;
        Alignment = alignment;
    }

    public static ElfProgramHeader Parse(
        byte[] data,
        int offset)
    {
        if (offset < 0 || offset + 32 > data.Length)
            throw new ArgumentException(
                "Dados insuficientes para um Program Header ELF32.");

        uint type =
            BitConverter.ToUInt32(data, offset);

        uint fileOffset =
            BitConverter.ToUInt32(data, offset + 4);

        uint virtualAddress =
            BitConverter.ToUInt32(data, offset + 8);

        uint physicalAddress =
            BitConverter.ToUInt32(data, offset + 12);

        uint fileSize =
            BitConverter.ToUInt32(data, offset + 16);

        uint memorySize =
            BitConverter.ToUInt32(data, offset + 20);

        uint flags =
            BitConverter.ToUInt32(data, offset + 24);

        uint alignment =
            BitConverter.ToUInt32(data, offset + 28);

        return new ElfProgramHeader(
            type,
            fileOffset,
            virtualAddress,
            physicalAddress,
            fileSize,
            memorySize,
            flags,
            alignment);
    }
}