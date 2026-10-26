using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using OmarchyArmInstaller.Core;
using OmarchyArmInstaller.Core.Hardware;

namespace OmarchyArmInstaller.Platform;

/// Exports registry keys whole, reading each value raw so the kinds .NET's
/// RegistryKey does not know (resource lists, full resource descriptors)
/// come through too.
[SupportedOSPlatform("windows")]
static class WindowsRegistry
{
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegQueryValueExW")]
    static extern int RegQueryValueEx(SafeRegistryHandle key, string name, IntPtr reserved, out uint type, byte[]? data, ref uint size);

    const int MaxValueBytes = 32 * 1024 * 1024;

    /// A key under HKLM, with its subkeys, or null when it does not exist or
    /// cannot be opened. Subkeys named in skip (at any depth) are left out.
    public static RegistryDump? Dump(string path, IReadOnlySet<string>? skip = null, int maxDepth = 16)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(path, RegistryRights.ReadKey);
            return key is null ? null : Dump(key, skip, 0, maxDepth);
        }
        catch (Exception e)
        {
            Log.Info($@"Registry: HKLM\{path}: {e.Message}");
            return null;
        }
    }

    static RegistryDump Dump(RegistryKey key, IReadOnlySet<string>? skip, int depth, int maxDepth)
    {
        var dump = new RegistryDump();
        string[] names;
        try { names = key.GetValueNames(); } catch { names = []; }
        foreach (var name in names)
        {
            var value = Read(key, name);
            if (value is not null) dump.Values[name] = value;
        }
        if (depth >= maxDepth) return dump;
        string[] subs;
        try { subs = key.GetSubKeyNames(); } catch { subs = []; }
        foreach (var name in subs)
        {
            if (skip is not null && skip.Contains(name)) continue;
            try
            {
                using var sub = key.OpenSubKey(name, RegistryRights.ReadKey);
                if (sub is not null) dump.Keys[name] = Dump(sub, skip, depth + 1, maxDepth);
            }
            catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                // A key only SYSTEM may read: note that it was there.
                dump.Keys[name] = new RegistryDump { Values = { ["(unreadable)"] = new RegistryValue("Error", e.GetType().Name) } };
            }
        }
        return dump;
    }

    static RegistryValue? Read(RegistryKey key, string name)
    {
        uint size = 0;
        var rc = RegQueryValueEx(key.Handle, name, IntPtr.Zero, out var type, null, ref size);
        if (rc != 0 && rc != 234 /* ERROR_MORE_DATA */) return null;
        if (size > MaxValueBytes) return new RegistryValue(Kind(type), null, new { size, leftOut = "larger than 32 MiB" });
        var data = new byte[size];
        rc = RegQueryValueEx(key.Handle, name, IntPtr.Zero, out type, data, ref size);
        if (rc != 0) return null;
        if (size < data.Length) data = data[..(int)size];
        return Decode(type, data);
    }

    static string Kind(uint type) => type switch
    {
        0 => "None", 1 => "String", 2 => "ExpandString", 3 => "Binary", 4 => "DWord", 5 => "DWordBigEndian", 6 => "Link",
        7 => "MultiString", 8 => "ResourceList", 9 => "FullResourceDescriptor", 10 => "ResourceRequirementsList", 11 => "QWord",
        _ => $"Type{type}",
    };

    public static RegistryValue Decode(uint type, byte[] data)
    {
        var kind = Kind(type);
        switch (type)
        {
            case 1 or 2 or 6:
                return new RegistryValue(kind, Utf16(data));
            case 7:
                return new RegistryValue(kind, Encoding.Unicode.GetString(data).Split('\0', StringSplitOptions.RemoveEmptyEntries));
            case 4 when data.Length >= 4:
                return new RegistryValue(kind, BitConverter.ToUInt32(data, 0));
            case 5 when data.Length >= 4:
                return new RegistryValue(kind, (uint)((data[0] << 24) | (data[1] << 16) | (data[2] << 8) | data[3]));
            case 11 when data.Length >= 8:
                return new RegistryValue(kind, BitConverter.ToUInt64(data, 0));
            case 8:
                return new RegistryValue(kind, data, CmResources.DecodeList(data));
            case 9:
                return new RegistryValue(kind, data, CmResources.DecodeFullDescriptor(data));
            default:
                return new RegistryValue(kind, data);
        }
    }

    static string Utf16(byte[] data)
    {
        var s = Encoding.Unicode.GetString(data, 0, data.Length & ~1);
        var end = s.IndexOf('\0');
        return end >= 0 ? s[..end] : s;
    }
}

/// Decodes configuration-manager property values (DEVPROP_TYPE_*).
static class DevProp
{
    const uint TypeMask = 0x0FFF, ModArray = 0x1000, ModList = 0x2000;

    public static (string Type, object? Value) Decode(uint type, byte[] data, int size)
    {
        var baseType = type & TypeMask;
        var name = Name(baseType);
        if ((type & ModList) != 0)
        {
            if (baseType is 0x12 or 0x19 or 0x14)
                return (name + "List", Encoding.Unicode.GetString(data, 0, size).Split('\0', StringSplitOptions.RemoveEmptyEntries));
            return (name + "List", data[..size]);
        }
        if ((type & ModArray) != 0)
        {
            if (baseType == 3) return ("Binary", data[..size]);
            var width = Width(baseType);
            if (width == 0) return (name + "Array", data[..size]);
            var items = new List<object?>();
            for (var i = 0; i + width <= size; i += width) items.Add(Scalar(baseType, data, i, width));
            return (name + "Array", items);
        }
        return baseType switch
        {
            0 or 1 => (name, null),
            0x12 or 0x19 or 0x14 => (name, Utf16(data, size)),
            0x13 => (name, data[..size]),
            _ when Width(baseType) is var w and > 0 && size >= w => (name, Scalar(baseType, data, 0, w)),
            _ => (name, data[..size]),
        };
    }

    static string Utf16(byte[] data, int size)
    {
        var s = Encoding.Unicode.GetString(data, 0, size & ~1);
        var end = s.IndexOf('\0');
        return end >= 0 ? s[..end] : s;
    }

    static int Width(uint t) => t switch
    {
        2 or 3 or 0x11 => 1, 4 or 5 => 2, 6 or 7 or 0xA or 0x16 or 0x17 or 0x18 => 4, 8 or 9 or 0xB or 0xE or 0xF or 0x10 => 8,
        0xC => 16, 0xD => 16, 0x15 => 20, _ => 0,
    };

    static object? Scalar(uint t, byte[] d, int o, int w) => t switch
    {
        2 => (sbyte)d[o],
        3 => d[o],
        0x11 => d[o] != 0,
        4 => BitConverter.ToInt16(d, o),
        5 => BitConverter.ToUInt16(d, o),
        6 => BitConverter.ToInt32(d, o),
        7 or 0x16 => BitConverter.ToUInt32(d, o),
        0x17 or 0x18 => "0x" + BitConverter.ToUInt32(d, o).ToString("x8"),
        8 => BitConverter.ToInt64(d, o),
        9 => BitConverter.ToUInt64(d, o),
        0xA => BitConverter.ToSingle(d, o),
        0xB => BitConverter.ToDouble(d, o),
        0xE => BitConverter.ToInt64(d, o) / 10000.0,
        0xF => DateTime.FromOADate(BitConverter.ToDouble(d, o)).ToString("yyyy-MM-ddTHH:mm:ss"),
        0x10 => FileTime(BitConverter.ToInt64(d, o)),
        0xD => new Guid(d.AsSpan(o, 16)).ToString(),
        0x15 => $"{{{new Guid(d.AsSpan(o, 16))}}}/{BitConverter.ToUInt32(d, o + 16)}",
        _ => Convert.ToHexString(d, o, w).ToLowerInvariant(),
    };

    static string? FileTime(long ft)
    {
        try { return ft == 0 ? null : DateTime.FromFileTimeUtc(ft).ToString("yyyy-MM-ddTHH:mm:ssZ"); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    static string Name(uint t) => t switch
    {
        0 => "Empty", 1 => "Null", 2 => "SByte", 3 => "Byte", 4 => "Int16", 5 => "UInt16", 6 => "Int32", 7 => "UInt32", 8 => "Int64",
        9 => "UInt64", 0xA => "Float", 0xB => "Double", 0xC => "Decimal", 0xD => "Guid", 0xE => "Currency", 0xF => "Date",
        0x10 => "FileTime", 0x11 => "Boolean", 0x12 => "String", 0x13 => "SecurityDescriptor", 0x14 => "SecurityDescriptorString",
        0x15 => "DevPropKey", 0x16 => "DevPropType", 0x17 => "Error", 0x18 => "NtStatus", 0x19 => "StringIndirect", _ => $"Type{t:x}",
    };

    /// The names devpkey.h gives the keys a device usually has.
    public static string KeyName(Guid fmtid, uint pid)
    {
        var f = fmtid.ToString();
        var name = f switch
        {
            "a45c254e-df1c-4efd-8020-67d146a850e0" => pid switch
            {
                2 => "DeviceDesc", 3 => "HardwareIds", 4 => "CompatibleIds", 6 => "Service", 9 => "Class", 10 => "ClassGuid", 11 => "Driver",
                12 => "ConfigFlags", 13 => "Manufacturer", 14 => "FriendlyName", 15 => "LocationInfo", 16 => "PDOName", 17 => "Capabilities",
                18 => "UINumber", 19 => "UpperFilters", 20 => "LowerFilters", 21 => "BusTypeGuid", 22 => "LegacyBusType", 23 => "BusNumber",
                24 => "EnumeratorName", 25 => "Security", 26 => "SecuritySDS", 27 => "DevType", 28 => "Exclusive", 29 => "Characteristics",
                30 => "Address", 31 => "UINumberDescFormat", 32 => "PowerData", 33 => "RemovalPolicy", 34 => "RemovalPolicyDefault",
                35 => "RemovalPolicyOverride", 36 => "InstallState", 37 => "LocationPaths", 38 => "BaseContainerId", _ => null,
            },
            "78c34fc8-104a-4aca-9ea4-524d52996e57" => pid switch
            {
                6 => "SessionId", 39 => "Model", 100 => "InstallDate", 101 => "FirstInstallDate", 102 => "LastArrivalDate",
                103 => "LastRemovalDate", 256 => "InstanceId", _ => null,
            },
            "4340a6c5-93fa-4706-972c-7b648008a5a7" => pid switch
            {
                2 => "DevNodeStatus", 3 => "ProblemCode", 4 => "EjectionRelations", 5 => "RemovalRelations", 6 => "PowerRelations",
                7 => "BusRelations", 8 => "Parent", 9 => "Children", 10 => "Siblings", 11 => "TransportRelations", 12 => "ProblemStatus", _ => null,
            },
            "8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c" => pid switch { 2 => "ContainerId", 4 => "InLocalMachineContainer", _ => null },
            "80d81ea6-7473-4b0c-8216-efc11a2c4c8b" => pid switch
            {
                2 => "ModelId", 3 => "FriendlyNameAttributes", 4 => "ManufacturerAttributes", 5 => "PresenceNotForDevice",
                6 => "SignalStrength", 7 => "IsAssociateableByAny", 8 => "ShowInUninstallUI", _ => null,
            },
            "540b947e-8b40-45bc-a8a2-6a0b894cbda2" => pid switch
            {
                1 => "Numa_Proximity_Domain", 2 => "DHP_Rebalance_Policy", 3 => "Numa_Node", 4 => "BusReportedDeviceDesc", 5 => "IsPresent",
                6 => "HasProblem", 7 => "ConfigurationId", 8 => "ReportedDeviceIdsHash", 9 => "PhysicalDeviceLocation", 10 => "BiosDeviceName",
                11 => "DriverProblemDesc", 12 => "DebuggerSafe", 13 => "PostInstallInProgress", 14 => "Stack", 15 => "ExtendedConfigurationIds",
                16 => "IsRebootRequired", 17 => "FirmwareDate", 18 => "FirmwareVersion", 19 => "FirmwareRevision", 20 => "DependencyProviders",
                21 => "DependencyDependents", 22 => "SoftRestartSupported", 23 => "ExtendedAddress", 24 => "AssignedToGuest",
                25 => "CreatorProcessId", 26 => "FirmwareVendor", _ => null,
            },
            "a8b865dd-2e3d-4094-ad97-e593a70c75d6" => pid switch
            {
                2 => "DriverDate", 3 => "DriverVersion", 4 => "DriverDesc", 5 => "DriverInfPath", 6 => "DriverInfSection",
                7 => "DriverInfSectionExt", 8 => "MatchingDeviceId", 9 => "DriverProvider", 10 => "DriverPropPageProvider",
                11 => "DriverCoInstallers", 12 => "ResourcePickerTags", 13 => "ResourcePickerExceptions", 14 => "DriverRank",
                15 => "DriverLogoLevel", _ => null,
            },
            "b725f130-47ef-101a-a5f1-02608c9eebac" => pid == 10 ? "Name" : null,
            "afd97640-86a3-4210-b67c-289c41aabe55" => pid switch { 2 => "SafeRemovalRequired", 3 => "SafeRemovalRequiredOverride", _ => null },
            _ => null,
        };
        return name is not null ? $"Device_{name}" : $"{{{f}}}/{pid}";
    }
}
