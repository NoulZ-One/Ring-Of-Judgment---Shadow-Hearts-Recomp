namespace SH.ELF;

public sealed class MipsFunctionAnalyzer
{
    private readonly MipsInstructionReader _reader;

    public MipsFunctionAnalyzer(
        MipsInstructionReader reader)
    {
        _reader = reader;
    }

    public List<uint> FindFunctionCalls(
        uint startAddress,
        int instructionCount)
    {
        HashSet<uint> functionAddresses = new();

        uint currentAddress = startAddress;

        for (int i = 0; i < instructionCount; i++)
        {
            MipsInstruction instruction =
                _reader.Read(currentAddress);

            if (instruction.Mnemonic == "JAL")
            {
                uint? target =
                    _reader.GetBranchTarget(
                        currentAddress,
                        instruction);

                if (target.HasValue)
                {
                    functionAddresses.Add(
                        target.Value);
                }
            }

            currentAddress += 4;
        }

        return functionAddresses
            .OrderBy(address => address)
            .ToList();
    }
}