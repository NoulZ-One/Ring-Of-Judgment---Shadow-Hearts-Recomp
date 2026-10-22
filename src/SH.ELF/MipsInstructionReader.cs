namespace SH.ELF;

public sealed class MipsInstructionReader
{
    private readonly ElfImage _image;

    public MipsInstructionReader(ElfImage image)
    {
        _image = image;
    }

    public MipsInstruction Read(uint virtualAddress)
    {
        if ((virtualAddress & 3) != 0)
            throw new ArgumentException(
                $"Endereço 0x{virtualAddress:X8} não está alinhado a 4 bytes.",
                nameof(virtualAddress));

        // Não exige flag de execução: alguns executáveis PS2 usam um único
        // segmento RWX. Só garante que o endereço está carregado.
        uint raw;

        if (!_image.TryReadUInt32(virtualAddress, out raw))
            throw new ArgumentOutOfRangeException(
                nameof(virtualAddress),
                $"0x{virtualAddress:X8} está fora das regiões carregadas.");

        return MipsDecoder.Decode(raw);
    }

    // Destino de J/JAL e de qualquer branch relativo (BEQ, BNE, BLTZ,
    // BGEZAL, likely, BC1T...). Retorna null para as demais instruções.
    public uint? GetBranchTarget(
        uint instructionAddress,
        MipsInstruction instruction)
    {
        if (instruction.TryGetBranchTarget(instructionAddress, out uint target))
            return target;

        return null;
    }
}
