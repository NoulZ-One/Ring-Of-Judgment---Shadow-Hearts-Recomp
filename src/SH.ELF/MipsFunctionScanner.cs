using System.Buffers.Binary;

namespace SH.ELF;

public sealed class PointerScanResult
{
    // Quantas palavras (alinhadas, fora do código alcançado) apontam para um alvo válido.
    public int PointerWords { get; }

    // Alvos distintos referenciados por essas palavras.
    public HashSet<uint> Targets { get; }

    public PointerScanResult(int pointerWords, HashSet<uint> targets)
    {
        PointerWords = pointerWords;
        Targets = targets;
    }
}

// Procura funções que a varredura de JAL não encontra:
//  - prólogos típicos do compilador (ADDIU $sp,$sp,-N seguido de salvar $ra);
//  - ponteiros de função guardados em dados (tabelas, vtables, callbacks).
public static class MipsFunctionScanner
{
    // Quantas instruções depois do ajuste de $sp procurar o salvamento de $ra.
    private const int PrologueLookahead = 8;

    public static List<uint> FindPrologueCandidates(
        ElfImage image,
        Func<uint, bool> isReached)
    {
        List<uint> result = new();

        foreach (ElfImage.Region region in image.Regions)
        {
            int words = (int)(region.FileSize / 4);

            for (int index = 0; index < words; index++)
            {
                uint address = region.VirtualAddress + (uint)(index * 4);

                if (isReached(address))
                    continue;

                if (!IsStackAdjust(ReadWord(region, index)))
                    continue;

                if (SavesReturnAddress(region, index + 1, words))
                    result.Add(address);
            }
        }

        return result;
    }

    public static PointerScanResult FindFunctionPointers(
        ElfImage image,
        Func<uint, bool> isReached,
        HashSet<uint> validTargets)
    {
        HashSet<uint> targets = new();
        int pointerWords = 0;

        foreach (ElfImage.Region region in image.Regions)
        {
            int words = (int)(region.FileSize / 4);

            for (int index = 0; index < words; index++)
            {
                uint address = region.VirtualAddress + (uint)(index * 4);

                // Palavras que já foram decodificadas como instrução não contam.
                if (isReached(address))
                    continue;

                uint value = ReadWord(region, index);

                if (validTargets.Contains(value))
                {
                    pointerWords++;
                    targets.Add(value);
                }
            }
        }

        return new PointerScanResult(pointerWords, targets);
    }

    private static uint ReadWord(ElfImage.Region region, int index)
    {
        return BinaryPrimitives.ReadUInt32LittleEndian(
            region.Data.AsSpan(index * 4, 4));
    }

    // ADDIU $sp,$sp,-N  (0x27BDxxxx)  ou  DADDIU $sp,$sp,-N  (0x67BDxxxx), com N > 0.
    private static bool IsStackAdjust(uint raw)
    {
        uint top = raw & 0xFFFF0000;

        return (top == 0x27BD0000 || top == 0x67BD0000) &&
               (raw & 0x8000) != 0;
    }

    // SD/SW/SQ $ra, off($sp) nas próximas instruções, sem passar por um JR $ra.
    private static bool SavesReturnAddress(
        ElfImage.Region region,
        int startIndex,
        int words)
    {
        for (int step = 0; step < PrologueLookahead; step++)
        {
            int index = startIndex + step;

            if (index >= words)
                return false;

            uint raw = ReadWord(region, index);

            if (raw == 0x03E00008)
                return false;

            uint top = raw & 0xFFFF0000;

            if (top == 0xFFBF0000 ||
                top == 0xAFBF0000 ||
                top == 0x7FBF0000)
                return true;
        }

        return false;
    }
}
