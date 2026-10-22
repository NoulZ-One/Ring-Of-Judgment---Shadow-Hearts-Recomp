namespace SH.ELF;

// Resolve JR em registrador que nao e $ra quando ele e o despacho de um switch
// (jump table). O padrao procurado, em qualquer ordem das instrucoes:
//
//   sltiu $at, $idx, N        ; limite (opcional)
//   sll   $v0, $idx, 2
//   lui   $at, hi             ; base da tabela (constante: LUI [+ ADDIU/ORI])
//   addu  $at, $at, $v0
//   lw    $v0, lo($at)
//   jr    $v0
//
// O endereco da tabela e conferido contra as relocacoes R_MIPS_32 do ELF:
// cada entrada de uma tabela real tem uma relocacao nessa palavra. Os destinos
// sao os valores dessas palavras, que ja estao resolvidos na imagem.
public sealed class JumpTableResolver
{
    public sealed record Table(
        uint JrAddress,
        uint TableAddress,
        IReadOnlyList<uint> Targets,
        int DeclaredEntries);

    private const int Window = 48;
    private const int MaxEntries = 4096;

    private readonly ElfImage _image;
    private readonly HashSet<uint> _pointers = new();

    public JumpTableResolver(ElfImage image, IEnumerable<ElfRelocation> relocations)
    {
        _image = image;

        foreach (ElfRelocation relocation in relocations)
        {
            // R_MIPS_32
            if (relocation.Type == 2)
                _pointers.Add(relocation.Offset);
        }
    }

    public int PointerCount => _pointers.Count;

    // Registrador escrito pela instrucao, ou null.
    private static int? Writes(MipsInstruction i)
    {
        switch (i.Mnemonic)
        {
            case "ADD": case "ADDU": case "SUB": case "SUBU":
            case "AND": case "OR": case "XOR": case "NOR":
            case "SLT": case "SLTU":
            case "SLL": case "SRL": case "SRA":
            case "SLLV": case "SRLV": case "SRAV":
            case "DADD": case "DADDU": case "DSUB": case "DSUBU":
            case "DSLL": case "DSRL": case "DSRA":
            case "DSLL32": case "DSRL32": case "DSRA32":
            case "DSLLV": case "DSRLV": case "DSRAV":
            case "MOVZ": case "MOVN":
            case "MFHI": case "MFLO": case "MFHI1": case "MFLO1":
            case "JALR":
                return i.Rd;

            case "ADDIU": case "ADDI": case "DADDIU": case "DADDI":
            case "SLTI": case "SLTIU":
            case "ANDI": case "ORI": case "XORI": case "LUI":
            case "LB": case "LBU": case "LH": case "LHU":
            case "LW": case "LWU": case "LD": case "LQ":
            case "LWL": case "LWR": case "LDL": case "LDR":
            case "MFC0": case "MFC1": case "CFC1": case "CFC2": case "QMFC2":
                return i.Rt;

            case "JAL":
                return 31;
        }

        // MMI escreve rd (PADDUB rd, rs, $zero e o move de 128 bits).
        if (i.Mnemonic.StartsWith('P') && i.Mnemonic != "PREF")
            return i.Rd;

        return null;
    }

    private static int FindWriter(MipsInstruction[] code, int before, int register)
    {
        if (register == 0)
            return -1;

        for (int k = before - 1; k >= 0 && k >= before - Window; k--)
        {
            if (Writes(code[k]) == register)
                return k;
        }

        return -1;
    }

    // Move de registrador: ADDU/OR/PADDUB rd, rs, $zero.
    private static bool IsMove(MipsInstruction i) =>
        i.Rt == 0 && (i.Mnemonic is "ADDU" or "OR" or "DADDU" or "PADDUB");

    // Como FindWriter, mas atravessa moves ate a definicao real do valor.
    private static int FindDefinition(MipsInstruction[] code, int before, int register)
    {
        for (int hop = 0; hop < 4; hop++)
        {
            int k = FindWriter(code, before, register);

            if (k < 0)
                return -1;

            if (!IsMove(code[k]))
                return k;

            before = k;
            register = code[k].Rs;

            if (register == 0)
                return -1;
        }

        return -1;
    }

    // Valor constante de um registrador antes da instrucao `before`.
    private static bool TryConstant(
        MipsInstruction[] code,
        int before,
        int register,
        int depth,
        out uint value)
    {
        value = 0;

        if (register == 0)
            return true;

        if (depth > 4)
            return false;

        int k = FindWriter(code, before, register);

        if (k < 0)
            return false;

        MipsInstruction w = code[k];

        if (w.Mnemonic == "LUI")
        {
            value = (uint)w.Immediate << 16;
            return true;
        }

        if (w.Mnemonic is "ADDIU" or "ADDI" or "ORI")
        {
            if (!TryConstant(code, k, w.Rs, depth + 1, out uint source))
                return false;

            value = w.Mnemonic == "ORI"
                ? source | w.Immediate
                : unchecked(source + (uint)(int)w.SignedImmediate);

            return true;
        }

        if (IsMove(w))
            return TryConstant(code, k, w.Rs, depth + 1, out value);

        return false;
    }

    public bool TryResolve(
        MipsInstruction[] code,
        int jrIndex,
        uint functionStart,
        uint functionEnd,
        out Table? table,
        out string reason)
    {
        table = null;
        reason = "";

        MipsInstruction jr = code[jrIndex];
        uint jrAddress = functionStart + (uint)(jrIndex * 4);

        // 1) o registrador do JR vem de um LW
        int loadIndex = FindDefinition(code, jrIndex, jr.Rs);

        if (loadIndex < 0)
        {
            reason = "registrador do JR sem escritor proximo";
            return false;
        }

        MipsInstruction load = code[loadIndex];

        if (load.Mnemonic is not ("LW" or "LWU"))
        {
            reason = $"registrador do JR escrito por {load.Mnemonic}";
            return false;
        }

        // 2) o endereco do LW e base constante + indice
        int addIndex = FindDefinition(code, loadIndex, load.Rs);

        if (addIndex < 0)
        {
            reason = "base do LW sem escritor proximo";
            return false;
        }

        MipsInstruction add = code[addIndex];

        if (add.Mnemonic is not ("ADDU" or "ADD" or "DADDU"))
        {
            reason = $"base do LW escrita por {add.Mnemonic}";
            return false;
        }

        bool firstIsConstant = TryConstant(code, addIndex, add.Rs, 0, out uint first);
        bool secondIsConstant = TryConstant(code, addIndex, add.Rt, 0, out uint second);

        if (firstIsConstant == secondIsConstant)
        {
            reason = "soma sem exatamente uma parcela constante";
            return false;
        }

        uint constant = firstIsConstant ? first : second;

        uint tableAddress = unchecked(
            constant + (uint)(int)load.SignedImmediate);

        // 3) a tabela tem que ter relocacao R_MIPS_32 na primeira palavra
        if (!_pointers.Contains(tableAddress))
        {
            reason = $"sem relocacao R_MIPS_32 em 0x{tableAddress:X8}";
            return false;
        }

        // 4) limite declarado: SLTIU $x, $idx, N antes do despacho
        int declared = int.MaxValue;

        for (int k = jrIndex - 1; k >= 0 && k >= jrIndex - Window; k--)
        {
            if (code[k].Mnemonic == "SLTIU")
            {
                declared = code[k].Immediate;
                break;
            }
        }

        // 5) entradas: palavras consecutivas com relocacao, apontando para a funcao
        List<uint> targets = new();
        HashSet<uint> seen = new();

        for (int n = 0; n < Math.Min(declared, MaxEntries); n++)
        {
            uint entryAddress = tableAddress + (uint)(n * 4);

            if (!_pointers.Contains(entryAddress))
                break;

            if (!_image.TryReadUInt32(entryAddress, out uint value))
                break;

            if (value < functionStart || value >= functionEnd || (value & 3) != 0)
                break;

            if (seen.Add(value))
                targets.Add(value);
        }

        if (targets.Count == 0)
        {
            reason = $"tabela em 0x{tableAddress:X8} sem destinos dentro da funcao";
            return false;
        }

        table = new Table(jrAddress, tableAddress, targets, declared);

        return true;
    }
}
