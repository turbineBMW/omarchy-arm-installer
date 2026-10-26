using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OmarchyArmInstaller.Core.Hardware;

/// Takes what identifies one machine out of everything the report carries:
/// MAC addresses and USB serial numbers wherever they appear, the serial
/// numbers and UUID the SMBIOS table held, the computer and user names, and
/// registry values whose names say they hold such things. The same secret
/// always gets the same placeholder, so links between files still match.
public sealed class Scrubber
{
    // A MAC address or other 12-digit hex identifier, not the last group of a GUID.
    static readonly Regex Mac = new("(?<![0-9A-Fa-f-])(?:[0-9A-Fa-f]{2}[:-]){5}[0-9A-Fa-f]{2}(?![0-9A-Fa-f])|(?<![0-9A-Fa-f-])[0-9A-Fa-f]{12}(?![0-9A-Fa-f-])");
    // USB devices with a serial number use it as the last part of their ID,
    // in instance IDs (backslashes) and symbolic links (hashes) alike.
    static readonly Regex UsbSerial = new(@"((?:^|[\\#])(?:USB|USB4|HID|WINUSB)([\\#])[^\\#]+\2)([^\\#&\s]{4,})(?=$|[\\#])", RegexOptions.IgnoreCase);
    // Registry values and properties that hold a machine's own identity.
    static readonly Regex PersonalName = new(
        @"serialn|^serial$|serialnumber|assettag|networkaddress|permanentaddress|netcfginstanceid|uniqueid|password|machineguid|computername|hostname|username|ownername|licensekey|productkey|bluetoothaddress|^bd_?addr",
        RegexOptions.IgnoreCase);
    // What firmware writes where it has no serial number.
    static readonly Regex Placeholder = new(@"^(0+|f+|x+|n/?a|none|unknown|default( string)?|to be filled.*|not specified|system serial number|empty|\.+|-+|\s*)$", RegexOptions.IgnoreCase);

    readonly Dictionary<string, string> placeholders = new(StringComparer.OrdinalIgnoreCase);
    readonly List<(string Text, Regex Pattern)> secrets = [];
    readonly List<string> redactions;
    bool macCounted, secretCounted, nameCounted;
    public int DroppedValues { get; private set; }

    public Scrubber(IEnumerable<string> smbiosSecrets, IEnumerable<string> names, List<string> redactions)
    {
        this.redactions = redactions;
        foreach (var s in smbiosSecrets.Select(s => s.Trim()).Where(s => s.Length >= 5 && !Placeholder.IsMatch(s)).Distinct(StringComparer.OrdinalIgnoreCase))
            secrets.Add((s, new Regex(Regex.Escape(s), RegexOptions.IgnoreCase)));
        foreach (var n in names.Select(n => n.Trim()).Where(n => n.Length >= 3 && !Placeholder.IsMatch(n)).Distinct(StringComparer.OrdinalIgnoreCase))
            secrets.Add((n, new Regex($@"(?<![A-Za-z0-9]){Regex.Escape(n)}(?![A-Za-z0-9])", RegexOptions.IgnoreCase)));
    }

    string Placeholder_(string secret, ref bool counted, string what)
    {
        if (!placeholders.TryGetValue(secret, out var p)) placeholders[secret] = p = $"redacted{placeholders.Count + 1}";
        if (!counted) { redactions.Add(what); counted = true; }
        return p;
    }

    /// A string with every secret replaced.
    public string Text(string value)
    {
        if (value.Length == 0) return value;
        value = UsbSerial.Replace(value, m => m.Groups[1].Value + Placeholder_(m.Groups[3].Value, ref macCounted, "USB serial numbers and MAC addresses in device IDs"));
        value = Mac.Replace(value, x => Placeholder_(x.Value, ref macCounted, "USB serial numbers and MAC addresses in device IDs"));
        foreach (var (text, pattern) in secrets)
        {
            if (!pattern.IsMatch(value)) continue;
            var isName = pattern.ToString().StartsWith("(?<!");
            value = pattern.Replace(value, isName
                ? Placeholder_(text, ref nameCounted, "The computer and user names, wherever they appear")
                : Placeholder_(text, ref secretCounted, "Serial numbers and the system UUID, wherever else they appear"));
        }
        return value;
    }

    public string? TextOrNull(string? value) => value is null ? null : Text(value);

    /// Whether a value or property with this name holds the machine's identity.
    public bool IsPersonalName(string name) => PersonalName.IsMatch(name);

    /// Bytes with any secret spelled in them (ASCII or UTF-16) overwritten.
    public byte[] Bytes(byte[] data)
    {
        if (data.Length < 5 || secrets.Count == 0) return data;
        byte[]? copy = null;
        foreach (var (text, _) in secrets)
        {
            foreach (var needle in new[] { Encoding.ASCII.GetBytes(text), Encoding.Unicode.GetBytes(text) })
            {
                if (needle.Length > data.Length) continue;
                var span = (copy ?? data).AsSpan();
                var at = IndexOf(span, needle);
                while (at >= 0)
                {
                    copy ??= (byte[])data.Clone();
                    for (var i = 0; i < needle.Length; i++) copy[at + i] = needle[i] == 0 ? (byte)0 : (byte)'X';
                    Placeholder_(text, ref secretCounted, "Serial numbers and the system UUID, wherever else they appear");
                    var next = IndexOf(copy.AsSpan(at + needle.Length), needle);
                    at = next < 0 ? -1 : at + needle.Length + next;
                }
            }
        }
        return copy ?? data;
    }

    static int IndexOf(ReadOnlySpan<byte> hay, byte[] needle)
    {
        // Case-insensitive for ASCII letters, so a serial typed either way is caught.
        for (var i = 0; i + needle.Length <= hay.Length; i++)
        {
            var j = 0;
            for (; j < needle.Length; j++)
            {
                var a = hay[i + j];
                var b = needle[j];
                if (a == b) continue;
                if (a is >= (byte)'a' and <= (byte)'z') a -= 32;
                if (b is >= (byte)'a' and <= (byte)'z') b -= 32;
                if (a != b) break;
            }
            if (j == needle.Length) return i;
        }
        return -1;
    }

    /// A property or registry value's data, scrubbed whatever its shape.
    public object? Value(object? value) => value switch
    {
        null => null,
        string s => Text(s),
        string[] a => a.Select(Text).ToArray(),
        byte[] b => Bytes(b),
        List<object?> l => l.Select(Value).ToList(),
        _ => value,
    };

    public RegistryValue Value(string name, RegistryValue v)
    {
        if (IsPersonalName(name) && v.Value is not null && v.Kind is not ("DWord" or "QWord" or "DWordBigEndian"))
        {
            DroppedValues++;
            return new RegistryValue(v.Kind, "redacted");
        }
        return v with { Value = Value(v.Value) };
    }

    public RegistryDump Dump(RegistryDump dump)
    {
        var result = new RegistryDump();
        foreach (var (name, value) in dump.Values) result.Values[Text(name)] = Value(name, value);
        foreach (var (name, key) in dump.Keys)
        {
            // A key named after an address (a Bluetooth radio's paired peers)
            // or as an identifier goes whole, contents and all.
            if (IsPersonalName(name) || Mac.IsMatch(name)) { DroppedValues++; continue; }
            result.Keys[Text(name)] = Dump(key);
        }
        return result;
    }

    public DeviceProperty Property(DeviceProperty p)
    {
        if (IsPersonalName(p.Key) && p.Value is not null) { DroppedValues++; return p with { Value = "redacted" }; }
        return p with { Value = Value(p.Value) };
    }

    /// A JSON document (what PowerShell gathered), scrubbed in place.
    public void Json(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var name in o.Select(p => p.Key).ToList())
                {
                    var child = o[name];
                    if (child is JsonValue v)
                    {
                        if (IsPersonalName(name)) { o[name] = "redacted"; DroppedValues++; }
                        else if (v.TryGetValue<string>(out var s)) o[name] = Text(s);
                    }
                    else Json(child);
                }
                break;
            case JsonArray a:
                for (var i = 0; i < a.Count; i++)
                {
                    if (a[i] is JsonValue v) { if (v.TryGetValue<string>(out var s)) a[i] = Text(s); }
                    else Json(a[i]);
                }
                break;
        }
    }
}
