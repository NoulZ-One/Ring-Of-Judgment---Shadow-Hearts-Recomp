namespace SH.ELF;

public sealed class MipsFunction
{
    public uint StartAddress { get; }

    public bool WasBlockLimitReached { get; set; }

    public bool WasInstructionLimitReached { get; set; }

    public bool HasReturn { get; set; }

    public List<MipsBasicBlock> BasicBlocks { get; }

    public List<uint> CalledFunctions { get; }

    public MipsFunction(uint startAddress)
    {
        StartAddress = startAddress;

        BasicBlocks =
            new List<MipsBasicBlock>();

        CalledFunctions =
            new List<uint>();
    }

    public void AddBasicBlock(
        MipsBasicBlock block)
    {
        BasicBlocks.Add(block);
    }

    public void AddCalledFunction(
        uint address)
    {
        if (!CalledFunctions.Contains(address))
        {
            CalledFunctions.Add(address);
        }
    }
}