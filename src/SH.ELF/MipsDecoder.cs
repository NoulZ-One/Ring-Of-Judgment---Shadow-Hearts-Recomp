namespace SH.ELF;

// Decoder do Emotion Engine (MIPS R5900): MIPS III + MMI + COP0/COP1/COP2.
// O VU0 em modo macro (COP2 com CO=1) e decodificado em Vu0.cs.
public static class MipsDecoder
{
    public static MipsInstruction Decode(uint instruction)
    {
        byte opcode =
            (byte)((instruction >> 26) & 0x3F);

        byte rs =
            (byte)((instruction >> 21) & 0x1F);

        byte rt =
            (byte)((instruction >> 16) & 0x1F);

        byte rd =
            (byte)((instruction >> 11) & 0x1F);

        byte shamt =
            (byte)((instruction >> 6) & 0x1F);

        byte funct =
            (byte)(instruction & 0x3F);

        ushort immediate =
            (ushort)(instruction & 0xFFFF);

        short signedImmediate =
            (short)immediate;

        uint target =
            instruction & 0x03FFFFFF;

        string mnemonic = opcode switch
        {
            0x00 => DecodeSpecial(instruction, funct),
            0x01 => DecodeRegimm(rt),

            0x02 => "J",
            0x03 => "JAL",

            0x04 => "BEQ",
            0x05 => "BNE",
            0x06 => "BLEZ",
            0x07 => "BGTZ",

            0x08 => "ADDI",
            0x09 => "ADDIU",
            0x0A => "SLTI",
            0x0B => "SLTIU",

            0x0C => "ANDI",
            0x0D => "ORI",
            0x0E => "XORI",
            0x0F => "LUI",

            0x10 => DecodeCop0(rs, rt, funct),
            0x11 => DecodeCop1(rs, rt, funct),
            0x12 => DecodeCop2(instruction, rs, rt),

            0x14 => "BEQL",
            0x15 => "BNEL",
            0x16 => "BLEZL",
            0x17 => "BGTZL",

            0x18 => "DADDI",
            0x19 => "DADDIU",
            0x1A => "LDL",
            0x1B => "LDR",

            0x1C => DecodeMmi(funct, shamt),

            0x1E => "LQ",
            0x1F => "SQ",

            0x20 => "LB",
            0x21 => "LH",
            0x22 => "LWL",
            0x23 => "LW",
            0x24 => "LBU",
            0x25 => "LHU",
            0x26 => "LWR",
            0x27 => "LWU",

            0x28 => "SB",
            0x29 => "SH",
            0x2A => "SWL",
            0x2B => "SW",
            0x2C => "SDL",
            0x2D => "SDR",
            0x2E => "SWR",
            0x2F => "CACHE",

            0x31 => "LWC1",
            0x33 => "PREF",
            0x36 => "LQC2",
            0x37 => "LD",

            0x39 => "SWC1",
            0x3E => "SQC2",
            0x3F => "SD",

            _ => "UNKNOWN"
        };

        return new MipsInstruction(
            instruction,
            opcode,
            rs,
            rt,
            rd,
            shamt,
            funct,
            immediate,
            signedImmediate,
            target,
            mnemonic);
    }

    private static string DecodeSpecial(uint instruction, byte funct)
    {
        if (funct == 0x00)
        {
            return instruction == 0
                ? "NOP"
                : "SLL";
        }

        return funct switch
        {
            0x02 => "SRL",
            0x03 => "SRA",

            0x04 => "SLLV",
            0x06 => "SRLV",
            0x07 => "SRAV",

            0x08 => "JR",
            0x09 => "JALR",

            0x0A => "MOVZ",
            0x0B => "MOVN",

            0x0C => "SYSCALL",
            0x0D => "BREAK",
            0x0F => "SYNC",

            0x10 => "MFHI",
            0x11 => "MTHI",
            0x12 => "MFLO",
            0x13 => "MTLO",

            0x14 => "DSLLV",
            0x16 => "DSRLV",
            0x17 => "DSRAV",

            0x18 => "MULT",
            0x19 => "MULTU",
            0x1A => "DIV",
            0x1B => "DIVU",

            0x20 => "ADD",
            0x21 => "ADDU",
            0x22 => "SUB",
            0x23 => "SUBU",

            0x24 => "AND",
            0x25 => "OR",
            0x26 => "XOR",
            0x27 => "NOR",

            0x28 => "MFSA",
            0x29 => "MTSA",

            0x2A => "SLT",
            0x2B => "SLTU",

            0x2C => "DADD",
            0x2D => "DADDU",
            0x2E => "DSUB",
            0x2F => "DSUBU",

            0x30 => "TGE",
            0x31 => "TGEU",
            0x32 => "TLT",
            0x33 => "TLTU",
            0x34 => "TEQ",
            0x36 => "TNE",

            0x38 => "DSLL",
            0x3A => "DSRL",
            0x3B => "DSRA",
            0x3C => "DSLL32",
            0x3E => "DSRL32",
            0x3F => "DSRA32",

            _ => "UNKNOWN"
        };
    }

    // O campo rt escolhe a instrução.
    private static string DecodeRegimm(byte rt)
    {
        return rt switch
        {
            0x00 => "BLTZ",
            0x01 => "BGEZ",
            0x02 => "BLTZL",
            0x03 => "BGEZL",

            0x08 => "TGEI",
            0x09 => "TGEIU",
            0x0A => "TLTI",
            0x0B => "TLTIU",
            0x0C => "TEQI",
            0x0E => "TNEI",

            0x10 => "BLTZAL",
            0x11 => "BGEZAL",
            0x12 => "BLTZALL",
            0x13 => "BGEZALL",

            0x18 => "MTSAB",
            0x19 => "MTSAH",

            _ => "UNKNOWN"
        };
    }

    private static string DecodeCop0(byte rs, byte rt, byte funct)
    {
        if (rs == 0x00)
            return "MFC0";

        if (rs == 0x04)
            return "MTC0";

        if (rs == 0x08)
        {
            return rt switch
            {
                0 => "BC0F",
                1 => "BC0T",
                2 => "BC0FL",
                3 => "BC0TL",
                _ => "UNKNOWN"
            };
        }

        // Bit CO (bit 25) ligado: rs >= 0x10.
        if ((rs & 0x10) != 0)
        {
            return funct switch
            {
                0x01 => "TLBR",
                0x02 => "TLBWI",
                0x06 => "TLBWR",
                0x08 => "TLBP",
                0x18 => "ERET",
                0x38 => "EI",
                0x39 => "DI",
                _ => "UNKNOWN"
            };
        }

        return "UNKNOWN";
    }

    // Layout COP1: fmt = rs, ft = rt, fs = rd, fd = shamt.
    private static string DecodeCop1(byte rs, byte rt, byte funct)
    {
        switch (rs)
        {
            case 0x00: return "MFC1";
            case 0x02: return "CFC1";
            case 0x04: return "MTC1";
            case 0x06: return "CTC1";

            case 0x08:
                return rt switch
                {
                    0 => "BC1F",
                    1 => "BC1T",
                    2 => "BC1FL",
                    3 => "BC1TL",
                    _ => "UNKNOWN"
                };

            // fmt S (single)
            case 0x10:
                return funct switch
                {
                    0x00 => "ADD.S",
                    0x01 => "SUB.S",
                    0x02 => "MUL.S",
                    0x03 => "DIV.S",
                    0x04 => "SQRT.S",
                    0x05 => "ABS.S",
                    0x06 => "MOV.S",
                    0x07 => "NEG.S",

                    0x16 => "RSQRT.S",

                    0x18 => "ADDA.S",
                    0x19 => "SUBA.S",
                    0x1A => "MULA.S",
                    0x1C => "MADD.S",
                    0x1D => "MSUB.S",
                    0x1E => "MADDA.S",
                    0x1F => "MSUBA.S",

                    0x24 => "CVT.W.S",

                    0x28 => "MAX.S",
                    0x29 => "MIN.S",

                    0x30 => "C.F.S",
                    0x32 => "C.EQ.S",
                    0x34 => "C.LT.S",
                    0x36 => "C.LE.S",

                    _ => "UNKNOWN"
                };

            // fmt W (word)
            case 0x14:
                return funct == 0x20
                    ? "CVT.S.W"
                    : "UNKNOWN";

            default:
                return "UNKNOWN";
        }
    }

    private static string DecodeCop2(uint instruction, byte rs, byte rt)
    {
        switch (rs)
        {
            case 0x01: return "QMFC2";
            case 0x02: return "CFC2";
            case 0x05: return "QMTC2";
            case 0x06: return "CTC2";

            case 0x08:
                return rt switch
                {
                    0 => "BC2F",
                    1 => "BC2T",
                    2 => "BC2FL",
                    3 => "BC2TL",
                    _ => "UNKNOWN"
                };

            default:
                // Instruções VU0 macro (CO = 1).
                return rs >= 0x10
                    ? Vu0.DecodeMacro(instruction)
                    : "UNKNOWN";
        }
    }

    // Opcode 0x1C. O funct escolhe a instrução; para MMI0..MMI3 e
    // PMFHL o campo shamt escolhe a sub-instrução.
    private static string DecodeMmi(byte funct, byte shamt)
    {
        switch (funct)
        {
            case 0x08: return DecodeMmi0(shamt);
            case 0x28: return DecodeMmi1(shamt);
            case 0x09: return DecodeMmi2(shamt);
            case 0x29: return DecodeMmi3(shamt);

            case 0x30:
                return shamt switch
                {
                    0 => "PMFHL.LW",
                    1 => "PMFHL.UW",
                    2 => "PMFHL.SLW",
                    3 => "PMFHL.LH",
                    4 => "PMFHL.SH",
                    _ => "UNKNOWN"
                };

            case 0x31:
                return shamt == 0
                    ? "PMTHL.LW"
                    : "UNKNOWN";

            default:
                return funct switch
                {
                    0x00 => "MADD",
                    0x01 => "MADDU",
                    0x04 => "PLZCW",

                    0x10 => "MFHI1",
                    0x11 => "MTHI1",
                    0x12 => "MFLO1",
                    0x13 => "MTLO1",

                    0x18 => "MULT1",
                    0x19 => "MULTU1",
                    0x1A => "DIV1",
                    0x1B => "DIVU1",

                    0x20 => "MADD1",
                    0x21 => "MADDU1",

                    0x34 => "PSLLH",
                    0x36 => "PSRLH",
                    0x37 => "PSRAH",

                    0x3C => "PSLLW",
                    0x3E => "PSRLW",
                    0x3F => "PSRAW",

                    _ => "UNKNOWN"
                };
        }
    }

    private static string DecodeMmi0(byte shamt)
    {
        return shamt switch
        {
            0x00 => "PADDW",
            0x01 => "PSUBW",
            0x02 => "PCGTW",
            0x03 => "PMAXW",

            0x04 => "PADDH",
            0x05 => "PSUBH",
            0x06 => "PCGTH",
            0x07 => "PMAXH",

            0x08 => "PADDB",
            0x09 => "PSUBB",
            0x0A => "PCGTB",

            0x10 => "PADDSW",
            0x11 => "PSUBSW",
            0x12 => "PEXTLW",
            0x13 => "PPACW",

            0x14 => "PADDSH",
            0x15 => "PSUBSH",
            0x16 => "PEXTLH",
            0x17 => "PPACH",

            0x18 => "PADDSB",
            0x19 => "PSUBSB",
            0x1A => "PEXTLB",
            0x1B => "PPACB",

            0x1E => "PEXT5",
            0x1F => "PPAC5",

            _ => "UNKNOWN"
        };
    }

    private static string DecodeMmi1(byte shamt)
    {
        return shamt switch
        {
            0x01 => "PABSW",
            0x02 => "PCEQW",
            0x03 => "PMINW",

            0x04 => "PADSBH",
            0x05 => "PABSH",
            0x06 => "PCEQH",
            0x07 => "PMINH",

            0x0A => "PCEQB",

            0x10 => "PADDUW",
            0x11 => "PSUBUW",
            0x12 => "PEXTUW",

            0x14 => "PADDUH",
            0x15 => "PSUBUH",
            0x16 => "PEXTUH",

            0x18 => "PADDUB",
            0x19 => "PSUBUB",
            0x1A => "PEXTUB",
            0x1B => "QFSRV",

            _ => "UNKNOWN"
        };
    }

    private static string DecodeMmi2(byte shamt)
    {
        return shamt switch
        {
            0x00 => "PMADDW",
            0x02 => "PSLLVW",
            0x03 => "PSRLVW",
            0x04 => "PMSUBW",

            0x08 => "PMFHI",
            0x09 => "PMFLO",
            0x0A => "PINTH",

            0x0C => "PMULTW",
            0x0D => "PDIVW",
            0x0E => "PCPYLD",

            0x10 => "PMADDH",
            0x11 => "PHMADH",
            0x12 => "PAND",
            0x13 => "PXOR",

            0x14 => "PMSUBH",
            0x15 => "PHMSBH",

            0x1A => "PEXEH",
            0x1B => "PREVH",
            0x1C => "PMULTH",
            0x1D => "PDIVBW",
            0x1E => "PEXEW",
            0x1F => "PROT3W",

            _ => "UNKNOWN"
        };
    }

    private static string DecodeMmi3(byte shamt)
    {
        return shamt switch
        {
            0x00 => "PMADDUW",
            0x03 => "PSRAVW",

            0x08 => "PMTHI",
            0x09 => "PMTLO",
            0x0A => "PINTEH",

            0x0C => "PMULTUW",
            0x0D => "PDIVUW",
            0x0E => "PCPYUD",

            0x12 => "POR",
            0x13 => "PNOR",

            0x1A => "PEXCH",
            0x1B => "PCPYH",
            0x1E => "PEXCW",

            _ => "UNKNOWN"
        };
    }
}
