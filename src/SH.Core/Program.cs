using SH.ISO;

string isoPath = @"E:\Projeto Recomp\Shadow Hearts (USA).iso";

using var iso = new IsoReader(isoPath);

byte[] sector16 = iso.ReadSector(16);

IsoVolumeDescriptor volume =
    IsoVolumeDescriptor.Parse(sector16);

Console.WriteLine("=== Shadow Hearts ISO ===");
Console.WriteLine();

Console.WriteLine($"Sistema:       {volume.SystemIdentifier}");
Console.WriteLine($"Volume:        {volume.VolumeIdentifier}");
Console.WriteLine($"Setores:       {volume.VolumeSpaceSize}");
Console.WriteLine($"Tamanho setor: {volume.LogicalBlockSize} bytes");
Console.WriteLine($"Raiz:          setor {volume.RootDirectorySector}");
Console.WriteLine();
Console.WriteLine("=== Diretório raiz ===");

byte[] rootSector = iso.ReadSector(
    (int)volume.RootDirectorySector);

Console.WriteLine(
    $"Tamanho do setor: {rootSector.Length} bytes");

Console.WriteLine();
Console.WriteLine("Primeiros 128 bytes:");

for (int i = 0; i < 128; i++)
{
    Console.Write($"{rootSector[i]:X2} ");

    if ((i + 1) % 16 == 0)
        Console.WriteLine();

        Console.WriteLine();
Console.WriteLine("=== Arquivos do diretório raiz ===");

IsoDirectory directory = new(iso);

List<IsoDirectoryRecord> records =
    directory.Read((int)volume.RootDirectorySector);

foreach (IsoDirectoryRecord record in records)
{
    string type = record.IsDirectory
        ? "<DIR>"
        : "<FILE>";

    Console.WriteLine(
        $"{type,-6} {record.FileIdentifier,-30} " +
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

Console.WriteLine();
Console.WriteLine("Primeiros 64 bytes:");

for (int j = 0; j < executableHeader.Length; j++)
{
    Console.Write($"{executableHeader[j]:X2} ");

    if ((j + 1) % 16 == 0)
        Console.WriteLine();
}
}