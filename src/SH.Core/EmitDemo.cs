using System.Text;
using SH.ELF;

// Gera o C++ de uma funcao (Run), mede a cobertura do gerador em todo o
// executavel (Coverage) e analisa os JR de jump table (JumpTableReport).
internal static class EmitDemo
{
    // Resolvedor de jump tables a partir das relocacoes R_MIPS_32 do .relmain.
    public static JumpTableResolver CreateResolver(
        byte[] elf,
        IReadOnlyList<ElfSection> sections,
        ElfImage image)
    {
        ElfSection relocationSection =
            sections.First(s => s.Name == ".relmain");

        return new JumpTableResolver(
            image,
            ElfRelocations.ReadRel(elf, relocationSection));
    }

    private static List<ElfSymbol> Unique(IEnumerable<ElfSymbol> allFunctions)
    {
        return allFunctions
            .Where(f => f.Size >= 4 && f.Name.Length > 0)
            .GroupBy(f => f.Address)
            .Select(g => g.OrderByDescending(f => f.Size).First())
            .OrderBy(f => f.Address)
            .ToList();
    }

    // Gera o C++ do executavel inteiro em .\out: ps2rt.h, ps2_functions.h (prototipos),
    // ps2_functions_NNN.cpp (corpos, em lotes), ps2_table.cpp (endereco -> funcao),
    // ps2_runtime.cpp (runtime minimo) e ps2_selftest.cpp (chama o memclr pela tabela).
    // Sem comentarios por padrao: com eles o resultado passa de 70 MB.
    public static void EmitAll(
        ElfImage image,
        IEnumerable<ElfSymbol> allFunctions,
        JumpTableResolver? jumpTables,
        string directory,
        bool comments = false)
    {
        Console.WriteLine();
        Console.WriteLine("=== Gerando C++ de todas as funcoes ===");

        List<ElfSymbol> functions = Unique(allFunctions);

        Directory.CreateDirectory(directory);

        // Nome em C++ de cada funcao: o corpo recompilado, ou o hle_* quando o
        // runtime a substitui (registro em HleRegistry.cs).
        static string CppName(ElfSymbol f) =>
            Hle.Contains(f.Name) ? Hle.HostName(f.Name) : MipsCppEmitter.FunctionName(f);

        Dictionary<uint, string> directCalls = functions
            .ToDictionary(f => f.Address, f => CppName(f));

        const int ShardChars = 6_000_000;

        StringBuilder shard = new();
        int shardCount = 0;
        long totalChars = 0;
        long unsupported = 0;
        long dispatched = 0;
        int failed = 0;
        List<ElfSymbol> emitted = new();

        void Flush()
        {
            if (shard.Length == 0)
                return;

            File.WriteAllText(
                Path.Combine(directory, $"ps2_functions_{shardCount:D3}.cpp"),
                "#include \"ps2_functions.h\"\n\n" + shard);

            shardCount++;
            shard.Clear();
        }

        foreach (ElfSymbol function in functions)
        {
            // substituida por codigo nativo: nao gera o corpo recompilado
            if (Hle.Contains(function.Name))
            {
                emitted.Add(function);
                continue;
            }

            MipsCppEmitter.Result result;

            try
            {
                result = MipsCppEmitter.EmitFunction(
                    image, function, null, jumpTables, directCalls,
                    comments, includeHeader: false);
            }
            catch (Exception ex) when (
                ex is ArgumentOutOfRangeException or InvalidDataException)
            {
                failed++;
                continue;
            }

            emitted.Add(function);
            unsupported += result.Unsupported;
            dispatched += result.RuntimeDispatches;
            totalChars += result.Code.Length;

            shard.AppendLine(result.Code);

            if (shard.Length >= ShardChars)
                Flush();
        }

        Flush();

        // prototipos e tabela de despacho
        StringBuilder header = new();
        header.AppendLine("#pragma once");
        header.AppendLine("#include \"ps2rt.h\"");
        header.AppendLine();
        header.AppendLine("struct Ps2FuncEntry { uint32_t address; void (*fn)(Ctx&); const char* name; };");
        header.AppendLine("extern const Ps2FuncEntry ps2_function_table[];");
        header.AppendLine("extern const unsigned ps2_function_count;");
        header.AppendLine();

        StringBuilder table = new();
        table.AppendLine("#include \"ps2_functions.h\"");
        table.AppendLine();
        table.AppendLine("const Ps2FuncEntry ps2_function_table[] =");
        table.AppendLine("{");

        foreach (ElfSymbol function in emitted)
        {
            string name = CppName(function);

            header.AppendLine($"void {name}(Ctx& c);");
            string escaped = function.Name.Replace("\\", "\\\\").Replace("\"", "\\\"");

            table.AppendLine($"    {{ 0x{function.Address:X8}u, {name}, \"{escaped}\" }},");
        }

        table.AppendLine("};");
        table.AppendLine(
            "const unsigned ps2_function_count = sizeof(ps2_function_table) / sizeof(ps2_function_table[0]);");

        File.WriteAllText(Path.Combine(directory, "ps2rt.h"), MipsCppEmitter.RuntimeHeader);
        File.WriteAllText(Path.Combine(directory, "ps2_functions.h"), header.ToString());
        File.WriteAllText(Path.Combine(directory, "ps2_table.cpp"), table.ToString());
        File.WriteAllText(Path.Combine(directory, "ps2_runtime.cpp"), MipsCppEmitter.RuntimeSource);

        // imagem da RAM (a parte que veio do arquivo), carregada em 0x00200000
        ElfImage.Region region = image.Regions[0];

        File.WriteAllBytes(
            Path.Combine(directory, "ps2_image.bin"),
            region.Data.AsSpan(0, (int)region.FileSize).ToArray());

        File.WriteAllText(
            Path.Combine(directory, "ps2_main.cpp"),
            MainSource(region.VirtualAddress, image.EntryPoint));

        File.WriteAllText(
            Path.Combine(directory, "ps2_syscall_names.cpp"),
            SyscallNamesSource(image, emitted));

        // corpos das funcoes substituidas (HLE)
        StringBuilder hle = new();
        hle.AppendLine("#include \"ps2_functions.h\"");
        hle.AppendLine("#include <cstdio>");
        hle.AppendLine("#include <cstring>");
        hle.AppendLine("#include <cstdlib>");
        hle.AppendLine();

        foreach (ElfSymbol function in emitted.Where(f => Hle.Contains(f.Name)))
        {
            hle.AppendLine($"// {function.Name} (0x{function.Address:X8}) substituida pelo runtime");
            hle.AppendLine($"void {Hle.HostName(function.Name)}(Ctx& c)");
            hle.AppendLine("{");
            hle.AppendLine($"    PS2_TRACE_ENTER(0x{function.Address:X8}u);");
            hle.AppendLine("    (void)c;");
            hle.AppendLine(Hle.Functions[function.Name].TrimEnd());
            hle.AppendLine("}");
            hle.AppendLine();
        }

        File.WriteAllText(Path.Combine(directory, "ps2_hle.cpp"), hle.ToString());

        ElfSymbol? memclr = emitted.FirstOrDefault(f => f.Name == "memclr");

        if (memclr != null)
        {
            File.WriteAllText(
                Path.Combine(directory, "ps2_selftest.cpp"),
                SelfTestSource(memclr.Address));
        }

        Console.WriteLine($"Funcoes geradas: {emitted.Count} (falharam: {failed}; substituidas por HLE: {emitted.Count(f => Hle.Contains(f.Name))})");
        Console.WriteLine($"Lotes: {shardCount}, texto: {totalChars / 1_000_000.0:F1} MB");
        Console.WriteLine($"Instrucoes sem suporte (viram parada em tempo de execucao): {unsupported}");
        Console.WriteLine($"JR indireto resolvido em tempo de execucao: {dispatched}");
        Console.WriteLine($"Arquivos em: {directory}");
        Console.WriteLine("Para compilar (exemplo): g++ -std=c++17 -O0 -c *.cpp  (um .o por arquivo; inclua ps2_hle.cpp)");
        Console.WriteLine("Teste rapido: compile tudo e linke com ps2_selftest.cpp (exige o memclr).");
        Console.WriteLine("Primeira execucao: compile com ps2_main.cpp (no lugar do selftest) e rode com ps2_image.bin na pasta.");
    }

    // Codigo alcancavel que nao esta em nenhum simbolo de funcao (por exemplo o
    // crt0): o entry point e os destinos de JAL/J fora das funcoes conhecidas.
    // Cada um vira uma funcao "gap_XXXXXXXX".
    public static List<ElfSymbol> GapFunctions(
        byte[] elf,
        IReadOnlyList<ElfSection> sections,
        ElfImage image,
        IEnumerable<ElfSymbol> allFunctions)
    {
        ElfSection relocationSection = sections.First(s => s.Name == ".relmain");
        List<ElfRelocation> relocations = ElfRelocations.ReadRel(elf, relocationSection);

        List<ElfSymbol> functions = Unique(allFunctions);

        uint textStart = image.Regions[0].VirtualAddress;
        uint textEnd = functions.Max(f => f.Address + f.Size);

        uint[] starts = functions.Select(f => f.Address).ToArray();
        uint[] ends = functions.Select(f => f.Address + f.Size).ToArray();

        bool Covered(uint address)
        {
            int index = Array.BinarySearch(starts, address);

            if (index < 0)
                index = ~index - 1;

            return index >= 0 && address < ends[index];
        }

        SortedSet<uint> candidates = new();

        void Consider(uint target)
        {
            if (target >= textStart && target < textEnd &&
                (target & 3) == 0 && !Covered(target))
            {
                candidates.Add(target);
            }
        }

        Consider(image.EntryPoint);

        foreach (ElfRelocation relocation in relocations)
        {
            if (!image.TryReadUInt32(relocation.Offset, out uint word))
                continue;

            // So JAL/J (R_MIPS_26) contam como evidencia de codigo. Ponteiros em dados
            // (R_MIPS_32) tambem caem entre funcoes, mas na maioria sao dados.
            if (relocation.Type != 4)
                continue;

            MipsInstruction instruction = MipsDecoder.Decode(word);

            if (instruction.TryGetBranchTarget(relocation.Offset, out uint target))
                Consider(target);
        }

        List<uint> ordered = candidates.ToList();
        List<ElfSymbol> result = new();

        for (int k = 0; k < ordered.Count; k++)
        {
            uint start = ordered[k];
            uint limit = textEnd;

            int next = Array.BinarySearch(starts, start);

            if (next < 0)
                next = ~next;

            if (next < starts.Length)
                limit = Math.Min(limit, starts[next]);

            if (k + 1 < ordered.Count)
                limit = Math.Min(limit, ordered[k + 1]);

            // termina no primeiro JR $ra (com o delay slot), se vier antes do limite
            uint end = limit;

            for (uint a = start; a + 8 <= limit; a += 4)
            {
                if (!image.TryReadUInt32(a, out uint w))
                    break;

                MipsInstruction d = MipsDecoder.Decode(w);

                if (d.Mnemonic == "JR" && d.Rs == 31)
                {
                    end = a + 8;
                    break;
                }
            }

            if (end > start)
            {
                result.Add(new ElfSymbol(
                    $"gap_{start:X8}", start, end - start, 2, 1, 0));
            }
        }

        Console.WriteLine();
        Console.WriteLine("=== Codigo fora dos simbolos de funcao ===");
        Console.WriteLine($"Funcoes adicionadas: {result.Count}");

        foreach (ElfSymbol gap in result.Take(30))
            Console.WriteLine($"  0x{gap.Address:X8}  {gap.Size,6} bytes  {gap.Name}");

        return result;
    }

    // Programa de teste: carrega a imagem na RAM e executa a partir do entry point.
    private static string MainSource(uint loadAddress, uint entry)
    {
        return
            "#include \"ps2_functions.h\"\n" +
            "#include <cstdio>\n" +
            "#include <cstdlib>\n\n" +
            "static uint8_t ram[32u * 1024 * 1024];\n" +
            "static uint8_t spr[16u * 1024];\n\n" +
            "int main(int argc, char** argv)\n" +
            "{\n" +
            "    const char* env_image = std::getenv(\"PS2_IMAGE\");\n" +
            "    const char* path = env_image ? env_image : \"ps2_image.bin\";\n" +
            "    std::FILE* f = std::fopen(path, \"rb\");\n\n" +
            "    if (!f)\n" +
            "    {\n" +
            "        std::fprintf(stderr, \"nao achei %s\\n\", path);\n" +
            "        return 2;\n" +
            "    }\n\n" +
            $"    size_t n = std::fread(ram + 0x{loadAddress:X8}u, 1, sizeof(ram) - 0x{loadAddress:X8}u, f);\n" +
            "    std::fclose(f);\n" +
            $"    std::fprintf(stderr, \"imagem carregada: %zu bytes em 0x{loadAddress:X8}\\n\", n);\n\n" +
            "    ps2_iso_open_default(argc, argv);\n\n" +
            "    Ctx c{};\n" +
            "    c.mem = ram;\n" +
            "    c.spr = spr;\n" +
            "    vu0_init(c);\n\n" +
            $"    ps2_call(c, 0x{entry:X8}u);   // entry point\n\n" +
            "    std::fprintf(stderr, \"o entry point retornou\\n\");\n" +
            "    return 0;\n" +
            "}\n";
    }

    // Nomes dos stubs de syscall (ADDIU $v1, $zero, N; SYSCALL), para as mensagens de erro.
    private static string SyscallNamesSource(ElfImage image, IEnumerable<ElfSymbol> functions)
    {
        SortedDictionary<int, string> names = new();

        foreach (ElfSymbol function in functions.Where(f => f.Size <= 64))
        {
            int? number = null;

            for (uint offset = 0; offset + 4 <= function.Size; offset += 4)
            {
                if (!image.TryReadUInt32(function.Address + offset, out uint raw))
                    break;

                MipsInstruction i = MipsDecoder.Decode(raw);

                if (i.Mnemonic == "ADDIU" && i.Rt == 3 && i.Rs == 0)
                    number = i.SignedImmediate;
                else if (i.Mnemonic == "SYSCALL" && number.HasValue)
                {
                    names.TryAdd(number.Value, function.Name);
                    break;
                }
            }
        }

        StringBuilder text = new();
        text.AppendLine("const char* ps2_syscall_name(int number)");
        text.AppendLine("{");
        text.AppendLine("    switch (number)");
        text.AppendLine("    {");

        foreach (KeyValuePair<int, string> entry in names)
            text.AppendLine($"        case {entry.Key}: return \"{entry.Value}\";");

        text.AppendLine("        default: return \"?\";");
        text.AppendLine("    }");
        text.AppendLine("}");

        return text.ToString();
    }

    private static string SelfTestSource(uint memclrAddress)
    {
        return
            "#include \"ps2_functions.h\"\n" +
            "#include <cstdio>\n\n" +
            "static uint8_t ram[32u * 1024 * 1024];\n\n" +
            "int main()\n" +
            "{\n" +
            "    ps2_iso_open_default(argc, argv);\n\n" +
            "    Ctx c{};\n" +
            "    c.mem = ram;\n" +
            "    vu0_init(c);\n\n" +
            "    for (int i = 0; i < 64; i++) ram[0x00300000 + i] = 0xAA;\n" +
            "    c.r[4].lo = 0x00300000;\n" +
            "    c.r[5].lo = 32;\n" +
            $"    ps2_call(c, 0x{memclrAddress:X8}u);\n\n" +
            "    int zeros = 0;\n" +
            "    for (int i = 0; i < 64; i++) zeros += ram[0x00300000 + i] == 0;\n" +
            "    std::printf(\"memclr pela tabela: %d bytes zerados (esperado 32)\\n\", zeros);\n" +
            "    return zeros == 32 ? 0 : 1;\n" +
            "}\n";
    }

    public static void Run(
        ElfImage image,
        IEnumerable<ElfSymbol> allFunctions,
        string functionName,
        JumpTableResolver? jumpTables = null)
    {
        Console.WriteLine();
        Console.WriteLine($"=== Gerador de C++: {functionName} ===");

        List<ElfSymbol> functions = Unique(allFunctions);

        ElfSymbol? function = functions
            .Where(f => f.Name == functionName)
            .OrderByDescending(f => f.Size)
            .FirstOrDefault();

        if (function == null)
        {
            Console.WriteLine("Funcao nao encontrada.");
            return;
        }

        Dictionary<uint, string> names = functions
            .ToDictionary(f => f.Address, f => f.Name);

        MipsCppEmitter.Result result =
            MipsCppEmitter.EmitFunction(image, function, names, jumpTables);

        string directory = Path.Combine(Directory.GetCurrentDirectory(), "out");

        Directory.CreateDirectory(directory);

        File.WriteAllText(
            Path.Combine(directory, "ps2rt.h"),
            MipsCppEmitter.RuntimeHeader);

        File.WriteAllText(
            Path.Combine(directory, MipsCppEmitter.FunctionName(function) + ".cpp"),
            result.Code);

        Console.WriteLine(result.Code);
        Console.WriteLine(
            $"Instrucoes: {result.Instructions}, sem suporte: {result.Unsupported}");
        Console.WriteLine($"Arquivos em: {directory}");
    }

    // Roda o gerador em todas as funcoes (sem gravar nada) e conta o que
    // ainda nao tem suporte: e a lista de pendencias do recompilador.
    public static void Coverage(
        ElfImage image,
        IEnumerable<ElfSymbol> allFunctions,
        JumpTableResolver? jumpTables = null)
    {
        Console.WriteLine();
        Console.WriteLine("=== Cobertura do gerador de C++ (todas as funcoes) ===");

        List<ElfSymbol> functions = Unique(allFunctions);

        long instructions = 0;
        long unsupported = 0;
        long dispatched = 0;
        int cleanFunctions = 0;
        int failed = 0;
        Dictionary<string, int> byMnemonic = new();

        foreach (ElfSymbol function in functions)
        {
            if (Hle.Contains(function.Name))
                continue;

            MipsCppEmitter.Result result;

            try
            {
                result = MipsCppEmitter.EmitFunction(image, function, null, jumpTables);
            }
            catch (Exception ex) when (
                ex is ArgumentOutOfRangeException or InvalidDataException)
            {
                failed++;
                continue;
            }

            instructions += result.Instructions;
            unsupported += result.Unsupported;
            dispatched += result.RuntimeDispatches;

            if (result.Unsupported == 0)
                cleanFunctions++;

            foreach (KeyValuePair<string, int> entry in result.UnsupportedByMnemonic)
            {
                byMnemonic[entry.Key] =
                    byMnemonic.GetValueOrDefault(entry.Key) + entry.Value;
            }
        }

        Console.WriteLine($"Funcoes: {functions.Count} (falharam ao gerar: {failed})");
        Console.WriteLine($"Funcoes 100% suportadas: {cleanFunctions}");
        Console.WriteLine($"Instrucoes: {instructions}");
        Console.WriteLine(
            $"Sem suporte: {unsupported} ({100.0 * unsupported / Math.Max(1, instructions):F2}%)");
        Console.WriteLine($"JR indireto resolvido em tempo de execucao: {dispatched}");

        foreach (KeyValuePair<string, int> entry in byMnemonic
                     .OrderByDescending(e => e.Value)
                     .Take(30))
        {
            Console.WriteLine($"  {entry.Value,8}  {entry.Key}");
        }
    }

    // Todos os JR que nao sao $ra: quantos viram jump table e por que os
    // outros nao. Mostra o contexto (instrucoes anteriores) dos primeiros
    // nao resolvidos, para ver que padroes faltam.
    public static void JumpTableReport(
        ElfImage image,
        IEnumerable<ElfSymbol> allFunctions,
        JumpTableResolver resolver)
    {
        Console.WriteLine();
        Console.WriteLine("=== JR em registrador que nao e $ra ===");
        Console.WriteLine($"Palavras com relocacao R_MIPS_32: {resolver.PointerCount}");

        int total = 0;
        int resolved = 0;
        int totalEntries = 0;
        int largest = 0;
        Dictionary<string, int> reasons = new();
        List<string> resolvedSamples = new();
        List<string> unresolvedSamples = new();

        foreach (ElfSymbol function in Unique(allFunctions))
        {
            int count = (int)(function.Size / 4);
            uint end = function.Address + (uint)(count * 4);

            MipsInstruction[] code = new MipsInstruction[count];

            for (int i = 0; i < count; i++)
            {
                code[i] = MipsDecoder.Decode(
                    image.ReadUInt32(function.Address + (uint)(i * 4)));
            }

            for (int i = 0; i < count; i++)
            {
                if (code[i].Mnemonic != "JR" || code[i].Rs == 31)
                    continue;

                total++;

                uint address = function.Address + (uint)(i * 4);

                if (resolver.TryResolve(
                        code, i, function.Address, end,
                        out JumpTableResolver.Table? table, out string reason) &&
                    table != null)
                {
                    resolved++;
                    totalEntries += table.Targets.Count;
                    largest = Math.Max(largest, table.Targets.Count);

                    if (resolvedSamples.Count < 8)
                    {
                        resolvedSamples.Add(
                            $"  0x{address:X8}  tabela 0x{table.TableAddress:X8}  " +
                            $"{table.Targets.Count} destinos (limite {(table.DeclaredEntries == int.MaxValue ? "?" : table.DeclaredEntries.ToString())})  {function.Name}");
                    }

                    continue;
                }

                reasons[reason] = reasons.GetValueOrDefault(reason) + 1;

                if (unresolvedSamples.Count < 14)
                {
                    unresolvedSamples.Add(
                        $"  0x{address:X8}  {function.Name}  [{reason}]");

                    int from = Math.Max(0, i - 8);

                    for (int k = from; k <= i; k++)
                    {
                        uint a = function.Address + (uint)(k * 4);

                        unresolvedSamples.Add(
                            $"      0x{a:X8}  {code[k].Format(a)}");
                    }
                }
            }
        }

        Console.WriteLine($"Total: {total}");
        Console.WriteLine($"Resolvidos como jump table: {resolved} (entradas: {totalEntries}, maior: {largest})");
        Console.WriteLine($"Nao resolvidos: {total - resolved}");

        foreach (KeyValuePair<string, int> entry in reasons.OrderByDescending(r => r.Value))
            Console.WriteLine($"  {entry.Value,5}  {entry.Key}");

        Console.WriteLine("Exemplos resolvidos:");

        foreach (string line in resolvedSamples)
            Console.WriteLine(line);

        Console.WriteLine("Exemplos nao resolvidos:");

        foreach (string line in unresolvedSamples)
            Console.WriteLine(line);
    }
}
