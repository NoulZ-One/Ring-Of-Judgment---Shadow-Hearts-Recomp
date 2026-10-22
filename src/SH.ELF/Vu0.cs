namespace SH.ELF;

// VU0 em modo macro: instrucoes COP2 com o bit CO ligado (rs >= 0x10).
//
// Campos (iguais aos do COP1): ft = bits 20..16 (Rt), fs = bits 15..11 (Rd),
// fd = bits 10..6 (Shamt). Os bits 24..21 (Rs & 0xF) sao a mascara de destino
// (x=8, y=4, z=2, w=1) ou, em DIV/SQRT/RSQRT/MTIR, os seletores fsf/ftf.
public static class Vu0
{
    // SPECIAL1: indice = funct (0x00..0x3B).
    private static readonly string?[] Special1 =
    {
        "VADDx", "VADDy", "VADDz", "VADDw",
        "VSUBx", "VSUBy", "VSUBz", "VSUBw",
        "VMADDx", "VMADDy", "VMADDz", "VMADDw",
        "VMSUBx", "VMSUBy", "VMSUBz", "VMSUBw",
        "VMAXx", "VMAXy", "VMAXz", "VMAXw",
        "VMINIx", "VMINIy", "VMINIz", "VMINIw",
        "VMULx", "VMULy", "VMULz", "VMULw",
        "VMULq", "VMAXi", "VMULi", "VMINIi",
        "VADDq", "VMADDq", "VADDi", "VMADDi",
        "VSUBq", "VMSUBq", "VSUBi", "VMSUBi",
        "VADD", "VMADD", "VMUL", "VMAX",
        "VSUB", "VMSUB", "VOPMSUB", "VMINI",
        "VIADD", "VISUB", "VIADDI", null,
        "VIAND", "VIOR", null, null,
        "VCALLMS", "VCALLMSR", null, null
    };

    // SPECIAL2: indice = (raw & 3) | ((raw >> 4) & 0x7C), para funct 0x3C..0x3F.
    private static readonly string?[] Special2 =
    {
        "VADDAx", "VADDAy", "VADDAz", "VADDAw",
        "VSUBAx", "VSUBAy", "VSUBAz", "VSUBAw",
        "VMADDAx", "VMADDAy", "VMADDAz", "VMADDAw",
        "VMSUBAx", "VMSUBAy", "VMSUBAz", "VMSUBAw",
        "VITOF0", "VITOF4", "VITOF12", "VITOF15",
        "VFTOI0", "VFTOI4", "VFTOI12", "VFTOI15",
        "VMULAx", "VMULAy", "VMULAz", "VMULAw",
        "VMULAq", "VABS", "VMULAi", "VCLIPw",
        "VADDAq", "VMADDAq", "VADDAi", "VMADDAi",
        "VSUBAq", "VMSUBAq", "VSUBAi", "VMSUBAi",
        "VADDA", "VMADDA", "VMULA", null,
        "VSUBA", "VMSUBA", "VOPMULA", "VNOP",
        "VMOVE", "VMR32", null, null,
        "VLQI", "VSQI", "VLQD", "VSQD",
        "VDIV", "VSQRT", "VRSQRT", "VWAITQ",
        "VMTIR", "VMFIR", "VILWR", "VISWR",
        "VRNEXT", "VRGET", "VRINIT", "VRXOR"
    };

    public static string DecodeMacro(uint raw)
    {
        uint funct = raw & 0x3F;

        if (funct < 0x3C)
            return Special1[funct] ?? "UNKNOWN";

        uint index = (raw & 3) | ((raw >> 4) & 0x7C);

        return index < Special2.Length
            ? Special2[index] ?? "UNKNOWN"
            : "UNKNOWN";
    }

    // Operacoes aritmeticas vetoriais: V + OP [A] + fonte.
    //   OP: ADD SUB MUL MADD MSUB MAX MINI(=MIN)
    //   A (maiusculo no fim do OP): grava no ACC em vez de em fd
    //   fonte: nenhuma = vetor ft, x/y/z/w = broadcast, q = registrador Q, i = registrador I
    public static bool TryParseArith(
        string name,
        out string op,
        out string source,
        out bool toAcc)
    {
        op = "";
        source = "VEC";
        toAcc = false;

        if (name.Length < 4 || name[0] != 'V')
            return false;

        string body = name[1..];

        switch (body[^1])
        {
            case 'x': source = "X"; body = body[..^1]; break;
            case 'y': source = "Y"; body = body[..^1]; break;
            case 'z': source = "Z"; body = body[..^1]; break;
            case 'w': source = "W"; body = body[..^1]; break;
            case 'q': source = "Q"; body = body[..^1]; break;
            case 'i': source = "I"; body = body[..^1]; break;
        }

        if (body.Length > 1 && body[^1] == 'A')
        {
            toAcc = true;
            body = body[..^1];
        }

        switch (body)
        {
            case "ADD": op = "ADD"; return true;
            case "SUB": op = "SUB"; return true;
            case "MUL": op = "MUL"; return true;
            case "MADD": op = "MADD"; return true;
            case "MSUB": op = "MSUB"; return true;
            case "MAX": op = "MAX"; return true;
            case "MINI": op = "MIN"; return true;
            default: return false;
        }
    }

    private static string Lane(int index) => "xyzw"[index & 3].ToString();

    private static string DestSuffix(int dest)
    {
        string text = "";

        if ((dest & 8) != 0) text += "x";
        if ((dest & 4) != 0) text += "y";
        if ((dest & 2) != 0) text += "z";
        if ((dest & 1) != 0) text += "w";

        return "." + text;
    }

    public static string Format(MipsInstruction i)
    {
        string name = i.Mnemonic;

        if (name == "UNKNOWN")
            return $".word 0x{i.Raw:X8}";

        int dest = i.Rs & 0xF;
        int fsf = i.Rs & 3;
        int ftf = (i.Rs >> 2) & 3;

        string d = DestSuffix(dest);
        string fd = $"$vf{i.Shamt}";
        string fs = $"$vf{i.Rd}";
        string ft = $"$vf{i.Rt}";

        if (TryParseArith(name, out _, out string source, out bool toAcc))
        {
            string third = source switch
            {
                "VEC" => ft,
                "Q" => "Q",
                "I" => "I",
                _ => $"{ft}.{source.ToLowerInvariant()}"
            };

            return toAcc
                ? $"{name}{d} ACC, {fs}, {third}"
                : $"{name}{d} {fd}, {fs}, {third}";
        }

        int imm5 = i.Shamt >= 16 ? i.Shamt - 32 : i.Shamt;

        return name switch
        {
            "VNOP" or "VWAITQ" or "VCALLMSR" => name,

            "VOPMSUB" => $"{name}.xyz {fd}, {fs}, {ft}",
            "VOPMULA" => $"{name}.xyz ACC, {fs}, {ft}",

            "VMOVE" or "VMR32" or "VABS" or
            "VITOF0" or "VITOF4" or "VITOF12" or "VITOF15" or
            "VFTOI0" or "VFTOI4" or "VFTOI12" or "VFTOI15" =>
                $"{name}{d} {ft}, {fs}",

            "VCLIPw" => $"{name} {fs}.xyz, {ft}.w",

            "VDIV" or "VRSQRT" =>
                $"{name} Q, {fs}.{Lane(fsf)}, {ft}.{Lane(ftf)}",

            "VSQRT" => $"{name} Q, {ft}.{Lane(ftf)}",

            "VIADD" or "VISUB" or "VIAND" or "VIOR" =>
                $"{name} $vi{i.Shamt}, $vi{i.Rd}, $vi{i.Rt}",

            "VIADDI" => $"{name} $vi{i.Rt}, $vi{i.Rd}, {imm5}",

            "VMTIR" => $"{name} $vi{i.Rt}, {fs}.{Lane(fsf)}",
            "VMFIR" => $"{name}{d} {ft}, $vi{i.Rd}",

            "VCALLMS" => $"{name} 0x{((i.Raw >> 6) & 0x7FFF) * 8:X4}",

            _ => $"{name} 0x{i.Raw & 0x1FFFFFF:X}"
        };
    }
}
