using System.Buffers.Binary;
using SH.ELF;

// Relatórios de análise do executável. Ficam fora do Program.cs
// para não colidir com os nomes de variáveis de lá.
internal static class EeReports
{
    // Uma entrada por endereço (aliases contam uma vez), só com tamanho.
    private static List<ElfSymbol> Unique(IEnumerable<ElfSymbol> functions)
    {
        return functions
            .Where(f => f.Size >= 4 && f.Name.Length > 0)
            .GroupBy(f => f.Address)
            .Select(g => g.OrderByDescending(f => f.Size).First())
            .OrderBy(f => f.Address)
            .ToList();
    }

    private static IEnumerable<(uint Address, MipsInstruction Instruction)> Words(
        ElfImage image,
        ElfSymbol function)
    {
        for (uint offset = 0; offset + 4 <= function.Size; offset += 4)
        {
            uint address = function.Address + offset;

            if (!image.TryReadUInt32(address, out uint raw))
                yield break;

            yield return (address, MipsDecoder.Decode(raw));
        }
    }

    // O .reginfo guarda o valor de $gp (ri_gp_value) no offset 20.
    public static void GpValue(
        byte[] elf,
        IReadOnlyList<ElfSection> sections)
    {
        Console.WriteLine();
        Console.WriteLine("=== $gp ===");

        ElfSection? reginfo =
            sections.FirstOrDefault(s => s.Name == ".reginfo");

        if (reginfo == null || reginfo.Size < 24)
        {
            Console.WriteLine(".reginfo ausente ou curto demais.");
            return;
        }

        uint gp = BinaryPrimitives.ReadUInt32LittleEndian(
            elf.AsSpan((int)reginfo.Offset + 20, 4));

        Console.WriteLine($"gp no .reginfo: 0x{gp:X8}");
        Console.WriteLine("gp montado no crt0 ($a0): 0x004DE4F0");
    }

    public static void Mnemonics(
        ElfImage image,
        IEnumerable<ElfSymbol> allFunctions)
    {
        Console.WriteLine();
        Console.WriteLine("=== Mnemônicos mais frequentes (dentro das funções) ===");

        Dictionary<string, long> counts = new();
        long total = 0;

        foreach (ElfSymbol function in Unique(allFunctions))
        {
            foreach ((uint _, MipsInstruction instruction) in Words(image, function))
            {
                counts[instruction.Mnemonic] =
                    counts.GetValueOrDefault(instruction.Mnemonic) + 1;

                total++;
            }
        }

        foreach (KeyValuePair<string, long> entry in counts
                     .OrderByDescending(c => c.Value)
                     .Take(30))
        {
            Console.WriteLine(
                $"  {entry.Value,8}  {100.0 * entry.Value / Math.Max(1, total),5:F1}%  {entry.Key}");
        }

        Console.WriteLine("MMI (mnemônicos que começam com P):");

        foreach (KeyValuePair<string, long> entry in counts
                     .Where(c => c.Key.StartsWith('P') && c.Key != "PREF")
                     .OrderByDescending(c => c.Value)
                     .Take(12))
        {
            Console.WriteLine($"  {entry.Value,8}  {entry.Key}");
        }
    }

    public static void Vu0Users(
        ElfImage image,
        IEnumerable<ElfSymbol> allFunctions)
    {
        Console.WriteLine();
        Console.WriteLine("=== Funções que usam VU0/COP2 ===");

        List<(string Name, uint Address, int Vu0, int Cop2)> users = new();

        foreach (ElfSymbol function in Unique(allFunctions))
        {
            int vu0 = 0;
            int cop2 = 0;

            foreach ((uint _, MipsInstruction instruction) in Words(image, function))
            {
                if (instruction.IsVu0Macro)
                    vu0++;
                else if (instruction.Mnemonic is "QMFC2" or "QMTC2" or
                         "CFC2" or "CTC2" or "LQC2" or "SQC2")
                    cop2++;
            }

            if (vu0 + cop2 > 0)
                users.Add((function.Name, function.Address, vu0, cop2));
        }

        int library = users.Count(u =>
            u.Name.Contains("Vu0", StringComparison.OrdinalIgnoreCase));

        Console.WriteLine($"Funções que usam: {users.Count}");
        Console.WriteLine($"  com 'Vu0' no nome (biblioteca): {library}");
        Console.WriteLine($"  outras:                         {users.Count - library}");

        foreach ((string name, uint address, int vu0, int cop2) in users
                     .OrderByDescending(u => u.Vu0 + u.Cop2)
                     .Take(40))
        {
            Console.WriteLine(
                $"  0x{address:X8}  VU0={vu0,4}  COP2={cop2,4}  {name}");
        }
    }

    public static void Syscalls(
        ElfImage image,
        IEnumerable<ElfSymbol> allFunctions)
    {
        Console.WriteLine();
        Console.WriteLine("=== SYSCALL (número lido de $v1) ===");

        int found = 0;
        int unresolved = 0;
        HashSet<int> numbers = new();

        foreach (ElfSymbol function in Unique(allFunctions))
        {
            int? lastV1 = null;

            foreach ((uint address, MipsInstruction instruction) in Words(image, function))
            {
                // li $v1, N  ==  ADDIU $v1, $zero, N
                if (instruction.Mnemonic == "ADDIU" &&
                    instruction.Rt == 3 &&
                    instruction.Rs == 0)
                {
                    lastV1 = instruction.SignedImmediate;
                    continue;
                }

                if (instruction.Mnemonic != "SYSCALL")
                    continue;

                found++;

                if (lastV1.HasValue)
                {
                    numbers.Add(lastV1.Value);

                    Console.WriteLine(
                        $"  0x{address:X8}  syscall={lastV1.Value,5}  " +
                        $"(0x{(ushort)lastV1.Value:X4})  {function.Name}");
                }
                else
                {
                    unresolved++;

                    Console.WriteLine(
                        $"  0x{address:X8}  syscall=  ?    {function.Name}");
                }
            }
        }

        Console.WriteLine(
            $"Total: {found}, números distintos: {numbers.Count}, sem número: {unresolved}");
    }
}
