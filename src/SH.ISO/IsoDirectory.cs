namespace SH.ISO;

public sealed class IsoDirectory
{
    private const int SectorSize = 2048;

    private readonly IsoReader _reader;

    public IsoDirectory(IsoReader reader)
    {
        _reader = reader;
    }

    // Mantido por compatibilidade: lê apenas o primeiro setor.
    public List<IsoDirectoryRecord> Read(int sector)
    {
        return Read(sector, SectorSize);
    }

    // Lê o diretório inteiro, inclusive quando ocupa vários setores.
    public List<IsoDirectoryRecord> Read(int sector, uint dataLength)
    {
        int sectorCount =
            Math.Max(1, (int)((dataLength + SectorSize - 1) / SectorSize));

        List<IsoDirectoryRecord> records = new();

        for (int s = 0; s < sectorCount; s++)
        {
            byte[] data = _reader.ReadSector(sector + s);

            int offset = 0;

            while (offset < data.Length)
            {
                byte length = data[offset];

                // Registros não cruzam o limite do setor; length == 0
                // significa padding até o fim deste setor.
                if (length == 0)
                    break;

                if (offset + length > data.Length)
                    break;

                records.Add(IsoDirectoryRecord.Parse(data, offset));

                offset += length;
            }
        }

        return records;
    }
}
