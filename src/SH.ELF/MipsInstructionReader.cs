namespace SH.ELF;

public sealed class MipsInstructionReader
{
    private readonly byte[] _memory;
    private readonly uint _baseAddress;

    public MipsInstructionReader(
        byte[] memory,
        uint baseAddress)
    {
        _memory = memory;
        _baseAddress = baseAddress;
    }

    public MipsInstruction Read(uint virtualAddress)
    {
        if (virtualAddress < _baseAddress)
            throw new ArgumentOutOfRangeException(
                nameof(virtualAddress),
                "O endereço está antes do segmento.");

        uint offset =
            virtualAddress - _baseAddress;

        if (offset + 4 > _memory.Length)
            throw new InvalidDataException(
                "A instrução ultrapassa os limites da memória.");

        uint raw =
            BitConverter.ToUInt32(
                _memory,
                checked((int)offset));

        return MipsDecoder.Decode(raw);
    }

    public uint? GetBranchTarget(
    uint instructionAddress,
    MipsInstruction instruction)
{
    if (instruction.Mnemonic == "J" ||
        instruction.Mnemonic == "JAL")
    {
        uint nextInstruction =
            instructionAddress + 4;

        uint target =
            (nextInstruction & 0xF0000000) |
            (instruction.Target << 2);

        return target;
    }

    if (instruction.Mnemonic != "BEQ" &&
        instruction.Mnemonic != "BNE" &&
        instruction.Mnemonic != "BLEZ" &&
        instruction.Mnemonic != "BGTZ")
    {
        return null;
    }

    int offset =
        instruction.SignedImmediate * 4;

    uint nextBranchInstruction =
        instructionAddress + 4;

    return unchecked(
        (uint)((int)nextBranchInstruction + offset));
}
}