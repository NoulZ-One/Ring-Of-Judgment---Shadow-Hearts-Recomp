using System.Buffers.Binary;
using System.Text;

namespace SH.ELF;

public sealed record ElfSection(
    int Index,
    string Name,
    uint Type,
    uint Flags,
    uint Address,
    uint Offset,
    uint Size,
    uint Link,
    uint Info,
    uint EntrySize);

public sealed record ElfSymbol(
    string Name,
    uint Address,
    uint Size,
    byte Type,
    byte Bind,
    ushort SectionIndex);

// Leitura da tabela de seções e da .symtab de um ELF32 little-endian.
public static class ElfSections
{
    private const int SectionHeaderSize = 40;
    private const int SymbolSize = 16;

    public const uint SectionTypeSymtab = 2;

    private readonly record struct RawSection(
        uint Name,
        uint Type,
        uint Flags,
        uint Address,
        uint Offset,
        uint Size,
        uint Link,
        uint Info,
        uint EntrySize);

    public static List<ElfSection> Read(byte[] elf, ElfHeader header)
    {
        List<ElfSection> sections = new();

        if (header.SectionHeaderCount == 0 ||
            header.SectionHeaderOffset == 0)
        {
            return sections;
        }

        if (header.SectionHeaderEntrySize < SectionHeaderSize)
            throw new InvalidDataException(
                "Tamanho de section header inválido.");

        long tableEnd =
            (long)header.SectionHeaderOffset +
            (long)header.SectionHeaderEntrySize * header.SectionHeaderCount;

        if (tableEnd > elf.Length)
            throw new InvalidDataException(
                "A tabela de seções ultrapassa o fim do arquivo.");

        int count = header.SectionHeaderCount;

        RawSection[] raw = new RawSection[count];

        for (int i = 0; i < count; i++)
        {
            int o = checked(
                (int)header.SectionHeaderOffset +
                i * header.SectionHeaderEntrySize);

            ReadOnlySpan<byte> s = elf.AsSpan(o, SectionHeaderSize);

            raw[i] = new RawSection(
                BinaryPrimitives.ReadUInt32LittleEndian(s[0..]),
                BinaryPrimitives.ReadUInt32LittleEndian(s[4..]),
                BinaryPrimitives.ReadUInt32LittleEndian(s[8..]),
                BinaryPrimitives.ReadUInt32LittleEndian(s[12..]),
                BinaryPrimitives.ReadUInt32LittleEndian(s[16..]),
                BinaryPrimitives.ReadUInt32LittleEndian(s[20..]),
                BinaryPrimitives.ReadUInt32LittleEndian(s[24..]),
                BinaryPrimitives.ReadUInt32LittleEndian(s[28..]),
                BinaryPrimitives.ReadUInt32LittleEndian(s[36..]));
        }

        // Tabela de nomes das seções (.shstrtab).
        long nameTableOffset = 0;
        long nameTableSize = 0;

        if (header.SectionNameTableIndex < count)
        {
            RawSection names = raw[header.SectionNameTableIndex];

            nameTableOffset = names.Offset;
            nameTableSize = names.Size;
        }

        for (int i = 0; i < count; i++)
        {
            RawSection r = raw[i];

            sections.Add(new ElfSection(
                i,
                ReadString(elf, nameTableOffset, nameTableSize, r.Name),
                r.Type,
                r.Flags,
                r.Address,
                r.Offset,
                r.Size,
                r.Link,
                r.Info,
                r.EntrySize));
        }

        return sections;
    }

    public static List<ElfSymbol> ReadSymbols(
        byte[] elf,
        IReadOnlyList<ElfSection> sections)
    {
        List<ElfSymbol> symbols = new();

        foreach (ElfSection symtab in sections)
        {
            if (symtab.Type != SectionTypeSymtab)
                continue;

            if (symtab.Link >= sections.Count)
                continue;

            ElfSection strtab = sections[(int)symtab.Link];

            long end = (long)symtab.Offset + symtab.Size;

            if (end > elf.Length)
                continue;

            long step =
                symtab.EntrySize >= SymbolSize
                    ? symtab.EntrySize
                    : SymbolSize;

            for (long offset = symtab.Offset;
                 offset + SymbolSize <= end;
                 offset += step)
            {
                ReadOnlySpan<byte> s = elf.AsSpan((int)offset, SymbolSize);

                uint name = BinaryPrimitives.ReadUInt32LittleEndian(s[0..]);
                uint value = BinaryPrimitives.ReadUInt32LittleEndian(s[4..]);
                uint size = BinaryPrimitives.ReadUInt32LittleEndian(s[8..]);
                byte info = s[12];
                ushort sectionIndex =
                    BinaryPrimitives.ReadUInt16LittleEndian(s[14..]);

                symbols.Add(new ElfSymbol(
                    ReadString(elf, strtab.Offset, strtab.Size, name),
                    value,
                    size,
                    (byte)(info & 0x0F),
                    (byte)(info >> 4),
                    sectionIndex));
            }
        }

        return symbols;
    }

    private static string ReadString(
        byte[] elf,
        long tableOffset,
        long tableSize,
        uint nameOffset)
    {
        if (nameOffset >= tableSize)
            return string.Empty;

        long start = tableOffset + nameOffset;
        long limit = Math.Min(elf.Length, tableOffset + tableSize);

        if (start >= limit)
            return string.Empty;

        long end = start;

        while (end < limit && elf[end] != 0)
            end++;

        return Encoding.ASCII.GetString(
            elf,
            (int)start,
            (int)(end - start));
    }
}
