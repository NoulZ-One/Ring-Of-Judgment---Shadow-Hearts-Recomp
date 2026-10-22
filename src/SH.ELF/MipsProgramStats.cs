namespace SH.ELF;

// Estatísticas de um MipsProgram, contando cada endereço de instrução uma única vez
// (blocos de funções diferentes podem compartilhar endereços).
public sealed class MipsProgramStats
{
    public int Functions { get; private set; }

    public int Blocks { get; private set; }

    public int Instructions => ReachedAddresses.Count;

    public int Unknown { get; private set; }

    public int Generic { get; private set; }

    public int InvalidBlocks { get; private set; }

    public int WithoutReturn { get; private set; }

    public HashSet<uint> ReachedAddresses { get; } = new();

    public static MipsProgramStats From(MipsProgram program)
    {
        MipsProgramStats stats = new();

        foreach (MipsFunction function in program.Functions.Values)
        {
            stats.Functions++;

            if (!function.HasReturn)
                stats.WithoutReturn++;

            foreach (MipsBasicBlock block in function.BasicBlocks)
            {
                stats.Blocks++;

                if (block.IsInvalid)
                    stats.InvalidBlocks++;

                for (int index = 0; index < block.Instructions.Count; index++)
                {
                    uint address = block.StartAddress + (uint)(index * 4);

                    if (!stats.ReachedAddresses.Add(address))
                        continue;

                    MipsInstruction instruction = block.Instructions[index];

                    if (instruction.IsUnknown)
                        stats.Unknown++;

                    if (instruction.IsGenericGroup)
                        stats.Generic++;
                }
            }
        }

        return stats;
    }
}
