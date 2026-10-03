namespace SH.ISO;

public sealed class IsoReader : IDisposable
{
    private const int SectorSize = 2048;

    private readonly FileStream _stream;

    public long Length => _stream.Length;

    public IsoReader(string path)
    {
        _stream = File.OpenRead(path);
    }

    public byte[] ReadSector(int sector)
    {
        long offset = (long)sector * SectorSize;

        _stream.Seek(offset, SeekOrigin.Begin);

        byte[] buffer = new byte[SectorSize];

        int totalRead = 0;

        while (totalRead < buffer.Length)
        {
            int read = _stream.Read(
                buffer,
                totalRead,
                buffer.Length - totalRead);

            if (read == 0)
                throw new EndOfStreamException();

            totalRead += read;
        }

        return buffer;
    }

    public void Dispose()
    {
        _stream.Dispose();
    }

    public byte[] ReadBytes(int sector, int size)
{
    long offset = (long)sector * SectorSize;

    _stream.Seek(offset, SeekOrigin.Begin);

    byte[] buffer = new byte[size];

    int totalRead = 0;

    while (totalRead < buffer.Length)
    {
        int read = _stream.Read(
            buffer,
            totalRead,
            buffer.Length - totalRead);

        if (read == 0)
            throw new EndOfStreamException();

        totalRead += read;
    }

    return buffer;
}
}