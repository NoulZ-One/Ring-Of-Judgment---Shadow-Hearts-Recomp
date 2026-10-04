namespace SH.ELF;

public sealed class MipsProgramBuilder
{
    private readonly MipsFunctionBuilder _functionBuilder;

    public MipsProgramBuilder(
        MipsFunctionBuilder functionBuilder)
    {
        _functionBuilder = functionBuilder;
    }

    public MipsProgram Build(
        IEnumerable<uint> functionAddresses,
        int maxInstructionsPerBlock,
        int maxBlocksPerFunction)
    {
        MipsProgram program =
            new();

        Queue<uint> pendingFunctions =
            new();

        HashSet<uint> discoveredFunctions =
            new();

        foreach (uint address in functionAddresses)
        {
            if (discoveredFunctions.Add(address))
            {
                pendingFunctions.Enqueue(address);
            }
        }

        while (pendingFunctions.Count > 0)
        {
            uint functionAddress =
                pendingFunctions.Dequeue();

            MipsFunction function =
                _functionBuilder.Build(
                    functionAddress,
                    maxInstructionsPerBlock,
                    maxBlocksPerFunction);

            program.AddFunction(function);

            foreach (uint calledAddress in
                function.CalledFunctions)
            {
                if (discoveredFunctions.Add(calledAddress))
                {
                    pendingFunctions.Enqueue(
                        calledAddress);
                }
            }
        }

        return program;
    }
}