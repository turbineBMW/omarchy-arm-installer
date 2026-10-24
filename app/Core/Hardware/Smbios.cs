using System.Text;

namespace SurfaceUsbCreator.Core.Hardware;

/// One SMBIOS structure: its formatted area (header included) and its strings.
public sealed class SmbiosStructure
{
    public required byte[] Formatted { get; init; }
    public required List<string> Strings { get; init; }

    public byte Type => Formatted[0];
    public byte Length => Formatted[1];

    /// The string a formatted-area byte at <offset> refers to; null when the
    /// structure is too short for it or the field holds no string.
    public string? String(int offset)
    {
        if (offset >= Formatted.Length) return null;
        var index = Formatted[offset];
        return index == 0 || index > Strings.Count ? null : Strings[index - 1];
    }

    public byte? Byte(int offset) => offset < Formatted.Length ? Formatted[offset] : null;
}

/// The identity fields CHIDs, device trees and reports are keyed on.
public sealed record SmbiosIdentity(
    string? Manufacturer, string? Family, string? ProductName, string? Sku, string? Version,
    string? BaseboardManufacturer, string? BaseboardProduct,
    string? BiosVendor, string? BiosVersion, string? BiosDate, byte? BiosMajor, byte? BiosMinor,
    byte? EnclosureType);

/// Reads and redacts a raw SMBIOS structure table (the data Windows'
/// GetSystemFirmwareTable('RSMB') returns after its 8-byte header, and what
/// Linux exposes as /sys/firmware/dmi/tables/DMI).
public static class Smbios
{
    public static List<SmbiosStructure> Parse(ReadOnlySpan<byte> table)
    {
        var list = new List<SmbiosStructure>();
        var at = 0;
        while (at + 4 <= table.Length)
        {
            var length = table[at + 1];
            if (length < 4 || at + length > table.Length) break;
            var formatted = table.Slice(at, length).ToArray();

            // Strings follow, each NUL-terminated, the set ending in a second NUL.
            var strings = new List<string>();
            var p = at + length;
            if (p + 1 < table.Length && table[p] == 0 && table[p + 1] == 0)
            {
                p += 2;
            }
            else
            {
                while (p < table.Length && table[p] != 0)
                {
                    var end = table[p..].IndexOf((byte)0);
                    if (end < 0) { p = table.Length; break; }
                    strings.Add(Encoding.ASCII.GetString(table.Slice(p, end)));
                    p += end + 1;
                }
                p++;
            }
            list.Add(new SmbiosStructure { Formatted = formatted, Strings = strings });
            if (formatted[0] == 127) break; // end-of-table structure
            at = p;
        }
        return list;
    }

    public static byte[] Serialize(IEnumerable<SmbiosStructure> structures)
    {
        using var output = new MemoryStream();
        foreach (var s in structures)
        {
            output.Write(s.Formatted);
            if (s.Strings.Count == 0)
            {
                output.Write([0, 0]);
                continue;
            }
            foreach (var str in s.Strings)
            {
                output.Write(Encoding.ASCII.GetBytes(str));
                output.WriteByte(0);
            }
            output.WriteByte(0);
        }
        return output.ToArray();
    }

    public static SmbiosIdentity Identity(IReadOnlyList<SmbiosStructure> structures)
    {
        var bios = structures.FirstOrDefault(s => s.Type == 0);
        var system = structures.FirstOrDefault(s => s.Type == 1);
        var board = structures.FirstOrDefault(s => s.Type == 2);
        var chassis = structures.FirstOrDefault(s => s.Type == 3);

        // 0xFF in both release bytes means the BIOS does not report them.
        byte? major = bios?.Byte(0x14), minor = bios?.Byte(0x15);
        if (major == 0xFF && minor == 0xFF) major = minor = null;

        return new SmbiosIdentity(
            system?.String(0x04), system?.String(0x1A), system?.String(0x05), system?.String(0x19), system?.String(0x06),
            board?.String(0x04), board?.String(0x05),
            bios?.String(0x04), bios?.String(0x05), bios?.String(0x08), major, minor,
            chassis?.Byte(0x05) is { } type ? (byte)(type & 0x7F) : null);
    }

    /// String fields and bytes that identify one machine rather than a model:
    /// serial numbers, asset tags, the system UUID. (type, offset) pairs.
    static readonly (byte Type, int Offset, string Label)[] PersonalStrings =
    [
        (1, 0x07, "System serial number"),
        (2, 0x07, "Baseboard serial number"), (2, 0x08, "Baseboard asset tag"),
        (3, 0x07, "Chassis serial number"), (3, 0x08, "Chassis asset tag"),
        (4, 0x20, "Processor serial number"), (4, 0x21, "Processor asset tag"),
        (17, 0x18, "Memory serial numbers"), (17, 0x19, "Memory asset tags"),
        (22, 0x07, "Battery serial number"),
        (39, 0x07, "Power supply serial number"), (39, 0x08, "Power supply asset tag"),
    ];

    public const string Redacted = "redacted";

    /// A copy with serial numbers, asset tags and the system UUID replaced.
    /// Returns what was redacted, for the report's own record.
    public static (List<SmbiosStructure> Table, List<string> Redactions) Redact(IReadOnlyList<SmbiosStructure> structures)
    {
        var copy = new List<SmbiosStructure>();
        var redactions = new List<string>();
        foreach (var s in structures)
        {
            var formatted = (byte[])s.Formatted.Clone();
            var strings = new List<string>(s.Strings);
            foreach (var (type, offset, label) in PersonalStrings)
            {
                if (type != s.Type || offset >= formatted.Length) continue;
                var index = formatted[offset];
                if (index == 0 || index > strings.Count || strings[index - 1].Length == 0) continue;
                strings[index - 1] = Redacted;
                redactions.Add(label);
            }
            if (s.Type == 1 && formatted.Length >= 0x18)
            {
                Array.Clear(formatted, 0x08, 16); // system UUID
                redactions.Add("System UUID");
            }
            if (s.Type == 22 && formatted.Length >= 0x12)
            {
                formatted[0x10] = formatted[0x11] = 0; // SBDS serial number
                redactions.Add("Battery serial number");
            }
            copy.Add(new SmbiosStructure { Formatted = formatted, Strings = strings });
        }
        return (copy, redactions.Distinct().ToList());
    }
}
