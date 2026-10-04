namespace SH.ELF;

public static class MipsRegister
{
    private static readonly string[] Names =
    {
        "$zero",
        "$at",

        "$v0",
        "$v1",

        "$a0",
        "$a1",
        "$a2",
        "$a3",

        "$t0",
        "$t1",
        "$t2",
        "$t3",
        "$t4",
        "$t5",
        "$t6",
        "$t7",

        "$s0",
        "$s1",
        "$s2",
        "$s3",
        "$s4",
        "$s5",
        "$s6",
        "$s7",

        "$t8",
        "$t9",

        "$k0",
        "$k1",

        "$gp",
        "$sp",
        "$fp",
        "$ra"
    };

    public static string GetName(byte register)
    {
        if (register >= Names.Length)
            return $"${register}";

        return Names[register];
    }
}