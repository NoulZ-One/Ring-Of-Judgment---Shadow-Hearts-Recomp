using System.Text.RegularExpressions;
using SH.ELF;

// Mapa do executável: onde ficam biblioteca (SDK) e código do jogo, e
// quem chama microprogramas do VU0. Arquivo separado para não colidir
// com os nomes de variáveis do Program.cs.
internal static class EeMap
{
    // GCC 2.x / Metrowerks: "Nome__7CCameraFf", "Nome__FPiPfi".
    private static readonly Regex Mangled =
        new(@"__(\d|F|Q)", RegexOptions.Compiled);

    private static List<ElfSymbol> Unique(IEnumerable<ElfSymbol> functions)
    {
        return functions
            .Where(f => f.Size >= 4 && f.Name.Length > 0)
            .GroupBy(f => f.Address)
            .Select(g => g.OrderByDescending(f => f.Size).First())
            .OrderBy(f => f.Address)
            .ToList();
    }

    private static bool IsSce(string name) =>
        name.StartsWith("sce", StringComparison.Ordinal) ||
        name.StartsWith("_sce", StringComparison.Ordinal);

    public static void Buckets(IEnumerable<ElfSymbol> allFunctions)
    {
        Console.WriteLine();
        Console.WriteLine("=== Mapa por faixa de 64 KB ===");
        Console.WriteLine("faixa                       funções    C++    sce*     bytes");

        List<ElfSymbol> functions = Unique(allFunctions);

        foreach (IGrouping<uint, ElfSymbol> bucket in functions
                     .GroupBy(f => f.Address >> 16)
                     .OrderBy(g => g.Key))
        {
            int total = bucket.Count();
            int cpp = bucket.Count(f => Mangled.IsMatch(f.Name));
            int sce = bucket.Count(f => IsSce(f.Name));
            long bytes = bucket.Sum(f => (long)f.Size);

            uint start = bucket.Key << 16;

            Console.WriteLine(
                $"0x{start:X8}-0x{start + 0xFFFF:X8}  {total,8}  {cpp,5}  {sce,6}  {bytes,8}");
        }

        int allCpp = functions.Count(f => Mangled.IsMatch(f.Name));
        int allSce = functions.Count(f => IsSce(f.Name));

        Console.WriteLine(
            $"Total: {functions.Count} funções, {allCpp} com nome C++ mangled, {allSce} com prefixo sce");
    }

    // VCALLMS / VCALLMSR executam microprogramas do VU0 (modo micro).
    public static void Vu0MicroCalls(
        ElfImage image,
        IEnumerable<ElfSymbol> allFunctions)
    {
        Console.WriteLine();
        Console.WriteLine("=== VCALLMS / VCALLMSR (microprogramas do VU0) ===");

        int found = 0;

        foreach (ElfSymbol function in Unique(allFunctions))
        {
            for (uint offset = 0; offset + 4 <= function.Size; offset += 4)
            {
                uint address = function.Address + offset;

                if (!image.TryReadUInt32(address, out uint raw))
                    break;

                if (!MipsDecoder.Decode(raw).IsVu0Macro)
                    continue;

                uint funct = raw & 0x3F;

                if (funct != 0x38 && funct != 0x39)
                    continue;

                found++;

                if (funct == 0x38)
                {
                    uint microAddress = ((raw >> 6) & 0x7FFF) * 8;

                    Console.WriteLine(
                        $"  0x{address:X8}  VCALLMS   micro=0x{microAddress:X4}  {function.Name}");
                }
                else
                {
                    Console.WriteLine(
                        $"  0x{address:X8}  VCALLMSR                 {function.Name}");
                }
            }
        }

        Console.WriteLine($"Total: {found}");
    }
}
