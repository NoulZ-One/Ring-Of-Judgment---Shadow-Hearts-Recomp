using System;
using System.Collections.Generic;
using System.Linq;
using SH.ISO;
using SH.ELF;

const string isoPath =
    @"E:\Projeto Recomp\Shadow Hearts (USA).iso";

using IsoReader iso = new(isoPath);

Console.WriteLine("=== Shadow Hearts Recompiler ===");
Console.WriteLine();

Console.WriteLine("Tamanho da ISO:");
Console.WriteLine($"{iso.Length} bytes");

Console.WriteLine();
Console.WriteLine("=== Volume ISO-9660 ===");

byte[] volumeSector = iso.ReadSector(16);

IsoVolumeDescriptor volume =
    IsoVolumeDescriptor.Parse(volumeSector);

Console.WriteLine($"Sistema:       {volume.SystemIdentifier}");
Console.WriteLine($"Volume:        {volume.VolumeIdentifier}");
Console.WriteLine($"Setores:       {volume.VolumeSpaceSize}");
Console.WriteLine($"Tamanho setor: {volume.LogicalBlockSize} bytes");
Console.WriteLine($"Raiz:          setor {volume.RootDirectorySector}");

Console.WriteLine();
Console.WriteLine("=== Diretório raiz ===");

IsoDirectory directory = new(iso);

List<IsoDirectoryRecord> records =
    directory.Read((int)volume.RootDirectorySector);

foreach (IsoDirectoryRecord record in records)
{
    Console.WriteLine(
        $"{(record.IsDirectory ? "<DIR> " : "<FILE>")} " +
        $"{record.FileIdentifier,-32} " +
        $"Setor: {record.ExtentLocation,-8} " +
        $"Tamanho: {record.DataLength}");
}

Console.WriteLine();
Console.WriteLine("=== Executável ===");

IsoDirectoryRecord? executable = records
    .FirstOrDefault(r =>
        r.FileIdentifier.StartsWith("SLUS_203.47"));

if (executable == null)
{
    Console.WriteLine("Executável não encontrado.");
    return;
}

Console.WriteLine($"Arquivo: {executable.FileIdentifier}");
Console.WriteLine($"Setor: {executable.ExtentLocation}");
Console.WriteLine($"Tamanho: {executable.DataLength} bytes");

byte[] executableHeader =
    iso.ReadBytes(
        (int)executable.ExtentLocation,
        64);

        byte[] executableData =
    iso.ReadBytes(
        (int)executable.ExtentLocation,
        (int)executable.DataLength);

Console.WriteLine();
Console.WriteLine("Primeiros 64 bytes:");

for (int j = 0; j < executableHeader.Length; j++)
{
    Console.Write($"{executableHeader[j]:X2} ");

    if ((j + 1) % 16 == 0)
        Console.WriteLine();
}

Console.WriteLine();
Console.WriteLine("=== Cabeçalho ELF ===");

ElfHeader elfHeader =
    ElfHeader.Parse(executableHeader);

Console.WriteLine(
    $"Classe:                 ELF{(elfHeader.Class == 1 ? 32 : elfHeader.Class)}");

Console.WriteLine(
    $"Endianness:             {(elfHeader.Data == 1 ? "Little-endian" : elfHeader.Data)}");

Console.WriteLine($"Versão:                 {elfHeader.Version}");
Console.WriteLine($"Tipo:                   0x{elfHeader.Type:X4}");
Console.WriteLine($"Máquina:                {elfHeader.Machine} (MIPS)");
Console.WriteLine($"Entry Point:            0x{elfHeader.EntryPoint:X8}");
Console.WriteLine(
    $"Program Header Offset:  0x{elfHeader.ProgramHeaderOffset:X8}");

Console.WriteLine(
    $"Section Header Offset:  0x{elfHeader.SectionHeaderOffset:X8}");

Console.WriteLine($"Flags:                  0x{elfHeader.Flags:X8}");
Console.WriteLine($"Tamanho do Header:      {elfHeader.HeaderSize}");
Console.WriteLine(
    $"Tamanho PH Entry:       {elfHeader.ProgramHeaderEntrySize}");

Console.WriteLine(
    $"Quantidade PH:          {elfHeader.ProgramHeaderCount}");

Console.WriteLine(
    $"Tamanho SH Entry:       {elfHeader.SectionHeaderEntrySize}");

Console.WriteLine(
    $"Quantidade SH:          {elfHeader.SectionHeaderCount}");

Console.WriteLine(
    $"Índice SH String Table: {elfHeader.SectionNameTableIndex}");

    Console.WriteLine();
Console.WriteLine();
Console.WriteLine("=== Program Headers ===");

ElfReader elfReader =
    new(executableData);

    

for (int i = 0; i < elfReader.ProgramHeaders.Count; i++)
{
    ElfProgramHeader programHeader =
        elfReader.ProgramHeaders[i];

    Console.WriteLine();
    Console.WriteLine($"--- Program Header #{i} ---");

    Console.WriteLine(
        $"Tipo:              0x{programHeader.Type:X8}");

    Console.WriteLine(
        $"Offset no arquivo: 0x{programHeader.Offset:X8}");

    Console.WriteLine(
        $"Endereço virtual:  0x{programHeader.VirtualAddress:X8}");

    Console.WriteLine(
        $"Endereço físico:   0x{programHeader.PhysicalAddress:X8}");

    Console.WriteLine(
        $"Tamanho no arquivo: {programHeader.FileSize} bytes");

    Console.WriteLine(
        $"Tamanho na memória: {programHeader.MemorySize} bytes");

    Console.WriteLine(
        $"Flags:             0x{programHeader.Flags:X8}");

    Console.WriteLine(
        $"Alinhamento:       0x{programHeader.Alignment:X8}");
}

    Console.WriteLine();
    

Console.WriteLine();
Console.WriteLine("=== VALORES DOS PONTEIROS INDIRETOS ===");

uint[] pointerAddressesDiagnostic =
{
    0x004ABEE0,
    0x004ABEE8
};

foreach (uint virtualAddress in pointerAddressesDiagnostic)
{
    ElfProgramHeader? containingSegment = null;

    foreach (ElfProgramHeader programHeader in elfReader.ProgramHeaders)
    {
        uint segmentStart =
            programHeader.VirtualAddress;

        uint segmentEnd =
            segmentStart + programHeader.FileSize;

        if (virtualAddress >= segmentStart &&
            virtualAddress + 4 <= segmentEnd)
        {
            containingSegment = programHeader;
            break;
        }
    }

    if (containingSegment == null)
    {
        Console.WriteLine(
            $"0x{virtualAddress:X8} -> segmento não encontrado.");

        continue;
    }

    uint fileOffset =
        containingSegment.Offset +
        (virtualAddress - containingSegment.VirtualAddress);

    uint value =
        BitConverter.ToUInt32(
            executableData,
            checked((int)fileOffset));

    Console.WriteLine(
        $"Endereço virtual: 0x{virtualAddress:X8}");

    Console.WriteLine(
        $"Offset no ELF:    0x{fileOffset:X8}");

    Console.WriteLine(
        $"Valor:            0x{value:X8}");

    Console.WriteLine();
}

Console.WriteLine();
Console.WriteLine("=== Segmentos ELF ===");

for (int s = 0; s < elfReader.Segments.Count; s++)
{
    ElfSegment segment =
        elfReader.Segments[s];

    Console.WriteLine();
    Console.WriteLine($"--- Segmento #{s} ---");

    Console.WriteLine(
        $"Carregável:       {segment.IsLoadable}");

    Console.WriteLine(
        $"Offset no arquivo: 0x{segment.FileOffset:X8}");

    Console.WriteLine(
        $"Endereço virtual: 0x{segment.VirtualAddress:X8}");

    Console.WriteLine(
        $"Tamanho arquivo:  {segment.FileSize} bytes");

    Console.WriteLine(
        $"Tamanho memória:  {segment.MemorySize} bytes");

    Console.WriteLine(
        $"Legível:          {segment.IsReadable}");

    Console.WriteLine(
        $"Gravável:         {segment.IsWritable}");

    Console.WriteLine(
        $"Executável:       {segment.IsExecutable}");
}
    Console.WriteLine();
        Console.WriteLine("=== Teste de carregamento ===");

        ElfSegment firstSegment =
            elfReader.Segments[0];

        byte[] segmentMemory =
            firstSegment.Load(executableData);

        Console.WriteLine(
            $"Endereço virtual: 0x{firstSegment.VirtualAddress:X8}");

        Console.WriteLine(
            $"Tamanho carregado: {segmentMemory.Length} bytes");

        Console.WriteLine(
            $"Tamanho esperado:  {firstSegment.MemorySize} bytes");

        Console.WriteLine();
        Console.WriteLine("=== Entry Point ===");

        uint entryPoint =
            elfHeader.EntryPoint;

        if (entryPoint < firstSegment.VirtualAddress ||
            entryPoint >= firstSegment.VirtualAddress + firstSegment.MemorySize)
{
        throw new InvalidDataException(
            "O Entry Point está fora do segmento.");
}

        int entryOffset =
            checked((int)(
                entryPoint -
                firstSegment.VirtualAddress));

        Console.WriteLine(
            $"Entry Point:       0x{entryPoint:X8}");

        Console.WriteLine(
            $"Offset no segmento: 0x{entryOffset:X8}");

        uint instruction =
            BitConverter.ToUInt32(
                segmentMemory,
                entryOffset);

        Console.WriteLine(
            $"Instrução:         0x{instruction:X8}");

            MipsInstruction decoded =
                MipsDecoder.Decode(instruction);
        


        Console.WriteLine();
        Console.WriteLine("=== Código MIPS no Entry Point ===");

        MipsInstructionReader instructionReader =
            new(
                segmentMemory,
                firstSegment.VirtualAddress);

        uint currentAddress = entryPoint;

        Dictionary<string, int> instructionCounts = new();

        const int instructionCount = 256;

        for (int i = 0; i < instructionCount; i++)
{
        MipsInstruction current =
            instructionReader.Read(currentAddress);

        if (instructionCounts.ContainsKey(current.Mnemonic))
        {
            instructionCounts[current.Mnemonic]++;
        }
        else
        {
            instructionCounts[current.Mnemonic] = 1;
        }

        if (currentAddress == 0x00200234)
        {
        Console.WriteLine(
            $"DEBUG: {current.DebugInfo}");
        }


    uint? branchTarget =
        instructionReader.GetBranchTarget(
            currentAddress,
            current);

    if (branchTarget.HasValue)
    {
        Console.WriteLine(
            $"0x{currentAddress:X8}  " +
            $"{current.Assembly,-30} " +
            $"-> 0x{branchTarget.Value:X8}");
    }
    else
    {
        Console.WriteLine(
            $"0x{currentAddress:X8}  " +
            $"{current.Assembly}");
    }

    if (current.Mnemonic == "UNKNOWN")
    {
        Console.WriteLine(
            $"                  {current.DebugInfo}");
    }

        currentAddress += 4;
    }

        Console.WriteLine();
        Console.WriteLine("=== Frequência das instruções ===");

        foreach (var entry in instructionCounts
            .OrderByDescending(x => x.Value))
    {
    Console.WriteLine(
        $"{entry.Key,-12} {entry.Value}");
    }

        Console.WriteLine();
        Console.WriteLine("=== BASIC BLOCK ===");

        MipsBasicBlockBuilder blockBuilder =
            new(instructionReader);

        MipsBasicBlock block =
            blockBuilder.Build(
                entryPoint,
                32);

        Console.WriteLine();
        Console.WriteLine(
            $"Bloco iniciado em: 0x{block.StartAddress:X8}");

        foreach (MipsInstruction blockInstruction in block.Instructions)
        {
        Console.WriteLine(
            blockInstruction.Assembly);
    }

        Console.WriteLine();

        if (block.BranchTarget.HasValue)
    {
        Console.WriteLine(
            $"Branch → 0x{block.BranchTarget.Value:X8}");
    }  

        if (block.FallthroughAddress.HasValue)
    {
        Console.WriteLine(
            $"Fallthrough → 0x{block.FallthroughAddress.Value:X8}");
    }

    Console.WriteLine();
    Console.WriteLine("=== FUNÇÕES ENCONTRADAS POR JAL ===");

    MipsFunctionAnalyzer functionAnalyzer =
        new(instructionReader);

    List<uint> functionAddresses =
        functionAnalyzer.FindFunctionCalls(
            entryPoint,
            instructionCount);

    foreach (uint address in functionAddresses)
{
        Console.WriteLine(
            $"Função possível: 0x{address:X8}");
}

    Console.WriteLine();
Console.WriteLine("=== PROGRAMA MIPS AUTOMÁTICO ===");

MipsFunctionBuilder automaticFunctionBuilder =
    new(blockBuilder);

MipsProgramBuilder programBuilder =
    new(automaticFunctionBuilder);

MipsProgram automaticProgram =
    programBuilder.Build(
        functionAddresses,
        64,
        1000);

    Console.WriteLine(
        $"Funções construídas: " +
        $"{automaticProgram.Functions.Count}");

    foreach (MipsFunction automaticFunction in
        automaticProgram.Functions.Values.OrderBy(
            f => f.StartAddress))
    {
    Console.WriteLine(
    $"Função: 0x{automaticFunction.StartAddress:X8} | " +
    $"Blocos: {automaticFunction.BasicBlocks.Count} | " +
    $"Chamadas: {automaticFunction.CalledFunctions.Count} | " +
    $"Retorno: {automaticFunction.HasReturn} | " +
    $"LimiteBloco: {automaticFunction.WasBlockLimitReached} | " +
    $"LimiteInstr: {automaticFunction.WasInstructionLimitReached}");
    }  
    Console.WriteLine();
    Console.WriteLine("=== CHAMADAS INDIRETAS ===");

    foreach (MipsFunction automaticFunction in
        automaticProgram.Functions.Values.OrderBy(
            f => f.StartAddress))
    {
    foreach (MipsBasicBlock automaticBlock in
        automaticFunction.BasicBlocks)
    {
        if (automaticBlock.IsIndirectCall)
        {
            Console.WriteLine(
                $"Função 0x{automaticFunction.StartAddress:X8} | " +
                $"Bloco 0x{automaticBlock.StartAddress:X8} | " +
                $"JALR");
        }
    }
}

        Console.WriteLine();
Console.WriteLine("=== ORIGEM DOS JALR ===");

foreach (MipsFunction diagnosticFunction in
    automaticProgram.Functions.Values.OrderBy(
        f => f.StartAddress))
{
    foreach (MipsBasicBlock diagnosticBlock in
        diagnosticFunction.BasicBlocks)
    {
        for (int diagnosticIndex = 0;
             diagnosticIndex < diagnosticBlock.Instructions.Count;
             diagnosticIndex++)
        {
            MipsInstruction diagnosticInstruction =
                diagnosticBlock.Instructions[diagnosticIndex];

            if (diagnosticInstruction.Mnemonic != "JALR")
                continue;

            uint diagnosticJalrAddress =
                diagnosticBlock.StartAddress +
                (uint)(diagnosticIndex * 4);

            string diagnosticTargetRegister =
                diagnosticInstruction.RsName;

            Console.WriteLine(
                $"Função 0x{diagnosticFunction.StartAddress:X8} | " +
                $"JALR 0x{diagnosticJalrAddress:X8} | " +
                $"Destino em {diagnosticTargetRegister}");

            int diagnosticStart =
                Math.Max(0, diagnosticIndex - 3);

            for (int diagnosticPreviousIndex =
                     diagnosticStart;
                 diagnosticPreviousIndex < diagnosticIndex;
                 diagnosticPreviousIndex++)
            {
                MipsInstruction diagnosticPreviousInstruction =
                    diagnosticBlock.Instructions[
                        diagnosticPreviousIndex];

                uint diagnosticPreviousAddress =
                    diagnosticBlock.StartAddress +
                    (uint)(diagnosticPreviousIndex * 4);

                Console.WriteLine(
                    $"    0x{diagnosticPreviousAddress:X8}  " +
                    $"{diagnosticPreviousInstruction.Mnemonic} " +
                    $"RS={diagnosticPreviousInstruction.RsName} " +
                    $"RT={diagnosticPreviousInstruction.RtName} " +
                    $"RD={diagnosticPreviousInstruction.RdName} " +
                    $"IMM=0x{diagnosticPreviousInstruction.Immediate:X4}");
            }

            Console.WriteLine();
        }
    }
}

Console.WriteLine();
Console.WriteLine("=== ULTIMA ESCRITA NO REGISTRADOR DO JALR ===");

foreach (MipsFunction diagnosticFunction2 in
    automaticProgram.Functions.Values.OrderBy(
        f => f.StartAddress))
{
    foreach (MipsBasicBlock diagnosticBlock2 in
        diagnosticFunction2.BasicBlocks)
    {
        for (int diagnosticIndex2 = 0;
             diagnosticIndex2 < diagnosticBlock2.Instructions.Count;
             diagnosticIndex2++)
        {
            MipsInstruction diagnosticInstruction2 =
                diagnosticBlock2.Instructions[diagnosticIndex2];

            if (diagnosticInstruction2.Mnemonic != "JALR")
                continue;

            string diagnosticTargetRegister2 =
                diagnosticInstruction2.RsName;

            Console.WriteLine(
                $"Função 0x{diagnosticFunction2.StartAddress:X8} | " +
                $"JALR em 0x{diagnosticBlock2.StartAddress + (uint)(diagnosticIndex2 * 4):X8} | " +
                $"Registrador: {diagnosticTargetRegister2}");

            bool diagnosticFoundWrite2 = false;

            for (int diagnosticSearchIndex2 =
                     diagnosticIndex2 - 1;
                 diagnosticSearchIndex2 >= 0;
                 diagnosticSearchIndex2--)
            {
                MipsInstruction diagnosticPrevious2 =
                    diagnosticBlock2.Instructions[
                        diagnosticSearchIndex2];

                bool diagnosticWritesTarget2 =
                    diagnosticPrevious2.RtName ==
                        diagnosticTargetRegister2 ||
                    diagnosticPrevious2.RdName ==
                        diagnosticTargetRegister2;

                if (!diagnosticWritesTarget2)
                    continue;

                uint diagnosticWriteAddress2 =
                    diagnosticBlock2.StartAddress +
                    (uint)(diagnosticSearchIndex2 * 4);

                Console.WriteLine(
                    $"    Última escrita encontrada: " +
                    $"0x{diagnosticWriteAddress2:X8} " +
                    $"{diagnosticPrevious2.Mnemonic} " +
                    $"RS={diagnosticPrevious2.RsName} " +
                    $"RT={diagnosticPrevious2.RtName} " +
                    $"RD={diagnosticPrevious2.RdName} " +
                    $"IMM=0x{diagnosticPrevious2.Immediate:X4}");

                if (diagnosticPrevious2.Mnemonic == "LW")
                {
                    Console.WriteLine(
                    $"    → Ponteiro carregado de " +
                    $"{diagnosticPrevious2.RsName} + " +
                    $"0x{diagnosticPrevious2.Immediate:X4}");
                }

                diagnosticFoundWrite2 = true;
                break;
            }

            if (!diagnosticFoundWrite2)
            {
                Console.WriteLine(
                    "    Nenhuma escrita encontrada " +
                    "antes do JALR neste bloco.");
            }

            Console.WriteLine();
        }
    }
}

        Console.WriteLine();
        Console.WriteLine("=== ENDEREÇOS DE PONTEIROS INDIRETOS ===");

        foreach (MipsFunction diagnosticFunction3 in
            automaticProgram.Functions.Values.OrderBy(
            f => f.StartAddress))
{
        foreach (MipsBasicBlock diagnosticBlock3 in
            diagnosticFunction3.BasicBlocks)
        {
        for (int diagnosticIndex3 = 0;
             diagnosticIndex3 < diagnosticBlock3.Instructions.Count;
             diagnosticIndex3++)
        {
            MipsInstruction diagnosticInstruction3 =
                diagnosticBlock3.Instructions[diagnosticIndex3];

            if (diagnosticInstruction3.Mnemonic != "JALR")
                continue;

            string diagnosticTargetRegister3 =
                diagnosticInstruction3.RsName;

            for (int diagnosticSearchIndex3 =
                     diagnosticIndex3 - 1;
                 diagnosticSearchIndex3 >= 0;
                 diagnosticSearchIndex3--)
            {
                MipsInstruction diagnosticPrevious3 =
                    diagnosticBlock3.Instructions[
                        diagnosticSearchIndex3];

                bool diagnosticWritesTarget3 =
                    diagnosticPrevious3.RtName ==
                        diagnosticTargetRegister3 ||
                    diagnosticPrevious3.RdName ==
                        diagnosticTargetRegister3;

                if (!diagnosticWritesTarget3)
                    continue;

                if (diagnosticPrevious3.Mnemonic != "LW")
                    break;

                if (diagnosticPrevious3.RsName != "$at")
                    break;

                if (diagnosticSearchIndex3 == 0)
                    break;

                MipsInstruction diagnosticLui3 =
                    diagnosticBlock3.Instructions[
                        diagnosticSearchIndex3 - 1];

                if (diagnosticLui3.Mnemonic != "LUI")
                    break;

                uint diagnosticHigh3 =
                    (uint)diagnosticLui3.Immediate << 16;

                short diagnosticOffset3 =
                    unchecked(
                        (short)diagnosticPrevious3.Immediate);

                uint diagnosticEffectiveAddress3 =
                    unchecked(
                        diagnosticHigh3 +
                        (uint)(int)diagnosticOffset3);

                uint diagnosticJalrAddress3 =
                    diagnosticBlock3.StartAddress +
                    (uint)(diagnosticIndex3 * 4);

                Console.WriteLine(
                    $"Função 0x{diagnosticFunction3.StartAddress:X8} | " +
                    $"JALR 0x{diagnosticJalrAddress3:X8}");

                Console.WriteLine(
                    $"    LUI: 0x{diagnosticLui3.Immediate:X4}");

                Console.WriteLine(
                    $"    LW:  {diagnosticPrevious3.RsName} + " +
                    $"0x{diagnosticPrevious3.Immediate:X4}");

                Console.WriteLine(
                        $"    Endereço efetivo: " +
                        $"0x{diagnosticEffectiveAddress3:X8}");

                ElfProgramHeader? diagnosticContainingSegment3 = null;

                foreach (ElfProgramHeader diagnosticProgramHeader3
                in elfReader.ProgramHeaders)
                {
                uint diagnosticSegmentStart3 =
                        diagnosticProgramHeader3.VirtualAddress;

                uint diagnosticSegmentEnd3 =
                        diagnosticSegmentStart3 +
                        diagnosticProgramHeader3.FileSize;

                if (diagnosticEffectiveAddress3 >= diagnosticSegmentStart3 &&
                    diagnosticEffectiveAddress3 + 4 <= diagnosticSegmentEnd3)
                    {
                        diagnosticContainingSegment3 =
                        diagnosticProgramHeader3;

                break;
                }
                }

                if (diagnosticContainingSegment3 == null)
                {
                Console.WriteLine(
                    "    Segmento ELF não encontrado para este endereço.");

                Console.WriteLine();

                break;
                }

                uint diagnosticFileOffset3 =
                    diagnosticContainingSegment3.Offset +
                    (diagnosticEffectiveAddress3 -
                    diagnosticContainingSegment3.VirtualAddress);

                uint diagnosticPointerValue3 =
                BitConverter.ToUInt32(
                    executableData,
                    checked((int)diagnosticFileOffset3));

                Console.WriteLine(
                    $"    Offset no ELF: " +
                    $"0x{diagnosticFileOffset3:X8}");

                Console.WriteLine(
                    $"    Valor armazenado: " +
                    $"0x{diagnosticPointerValue3:X8}");

                MipsFunction diagnosticIndirectTargetFunction3 =
                    automaticFunctionBuilder.Build(
                    diagnosticPointerValue3,
                    64,
                    1000);

            Console.WriteLine(
                    $"    Função destino: " +
                    $"0x{diagnosticIndirectTargetFunction3.StartAddress:X8}");

            Console.WriteLine(
                    $"    Blocos destino: " +
                    $"{diagnosticIndirectTargetFunction3.BasicBlocks.Count}");

            Console.WriteLine(
                    $"    Retorno destino: " +
                    $"{diagnosticIndirectTargetFunction3.HasReturn}");

            Console.WriteLine();


                Console.WriteLine();

                break;
            }
        }
    }
}


    Console.WriteLine();
    Console.WriteLine("=== CALL GRAPH ===");

    foreach (MipsFunction graphFunction in
        automaticProgram.Functions.Values.OrderBy(
            f => f.StartAddress))
    {
        Console.WriteLine(
            $"0x{graphFunction.StartAddress:X8}");

    foreach (uint calledFunction in graphFunction.CalledFunctions)
        {
        Console.WriteLine(
            $"    └──> 0x{calledFunction:X8}");
        }
    }


    Console.WriteLine();
    Console.WriteLine("=== ANÁLISE DA FUNÇÃO 0x00200340 ===");

    MipsBasicBlockBuilder functionBlockBuilder =
        new(instructionReader);

    MipsBasicBlock functionBlock =
        functionBlockBuilder.Build(
            0x00200340,
            64);


    Console.WriteLine();
    Console.WriteLine("=== Console.WriteLine");
    Console.WriteLine("=== FUNÇÕES SEM RETORNO ===");

    foreach (MipsFunction noReturnFunction in
        automaticProgram.Functions.Values
            .Where(f => !f.HasReturn)
            .OrderBy(f => f.StartAddress))
{
    Console.WriteLine();
    Console.WriteLine(
        $"=== FUNÇÃO 0x{noReturnFunction.StartAddress:X8} ===");

    foreach (MipsBasicBlock noReturnBlock in
        noReturnFunction.BasicBlocks.OrderBy(
            b => b.StartAddress))
    {
        Console.WriteLine(
            $"Bloco 0x{noReturnBlock.StartAddress:X8} | " +
            $"Instruções: {noReturnBlock.Instructions.Count}");

        if (noReturnBlock.IsIndirectCall)
        {
            Console.WriteLine(
                "    Chamada indireta: JALR");
        }

        for (int i = 0; i < noReturnBlock.Instructions.Count; i++)
        {
            MipsInstruction noReturnInstruction =
                noReturnBlock.Instructions[i];


                if (noReturnInstruction.Mnemonic == "JALR")
{
    Console.WriteLine(
        $"    >>> JALR encontrado | RD={noReturnInstruction.Rd} | " +
        $"RS={noReturnInstruction.Rs}");
}

            uint instructionAddress =
                noReturnBlock.StartAddress +
                (uint)(i * 4);

            Console.WriteLine(
                $"    0x{instructionAddress:X8}  " +
            $"{noReturnInstruction.Assembly}");
        }

        Console.WriteLine();
    }
}


            
    Console.WriteLine();
    Console.WriteLine("=== GRAFO DE FLUXO DE CONTROLE ===");

    MipsControlFlowGraphBuilder cfgBuilder =
        new(blockBuilder);

    MipsControlFlowGraph controlFlowGraph =
        cfgBuilder.Build(
            entryPoint,
            64,
            30);


    Console.WriteLine();
Console.WriteLine("=== FUNÇÃO 0x00200340 ===");

MipsFunctionBuilder functionBuilder =
    new(blockBuilder);

MipsFunction function =
    functionBuilder.Build(
        0x00200340,
        64,
        30);

Console.WriteLine(
    $"Início: 0x{function.StartAddress:X8}");

Console.WriteLine(
    $"Blocos: {function.BasicBlocks.Count}");

foreach (MipsBasicBlock functionCfgBlock in function.BasicBlocks)
{
    Console.WriteLine(
        $"Bloco: 0x{functionCfgBlock.StartAddress:X8} | " +
        $"Instruções: {functionCfgBlock.Instructions.Count}");

    if (functionCfgBlock.BranchTarget.HasValue)
    {
        Console.WriteLine(
            $"    Branch → " +
            $"0x{functionCfgBlock.BranchTarget.Value:X8}");
    }

    if (functionCfgBlock.FallthroughAddress.HasValue)
    {
        Console.WriteLine(
            $"    Continuação → " +
            $"0x{functionCfgBlock.FallthroughAddress.Value:X8}");
    }

    if (functionCfgBlock.CallTarget.HasValue)
    {
        Console.WriteLine(
            $"    Call → " +
            $"0x{functionCfgBlock.CallTarget.Value:X8}");
    }

    if (functionCfgBlock.IsReturn)
    {
        Console.WriteLine(
            "    Return");
    }
}

    Console.WriteLine();
    Console.WriteLine("=== CHAMADAS DA FUNÇÃO 0x00200028 ===");

    MipsFunction functionWithCalls =
        functionBuilder.Build(
            0x00200028,
            64,
            30);

    Console.WriteLine();
    Console.WriteLine("=== PROGRAMA MIPS ===");

    MipsProgram mipsProgram =
        new();

        mipsProgram.AddFunction(function);
        mipsProgram.AddFunction(functionWithCalls);

    Console.WriteLine(
        $"Funções registradas: {mipsProgram.Functions.Count}");

    foreach (MipsFunction registeredFunction in
        mipsProgram.Functions.Values.OrderBy(
            f => f.StartAddress))
{
    Console.WriteLine(
        $"Função: 0x{registeredFunction.StartAddress:X8} | " +
        $"Blocos: {registeredFunction.BasicBlocks.Count} | " +
        $"Chamadas: {registeredFunction.CalledFunctions.Count}");
}

    Console.WriteLine(
        $"Função: 0x{functionWithCalls.StartAddress:X8}");

    Console.WriteLine(
        $"Blocos: {functionWithCalls.BasicBlocks.Count}");

    Console.WriteLine(
        $"Chamadas: {functionWithCalls.CalledFunctions.Count}");

    foreach (uint calledAddress in functionWithCalls.CalledFunctions)
    {
        Console.WriteLine(
            $"    Call → 0x{calledAddress:X8}");
    }


        Console.WriteLine(
            $"Total de blocos: {controlFlowGraph.Blocks.Count}");

    foreach (var cfgEntry in controlFlowGraph.Blocks
        .OrderBy(entry => entry.Key))
    {
        MipsBasicBlock cfgBlock = cfgEntry.Value;

        Console.WriteLine(
            $"Bloco: 0x{cfgBlock.StartAddress:X8} | " +
            $"Instruções: {cfgBlock.Instructions.Count}");

    if (cfgBlock.IsReturn)
    {
        Console.WriteLine("    Return");
    }

    if (cfgBlock.BranchTarget.HasValue)
    {
        Console.WriteLine(
            $"    Branch/Jump → 0x{cfgBlock.BranchTarget.Value:X8}");
    } 

    if (cfgBlock.FallthroughAddress.HasValue)
    {
        Console.WriteLine(
            $"    Continuação → 0x{cfgBlock.FallthroughAddress.Value:X8}");
    }
    }

    Console.WriteLine(
        $"Bloco iniciado em: 0x{functionBlock.StartAddress:X8}");

    foreach (MipsInstruction functionInstruction in functionBlock.Instructions)
    {
        Console.WriteLine(
            $"    {functionInstruction.Assembly}");
    }

    if (functionBlock.BranchTarget.HasValue)
    {
        Console.WriteLine(
            $"Branch → 0x{functionBlock.BranchTarget.Value:X8}");
    }

    if (functionBlock.FallthroughAddress.HasValue)
    {
        Console.WriteLine(
            $"Fallthrough → 0x{functionBlock.FallthroughAddress.Value:X8}");
    }

    if (functionBlock.IsReturn)
    {
        Console.WriteLine(
            "Return → $ra");
    }