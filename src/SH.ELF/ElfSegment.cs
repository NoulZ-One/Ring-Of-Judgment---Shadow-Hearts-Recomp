namespace SH.ELF;

public sealed class ElfSegment
{
    public uint Type { get; }
    public uint FileOffset { get; }
    public uint VirtualAddress { get; }
    public uint PhysicalAddress { get; }
    public uint FileSize { get; }
    public uint MemorySize { get; }
    public uint Flags { get; }
    public uint Alignment { get; }

    public bool IsLoadable =>
        Type == 1;

    public bool IsReadable =>
        (Flags & 0x4) != 0;

    public bool IsWritable =>
        (Flags & 0x2) != 0;

    public bool IsExecutable =>
        (Flags & 0x1) != 0;

    public ElfSegment(
        ElfProgramHeader programHeader)
    {
        Type = programHeader.Type;
        FileOffset = programHeader.Offset;
        VirtualAddress = programHeader.VirtualAddress;
        PhysicalAddress = programHeader.PhysicalAddress;
        FileSize = programHeader.FileSize;
        MemorySize = programHeader.MemorySize;
        Flags = programHeader.Flags;
        Alignment = programHeader.Alignment;
    }

    public byte[] Load(byte[] elfData)
    {
        if (!IsLoadable)
            throw new InvalidOperationException(
                "O segmento não é carregável.");

        if (FileSize > MemorySize)
            throw new InvalidDataException(
                "O tamanho do arquivo é maior que o tamanho da memória.");

        long end =
            (long)FileOffset + FileSize;

        if (end > elfData.Length)
            throw new InvalidDataException(
                "O segmento ultrapassa os dados do ELF.");

        byte[] memory =
            new byte[checked((int)MemorySize)];

        Array.Copy(
            elfData,
            checked((int)FileOffset),
            memory,
            0,
            checked((int)FileSize));

        return memory;
    }
}