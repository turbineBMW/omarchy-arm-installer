namespace SurfaceUsbCreator.Core.Hardware;

/// The few things a report needs from a display's EDID.
public static class Edid
{
    static readonly byte[] Header = [0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00];

    public static bool IsValid(ReadOnlySpan<byte> edid) => edid.Length >= 128 && edid[..8].SequenceEqual(Header);

    /// The panel ID systemd's extra CHIDs use: the three-letter PNP vendor and
    /// the product code as four upper-case hex digits, e.g. SDC4195.
    public static string? PanelId(ReadOnlySpan<byte> edid)
    {
        if (!IsValid(edid)) return null;
        var vendor = (edid[8] << 8) | edid[9];
        var letters = new[] { (vendor >> 10) & 0x1F, (vendor >> 5) & 0x1F, vendor & 0x1F };
        if (letters.Any(l => l is < 1 or > 26)) return null;
        var product = edid[10] | (edid[11] << 8);
        return string.Concat(letters.Select(l => (char)('A' + l - 1))) + product.ToString("X4");
    }

    /// A copy without the panel's serial number (base block bytes 12-15 and
    /// any serial-number display descriptor), checksum fixed.
    public static byte[] Redact(ReadOnlySpan<byte> edid)
    {
        var copy = edid.ToArray();
        if (!IsValid(copy)) return copy;
        Array.Clear(copy, 12, 4);
        for (var d = 54; d <= 108; d += 18)
        {
            // Display descriptor tag 0xFF: serial number string.
            if (copy[d] == 0 && copy[d + 1] == 0 && copy[d + 3] == 0xFF)
            {
                for (var i = d + 5; i < d + 18; i++) copy[i] = 0x20;
                copy[d + 5] = (byte)'0';
                copy[d + 6] = 0x0A;
            }
        }
        byte sum = 0;
        for (var i = 0; i < 127; i++) sum += copy[i];
        copy[127] = (byte)(0x100 - sum);
        return copy;
    }
}
