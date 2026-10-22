using SH.ELF;

// Sondas pontuais de análise. Arquivo separado do EeReports.cs e do
// Program.cs para não colidir com os nomes de variáveis de lá.
internal static class EeProbes
{
    private static List<ElfSymbol> Unique(IEnumerable<ElfSymbol> functions)
    {
        return functions
            .Where(f => f.Size >= 4 && f.Name.Length > 0)
            .GroupBy(f => f.Address)
            .Select(g => g.OrderByDescending(f => f.Size).First())
            .OrderBy(f => f.Address)
            .ToList();
    }

    private static IEnumerable<(uint Address, uint Raw, MipsInstruction Instruction)> Words(
        ElfImage image,
        ElfSymbol function)
    {
        for (uint offset = 0; offset + 4 <= function.Size; offset += 4)
        {
            uint address = function.Address + offset;

            if (!image.TryReadUInt32(address, out uint raw))
                yield break;

            yield return (address, raw, MipsDecoder.Decode(raw));
        }
    }

    // Onde um mnemônico aparece, com quais palavras e em quais funções.
    // Serve para distinguir código real de dados/padding dentro de símbolos.
    public static void Mnemonic(
        ElfImage image,
        IEnumerable<ElfSymbol> allFunctions,
        string mnemonic)
    {
        Console.WriteLine();
        Console.WriteLine($"=== Sonda: {mnemonic} ===");

        Dictionary<uint, int> rawCounts = new();
        List<(string Name, uint Address, uint Size, int Count)> perFunction = new();
        long total = 0;

        foreach (ElfSymbol function in Unique(allFunctions))
        {
            int count = 0;

            foreach ((uint _, uint raw, MipsInstruction instruction) in Words(image, function))
            {
                if (instruction.Mnemonic != mnemonic)
                    continue;

                count++;
                total++;

                rawCounts[raw] = rawCounts.GetValueOrDefault(raw) + 1;
            }

            if (count > 0)
                perFunction.Add((function.Name, function.Address, function.Size, count));
        }

        Console.WriteLine($"Ocorrências: {total}");
        Console.WriteLine($"Palavras distintas: {rawCounts.Count}");
        Console.WriteLine($"Funções com pelo menos uma: {perFunction.Count}");

        int wholeFunction = perFunction.Count(f => f.Count * 4L >= f.Size);

        Console.WriteLine($"Funções inteiras só com esse mnemônico: {wholeFunction}");

        Console.WriteLine("Palavras mais frequentes:");

        foreach (KeyValuePair<uint, int> entry in rawCounts
                     .OrderByDescending(r => r.Value)
                     .Take(8))
        {
            Console.WriteLine(
                $"  {entry.Value,8}  0x{entry.Key:X8}  {MipsDecoder.Decode(entry.Key).Assembly}");
        }

        Console.WriteLine("Funções com mais ocorrências:");

        foreach ((string name, uint address, uint size, int count) in perFunction
                     .OrderByDescending(f => f.Count)
                     .Take(10))
        {
            double share = 100.0 * count / Math.Max(1, size / 4);

            Console.WriteLine(
                $"  0x{address:X8}  tam=0x{size:X}  {count,7}  ({share,5:F1}%)  {name}");
        }
    }

    // Quais instruções VU0 macro o jogo usa de fato. O índice segue o
    // formato do COP2: funct < 0x3C, ou o sub-código de SPECIAL2.
    public static void Vu0Opcodes(
        ElfImage image,
        IEnumerable<ElfSymbol> allFunctions)
    {
        Console.WriteLine();
        Console.WriteLine("=== Instruções VU0 macro por código ===");

        Dictionary<string, int> counts = new();

        foreach (ElfSymbol function in Unique(allFunctions))
        {
            foreach ((uint _, uint raw, MipsInstruction instruction) in Words(image, function))
            {
                if (!instruction.IsVu0Macro)
                    continue;

                uint funct = raw & 0x3F;

                uint special2 = (raw & 3) | ((raw >> 4) & 0x7C);

                string key = funct < 0x3C
                    ? $"funct    0x{funct:X2}"
                    : $"special2 0x{special2:X2}";

                counts[key] = counts.GetValueOrDefault(key) + 1;
            }
        }

        Console.WriteLine($"Códigos distintos: {counts.Count}");

        foreach (KeyValuePair<string, int> entry in counts
                     .OrderByDescending(c => c.Value)
                     .Take(45))
        {
            Console.WriteLine($"  {entry.Value,6}  {entry.Key}");
        }
    }

    // Dos 142 stubs de syscall, quais o jogo chama (via relocações
    // R_MIPS_26) e quantas vezes.
    public static void SyscallUsage(
        byte[] elf,
        ElfImage image,
        IReadOnlyList<ElfSection> sections,
        IReadOnlyList<ElfSymbol> symbols,
        IEnumerable<ElfSymbol> allFunctions)
    {
        Console.WriteLine();
        Console.WriteLine("=== Stubs de syscall realmente chamados ===");

        // endereço do stub -> (nome, número do syscall)
        Dictionary<uint, (string Name, int Number)> stubs = new();

        foreach (ElfSymbol function in Unique(allFunctions))
        {
            int lastV1 = 0;

            foreach ((uint _, uint _, MipsInstruction instruction) in Words(image, function))
            {
                // li $v1, N  ==  ADDIU $v1, $zero, N
                if (instruction.Mnemonic == "ADDIU" &&
                    instruction.Rt == 3 &&
                    instruction.Rs == 0)
                {
                    lastV1 = instruction.SignedImmediate;
                    continue;
                }

                if (instruction.Mnemonic == "SYSCALL")
                {
                    stubs[function.Address] = (function.Name, lastV1);
                    break;
                }
            }
        }

        ElfSection? relSection =
            sections.FirstOrDefault(s => s.Name == ".relmain");

        if (relSection == null)
        {
            Console.WriteLine(".relmain ausente.");
            return;
        }

        Dictionary<uint, int> calls = new();

        foreach (ElfRelocation relocation in ElfRelocations.ReadRel(elf, relSection))
        {
            if (relocation.Type != 4 || relocation.SymbolIndex >= symbols.Count)
                continue;

            uint target = symbols[(int)relocation.SymbolIndex].Address;

            if (stubs.ContainsKey(target))
                calls[target] = calls.GetValueOrDefault(target) + 1;
        }

        Console.WriteLine($"Stubs de syscall: {stubs.Count}");
        Console.WriteLine($"Chamados pelo menos uma vez: {calls.Count}");
        Console.WriteLine($"Chamadas no total: {calls.Values.Sum()}");

        foreach (KeyValuePair<uint, int> entry in calls
                     .OrderByDescending(c => c.Value))
        {
            (string name, int number) = stubs[entry.Key];

            Console.WriteLine(
                $"  {entry.Value,6}  syscall={number,5}  {name}");
        }
    }

    // Quais registradores de controle do VU0 o jogo le/escreve com CFC2/CTC2.
    // Os numeros: 16 status, 17 MAC, 18 clip, 20 R, 21 I, 22 Q, 23 P, 27 CMSAR0, 28 FBRST.
    public static void Cop2Controls(
        ElfImage image,
        IEnumerable<ElfSymbol> allFunctions)
    {
        Console.WriteLine();
        Console.WriteLine("=== CFC2/CTC2: registradores de controle usados ===");

        Dictionary<string, int> counts = new();

        foreach (ElfSymbol function in Unique(allFunctions))
        {
            foreach ((uint _, uint _, MipsInstruction instruction) in Words(image, function))
            {
                if (instruction.Mnemonic is not ("CFC2" or "CTC2"))
                    continue;

                string key = $"{instruction.Mnemonic} $vi{instruction.Rd}";

                counts[key] = counts.GetValueOrDefault(key) + 1;
            }
        }

        foreach (KeyValuePair<string, int> entry in counts
                     .OrderByDescending(c => c.Value))
        {
            Console.WriteLine($"  {entry.Value,6}  {entry.Key}");
        }
    }
}
