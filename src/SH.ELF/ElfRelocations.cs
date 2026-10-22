using System.Buffers.Binary;

namespace SH.ELF;

public sealed record ElfRelocation(
    uint Offset,
    byte Type,
    uint SymbolIndex);

// Leitura de seções SHT_REL (entradas de 8 bytes: r_offset + r_info).
public static class ElfRelocations
{
    private const int EntrySize = 8;

    private const uint SectionTypeRel = 9;

    public static List<ElfRelocation> ReadRel(
        byte[] elf,
        ElfSection section)
    {
        if (section.Type != SectionTypeRel)
            throw new NotSupportedException(
                $"A seção {section.Name} é do tipo 0x{section.Type:X}; " +
                "esperado 9 (SHT_REL).");

        long end = (long)section.Offset + section.Size;

        if (end > elf.Length)
            throw new InvalidDataException(
                "A seção de relocação ultrapassa o fim do arquivo.");

        List<ElfRelocation> relocations = new();

        for (long offset = section.Offset;
             offset + EntrySize <= end;
             offset += EntrySize)
        {
            ReadOnlySpan<byte> entry = elf.AsSpan((int)offset, EntrySize);

            uint rOffset =
                BinaryPrimitives.ReadUInt32LittleEndian(entry[0..]);

            uint info =
                BinaryPrimitives.ReadUInt32LittleEndian(entry[4..]);

            relocations.Add(new ElfRelocation(
                rOffset,
                (byte)(info & 0xFF),
                info >> 8));
        }

        return relocations;
    }

    public static string TypeName(byte type)
    {
        return type switch
        {
            0 => "R_MIPS_NONE",
            1 => "R_MIPS_16",
            2 => "R_MIPS_32",
            3 => "R_MIPS_REL32",
            4 => "R_MIPS_26",
            5 => "R_MIPS_HI16",
            6 => "R_MIPS_LO16",
            7 => "R_MIPS_GPREL16",
            8 => "R_MIPS_LITERAL",
            9 => "R_MIPS_GOT16",
            10 => "R_MIPS_PC16",
            11 => "R_MIPS_CALL16",
            12 => "R_MIPS_GPREL32",
            _ => $"tipo {type}"
        };
    }
}
