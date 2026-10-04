namespace SH.ELF;

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

        string mnemonic;

        if (opcode == 0x00)
        {
            mnemonic = funct switch
            {
                0x00 => instruction == 0
                    ? "NOP"
                    : "SLL",

                0x02 => "SRL",
                0x03 => "SRA",

                0x04 => "SLLV",
                0x06 => "SRLV",
                0x07 => "SRAV",

                0x08 => "JR",
                0x09 => "JALR",

                0x0C => "SYSCALL",
                0x0F => "SYNC",

                0x21 => "ADDU",
                0x23 => "SUBU",

                0x24 => "AND",
                0x25 => "OR",
                0x26 => "XOR",
                0x27 => "NOR",

                0x2A => "SLT",
                0x2B => "SLTU",

                0x2C => "DADD",
                0x2D => "DADDU",
                0x2E => "DSUB",
                0x2F => "DSUBU",

                0x38 => "DSLL",
                0x3A => "DSRL",
                0x3B => "DSRA",
                0x3C => "DSLL32",
                0x3E => "DSRL32",
                0x3F => "DSRA32",

                _ => "UNKNOWN"
            };
        }
        else
        {
            if (opcode == 0x10 &&
                rs == 16 &&
                funct == 0x38)
            {
                mnemonic = "EI";
            }
            else if (opcode == 0x11)
            {
                mnemonic =
                    DecodeCop1(
                        rs,
                        rt,
                        rd,
                        shamt,
                        funct);
            }
            else
            {
                mnemonic = opcode switch
                {
                    0x01 => "REGIMM",

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

                    0x10 => "COP0",
                    0x12 => "COP2",
                    0x1C => "MMI",

                    0x1E => "LQ",
                    0x1F => "SQ",

                    0x20 => "LB",
                    0x21 => "LH",
                    0x22 => "LWL",
                    0x23 => "LW",
                    0x24 => "LBU",
                    0x25 => "LHU",
                    0x26 => "LWR",

                    0x28 => "SB",
                    0x29 => "SH",
                    0x2A => "SWL",
                    0x2B => "SW",
                    0x2E => "SWR",

                    0x37 => "LD",
                    0x3F => "SD",

                    _ => "UNKNOWN"
                };
            }
        }

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

    private static string DecodeCop1(
        byte rs,
        byte rt,
        byte rd,
        byte shamt,
        byte funct)
    {
        if (rs == 16)
        {
            return funct switch
            {
                0x00 => "ADD.S",
                _ => "COP1"
            };
        }

        return "COP1";
    }
}