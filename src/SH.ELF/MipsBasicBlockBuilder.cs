namespace SH.ELF;

public sealed class MipsBasicBlockBuilder
{
    private readonly MipsInstructionReader _reader;

    public MipsBasicBlockBuilder(
        MipsInstructionReader reader)
    {
        _reader = reader;
    }

    public MipsBasicBlock Build(
        uint startAddress,
        int maxInstructions)
    {
        MipsBasicBlock block =
            new(startAddress);

        uint currentAddress =
            startAddress;

        for (int i = 0; i < maxInstructions; i++)
        {
            MipsInstruction instruction =
                _reader.Read(currentAddress);

            block.AddInstruction(instruction);

            if (IsConditionalBranch(instruction))
            {
                uint? branchTarget =
                    _reader.GetBranchTarget(
                        currentAddress,
                        instruction);

                block.BranchTarget =
                    branchTarget;

                uint delaySlotAddress =
                    currentAddress + 4;

                MipsInstruction delaySlot =
                    _reader.Read(delaySlotAddress);

                block.AddInstruction(delaySlot);

                block.FallthroughAddress =
                    currentAddress + 8;

                break;
            }

            if (IsUnconditionalJump(instruction))
{
    uint delaySlotAddress =
        currentAddress + 4;

    MipsInstruction delaySlot =
        _reader.Read(delaySlotAddress);

    block.AddInstruction(delaySlot);

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

        block.FallthroughAddress =
            currentAddress + 8;
    }

    break;
}

            currentAddress += 4;
        }

        if (block.Instructions.Count >= maxInstructions)
        {
            block.WasInstructionLimitReached = true;
        }

        return block;

    }

    private static bool IsConditionalBranch(
        MipsInstruction instruction)
    {
        return instruction.Mnemonic switch
        {
            "BEQ" => true,
            "BNE" => true,
            "BLEZ" => true,
            "BGTZ" => true,
            _ => false
        };
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