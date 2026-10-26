using System.Text;
using System.Text.Json.Serialization;

namespace OmarchyArmInstaller.Core.Hardware;

/// One registry value as the report carries it: its kind, its data (a
/// string, a list of strings, a number or bytes), and for resource lists
/// what the bytes mean.
public sealed record RegistryValue(string Kind, object? Value, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] object? Decoded = null);

/// A registry key exported whole: its values and its subkeys.
public sealed class RegistryDump
{
    public Dictionary<string, RegistryValue> Values { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, RegistryDump> Keys { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonIgnore]
    public int Count => 1 + Keys.Values.Sum(k => k.Count);
}

/// Everything the configuration manager says about a device: one property
/// per key, by the key's name where it has one.
public sealed record DeviceProperty(string Key, string Type, object? Value);

/// Decodes Windows' CM_RESOURCE_LIST / CM_FULL_RESOURCE_DESCRIPTOR, the
/// REG_RESOURCE_LIST and REG_FULL_RESOURCE_DESCRIPTOR registry values under
/// HARDWARE\RESOURCEMAP and HARDWARE\DESCRIPTION\System: the memory ranges,
/// interrupts, ports and bus connections each driver was given.
public static class CmResources
{
    /// A REG_RESOURCE_LIST: a count, then that many full descriptors.
    public static object? DecodeList(byte[] data)
    {
        try
        {
            if (data.Length < 4) return null;
            var count = BitConverter.ToUInt32(data, 0);
            if (count > 1024) return null;
            var lists = new List<object>();
            var offset = 4;
            for (var i = 0; i < count && offset < data.Length; i++)
            {
                var (full, next) = DecodeFull(data, offset);
                if (full is null) break;
                lists.Add(full);
                offset = next;
            }
            return lists;
        }
        catch (Exception e) when (e is ArgumentException or IndexOutOfRangeException)
        {
            return null;
        }
    }

    /// A REG_FULL_RESOURCE_DESCRIPTOR: one full descriptor without a count.
    public static object? DecodeFullDescriptor(byte[] data)
    {
        try
        {
            return DecodeFull(data, 0).Item1;
        }
        catch (Exception e) when (e is ArgumentException or IndexOutOfRangeException)
        {
            return null;
        }
    }

    static readonly string[] InterfaceTypes =
    [
        "Internal", "Isa", "Eisa", "MicroChannel", "TurboChannel", "PCIBus", "VMEBus", "NuBus", "PCMCIABus", "CBus", "MPIBus",
        "MPSABus", "ProcessorInternal", "InternalPowerBus", "PNPISABus", "PNPBus", "Vmcs", "ACPIBus",
    ];

    static (object?, int) DecodeFull(byte[] data, int offset)
    {
        // CM_FULL_RESOURCE_DESCRIPTOR: InterfaceType u32, BusNumber u32, then
        // CM_PARTIAL_RESOURCE_LIST: Version u16, Revision u16, Count u32, descriptors.
        if (offset + 16 > data.Length) return (null, data.Length);
        var interfaceType = BitConverter.ToInt32(data, offset);
        var bus = BitConverter.ToUInt32(data, offset + 4);
        var version = BitConverter.ToUInt16(data, offset + 8);
        var revision = BitConverter.ToUInt16(data, offset + 10);
        var count = BitConverter.ToUInt32(data, offset + 12);
        if (count > 4096) return (null, data.Length);
        offset += 16;
        var descriptors = new List<object>();
        for (var i = 0; i < count; i++)
        {
            // CM_PARTIAL_RESOURCE_DESCRIPTOR, packed to 4: Type u8, ShareDisposition u8, Flags u16, 16 bytes of union.
            if (offset + 20 > data.Length) break;
            var type = data[offset];
            var share = data[offset + 1];
            var flags = BitConverter.ToUInt16(data, offset + 2);
            var u = offset + 4;
            object d;
            var extra = 0;
            switch (type)
            {
                case 1: // Port
                    d = new { type = "Port", share = Share(share), flags, start = Hex(BitConverter.ToUInt64(data, u)), length = BitConverter.ToUInt32(data, u + 8) };
                    break;
                case 2: // Interrupt
                    d = (flags & 4) != 0
                        ? new { type = "Interrupt", share = Share(share), flags, messageSignaled = true, messageCount = BitConverter.ToUInt16(data, u),
                                vector = BitConverter.ToUInt32(data, u + 4), affinity = Hex(BitConverter.ToUInt64(data, u + 8)) }
                        : new { type = "Interrupt", share = Share(share), flags, level = BitConverter.ToUInt16(data, u), group = BitConverter.ToUInt16(data, u + 2),
                                vector = BitConverter.ToUInt32(data, u + 4), affinity = Hex(BitConverter.ToUInt64(data, u + 8)) };
                    break;
                case 4: // Dma
                    d = new { type = "Dma", share = Share(share), flags, channel = BitConverter.ToUInt32(data, u), port = BitConverter.ToUInt32(data, u + 4) };
                    break;
                case 5: // DeviceSpecific
                    {
                        var size = BitConverter.ToUInt32(data, u);
                        if (size > data.Length - offset - 20) size = (uint)(data.Length - offset - 20);
                        d = new { type = "DeviceSpecific", share = Share(share), flags, size, data = Convert.ToHexString(data, offset + 20, (int)size).ToLowerInvariant() };
                        extra = (int)size;
                    }
                    break;
                case 6: // BusNumber
                    d = new { type = "BusNumber", share = Share(share), flags, start = BitConverter.ToUInt32(data, u), length = BitConverter.ToUInt32(data, u + 4) };
                    break;
                case 3: // Memory
                    d = new { type = "Memory", share = Share(share), flags, start = Hex(BitConverter.ToUInt64(data, u)), length = BitConverter.ToUInt32(data, u + 8) };
                    break;
                case 7: // MemoryLarge: Flags say 40, 48 or 64-bit length
                    {
                        var start = BitConverter.ToUInt64(data, u);
                        var raw = BitConverter.ToUInt32(data, u + 8);
                        ulong length = (flags & 0x200) != 0 ? (ulong)raw << 8 : (flags & 0x400) != 0 ? (ulong)raw << 16 : (ulong)raw << 32;
                        d = new { type = "MemoryLarge", share = Share(share), flags, start = Hex(start), length = Hex(length) };
                    }
                    break;
                case 132: // Connection: GPIO or serial bus (I2C, SPI, UART) through the resource hub
                    {
                        var cls = data[u];
                        var ctype = data[u + 1];
                        var id = ((ulong)BitConverter.ToUInt32(data, u + 8) << 32) | BitConverter.ToUInt32(data, u + 4);
                        d = new
                        {
                            type = "Connection", share = Share(share), flags,
                            @class = cls switch { 1 => "Gpio", 2 => "Serial", _ => cls.ToString() },
                            connectionType = (cls, ctype) switch
                            {
                                (1, 2) => "GpioIo", (1, 3) => "GpioInt", (2, 1) => "I2C", (2, 2) => "SPI", (2, 3) => "UART", _ => ctype.ToString(),
                            },
                            id = Hex(id),
                        };
                    }
                    break;
                case 129: // DevicePrivate
                    d = new { type = "DevicePrivate", share = Share(share), flags, data = new[] { BitConverter.ToUInt32(data, u), BitConverter.ToUInt32(data, u + 4), BitConverter.ToUInt32(data, u + 8) } };
                    break;
                default:
                    d = new { type = type.ToString(), share = Share(share), flags, data = Convert.ToHexString(data, u, 16).ToLowerInvariant() };
                    break;
            }
            descriptors.Add(d);
            offset += 20 + extra;
        }
        return (new
        {
            interfaceType = interfaceType >= 0 && interfaceType < InterfaceTypes.Length ? InterfaceTypes[interfaceType] : interfaceType.ToString(),
            busNumber = bus,
            version,
            revision,
            descriptors,
        }, offset);
    }

    static string Share(byte s) => s switch { 0 => "Undetermined", 1 => "DeviceExclusive", 2 => "DriverExclusive", 3 => "Shared", _ => s.ToString() };
    static string Hex(ulong v) => "0x" + v.ToString("x");
}
