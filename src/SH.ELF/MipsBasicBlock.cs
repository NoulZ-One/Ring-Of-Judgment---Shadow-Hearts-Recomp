namespace SH.ELF;

public sealed class MipsBasicBlock
{
    public uint StartAddress { get; }

    public List<MipsInstruction> Instructions { get; }

    public uint? BranchTarget { get; set; }

    public uint? FallthroughAddress { get; set; }

     public bool IsReturn { get; set; }

     public bool WasInstructionLimitReached { get; set; }

     public uint? CallTarget { get; set; }

     public bool IsIndirectCall { get; set; }

    public MipsBasicBlock(uint startAddress)
    {
        StartAddress = startAddress;
        Instructions = new List<MipsInstruction>();
    }

    public void AddInstruction(MipsInstruction instruction)
    {
        Instructions.Add(instruction);
    }
    

}