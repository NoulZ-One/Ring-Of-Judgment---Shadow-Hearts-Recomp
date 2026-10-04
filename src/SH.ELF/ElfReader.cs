namespace SH.ELF;

public sealed class ElfReader
{
    public ElfHeader Header { get; }

    public IReadOnlyList<ElfProgramHeader> ProgramHeaders =>
        _programHeaders;

    public IReadOnlyList<ElfSegment> Segments =>
        _segments;

    private readonly List<ElfProgramHeader> _programHeaders;
    private readonly List<ElfSegment> _segments;

    public ElfReader(byte[] data)
    {
        Header = ElfHeader.Parse(data);

        _programHeaders = new List<ElfProgramHeader>();
        _segments = new List<ElfSegment>();

        for (int i = 0; i < Header.ProgramHeaderCount; i++)
        {
            int offset =
                checked(
                    (int)Header.ProgramHeaderOffset +
                    i * Header.ProgramHeaderEntrySize);

            ElfProgramHeader programHeader =
                ElfProgramHeader.Parse(data, offset);

            _programHeaders.Add(programHeader);

            ElfSegment segment =
                new(programHeader);

            _segments.Add(segment);
        }
    }
}