using System.Buffers.Binary;
using SH.ELF;
using SH.ISO;

string? isoPath =
    args.Length > 0
        ? args[0]
        : Environment.GetEnvironmentVariable("SH_ISO")
          ?? @"E:\Projeto Recomp\Shadow Hearts (USA).iso";

if (string.IsNullOrWhiteSpace(isoPath) || !File.Exists(isoPath))
{
    Console.WriteLine("Uso: SH.Core <caminho-da-iso>");
    Console.WriteLine("(ou defina a variável de ambiente SH_ISO)");
    return 1;
}

static string FormatFlags(ElfImage.Region region) =>
    $"{(region.IsReadable ? 'R' : '-')}" +
    $"{(region.IsWritable ? 'W' : '-')}" +
    $"{(region.IsExecutable ? 'X' : '-')}";

// Agrupa as instruções não decodificadas por "forma" (opcode/funct/rs...).
static string UnknownKey(MipsInstruction i) => i.Opcode switch
{
    0x00 => $"SPECIAL funct=0x{i.Funct:X2}",
    0x01 => $"REGIMM  rt=0x{i.Rt:X2}",
    0x10 => $"COP0    rs=0x{i.Rs:X2} funct=0x{i.Funct:X2}",
    0x11 => $"COP1    rs=0x{i.Rs:X2} funct=0x{i.Funct:X2}",
    0x12 => $"COP2    rs=0x{i.Rs:X2}",
    0x1C => $"MMI     funct=0x{i.Funct:X2} sa=0x{i.Shamt:X2}",
    _ => $"OPCODE  0x{i.Opcode:X2}"
};

try
{
    using IsoReader iso = new(isoPath);
    IsoFileSystem fs = new(iso);

    Console.WriteLine("=== Shadow Hearts Recompiler ===");
    Console.WriteLine();

    Console.WriteLine("=== Volume ISO-9660 ===");
    Console.WriteLine($"Volume:   {fs.Volume.VolumeIdentifier}");
    Console.WriteLine($"Setores:  {fs.Volume.VolumeSpaceSize}");
    Console.WriteLine($"Raiz:     setor {fs.Volume.RootDirectorySector}");
    Console.WriteLine();

    Console.WriteLine("=== SYSTEM.CNF ===");

    SystemConfig config = fs.ReadSystemConfig();

    foreach (KeyValuePair<string, string> entry in config.Entries)
        Console.WriteLine($"{entry.Key,-8} = {entry.Value}");

    Console.WriteLine();

    if (config.BootPath == null)
    {
        Console.WriteLine("SYSTEM.CNF não tem BOOT2/BOOT.");
        return 1;
    }

    if (!config.IsPlayStation2Boot)
    {
        Console.WriteLine(
            $"Aviso: a chave é {config.BootKey}, não BOOT2. " +
            "Isso é um disco de PS1 e o executável não é um ELF.");
        return 1;
    }

    Console.WriteLine($"Executável de boot: {config.BootPath}");

    IsoDirectoryRecord? bootRecord = fs.Find(config.BootPath);

    if (bootRecord == null)
    {
        Console.WriteLine("Não encontrado na ISO. Conteúdo da raiz:");

        foreach (IsoDirectoryRecord record in fs.ListRoot())
            Console.WriteLine($"  {record.FileIdentifier}");

        return 1;
    }

    Console.WriteLine(
        $"Setor {bootRecord.ExtentLocation}, " +
        $"{bootRecord.DataLength} bytes");
    Console.WriteLine();

    byte[] elfBytes = fs.ReadFile(bootRecord);
    ElfImage image = ElfImage.Load(elfBytes);

    Console.WriteLine("=== ELF ===");
    Console.WriteLine($"Máquina:      {image.Header.Machine} (MIPS)");
    Console.WriteLine($"Tipo:         0x{image.Header.Type:X4}");
    Console.WriteLine($"Flags:        0x{image.Header.Flags:X8}");
    Console.WriteLine($"Entry point:  0x{image.EntryPoint:X8}");
    Console.WriteLine($"Tamanho do arquivo: {elfBytes.Length} bytes");
    Console.WriteLine(
        $"Program headers:    offset 0x{image.Header.ProgramHeaderOffset:X}, " +
        $"qtd {image.Header.ProgramHeaderCount}");
    Console.WriteLine(
        $"Section headers:    offset 0x{image.Header.SectionHeaderOffset:X}, " +
        $"qtd {image.Header.SectionHeaderCount}");
    Console.WriteLine();

    Console.WriteLine("=== Regiões carregadas ===");

    foreach (ElfImage.Region region in image.Regions)
    {
        Console.WriteLine(
            $"0x{region.VirtualAddress:X8}-0x{region.EndAddress:X8}  " +
            $"{FormatFlags(region)}  " +
            $"arquivo={region.FileSize}  " +
            $"memória={region.Data.Length}  " +
            $"bss={region.Data.Length - region.FileSize}");
    }
    
    
    Console.WriteLine();
    Console.WriteLine("=== Seções ===");

    List<ElfSection> sections = ElfSections.Read(elfBytes, image.Header);

    foreach (ElfSection section in sections)
    {
        Console.WriteLine(
            $"[{section.Index,2}] {section.Name,-14} " +
            $"tipo=0x{section.Type:X8} " +
            $"end=0x{section.Address:X8} " +
            $"off=0x{section.Offset:X} " +
            $"tam=0x{section.Size:X}");
    }

    List<ElfSymbol> symbols = ElfSections.ReadSymbols(elfBytes, sections);
    List<ElfSymbol> functions = symbols
        .Where(s => s.Type == 2 && s.Name.Length > 0)
        .ToList();

    // Modo dump: se existir dump.txt na pasta atual (um nome de funcao, ou um endereco 0x..., por linha),
    // imprime so o disassembly dessas funcoes e termina, sem rodar o resto da analise.
    string dumpFile = Path.Combine(Directory.GetCurrentDirectory(), "dump.txt");

    if (File.Exists(dumpFile))
    {
        foreach (string raw in File.ReadAllLines(dumpFile))
        {
            string line = raw.Trim();

            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            // Curinga: "*MpegPlay*" lista simbolos (endereco, tamanho, nome) em vez de desmontar.
            if (line.Contains('*'))
            {
                string pattern = "^" + System.Text.RegularExpressions.Regex.Escape(line).Replace("\\*", ".*") + "$";

                Console.WriteLine();
                Console.WriteLine($"=== simbolos que casam com {line} ===");

                foreach (ElfSymbol match in functions
                    .Where(f => System.Text.RegularExpressions.Regex.IsMatch(f.Name, pattern))
                    .OrderBy(f => f.Address))
                    Console.WriteLine($"0x{match.Address:X8}  {match.Size,6}  {match.Name}");

                continue;
            }

            if (line.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                && uint.TryParse(line.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out uint wanted))
            {
                ElfSymbol? containing = functions.FirstOrDefault(s => s.Address <= wanted && wanted < s.Address + s.Size);

                if (containing == null)
                {
                    Console.WriteLine();
                    Console.WriteLine($"=== {line}: nenhuma funcao contem esse endereco ===");
                    continue;
                }

                line = containing.Name;
            }

            EeCensus.ShowFunction(image, functions, line);
        }

        return 0;
    }

    Console.WriteLine($"Símbolos na .symtab: {symbols.Count} (funções: {functions.Count})");

    foreach (ElfSymbol symbol in functions.Take(10))
        Console.WriteLine($"  0x{symbol.Address:X8}  {symbol.Name}");

    Console.WriteLine();
    Console.WriteLine("=== Primeiras instruções no entry point ===");

    MipsInstructionReader reader = new(image);

    uint address = image.EntryPoint;

    for (int i = 0; i < 16; i++)
    {
        if (image.FindRegion(address, 4) == null)
            break;

        MipsInstruction instruction = reader.Read(address);

        Console.WriteLine(
            $"0x{address:X8}  {instruction.Raw:X8}  {instruction.Format(address)}");

        address += 4;
    }

    // Varre toda a parte do segmento que veio do arquivo (código + dados)
    // e conta o que o decoder ainda não reconhece. Palavras de dados
    // aparecem como ruído espalhado; um padrão que se repete muito
    // costuma ser instrução que falta decodificar.
    
    
    Console.WriteLine();
    Console.WriteLine("=== Cobertura do decoder dentro das funções ===");

// Uma entrada por endereço (aliases com o mesmo endereço contam uma vez).
List<ElfSymbol> sized = functions
    .Where(f => f.Size >= 4)
    .GroupBy(f => f.Address)
    .Select(g => g.OrderByDescending(f => f.Size).First())
    .OrderBy(f => f.Address)
    .ToList();

long codeWords = 0;
long unknownWords = 0;
Dictionary<string, int> kinds = new();
List<string> samples = new();

foreach (ElfSymbol function in sized)
{
    for (uint offset = 0; offset + 4 <= function.Size; offset += 4)
    {
        uint functionAddress = function.Address + offset;

        if (!image.TryReadUInt32(functionAddress, out uint raw))
            break;

        MipsInstruction instruction = MipsDecoder.Decode(raw);
        codeWords++;

        if (!instruction.IsUnknown)
            continue;

        unknownWords++;

        string key = UnknownKey(instruction);
        kinds[key] = kinds.GetValueOrDefault(key) + 1;

        if (samples.Count < 15)
            samples.Add($"0x{functionAddress:X8}  {raw:X8}  {function.Name}");
    }
}

ElfSection? mainSection = sections.FirstOrDefault(s => s.Name == "main");

Console.WriteLine($"Funções com tamanho: {sized.Count} (sem tamanho: {functions.Count(f => f.Size < 4)})");
Console.WriteLine($"Palavras de código:  {codeWords} ({codeWords * 4} bytes" +
    (mainSection == null ? ")" : $" de {mainSection.Size} da seção main)"));
Console.WriteLine($"Não reconhecidas:    {unknownWords}");

foreach (KeyValuePair<string, int> kind in kinds.OrderByDescending(k => k.Value).Take(10))
    Console.WriteLine($"  {kind.Value,8}  {kind.Key}");

foreach (string sample in samples)
    Console.WriteLine($"  ex: {sample}");

ElfSection? comment = sections.FirstOrDefault(s => s.Name == ".comment");

if (comment != null)
{
    Console.WriteLine();
    Console.WriteLine(".comment: " + System.Text.Encoding.ASCII
        .GetString(elfBytes, (int)comment.Offset, (int)comment.Size)
        .TrimEnd('\0'));
}


    Console.WriteLine();
    Console.WriteLine("=== Relocações (.relmain) ===");

ElfSection? relSection = sections.FirstOrDefault(s => s.Name == ".relmain");

if (relSection != null)
{
    Console.WriteLine(
        $"link={relSection.Link} (tabela de símbolos), " +
        $"info={relSection.Info} (seção alvo), " +
        $"entsize={relSection.EntrySize}");

    List<ElfRelocation> relocations =
        ElfRelocations.ReadRel(elfBytes, relSection);

    uint imageBase = image.Regions[0].VirtualAddress;

    Console.WriteLine($"Entradas: {relocations.Count}");
    Console.WriteLine(
        $"Offsets: mín 0x{relocations.Min(r => r.Offset):X}, " +
        $"máx 0x{relocations.Max(r => r.Offset):X} " +
        $"(base da imagem 0x{imageBase:X})");

    // O offset pode ser um endereço virtual ou relativo à seção.
    uint Resolve(uint offset) =>
        offset >= imageBase ? offset : offset + imageBase;

    foreach (IGrouping<byte, ElfRelocation> group in relocations
                 .GroupBy(r => r.Type)
                 .OrderByDescending(g => g.Count()))
    {
        Dictionary<string, int> words = new();
        int inImage = 0;

        foreach (ElfRelocation r in group)
        {
            if (!image.TryReadUInt32(Resolve(r.Offset), out uint word))
                continue;

            inImage++;

            string mnemonic = MipsDecoder.Decode(word).Mnemonic;
            words[mnemonic] = words.GetValueOrDefault(mnemonic) + 1;
        }

        string top = string.Join(", ", words
            .OrderByDescending(w => w.Value)
            .Take(4)
            .Select(w => $"{w.Key}={w.Value}"));

        Console.WriteLine(
            $"  {ElfRelocations.TypeName(group.Key),-16} " +
            $"{group.Count(),7}  na imagem: {inImage,7}  palavras: {top}");
    }

    Console.WriteLine("Primeiras entradas:");

    foreach (ElfRelocation r in relocations.Take(8))
    {
        string symbolName =
            r.SymbolIndex < symbols.Count
                ? symbols[(int)r.SymbolIndex].Name
                : "?";

        Console.WriteLine(
            $"  0x{r.Offset:X8}  {ElfRelocations.TypeName(r.Type),-16} " +
            $"sím={r.SymbolIndex} {symbolName}");
    }
}

Console.WriteLine();
Console.WriteLine("=== Conferência: disassembly de memclr ===");

ElfSymbol? memclrSymbol = functions.FirstOrDefault(f => f.Name == "memclr");

if (memclrSymbol != null)
{
    for (uint offset = 0; offset < memclrSymbol.Size; offset += 4)
    {
        uint instructionAddress = memclrSymbol.Address + offset;

        MipsInstruction instruction =
            MipsDecoder.Decode(image.ReadUInt32(instructionAddress));

        Console.WriteLine(
            $"0x{instructionAddress:X8}  {instruction.Raw:X8}  " +
            instruction.Format(instructionAddress));
    }
}

Console.WriteLine();
Console.WriteLine("=== Uso de recursos do EE dentro das funções ===");

long vu0 = 0, cop2 = 0, mmi = 0, syscalls = 0;

foreach (ElfSymbol function in functions.Where(f => f.Size >= 4))
{
    for (uint offset = 0; offset + 4 <= function.Size; offset += 4)
    {
        if (!image.TryReadUInt32(function.Address + offset, out uint word))
            break;

        string mnemonic = MipsDecoder.Decode(word).Mnemonic;

        if (mnemonic == "VU0") vu0++;
        else if (mnemonic is "QMFC2" or "QMTC2" or "CFC2" or "CTC2" or "LQC2" or "SQC2") cop2++;
        else if (mnemonic == "SYSCALL") syscalls++;
        else if (mnemonic.StartsWith('P') && mnemonic != "PREF") mmi++;
    }
}

Console.WriteLine($"VU0 macro:               {vu0}");
Console.WriteLine($"COP2 (moves, LQC2/SQC2): {cop2}");
Console.WriteLine($"MMI (P*):                {mmi}");
Console.WriteLine($"SYSCALL:                 {syscalls}");

EeReports.GpValue(elfBytes, sections);
EeReports.Mnemonics(image, functions);
EeReports.Vu0Users(image, functions);
EeReports.Syscalls(image, functions);


EeProbes.Mnemonic(image, functions, "PADDUB");
EeProbes.Vu0Opcodes(image, functions);
EeProbes.SyscallUsage(elfBytes, image, sections, symbols, functions);
EeProbes.Cop2Controls(image, functions);

EeMap.Buckets(functions);
EeMap.Vu0MicroCalls(image, functions);

EmitDemo.Run(image, functions, "memclr");
JumpTableResolver jumpTables = EmitDemo.CreateResolver(elfBytes, sections, image);
EmitDemo.JumpTableReport(image, functions, jumpTables);

List<ElfSymbol> gaps = EmitDemo.GapFunctions(elfBytes, sections, image, functions);
List<ElfSymbol> allFunctions = functions.Concat(gaps).ToList();
EmitDemo.Coverage(image, allFunctions, jumpTables);
EmitDemo.EmitAll(image, allFunctions, jumpTables, Path.Combine(Directory.GetCurrentDirectory(), "out"));

EeCensus.Hardware(image, functions);
EeCensus.OutsideCalls(image, functions);
EeCensus.Microcode(image, symbols);
EeCensus.VuPrograms(symbols);
EeCensus.SyscallCallers(elfBytes, sections, symbols, functions);

// Funcoes para desmontar: variavel SH_DISASM (nomes separados por virgula). O padrao e o laco de vsync.
string[] disasmNames = (Environment.GetEnvironmentVariable("SH_DISASM")
        ?? "VSync__3GsxFi,cbVSync__Fi,TimerWatcherV__6CTimerFi,TimerWatcher0__6CTimerFi")
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

foreach (string disasmName in disasmNames)
    EeCensus.ShowFunction(image, functions, disasmName);


//EeCensus.CallSequenceAll(image, functions, "__9FileIOLib");
//EeCensus.CallSequenceAll(image, functions, "Rssd");


//EeCensus.CallSequence(image, functions, "LoadModules__6K2MainFv");
//EeCensus.SdkApiUsage(elfBytes, sections, symbols, functions);



    return 0;
}
catch (Exception ex) when (
    ex is InvalidDataException ||
    ex is NotSupportedException ||
    ex is FileNotFoundException ||
    ex is EndOfStreamException)
{
    Console.WriteLine($"Erro: {ex.Message}");
    return 1;
}