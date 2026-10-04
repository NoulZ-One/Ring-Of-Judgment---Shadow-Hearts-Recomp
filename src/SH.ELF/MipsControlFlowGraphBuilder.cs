
namespace SH.ELF;

public sealed class MipsControlFlowGraphBuilder
{
    private readonly MipsBasicBlockBuilder _blockBuilder;

    public MipsControlFlowGraphBuilder(
        MipsBasicBlockBuilder blockBuilder)
    {
        _blockBuilder = blockBuilder;
    }

    public MipsControlFlowGraph Build(
        uint entryAddress,
        int maxInstructionsPerBlock,
        int maxBlocks)
    {
        MipsControlFlowGraph graph = new();

        Queue<uint> pendingAddresses = new();
        HashSet<uint> visitedAddresses = new();

        pendingAddresses.Enqueue(entryAddress);

        while (pendingAddresses.Count > 0 &&
               graph.Blocks.Count < maxBlocks)
        {
            uint address = pendingAddresses.Dequeue();

            if (!visitedAddresses.Add(address))
                continue;

            MipsBasicBlock block =
                _blockBuilder.Build(
                    address,
                    maxInstructionsPerBlock);

            graph.AddBlock(block);

            if (block.IsReturn)
                continue;

            if (block.BranchTarget.HasValue)
{
            pendingAddresses.Enqueue(
                block.BranchTarget.Value);
}

            if (block.CallTarget.HasValue)
{
                pendingAddresses.Enqueue(
                    block.CallTarget.Value);
}

            if (block.FallthroughAddress.HasValue)
{
            pendingAddresses.Enqueue(
                block.FallthroughAddress.Value);
}
        }

        return graph;
    }
}