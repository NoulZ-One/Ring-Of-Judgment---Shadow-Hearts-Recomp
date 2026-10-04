namespace SH.ELF;

public static class MipsFpuRegister
{
    public static string GetName(byte register)
    {
        if (register >= 32)
            return $"$f{register}";

        return $"$f{register}";
    }
}