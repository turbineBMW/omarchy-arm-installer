using System.Security.Cryptography;
using System.Text;

namespace SurfaceUsbCreator.Core.Hardware;

public sealed record ChidValue(string Type, string Fields, Guid Value);

/// Computer Hardware IDs, as systemd-stub matches them to pick a device tree
/// (systemd src/fundamental/chid.c), the same values Microsoft's
/// ComputerHardwareIds and fwupd produce, plus systemd's extra ones that add
/// the built-in panel's EDID ID.
public static class Chid
{
    enum Field { Manufacturer, Family, ProductName, Sku, BoardManufacturer, BoardProduct, BiosVendor, BiosVersion, BiosMajor, BiosMinor, Enclosure, EdidPanel }

    const string FieldLetters = "MFPSmpBvRreE";

    static readonly (string Type, Field[] Fields)[] Types =
    [
        ("0", [Field.Manufacturer, Field.Family, Field.ProductName, Field.Sku, Field.BiosVendor, Field.BiosVersion, Field.BiosMajor, Field.BiosMinor]),
        ("1", [Field.Manufacturer, Field.Family, Field.ProductName, Field.BiosVendor, Field.BiosVersion, Field.BiosMajor, Field.BiosMinor]),
        ("2", [Field.Manufacturer, Field.ProductName, Field.BiosVendor, Field.BiosVersion, Field.BiosMajor, Field.BiosMinor]),
        ("3", [Field.Manufacturer, Field.Family, Field.ProductName, Field.Sku, Field.BoardManufacturer, Field.BoardProduct]),
        ("4", [Field.Manufacturer, Field.Family, Field.ProductName, Field.Sku]),
        ("5", [Field.Manufacturer, Field.Family, Field.ProductName]),
        ("6", [Field.Manufacturer, Field.Sku, Field.BoardManufacturer, Field.BoardProduct]),
        ("7", [Field.Manufacturer, Field.Sku]),
        ("8", [Field.Manufacturer, Field.ProductName, Field.BoardManufacturer, Field.BoardProduct]),
        ("9", [Field.Manufacturer, Field.ProductName]),
        ("10", [Field.Manufacturer, Field.Family, Field.BoardManufacturer, Field.BoardProduct]),
        ("11", [Field.Manufacturer, Field.Family]),
        ("12", [Field.Manufacturer, Field.Enclosure]),
        ("13", [Field.Manufacturer, Field.BoardManufacturer, Field.BoardProduct]),
        ("14", [Field.Manufacturer]),
        ("ext0", [Field.Manufacturer, Field.Family, Field.ProductName, Field.EdidPanel]),
        ("ext1", [Field.Manufacturer, Field.Family, Field.EdidPanel]),
        ("ext2", [Field.Manufacturer, Field.Sku, Field.EdidPanel]),
    ];

    // {70ffd812-4c7f-4c7d-0000-000000000000}, hashed in big-endian byte order.
    static readonly byte[] Namespace = [0x70, 0xff, 0xd8, 0x12, 0x4c, 0x7f, 0x4c, 0x7d, 0, 0, 0, 0, 0, 0, 0, 0];

    /// Every CHID whose fields are all present; types with a missing field are
    /// left out, as the spec says.
    public static List<ChidValue> Calculate(SmbiosIdentity id, string? edidPanel)
    {
        string? Value(Field f) => f switch
        {
            Field.Manufacturer => Text(id.Manufacturer),
            Field.Family => Text(id.Family),
            Field.ProductName => Text(id.ProductName),
            Field.Sku => Text(id.Sku),
            Field.BoardManufacturer => Text(id.BaseboardManufacturer),
            Field.BoardProduct => Text(id.BaseboardProduct),
            Field.BiosVendor => Text(id.BiosVendor),
            Field.BiosVersion => Text(id.BiosVersion),
            Field.BiosMajor => id.BiosMajor is { } major ? major.ToString("x2") : null,
            Field.BiosMinor => id.BiosMinor is { } minor ? minor.ToString("x2") : null,
            // Hex without padding; 0 ("other") is an empty string.
            Field.Enclosure => id.EnclosureType is { } e ? (e == 0 ? "" : e.ToString("x")) : null,
            Field.EdidPanel => string.IsNullOrEmpty(edidPanel) ? null : edidPanel,
            _ => null,
        };

        var result = new List<ChidValue>();
        foreach (var (type, fields) in Types)
        {
            var values = fields.Select(Value).ToList();
            if (values.Any(v => v is null)) continue;

            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
            sha.AppendData(Namespace);
            sha.AppendData(Encoding.Unicode.GetBytes(string.Join("&", values)));
            var hash = sha.GetHashAndReset();

            // Name-based UUID, version 5, RFC 4122 variant; the hash is big-endian.
            hash[6] = (byte)((hash[6] & 0x0F) | 0x50);
            hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
            var guid = new Guid(hash.AsSpan(0, 16), bigEndian: true);

            var letters = string.Concat(Enumerable.Range(0, FieldLetters.Length)
                .Select(i => fields.Contains((Field)i) ? FieldLetters[i] : '-'));
            result.Add(new ChidValue(type, letters, guid));
        }
        return result;
    }

    /// SMBIOS strings as the kernel exposes them: without trailing blanks; an
    /// empty string counts as missing.
    static string? Text(string? value)
    {
        var trimmed = value?.TrimEnd(' ', '\t', '\0');
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}
