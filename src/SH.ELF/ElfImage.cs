using System.Buffers.Binary;

namespace SH.ELF;

// Imagem de memória do ELF: todos os segmentos PT_LOAD mapeados em seus
// endereços virtuais, com .bss (MemorySize > FileSize) zerado.
public sealed class ElfImage
{
    public sealed class Region
    {
        public uint VirtualAddress { get; }

        // Tamanho total na memória; os bytes além de FileSize são .bss (zero).
        public byte[] Data { get; }

        public uint FileSize { get; }

        public uint Flags { get; }

        public bool IsExecutable => (Flags & 0x1) != 0;
        public bool IsWritable => (Flags & 0x2) != 0;
        public bool IsReadable => (Flags & 0x4) != 0;

        public ulong EndAddress =>
            (ulong)VirtualAddress + (ulong)Data.Length;

        internal Region(
            uint virtualAddress,
            byte[] data,
            uint fileSize,
            uint flags)
        {
            VirtualAddress = virtualAddress;
            Data = data;
            FileSize = fileSize;
            Flags = flags;
        }

        public bool Contains(uint address, int length)
        {
            return address >= VirtualAddress &&
                   (ulong)address + (ulong)length <= EndAddress;
        }
    }

    private readonly List<Region> _regions;

    public ElfHeader Header { get; }

    public IReadOnlyList<Region> Regions => _regions;

    public uint EntryPoint => Header.EntryPoint;

    private ElfImage(ElfHeader header, List<Region> regions)
    {
        Header = header;
        _regions = regions;
    }

    public static ElfImage Load(byte[] elfData)
    {
        ElfHeader header = ElfHeader.Parse(elfData);

        if (header.Machine != 8)
            throw new NotSupportedException(
                $"Máquina ELF {header.Machine}; esperado 8 (MIPS).");

        if (header.Type != 2)
            throw new NotSupportedException(
                $"Tipo ELF 0x{header.Type:X4}; esperado 0x0002 (ET_EXEC).");

        if (header.ProgramHeaderCount == 0)
            throw new InvalidDataException(
                "O ELF não possui program headers.");

        if (header.ProgramHeaderEntrySize < 32)
            throw new InvalidDataException(
                "Tamanho de program header inválido.");

        ElfReader reader = new(elfData);

        List<Region> regions = new();

        foreach (ElfSegment segment in reader.Segments)
        {
            if (!segment.IsLoadable || segment.MemorySize == 0)
                continue;

            regions.Add(new Region(
                segment.VirtualAddress,
                segment.Load(elfData),
                segment.FileSize,
                segment.Flags));
        }

        if (regions.Count == 0)
            throw new InvalidDataException(
                "O ELF não possui segmentos PT_LOAD.");

        regions.Sort((a, b) =>
            a.VirtualAddress.CompareTo(b.VirtualAddress));

        for (int i = 1; i < regions.Count; i++)
        {
            if (regions[i].VirtualAddress < regions[i - 1].EndAddress)
                throw new InvalidDataException(
                    $"Segmentos sobrepostos em 0x{regions[i].VirtualAddress:X8}.");
        }

        ElfImage image = new(header, regions);

        if (image.FindRegion(header.EntryPoint, 4) == null)
            throw new InvalidDataException(
                $"Entry point 0x{header.EntryPoint:X8} fora dos segmentos carregados.");

        return image;
    }

    public Region? FindRegion(uint address, int length = 1)
    {
        foreach (Region region in _regions)
        {
            if (region.Contains(address, length))
                return region;
        }

        return null;
    }

    public bool TryReadUInt32(uint address, out uint value)
    {
        value = 0;

        Region? region = FindRegion(address, 4);

        if (region == null)
            return false;

        int offset = checked((int)(address - region.VirtualAddress));

        value = BinaryPrimitives.ReadUInt32LittleEndian(
            region.Data.AsSpan(offset, 4));

        return true;
    }

    public uint ReadUInt32(uint address)
    {
        if (!TryReadUInt32(address, out uint value))
            throw new ArgumentOutOfRangeException(
                nameof(address),
                $"0x{address:X8} está fora das regiões carregadas.");

        return value;
    }
}
