namespace SH.ELF;

public sealed class MipsBasicBlockBuilder
{
    private readonly MipsInstructionReader _reader;

    private readonly Func<uint, uint?> _indirectCallResolver;

    public MipsBasicBlockBuilder(
        MipsInstructionReader reader,
        Func<uint, uint?> indirectCallResolver)
    {
        _reader = reader;
        _indirectCallResolver = indirectCallResolver;
    }

    public MipsBasicBlock Build(
        uint startAddress,
        int maxInstructions)
    {
        MipsBasicBlock block =
            new(startAddress);

        uint currentAddress =
            startAddress;

        bool terminated = false;

        for (int i = 0; i < maxInstructions; i++)
        {
            MipsInstruction instruction =
                _reader.Read(currentAddress);

            block.AddInstruction(instruction);

            // BAL / BGEZAL / BLTZAL: branch que também chama (link).
            if (instruction.IsBranchAndLink)
            {
                block.CallTarget =
                    _reader.GetBranchTarget(
                        currentAddress,
                        instruction);

                AddDelaySlot(block, currentAddress);

                block.FallthroughAddress =
                    currentAddress + 8;

                terminated = true;
                break;
            }

            // Branches relativos: BEQ, BNE, BLEZ, BGTZ, BLTZ, BGEZ,
            // versões likely e BC0/BC1/BC2.
            if (instruction.IsRelativeBranch)
            {
                block.BranchTarget =
                    _reader.GetBranchTarget(
                        currentAddress,
                        instruction);

                AddDelaySlot(block, currentAddress);

                // "B" (BEQ $r, $r) e BGEZ $zero nunca caem adiante.
                if (instruction.IsAlwaysTaken)
                    block.FallthroughAddress = null;
                else
                    block.FallthroughAddress = currentAddress + 8;

                terminated = true;
                break;
            }

            if (IsUnconditionalJump(instruction))
            {
                AddDelaySlot(block, currentAddress);

                if (instruction.Mnemonic == "JR" &&
                    instruction.Rs == 31)
                {
                    block.IsReturn = true;
                }
                else if (instruction.Mnemonic == "J")
                {
                    block.BranchTarget =
                        _reader.GetBranchTarget(
                            currentAddress,
                            instruction);
                }
                else if (instruction.Mnemonic == "JAL")
                {
                    block.CallTarget =
                        _reader.GetBranchTarget(
                            currentAddress,
                            instruction);

                    block.FallthroughAddress =
                        currentAddress + 8;
                }
                else if (instruction.Mnemonic == "JALR" &&
                         instruction.Rd == 31)
                {
                    block.IsIndirectCall = true;

                    block.ResolvedIndirectCallTarget =
                        _indirectCallResolver(
                            currentAddress);

                    block.FallthroughAddress =
                        currentAddress + 8;
                }

                terminated = true;
                break;
            }

            currentAddress += 4;
        }

        // Só é "limite atingido" se o bloco não terminou sozinho.
        if (!terminated)
        {
            block.WasInstructionLimitReached = true;
        }

        return block;
    }

    private void AddDelaySlot(
        MipsBasicBlock block,
        uint branchAddress)
    {
        MipsInstruction delaySlot =
            _reader.Read(branchAddress + 4);

        block.AddInstruction(delaySlot);
    }

    private static bool IsUnconditionalJump(
        MipsInstruction instruction)
    {
        return instruction.Mnemonic switch
        {
            "J" => true,
            "JAL" => true,
            "JR" => true,
            "JALR" => true,
            _ => false
        };
    }
}
