namespace SH.ELF;

public sealed class MipsProgram
{
    public Dictionary<uint, MipsFunction> Functions { get; }

    public MipsProgram()
    {
        Functions =
            new Dictionary<uint, MipsFunction>();
    }

    public void AddFunction(
        MipsFunction function)
    {
        if (Functions.ContainsKey(function.StartAddress))
            return;

        Functions.Add(
            function.StartAddress,
            function);
    }

    public bool TryGetFunction(
        uint address,
        out MipsFunction? function)
    {
        return Functions.TryGetValue(
            address,
            out function);
    }
}