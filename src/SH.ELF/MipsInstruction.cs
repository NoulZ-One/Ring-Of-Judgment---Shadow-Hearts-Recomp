namespace SH.ELF;

public sealed class MipsInstruction
{
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

    public string Cop1FsName => MipsFpuRegister.GetName(Rt);

    public string Cop1FtName => MipsFpuRegister.GetName(Shamt);

    public string Cop1FdName => MipsFpuRegister.GetName(Rd);

    public string Assembly
    {
        get
        {
            return Mnemonic switch
            {
                "ADD.S" =>
                    $"{Mnemonic} {Cop1FdName}, {Cop1FsName}, {Cop1FtName}",

                "SLL" or "SRL" or "SRA" or 
                "DSLL" or "DSRL" or "DSRA" or 
                "DSLL32" or "DSRL32" or "DSRA32"  =>
                    $"{Mnemonic} {RdName}, {RtName}, {Shamt}",

                "SLLV" or "SRLV" or "SRAV" =>
                    $"{Mnemonic} {RdName}, {RtName}, {RsName}",

                "LUI" =>
                    $"{Mnemonic} {RtName}, 0x{Immediate:X4}",

                "ADDI" or "ADDIU" or "SLTI" or "SLTIU" =>
                    $"{Mnemonic} {RtName}, {RsName}, 0x{Immediate:X4}",

                "ANDI" or "ORI" or "XORI" =>
                    $"{Mnemonic} {RtName}, {RsName}, 0x{Immediate:X4}",

                "ADDU" or "SUBU" or "DSUBU" or
                "AND" or "OR" or
                "XOR" or "NOR" or
                "SLT" or "SLTU" =>
                    $"{Mnemonic} {RdName}, {RsName}, {RtName}",

                "JR" =>
                    $"{Mnemonic} {RsName}",

                "JALR" =>
                    $"{Mnemonic} {RdName}, {RsName}",

                "BEQ" or "BNE" =>
                    $"{Mnemonic} {RsName}, {RtName}",

                "BLEZ" or "BGTZ" =>
                    $"{Mnemonic} {RsName}",

                "LB" or "LH" or "LWL" or "LW" or
                "LBU" or "LHU" or "LWR" or
                "SB" or "SH" or "SWL" or "SW" or "SWR" or
                "LD" or "SD" or "SQ" or "LQ" =>
                    $"{Mnemonic} {RtName}, {SignedImmediate}({RsName})",

                _ =>
                    Mnemonic
            };
        }
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