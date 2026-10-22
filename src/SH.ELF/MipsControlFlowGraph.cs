
namespace SH.ELF;

public sealed class MipsControlFlowGraph
{
    private readonly Dictionary<uint, MipsBasicBlock> _blocks = new();

    public IReadOnlyDictionary<uint, MipsBasicBlock> Blocks =>
        _blocks;

    public void AddBlock(MipsBasicBlock block)
    {
        if (_blocks.ContainsKey(block.StartAddress))
            return;

        _blocks.Add(block.StartAddress, block);
    }

    public bool TryGetBlock(
        uint address,
        out MipsBasicBlock? block)
    {
        return _blocks.TryGetValue(address, out block);
    }
}