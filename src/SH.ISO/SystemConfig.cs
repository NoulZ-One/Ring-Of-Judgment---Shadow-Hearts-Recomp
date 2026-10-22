using System.Text;

namespace SH.ISO;

// Interpreta o SYSTEM.CNF de um disco PS2 (e PS1).
//
//   BOOT2 = cdrom0:\SLUS_203.47;1
//   VER = 1.00
//   VMODE = NTSC
public sealed class SystemConfig
{
    private readonly Dictionary<string, string> _entries;

    public IReadOnlyDictionary<string, string> Entries => _entries;

    // "BOOT2" (PS2) ou "BOOT" (PS1).
    public string? BootKey { get; }

    // Valor bruto, como está no arquivo: cdrom0:\SLUS_203.47;1
    public string? BootRaw { get; }

    // Caminho já normalizado, relativo à raiz do disco: SLUS_203.47
    public string? BootPath { get; }

    public bool IsPlayStation2Boot =>
        string.Equals(BootKey, "BOOT2", StringComparison.OrdinalIgnoreCase);

    public string? Version => Get("VER");

    public string? VideoMode => Get("VMODE");

    private SystemConfig(Dictionary<string, string> entries)
    {
        _entries = entries;

        if (entries.TryGetValue("BOOT2", out string? boot2))
        {
            BootKey = "BOOT2";
            BootRaw = boot2;
        }
        else if (entries.TryGetValue("BOOT", out string? boot))
        {
            BootKey = "BOOT";
            BootRaw = boot;
        }

        BootPath = BootRaw == null
            ? null
            : NormalizeBootPath(BootRaw);
    }

    public string? Get(string key)
    {
        return _entries.TryGetValue(key, out string? value)
            ? value
            : null;
    }

    public static SystemConfig Parse(byte[] data)
    {
        return Parse(Encoding.ASCII.GetString(data));
    }

    public static SystemConfig Parse(string text)
    {
        Dictionary<string, string> entries =
            new(StringComparer.OrdinalIgnoreCase);

        // Aceita \r\n, \n, \r e para em padding (\0) ou EOF (0x1A).
        string[] lines = text.Split(
            new[] { '\r', '\n', '\0', '\x1A' },
            StringSplitOptions.RemoveEmptyEntries);

        foreach (string line in lines)
        {
            int separator = line.IndexOf('=');

            if (separator < 0)
                continue;

            string key = line[..separator].Trim();
            string value = line[(separator + 1)..].Trim();

            if (key.Length > 0)
                entries[key] = value;
        }

        return new SystemConfig(entries);
    }

    // "cdrom0:\SLUS_203.47;1" -> "SLUS_203.47"
    // "cdrom0:\DIR\FILE.ELF;1" -> "DIR/FILE.ELF"
    public static string? NormalizeBootPath(string raw)
    {
        string path = raw.Trim();

        int colon = path.IndexOf(':');

        if (colon >= 0)
            path = path[(colon + 1)..];

        int version = path.IndexOf(';');

        if (version >= 0)
            path = path[..version];

        path = path.Replace('\\', '/').Trim('/');

        return path.Length == 0
            ? null
            : path;
    }
}
