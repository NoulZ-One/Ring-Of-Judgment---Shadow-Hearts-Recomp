using SH.ELF;

// Levantamentos para o runtime: que enderecos de hardware o codigo acessa e
// quais chamadas vao para fora da tabela de funcoes.
internal static class EeCensus
{
    // Abaixo disto (aprox.) fica a biblioteca do SDK; acima, o codigo do jogo.
    private const uint LibraryLimit = 0x00230000;

    private static List<ElfSymbol> Unique(IEnumerable<ElfSymbol> functions)
    {
        return functions
            .Where(f => f.Size >= 4 && f.Name.Length > 0)
            .GroupBy(f => f.Address)
            .Select(g => g.OrderByDescending(f => f.Size).First())
            .OrderBy(f => f.Address)
            .ToList();
    }

    // Registrador de uso geral escrito pela instrucao (para invalidar constantes).
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

        if (i.Mnemonic.StartsWith('P') && i.Mnemonic != "PREF")
            return i.Rd;

        return null;
    }

    private static bool IsMemoryAccess(string mnemonic) =>
        mnemonic is "LB" or "LBU" or "LH" or "LHU" or "LW" or "LWU" or "LD" or "LQ" or
        "LWL" or "LWR" or "LDL" or "LDR" or "LWC1" or "LQC2" or
        "SB" or "SH" or "SW" or "SD" or "SQ" or "SWC1" or "SQC2" or
        "SWL" or "SWR" or "SDL" or "SDR";

    // Nome da regiao de hardware/memoria especial, ou null para RAM comum.
    private static string? Region(uint address)
    {
        uint segment = address & 0xE0000000u;

        // KSEG0/KSEG1 espelham o mapa fisico.
        if (segment == 0x80000000u || segment == 0xA0000000u)
            address &= 0x1FFFFFFFu;

        if (address >= 0x70000000u && address < 0x70004000u)
            return "scratchpad";

        // Constantes pequenas (contadores, deslocamentos) nao sao enderecos.
        if (address < 0x00001000u)
            return null;

        if (address < 0x00100000u)
            return "RAM do kernel/BIOS (abaixo de 1 MB)";

        if (address < 0x00200000u)
            return "RAM de 1 a 2 MB (antes da imagem do jogo)";

        if (address < 0x02000000u)
            return null;

        // Negativos (0xFFFFxxxx) e outros valores altos: nao sao acessos reais.
        if (address >= 0xC0000000u)
            return null;

        if (address >= 0x10000000u && address < 0x10002000u)
            return $"Timer {(address - 0x10000000u) >> 11}";

        if (address >= 0x10002000u && address < 0x10003000u) return "IPU";
        if (address >= 0x10003000u && address < 0x10003800u) return "GIF";
        if (address >= 0x10003800u && address < 0x10003C00u) return "VIF0";
        if (address >= 0x10003C00u && address < 0x10004000u) return "VIF1";
        if (address >= 0x10004000u && address < 0x10005000u) return "FIFO do VIF0";
        if (address >= 0x10005000u && address < 0x10006000u) return "FIFO do VIF1";
        if (address >= 0x10006000u && address < 0x10007000u) return "FIFO do GIF";
        if (address >= 0x10007000u && address < 0x10008000u) return "FIFO do IPU";

        switch (address & 0xFFFFFC00u)
        {
            case 0x10008000u: return "DMA canal 0 (VIF0)";
            case 0x10009000u: return "DMA canal 1 (VIF1)";
            case 0x1000A000u: return "DMA canal 2 (GIF)";
            case 0x1000B000u: return "DMA canal 3 (da IPU)";
            case 0x1000B400u: return "DMA canal 4 (para a IPU)";
            case 0x1000C000u: return "DMA canal 5 (SIF0)";
            case 0x1000C400u: return "DMA canal 6 (SIF1)";
            case 0x1000C800u: return "DMA canal 7 (SIF2)";
            case 0x1000D000u: return "DMA canal 8 (da SPR)";
            case 0x1000D400u: return "DMA canal 9 (para a SPR)";
        }

        if (address >= 0x1000E000u && address < 0x1000F000u) return "DMAC (controle global)";
        if (address >= 0x1000F000u && address < 0x1000F100u) return "INTC";
        if (address >= 0x1000F100u && address < 0x1000F200u) return "SIO";
        if (address >= 0x1000F200u && address < 0x1000F300u) return "SIF (SBUS)";
        if (address >= 0x1000F400u && address < 0x1000F600u) return "DMAC (extra)";

        if (address >= 0x11000000u && address < 0x11004000u) return "VU0 micro";
        if (address >= 0x11004000u && address < 0x11008000u) return "VU0 dados";
        if (address >= 0x11008000u && address < 0x1100C000u) return "VU1 micro";
        if (address >= 0x1100C000u && address < 0x11010000u) return "VU1 dados";

        if (address >= 0x12000000u && address < 0x12002000u) return "GS (registradores privilegiados)";

        if (address >= 0x1FC00000u) return "BIOS (ROM)";

        if (address >= 0x10000000u && address < 0x20000000u)
            return "hardware (nao mapeado)";

        return "fora do mapa";
    }

    // Loads e stores cujo endereco e uma constante montada por LUI/ADDIU/ORI.
    public static void Hardware(ElfImage image, IEnumerable<ElfSymbol> allFunctions)
    {
        Console.WriteLine();
        Console.WriteLine("=== Acessos a hardware e memoria especial ===");

        Dictionary<string, (int Library, int Game)> byRegion = new();
        Dictionary<(string Region, uint Address), int> byAddress = new();
        Dictionary<string, Dictionary<string, int>> gameFunctions = new();

        foreach (ElfSymbol function in Unique(allFunctions))
        {
            bool library = function.Address < LibraryLimit;

            uint[] value = new uint[32];
            bool[] known = new bool[32];
            known[0] = true;

            int count = (int)(function.Size / 4);
            MipsInstruction[] code = new MipsInstruction[count];

            for (int k = 0; k < count; k++)
            {
                if (!image.TryReadUInt32(function.Address + (uint)(k * 4), out uint word))
                {
                    count = k;
                    break;
                }

                code[k] = MipsDecoder.Decode(word);
            }

            // Destinos de desvio: o valor de um registrador pode vir de outro caminho.
            HashSet<uint> labels = new();

            for (int k = 0; k < count; k++)
            {
                uint at = function.Address + (uint)(k * 4);

                if ((code[k].IsRelativeBranch || code[k].Mnemonic == "J") &&
                    code[k].TryGetBranchTarget(at, out uint branchTarget))
                {
                    labels.Add(branchTarget);
                }
            }

            for (int index = 0; index < count; index++)
            {
                if (labels.Contains(function.Address + (uint)(index * 4)))
                {
                    Array.Clear(known);
                    known[0] = true;
                }

                MipsInstruction i = code[index];

                if (IsMemoryAccess(i.Mnemonic) && known[i.Rs])
                {
                    uint effective = unchecked(value[i.Rs] + (uint)(int)i.SignedImmediate);
                    string? region = Region(effective);

                    if (region != null)
                    {
                        (int lib, int game) = byRegion.GetValueOrDefault(region);

                        byRegion[region] = library ? (lib + 1, game) : (lib, game + 1);

                        (string, uint) key = (region, effective);
                        byAddress[key] = byAddress.GetValueOrDefault(key) + 1;

                        if (!library)
                        {
                            if (!gameFunctions.TryGetValue(function.Name, out Dictionary<string, int>? regions))
                            {
                                regions = new Dictionary<string, int>();
                                gameFunctions[function.Name] = regions;
                            }

                            regions[region] = regions.GetValueOrDefault(region) + 1;
                        }
                    }
                }

                // atualiza as constantes conhecidas
                switch (i.Mnemonic)
                {
                    case "LUI":
                        value[i.Rt] = (uint)i.Immediate << 16;
                        known[i.Rt] = true;
                        break;

                    case "ADDIU":
                    case "DADDIU":
                    case "ORI":
                        if (known[i.Rs])
                        {
                            value[i.Rt] = i.Mnemonic == "ORI"
                                ? value[i.Rs] | i.Immediate
                                : unchecked(value[i.Rs] + (uint)(int)i.SignedImmediate);
                            known[i.Rt] = true;
                        }
                        else
                        {
                            known[i.Rt] = false;
                        }
                        break;

                    default:
                        if (Writes(i) is int written && written != 0)
                            known[written] = false;
                        break;
                }

                // Depois de uma chamada os registradores nao valem mais.
                if (i.Mnemonic is "JAL" or "JALR")
                {
                    Array.Clear(known);
                }

                known[0] = true;
                value[0] = 0;
            }
        }

        Console.WriteLine("regiao                                        biblioteca    jogo");

        foreach (KeyValuePair<string, (int Library, int Game)> entry in byRegion
                     .OrderByDescending(e => e.Value.Library + e.Value.Game))
        {
            Console.WriteLine(
                $"{entry.Key,-44}  {entry.Value.Library,10}  {entry.Value.Game,6}");
        }

        Console.WriteLine("Enderecos mais acessados:");

        foreach (KeyValuePair<(string Region, uint Address), int> entry in byAddress
                     .OrderByDescending(e => e.Value)
                     .Take(40))
        {
            Console.WriteLine(
                $"  0x{entry.Key.Address:X8}  {entry.Value,5}  {entry.Key.Region}");
        }

        Console.WriteLine($"Funcoes do jogo (fora da biblioteca) que acessam hardware: {gameFunctions.Count}");

        foreach (KeyValuePair<string, Dictionary<string, int>> entry in gameFunctions
                     .OrderByDescending(e => e.Value.Values.Sum())
                     .Take(25))
        {
            string regions = string.Join(", ", entry.Value
                .OrderByDescending(r => r.Value)
                .Select(r => $"{r.Key} x{r.Value}"));

            Console.WriteLine($"  {entry.Key}: {regions}");
        }
    }

    // JAL/J para enderecos que nao sao inicio de funcao conhecida. Cada um
    // vira ps2_call em tempo de execucao e para o programa se nao existir.
    public static void OutsideCalls(ElfImage image, IEnumerable<ElfSymbol> allFunctions)
    {
        Console.WriteLine();
        Console.WriteLine("=== Chamadas para fora da tabela de funcoes ===");

        List<ElfSymbol> functions = Unique(allFunctions);
        HashSet<uint> entries = functions.Select(f => f.Address).ToHashSet();

        Dictionary<uint, (int Count, string Caller, uint Site)> outside = new();
        int sites = 0;

        foreach (ElfSymbol function in functions)
        {
            uint end = function.Address + function.Size;

            for (uint offset = 0; offset + 4 <= function.Size; offset += 4)
            {
                uint address = function.Address + offset;

                if (!image.TryReadUInt32(address, out uint raw))
                    break;

                MipsInstruction i = MipsDecoder.Decode(raw);

                if (i.Mnemonic is not ("JAL" or "J"))
                    continue;

                if (!i.TryGetBranchTarget(address, out uint target))
                    continue;

                // desvio dentro da propria funcao (laco)
                if (i.Mnemonic == "J" && target >= function.Address && target < end)
                    continue;

                if (entries.Contains(target))
                    continue;

                sites++;

                (int count, string caller, uint site) = outside.GetValueOrDefault(target);

                outside[target] = count == 0
                    ? (1, function.Name, address)
                    : (count + 1, caller, site);
            }
        }

        int kernel = outside.Count(e => e.Key < 0x00200000u);
        int middle = outside.Count(e => e.Key >= 0x00200000u && e.Key < 0x007ED880u);
        int beyond = outside.Count - kernel - middle;

        Console.WriteLine($"Pontos de chamada: {sites}, destinos distintos: {outside.Count}");
        Console.WriteLine($"  abaixo do ELF (kernel/BIOS):                     {kernel}");
        Console.WriteLine($"  dentro do ELF, mas nao e inicio de funcao:      {middle}");
        Console.WriteLine($"  alem do fim do ELF:                              {beyond}");

        foreach (KeyValuePair<uint, (int Count, string Caller, uint Site)> entry in outside
                     .OrderByDescending(e => e.Value.Count)
                     .Take(30))
        {
            Console.WriteLine(
                $"  0x{entry.Key:X8}  x{entry.Value.Count,-4} primeiro em 0x{entry.Value.Site:X8} ({entry.Value.Caller})");
        }
    }

    // Procura microcodigo do VU (VU0/VU1) dentro da imagem: cada instrucao tem
    // 64 bits (palavra inferior, depois a superior) e o NOP e o par
    // 0x8000033C / 0x000002FF (os 5 bits altos da superior sao flags I/E/M/D/T).
    // Tambem lista simbolos de dados com nome de VU e os que ficam na regiao
    // 0x48C000-0x490000, onde o gerador achou "codigo" que parece dado.
    public static void Microcode(ElfImage image, IReadOnlyList<ElfSymbol> symbols)
    {
        Console.WriteLine();
        Console.WriteLine("=== Possivel microcodigo VU na imagem ===");

        ElfImage.Region region = image.Regions[0];
        uint fileEnd = region.VirtualAddress + region.FileSize;

        List<uint> nops = new();

        for (uint address = region.VirtualAddress; address + 8 <= fileEnd; address += 8)
        {
            if (!image.TryReadUInt32(address, out uint lower) ||
                !image.TryReadUInt32(address + 4, out uint upper))
            {
                break;
            }

            if (lower == 0x8000033Cu && (upper & 0x07FFFFFFu) == 0x000002FFu)
                nops.Add(address);
        }

        Console.WriteLine($"Pares NOP encontrados: {nops.Count}");

        List<(uint Start, uint End, int Count)> clusters = new();

        foreach (uint address in nops)
        {
            if (clusters.Count > 0 && address - clusters[^1].End <= 0x400)
            {
                (uint start, _, int count) = clusters[^1];
                clusters[^1] = (start, address + 8, count + 1);
            }
            else
            {
                clusters.Add((address, address + 8, 1));
            }
        }

        foreach ((uint start, uint end, int count) in clusters
                     .Where(c => c.Count >= 3)
                     .OrderByDescending(c => c.Count)
                     .Take(40))
        {
            Console.WriteLine(
                $"  0x{start:X8}-0x{end:X8}  {count,4} pares NOP  ({end - start} bytes)");
        }

        Console.WriteLine("Simbolos de dados com nome de VU/microcodigo:");

        foreach (ElfSymbol symbol in symbols
                     .Where(s => s.Type != 2 && s.Name.Length > 0 &&
                                 (s.Name.Contains("vu", StringComparison.OrdinalIgnoreCase) ||
                                  s.Name.Contains("micro", StringComparison.OrdinalIgnoreCase) ||
                                  s.Name.Contains("mpg", StringComparison.OrdinalIgnoreCase)))
                     .OrderBy(s => s.Address)
                     .Take(40))
        {
            Console.WriteLine(
                $"  0x{symbol.Address:X8}  {symbol.Size,7} bytes  tipo={symbol.Type}  {symbol.Name}");
        }

        Console.WriteLine("Simbolos entre 0x0048C000 e 0x00490000:");

        foreach (ElfSymbol symbol in symbols
                     .Where(s => s.Address >= 0x0048C000u && s.Address < 0x00490000u && s.Name.Length > 0)
                     .OrderBy(s => s.Address)
                     .Take(60))
        {
            Console.WriteLine(
                $"  0x{symbol.Address:X8}  {symbol.Size,7} bytes  tipo={symbol.Type}  {symbol.Name}");
        }
    }

    // Microprogramas do VU nos simbolos do montador (*_MPG marca onde cada programa
    // comeca; .vutext sao os blocos de codigo; _$Entry* sao os pontos de entrada).
    public static void VuPrograms(IReadOnlyList<ElfSymbol> symbols)
    {
        Console.WriteLine();
        Console.WriteLine("=== Microprogramas do VU (simbolos do montador) ===");

        List<ElfSymbol> programs = symbols
            .Where(s => s.Name.EndsWith("_MPG", StringComparison.Ordinal))
            .DistinctBy(s => s.Name)
            .OrderBy(s => s.Address)
            .ToList();

        Console.WriteLine($"Programas (*_MPG): {programs.Count}");

        for (int i = 0; i < programs.Count; i++)
        {
            uint start = programs[i].Address;
            uint? next = i + 1 < programs.Count ? programs[i + 1].Address : null;

            Console.WriteLine(next.HasValue
                ? $"  0x{start:X8}  ate 0x{next.Value:X8}  {next.Value - start,6} bytes  {programs[i].Name}"
                : $"  0x{start:X8}  (ultimo)                    {programs[i].Name}");
        }

        List<ElfSymbol> blocks = symbols
            .Where(s => s.Name == ".vutext")
            .DistinctBy(s => s.Address)
            .OrderBy(s => s.Address)
            .ToList();

        if (blocks.Count > 0)
        {
            Console.WriteLine(
                $"Blocos .vutext: {blocks.Count}, de 0x{blocks[0].Address:X8} a 0x{blocks[^1].Address:X8}");
        }

        List<ElfSymbol> entries = symbols
            .Where(s => s.Name.StartsWith("_$Entry", StringComparison.Ordinal))
            .OrderBy(s => s.Address)
            .ToList();

        Console.WriteLine($"Pontos de entrada (_$Entry*): {entries.Count}");

        foreach (ElfSymbol entry in entries.Take(40))
            Console.WriteLine($"  0x{entry.Address:X8}  {entry.Name}");
    }

    // Quem chama os stubs de thread, semaforo, alarme e handler (pelas relocacoes
    // R_MIPS_26): mostra se o uso e da biblioteca do SDK ou do codigo do jogo.
    public static void SyscallCallers(
        byte[] elf,
        IReadOnlyList<ElfSection> sections,
        IReadOnlyList<ElfSymbol> symbols,
        IEnumerable<ElfSymbol> allFunctions)
    {
        Console.WriteLine();
        Console.WriteLine("=== Quem chama threads, semaforos, alarmes e handlers ===");

        string[] stubNames =
        {
            "CreateThread", "StartThread", "ExitThread", "ExitDeleteThread", "SleepThread",
            "WakeupThread", "iWakeupThread", "_iWakeupThread", "ChangeThreadPriority",
            "CreateSema", "WaitSema", "SignalSema", "iSignalSema", "PollSema", "DeleteSema",
            "SetAlarm", "AddIntcHandler", "AddDmacHandler", "SetVSyncFlag"
        };

        List<ElfSymbol> functions = Unique(allFunctions);

        uint[] starts = functions.Select(f => f.Address).ToArray();
        uint[] ends = functions.Select(f => f.Address + f.Size).ToArray();

        string CallerOf(uint address)
        {
            int index = Array.BinarySearch(starts, address);

            if (index < 0)
                index = ~index - 1;

            return index >= 0 && address < ends[index]
                ? functions[index].Name
                : "(fora de funcao)";
        }

        Dictionary<uint, string> stubs = functions
            .Where(f => stubNames.Contains(f.Name))
            .GroupBy(f => f.Address)
            .ToDictionary(g => g.Key, g => g.First().Name);

        ElfSection relocationSection = sections.First(s => s.Name == ".relmain");

        Dictionary<string, Dictionary<string, int>> callers = new();

        foreach (ElfRelocation relocation in ElfRelocations.ReadRel(elf, relocationSection))
        {
            if (relocation.Type != 4 || relocation.SymbolIndex >= symbols.Count)
                continue;

            uint target = symbols[(int)relocation.SymbolIndex].Address;

            if (!stubs.TryGetValue(target, out string? stub))
                continue;

            string caller = CallerOf(relocation.Offset);

            if (!callers.TryGetValue(stub, out Dictionary<string, int>? byCaller))
            {
                byCaller = new Dictionary<string, int>();
                callers[stub] = byCaller;
            }

            byCaller[caller] = byCaller.GetValueOrDefault(caller) + 1;
        }

        foreach (string stub in stubNames)
        {
            if (!callers.TryGetValue(stub, out Dictionary<string, int>? byCaller))
                continue;

            Console.WriteLine($"{stub}: {byCaller.Values.Sum()} chamadas");

            foreach (KeyValuePair<string, int> entry in byCaller
                         .OrderByDescending(c => c.Value)
                         .Take(8))
            {
                Console.WriteLine($"    {entry.Value,4}  {entry.Key}");
            }
        }
    }

    // Sequencia de chamadas de uma funcao, com os argumentos constantes (textos e
    // numeros) reconhecidos por LUI/ADDIU/ORI. Leitura linear e simples: serve para
    // descobrir o que o jogo pede a biblioteca (por exemplo, quais modulos do IOP carrega).
    public static void CallSequence(ElfImage image, IEnumerable<ElfSymbol> allFunctions, string functionName)
    {
        Console.WriteLine();
        Console.WriteLine($"=== Chamadas de {functionName} ===");

        List<ElfSymbol> functions = Unique(allFunctions);
        ElfSymbol? target = functions.FirstOrDefault(f => f.Name == functionName);

        if (target == null)
        {
            Console.WriteLine("  funcao nao encontrada");
            return;
        }

        uint[] starts = functions.Select(f => f.Address).ToArray();

        string NameAt(uint address)
        {
            int index = Array.BinarySearch(starts, address);

            if (index < 0)
                index = ~index - 1;

            if (index >= 0 &&
                address >= functions[index].Address &&
                address < functions[index].Address + functions[index].Size)
            {
                return functions[index].Name;
            }

            return $"0x{address:X8}";
        }

        string? ReadText(uint address)
        {
            System.Text.StringBuilder text = new();

            for (uint offset = 0; offset < 160; offset++)
            {
                uint at = address + offset;

                if (!image.TryReadUInt32(at & ~3u, out uint word))
                    return null;

                byte value = (byte)(word >> (int)(8 * (at & 3)));

                if (value == 0)
                    return text.Length >= 3 ? text.ToString() : null;

                if (value < 0x20 || value > 0x7E)
                    return null;

                text.Append((char)value);
            }

            return null;
        }

        uint?[] lui = new uint?[32];
        string?[] texts = new string?[32];
        long?[] numbers = new long?[32];

        void Clear(int register)
        {
            lui[register] = null;
            texts[register] = null;
            numbers[register] = null;
        }

        void Copy(int from, int to)
        {
            lui[to] = lui[from];
            texts[to] = texts[from];
            numbers[to] = numbers[from];
        }

        void Apply(uint word)
        {
            uint op = word >> 26;
            int rs = (int)((word >> 21) & 31);
            int rt = (int)((word >> 16) & 31);
            int rd = (int)((word >> 11) & 31);
            uint imm = word & 0xFFFF;
            long simm = (short)imm;

            switch (op)
            {
                case 0x0F:   // LUI
                    Clear(rt);
                    lui[rt] = imm << 16;
                    return;

                case 0x09:   // ADDIU
                case 0x19:   // DADDIU
                case 0x0D:   // ORI
                {
                    long immediate = op == 0x0D ? imm : simm;

                    if (rs == 0)
                    {
                        Clear(rt);
                        numbers[rt] = immediate;
                    }
                    else if (lui[rs].HasValue)
                    {
                        uint address = (uint)((long)lui[rs].Value + immediate);

                        Clear(rt);
                        numbers[rt] = address;
                        texts[rt] = ReadText(address);
                    }
                    else
                    {
                        Clear(rt);
                    }

                    return;
                }

                case 0x00:   // SPECIAL
                {
                    uint funct = word & 0x3F;

                    if ((funct == 0x25 || funct == 0x21 || funct == 0x2D) && (word >> 16 & 31) == 0)
                    {
                        Copy(rs, rd);   // move rd, rs
                    }
                    else if (funct != 0x08 && funct != 0x0C && funct != 0x0D && rd != 0)
                    {
                        Clear(rd);
                    }

                    return;
                }

                case 0x0A: case 0x0B: case 0x0C: case 0x0E:   // SLTI, SLTIU, ANDI, XORI
                case 0x20: case 0x21: case 0x22: case 0x23:   // LB, LH, LWL, LW
                case 0x24: case 0x25: case 0x26: case 0x27:   // LBU, LHU, LWR, LWU
                case 0x37: case 0x1E:                         // LD, LQ
                    Clear(rt);
                    return;

                case 0x1C:   // MMI (PADDUB como move de 128 bits etc.)
                    if (rd != 0)
                        Clear(rd);

                    return;
            }
        }

        string Arguments()
        {
            List<string> parts = new();

            for (int register = 4; register <= 7; register++)
            {
                string name = $"a{register - 4}";

                if (texts[register] != null)
                {
                    parts.Add($"{name}=\"{texts[register]}\"");
                }
                else if (numbers[register].HasValue)
                {
                    long value = numbers[register]!.Value;

                    parts.Add(value >= -9 && value <= 9
                        ? $"{name}={value}"
                        : $"{name}=0x{(uint)value:X}");
                }
            }

            return string.Join("  ", parts);
        }

        void ClearCallerSaved()
        {
            for (int register = 2; register <= 15; register++)
                Clear(register);

            Clear(24);
            Clear(25);
        }

        uint end = target.Address + target.Size;

        for (uint pc = target.Address; pc < end; pc += 4)
        {
            if (!image.TryReadUInt32(pc, out uint word))
                break;

            uint op = word >> 26;
            bool jal = op == 0x03;
            bool jump = op == 0x02;
            bool jalr = op == 0x00 && (word & 0x3F) == 0x09;

            if (!jal && !jump && !jalr)
            {
                Apply(word);
                continue;
            }

            // o slot de atraso executa antes da chamada: pode montar um argumento
            if (image.TryReadUInt32(pc + 4, out uint delay))
                Apply(delay);

            if (jalr)
            {
                Console.WriteLine($"  0x{pc:X8}  JALR (indireta)  {Arguments()}");
                ClearCallerSaved();
            }
            else
            {
                uint callee = (pc & 0xF0000000u) | ((word & 0x03FFFFFFu) << 2);
                bool inside = callee >= target.Address && callee < end;

                if (!inside)
                {
                    Console.WriteLine($"  0x{pc:X8}  {(jal ? "JAL" : "J  ")} {NameAt(callee)}  {Arguments()}");
                }

                if (jal)
                    ClearCallerSaved();
            }

            pc += 4;   // pula o slot de atraso
        }
    }

    // CallSequence para todas as funcoes cujo nome contem o texto (por exemplo, "__9FileIOLib"
    // lista os metodos da classe FileIOLib), em ordem de endereco.
    public static void CallSequenceAll(ElfImage image, IEnumerable<ElfSymbol> allFunctions, string nameContains)
    {
        List<ElfSymbol> functions = Unique(allFunctions);

        foreach (ElfSymbol function in functions
                     .Where(f => f.Name.Contains(nameContains, StringComparison.Ordinal))
                     .OrderBy(f => f.Address))
        {
            CallSequence(image, functions, function.Name);
        }
    }

    // Funcoes do SDK (sce*) chamadas por codigo do jogo (a partir de 0x0022E000, onde
    // comecam as classes do jogo): e a lista do que precisa de versao nativa (HLE).
    public static void SdkApiUsage(
        byte[] elf,
        IReadOnlyList<ElfSection> sections,
        IReadOnlyList<ElfSymbol> symbols,
        IEnumerable<ElfSymbol> allFunctions)
    {
        Console.WriteLine();
        Console.WriteLine("=== Funcoes do SDK (sce*) chamadas pelo codigo do jogo ===");

        const uint GameStart = 0x0022E000u;

        List<ElfSymbol> functions = Unique(allFunctions);
        uint[] starts = functions.Select(f => f.Address).ToArray();

        ElfSymbol? FunctionAt(uint address)
        {
            int index = Array.BinarySearch(starts, address);

            if (index < 0)
                index = ~index - 1;

            return index >= 0 && address < functions[index].Address + functions[index].Size
                ? functions[index]
                : null;
        }

        Dictionary<uint, string> api = functions
            .Where(f => f.Name.StartsWith("sce", StringComparison.Ordinal))
            .GroupBy(f => f.Address)
            .ToDictionary(g => g.Key, g => g.First().Name);

        Dictionary<string, Dictionary<string, int>> usage = new();

        ElfSection relocationSection = sections.First(s => s.Name == ".relmain");

        foreach (ElfRelocation relocation in ElfRelocations.ReadRel(elf, relocationSection))
        {
            if (relocation.Type != 4 || relocation.SymbolIndex >= symbols.Count)
                continue;

            if (!api.TryGetValue(symbols[(int)relocation.SymbolIndex].Address, out string? apiName))
                continue;

            ElfSymbol? caller = FunctionAt(relocation.Offset);

            if (caller == null || caller.Address < GameStart)
                continue;

            if (!usage.TryGetValue(apiName, out Dictionary<string, int>? byCaller))
            {
                byCaller = new Dictionary<string, int>();
                usage[apiName] = byCaller;
            }

            byCaller[caller.Name] = byCaller.GetValueOrDefault(caller.Name) + 1;
        }

        string Library(string name)
        {
            System.Text.RegularExpressions.Match match = System.Text.RegularExpressions.Regex.Match(
                name, "^sce(Sif|Cd|Pad|Mc|Sdr|Sd|Mpeg|Ipu|Gs|Dma|Vif|Gif|Vu0|Vu1|Fs|Deci2)");

            return match.Success ? match.Groups[1].Value : "outras (sceOpen, sceRead...)";
        }

        Console.WriteLine($"Funcoes sce* com chamadas do jogo: {usage.Count} (de {api.Count} no ELF)");

        foreach (IGrouping<string, KeyValuePair<string, Dictionary<string, int>>> group in usage
                     .GroupBy(u => Library(u.Key))
                     .OrderBy(g => g.Key))
        {
            Console.WriteLine($"[{group.Key}]");

            foreach (KeyValuePair<string, Dictionary<string, int>> entry in group.OrderBy(u => u.Key))
            {
                string callers = string.Join(", ", entry.Value
                    .OrderByDescending(c => c.Value)
                    .Take(3)
                    .Select(c => $"{c.Key} x{c.Value}"));

                Console.WriteLine($"  {entry.Key,-32} {entry.Value.Values.Sum(),3} chamadas  ({callers})");
            }
        }
    }

    // Disassembly de uma funcao (uma linha por instrucao): serve para ler como o codigo
    // usa os campos de uma estrutura, por exemplo o _FIOHND da FileIOLib.
    public static void ShowFunction(ElfImage image, IEnumerable<ElfSymbol> allFunctions, string functionName)
    {
        ElfSymbol? function = Unique(allFunctions).FirstOrDefault(f => f.Name == functionName);

        Console.WriteLine();

        if (function == null)
        {
            Console.WriteLine($"=== {functionName}: funcao nao encontrada ===");
            return;
        }

        Console.WriteLine($"=== Disassembly de {functionName} (0x{function.Address:X8}, {function.Size} bytes) ===");

        MipsCppEmitter.Result result = MipsCppEmitter.EmitFunction(image, function, includeHeader: false);

        foreach (string line in result.Code.Split('\n'))
        {
            string trimmed = line.Trim();

            if (trimmed.StartsWith("// 0x", StringComparison.Ordinal))
                Console.WriteLine("  " + trimmed.Substring(3));
        }
    }
}

