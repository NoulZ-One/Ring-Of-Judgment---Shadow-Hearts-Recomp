namespace SH.ELF;

public sealed class MipsInstruction
{
    private static readonly HashSet<string> RsRtBranches = new()
    {
        "BEQ", "BNE", "BEQL", "BNEL"
    };

    private static readonly HashSet<string> RsBranches = new()
    {
        "BLEZ", "BGTZ", "BLEZL", "BGTZL",
        "BLTZ", "BGEZ", "BLTZL", "BGEZL",
        "BLTZAL", "BGEZAL", "BLTZALL", "BGEZALL"
    };

    private static readonly HashSet<string> CopBranches = new()
    {
        "BC0F", "BC0T", "BC0FL", "BC0TL",
        "BC1F", "BC1T", "BC1FL", "BC1TL",
        "BC2F", "BC2T", "BC2FL", "BC2TL"
    };

    public uint Raw { get; }

    public byte Opcode { get; }

    public byte Rs { get; }

    public byte Rt { get; }

    public byte Rd { get; }

    public byte Shamt { get; }

    public byte Funct { get; }

    public ushort Immediate { get; }

    public short SignedImmediate { get; }

    public uint Target { get; }

    public string Mnemonic { get; }

    public string DebugInfo =>
        $"RAW=0x{Raw:X8} " +
        $"OP=0x{Opcode:X2} " +
        $"RS={Rs} " +
        $"RT={Rt} " +
        $"RD={Rd} " +
        $"SH={Shamt} " +
        $"FUNCT=0x{Funct:X2} " +
        $"IMM=0x{Immediate:X4}";

    public string RsName => MipsRegister.GetName(Rs);

    public string RtName => MipsRegister.GetName(Rt);

    public string RdName => MipsRegister.GetName(Rd);

    public string FsName => MipsFpuRegister.GetName(Rs);

    public string FtName => MipsFpuRegister.GetName(Rt);

    public string FdName => MipsFpuRegister.GetName(Rd);

    // Layout COP1: fmt = rs, ft = rt, fs = rd, fd = shamt.
    public string Cop1FtName => MipsFpuRegister.GetName(Rt);

    public string Cop1FsName => MipsFpuRegister.GetName(Rd);

    public string Cop1FdName => MipsFpuRegister.GetName(Shamt);

    // Instrução que o decoder não reconheceu.
    public bool IsUnknown => Mnemonic == "UNKNOWN";

    // Rótulo genérico de grupo, sem a instrução específica decodificada.
    // O decoder atual não devolve mais nenhum; mantido por compatibilidade.
    public bool IsGenericGroup =>
        Mnemonic is "SPECIAL" or "REGIMM" or "COP0" or "COP1" or
        "COP2" or "MMI" or "VU0";

    // Instrução VU0 em modo macro (COP2 com o bit CO ligado).
    public bool IsVu0Macro => Opcode == 0x12 && (Rs & 0x10) != 0;

    // Qualquer branch relativo (inclui likely, link e BC0/BC1/BC2).
    public bool IsRelativeBranch =>
        RsRtBranches.Contains(Mnemonic) ||
        RsBranches.Contains(Mnemonic) ||
        CopBranches.Contains(Mnemonic);

    // BLTZAL, BGEZAL (BAL quando rs = $zero) e as versões likely.
    public bool IsBranchAndLink =>
        Mnemonic is "BLTZAL" or "BGEZAL" or "BLTZALL" or "BGEZALL";

    // Branch likely: o delay slot só executa se o desvio for tomado.
    public bool IsLikelyBranch =>
        Mnemonic is "BEQL" or "BNEL" or "BLEZL" or "BGTZL" or
        "BLTZL" or "BGEZL" or "BLTZALL" or "BGEZALL" or
        "BC0FL" or "BC0TL" or "BC1FL" or "BC1TL" or
        "BC2FL" or "BC2TL";

    // Desvio incondicional escrito como branch (B, BAL).
    public bool IsAlwaysTaken =>
        ((Mnemonic is "BEQ" or "BEQL") && Rs == Rt) ||
        ((Mnemonic is "BGEZ" or "BGEZL" or "BGEZAL" or "BGEZALL") && Rs == 0);

    // Destino de J/JAL e de branches relativos. Para os demais retorna false.
    public bool TryGetBranchTarget(uint address, out uint target)
    {
        target = 0;

        if (Mnemonic is "J" or "JAL")
        {
            target =
                ((address + 4) & 0xF0000000) |
                (Target << 2);

            return true;
        }

        if (IsRelativeBranch)
        {
            target = unchecked(
                (uint)((int)(address + 4) + SignedImmediate * 4));

            return true;
        }

        return false;
    }

    // Assembly sem endereço: branches mostram o deslocamento em bytes
    // relativo ao delay slot.
    public string Assembly => Render(null);

    // Assembly com endereço: branches e jumps mostram o destino absoluto.
    public string Format(uint address) => Render(address);

    private static string SignedHex(int value) =>
        value < 0
            ? $"-0x{-value:X}"
            : $"0x{value:X}";

    private string BranchText(uint? address)
    {
        if (address.HasValue &&
            TryGetBranchTarget(address.Value, out uint target))
        {
            return $"0x{target:X8}";
        }

        return SignedHex(SignedImmediate * 4);
    }

    private string JumpText(uint? address)
    {
        if (address.HasValue &&
            TryGetBranchTarget(address.Value, out uint target))
        {
            return $"0x{target:X8}";
        }

        uint absolute = Target << 2;

        return $"0x{absolute:X}";
    }

    private string Render(uint? address)
    {
        return Mnemonic switch
        {
            "NOP" => "NOP",

            "UNKNOWN" => $".word 0x{Raw:X8}",

            // rd, rt, sa
            "SLL" or "SRL" or "SRA" or
            "DSLL" or "DSRL" or "DSRA" or
            "DSLL32" or "DSRL32" or "DSRA32" or
            "PSLLH" or "PSRLH" or "PSRAH" or
            "PSLLW" or "PSRLW" or "PSRAW" =>
                $"{Mnemonic} {RdName}, {RtName}, {Shamt}",

            // rd, rt, rs
            "SLLV" or "SRLV" or "SRAV" or
            "DSLLV" or "DSRLV" or "DSRAV" or
            "PSLLVW" or "PSRLVW" or "PSRAVW" =>
                $"{Mnemonic} {RdName}, {RtName}, {RsName}",

            // rt, imm sem sinal
            "LUI" =>
                $"{Mnemonic} {RtName}, 0x{Immediate:X4}",

            "ANDI" or "ORI" or "XORI" =>
                $"{Mnemonic} {RtName}, {RsName}, 0x{Immediate:X4}",

            // rt, rs, imm com sinal
            "ADDI" or "ADDIU" or "DADDI" or "DADDIU" or
            "SLTI" or "SLTIU" =>
                $"{Mnemonic} {RtName}, {RsName}, {SignedHex(SignedImmediate)}",

            // rs, imm
            "TGEI" or "TGEIU" or "TLTI" or "TLTIU" or
            "TEQI" or "TNEI" =>
                $"{Mnemonic} {RsName}, {SignedHex(SignedImmediate)}",

            "MTSAB" or "MTSAH" =>
                $"{Mnemonic} {RsName}, 0x{Immediate:X4}",

            // rs, rt
            "TGE" or "TGEU" or "TLT" or "TLTU" or
            "TEQ" or "TNE" or
            "DIV" or "DIVU" or "DIV1" or "DIVU1" =>
                $"{Mnemonic} {RsName}, {RtName}",

            // rd, rs, rt
            "ADD" or "ADDU" or "SUB" or "SUBU" or
            "DADD" or "DADDU" or "DSUB" or "DSUBU" or
            "AND" or "OR" or "XOR" or "NOR" or
            "SLT" or "SLTU" or "MOVZ" or "MOVN" or
            "MULT" or "MULTU" or "MULT1" or "MULTU1" or
            "MADD" or "MADDU" or "MADD1" or "MADDU1" =>
                $"{Mnemonic} {RdName}, {RsName}, {RtName}",

            // rd
            "MFHI" or "MFLO" or "MFHI1" or "MFLO1" or
            "MFSA" or "PMFHI" or "PMFLO" or
            "PMFHL.LW" or "PMFHL.UW" or "PMFHL.SLW" or
            "PMFHL.LH" or "PMFHL.SH" =>
                $"{Mnemonic} {RdName}",

            // rs
            "MTHI" or "MTLO" or "MTHI1" or "MTLO1" or
            "MTSA" or "PMTHI" or "PMTLO" or "PMTHL.LW" =>
                $"{Mnemonic} {RsName}",

            "PLZCW" =>
                $"{Mnemonic} {RdName}, {RsName}",

            // rd, rt (MMI de dois operandos)
            "PABSW" or "PABSH" or "PEXEH" or "PREVH" or
            "PEXEW" or "PROT3W" or "PEXCH" or "PCPYH" or
            "PEXCW" or "PEXT5" or "PPAC5" =>
                $"{Mnemonic} {RdName}, {RtName}",

            "JR" =>
                $"{Mnemonic} {RsName}",

            "JALR" =>
                $"{Mnemonic} {RdName}, {RsName}",

            "J" or "JAL" =>
                $"{Mnemonic} {JumpText(address)}",

            // Branches
            "BEQ" or "BNE" or "BEQL" or "BNEL" =>
                $"{Mnemonic} {RsName}, {RtName}, {BranchText(address)}",

            "BLEZ" or "BGTZ" or "BLEZL" or "BGTZL" or
            "BLTZ" or "BGEZ" or "BLTZL" or "BGEZL" or
            "BLTZAL" or "BGEZAL" or "BLTZALL" or "BGEZALL" =>
                $"{Mnemonic} {RsName}, {BranchText(address)}",

            "BC0F" or "BC0T" or "BC0FL" or "BC0TL" or
            "BC1F" or "BC1T" or "BC1FL" or "BC1TL" or
            "BC2F" or "BC2T" or "BC2FL" or "BC2TL" =>
                $"{Mnemonic} {BranchText(address)}",

            // Memória
            "LB" or "LH" or "LWL" or "LW" or "LBU" or "LHU" or
            "LWR" or "LWU" or "LD" or "LDL" or "LDR" or "LQ" or
            "SB" or "SH" or "SWL" or "SW" or "SWR" or
            "SD" or "SDL" or "SDR" or "SQ" =>
                $"{Mnemonic} {RtName}, {SignedImmediate}({RsName})",

            "LWC1" or "SWC1" =>
                $"{Mnemonic} {MipsFpuRegister.GetName(Rt)}, {SignedImmediate}({RsName})",

            "LQC2" or "SQC2" =>
                $"{Mnemonic} $vf{Rt}, {SignedImmediate}({RsName})",

            "CACHE" or "PREF" =>
                $"{Mnemonic} {Rt}, {SignedImmediate}({RsName})",

            // COP0
            "MFC0" or "MTC0" =>
                $"{Mnemonic} {RtName}, $c0_{Rd}",

            // COP1
            "MFC1" or "MTC1" =>
                $"{Mnemonic} {RtName}, {Cop1FsName}",

            "CFC1" or "CTC1" =>
                $"{Mnemonic} {RtName}, $fcr{Rd}",

            "ADD.S" or "SUB.S" or "MUL.S" or "DIV.S" or
            "RSQRT.S" or "MADD.S" or "MSUB.S" or
            "MAX.S" or "MIN.S" =>
                $"{Mnemonic} {Cop1FdName}, {Cop1FsName}, {Cop1FtName}",

            "ABS.S" or "MOV.S" or "NEG.S" or
            "CVT.W.S" or "CVT.S.W" =>
                $"{Mnemonic} {Cop1FdName}, {Cop1FsName}",

            "SQRT.S" =>
                $"{Mnemonic} {Cop1FdName}, {Cop1FtName}",

            "ADDA.S" or "SUBA.S" or "MULA.S" or
            "MADDA.S" or "MSUBA.S" or
            "C.F.S" or "C.EQ.S" or "C.LT.S" or "C.LE.S" =>
                $"{Mnemonic} {Cop1FsName}, {Cop1FtName}",

            // COP2 (VU0)
            "QMFC2" or "QMTC2" =>
                $"{Mnemonic} {RtName}, $vf{Rd}",

            "CFC2" or "CTC2" =>
                $"{Mnemonic} {RtName}, $vi{Rd}",

            _ when IsVu0Macro => Vu0.Format(this),

            // MMI de três operandos (rd, rs, rt): PADDW, PAND, QFSRV...
            _ when Mnemonic.StartsWith('P') || Mnemonic == "QFSRV" =>
                $"{Mnemonic} {RdName}, {RsName}, {RtName}",

            _ => Mnemonic
        };
    }

    public MipsInstruction(
        uint raw,
        byte opcode,
        byte rs,
        byte rt,
        byte rd,
        byte shamt,
        byte funct,
        ushort immediate,
        short signedImmediate,
        uint target,
        string mnemonic)
    {
        Raw = raw;

        Opcode = opcode;

        Rs = rs;
        Rt = rt;
        Rd = rd;

        Shamt = shamt;
        Funct = funct;

        Immediate = immediate;
        SignedImmediate = signedImmediate;

        Target = target;

        Mnemonic = mnemonic;
    }
}
