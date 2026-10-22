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

    public uint? ResolvedIndirectCallTarget { get; set; }

    // JR com registrador diferente de $ra (tabela de switch, tail call).
    public bool IsIndirectJump { get; set; }

    // O desvio que termina o bloco é um branch-likely.
    public bool IsBranchLikely { get; set; }

    // O bloco tentou ler fora das regiões carregadas.
    public bool IsInvalid { get; set; }

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
