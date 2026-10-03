namespace SH.ISO;

public sealed class IsoDirectory
{
    private readonly IsoReader _reader;

    public IsoDirectory(IsoReader reader)
    {
        _reader = reader;
    }

    public List<IsoDirectoryRecord> Read(int sector)
    {
        byte[] data = _reader.ReadSector(sector);

        List<IsoDirectoryRecord> records = new();

        int offset = 0;

        while (offset < data.Length)
        {
            byte length = data[offset];

            if (length == 0)
            {
                offset++;
                continue;
            }

            if (offset + length > data.Length)
                break;

            IsoDirectoryRecord record =
                IsoDirectoryRecord.Parse(data, offset);

            records.Add(record);

            offset += length;
        }

        return records;
    }
}