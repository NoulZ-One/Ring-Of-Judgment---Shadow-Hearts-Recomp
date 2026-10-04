namespace SH.ELF;

public sealed class MipsFunctionBuilder
{
    private readonly MipsBasicBlockBuilder _blockBuilder;

    public MipsFunctionBuilder(
        MipsBasicBlockBuilder blockBuilder)
    {
        _blockBuilder = blockBuilder;
    }

    public MipsFunction Build(
        uint startAddress,
        int maxInstructionsPerBlock,
        int maxBlocks)
    {
        MipsFunction function =
            new(startAddress);

        Queue<uint> pendingAddresses =
            new();

        HashSet<uint> visitedAddresses =
            new();

        pendingAddresses.Enqueue(startAddress);

        while (pendingAddresses.Count > 0 &&
               function.BasicBlocks.Count < maxBlocks)
        {
            uint address =
                pendingAddresses.Dequeue();

            if (!visitedAddresses.Add(address))
                continue;

            MipsBasicBlock block =
                _blockBuilder.Build(
                    address,
                    maxInstructionsPerBlock);

            function.AddBasicBlock(block);

        if (block.IsReturn)
        {
            function.HasReturn = true;
        }

        if (block.WasInstructionLimitReached)
        {
            function.WasInstructionLimitReached = true;
        }

        if (block.CallTarget.HasValue)
        {
                function.AddCalledFunction(
                    block.CallTarget.Value);
        }

        if (block.BranchTarget.HasValue)
        {
                pendingAddresses.Enqueue(
                    block.BranchTarget.Value);
        }

        if (block.FallthroughAddress.HasValue)
        {
                pendingAddresses.Enqueue(
                    block.FallthroughAddress.Value);
        }
        }

        if (function.BasicBlocks.Count >= maxBlocks)
        {
            function.WasBlockLimitReached = true;
        }

        return function;
    }
}