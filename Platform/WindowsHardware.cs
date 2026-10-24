using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using SurfaceUsbCreator.Core;
using SurfaceUsbCreator.Core.Hardware;

namespace SurfaceUsbCreator.Platform;

/// Reads a hardware report's raw material from Windows: SMBIOS and ACPI
/// through the firmware-table API (plus the registry, which keeps every SSDT
/// where the API returns only the first), devices and their drivers through
/// the configuration manager, and EDIDs from the display devices' registry keys.
[SupportedOSPlatform("windows")]
public sealed class WindowsHardware : IHardwareSource
{
    public Task<CollectedHardware> CollectAsync(IProgress<string> status, CancellationToken ct) => Task.Run(() =>
    {
        var problems = new List<string>();

        status.Report("Reading the SMBIOS table");
        byte[]? smbios = null;
        (byte, byte, byte) version = default;
        try
        {
            // RawSMBIOSData: calling method, major, minor, DMI revision, length, table.
            var raw = FirmwareTables.Get(FirmwareTables.Rsmb, 0);
            version = (raw[1], raw[2], raw[3]);
            var length = BitConverter.ToInt32(raw, 4);
            smbios = raw.AsSpan(8, Math.Min(length, raw.Length - 8)).ToArray();
            Log.Info($"SMBIOS {raw[1]}.{raw[2]}, {smbios.Length} bytes");
        }
        catch (Exception e)
        {
            Log.Error("reading SMBIOS", e);
            problems.Add($"SMBIOS table: {e.Message}");
        }

        ct.ThrowIfCancellationRequested();
        status.Report("Reading the ACPI tables");
        var acpi = ReadAcpi(problems);
        Log.Info($"ACPI: {string.Join(", ", acpi.Select(t => $"{t.Signature} ({t.Data.Length})"))}");

        ct.ThrowIfCancellationRequested();
        status.Report("Listing devices and their drivers");
        var devices = new List<DeviceInfo>();
        try
        {
            devices = DeviceList.Present();
            Log.Info($"Devices: {devices.Count} present, {devices.Count(d => d.Problem is > 0)} with a problem code");
        }
        catch (Exception e)
        {
            Log.Error("listing devices", e);
            problems.Add($"Device list: {e.Message}");
        }

        ct.ThrowIfCancellationRequested();
        status.Report("Reading the displays' EDID");
        var displays = ReadDisplays(devices, problems);

        return new CollectedHardware
        {
            Smbios = smbios,
            SmbiosVersion = version,
            Acpi = acpi,
            Displays = displays,
            Devices = devices,
            Os = OsDescription(),
            Problems = problems,
        };
    }, ct);

    static List<AcpiTable> ReadAcpi(List<string> problems)
    {
        var tables = new List<AcpiTable>();
        var seen = new HashSet<string>();
        void Add(byte[] data, string source)
        {
            if (data.Length < 36 || BitConverter.ToUInt32(data, 4) != data.Length)
            {
                Log.Info($"ACPI: ignoring {data.Length} bytes from {source} (not a whole table)");
                return;
            }
            if (!seen.Add(Convert.ToHexString(SHA256.HashData(data)))) return;
            tables.Add(new AcpiTable(Encoding.ASCII.GetString(data, 0, 4), data));
        }

        try
        {
            // EnumSystemFirmwareTables leaves out the DSDT and XSDT.
            var ids = FirmwareTables.List(FirmwareTables.Acpi).ToList();
            foreach (var extra in new[] { "DSDT", "XSDT" }.Select(s => BitConverter.ToUInt32(Encoding.ASCII.GetBytes(s))))
                if (!ids.Contains(extra)) ids.Add(extra);
            foreach (var id in ids.Distinct())
            {
                var signature = Encoding.ASCII.GetString(BitConverter.GetBytes(id));
                try
                {
                    Add(FirmwareTables.Get(FirmwareTables.Acpi, id), signature);
                }
                catch (Exception e)
                {
                    Log.Info($"ACPI: {signature}: {e.Message}");
                    if (signature is "DSDT" or "FACP") problems.Add($"ACPI table {signature}: {e.Message}");
                }
            }
        }
        catch (Exception e)
        {
            Log.Error("listing ACPI tables", e);
            problems.Add($"ACPI tables: {e.Message}");
        }

        // Every SSDT, and anything the API missed, from HKLM\HARDWARE\ACPI.
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(@"HARDWARE\ACPI");
            if (root is not null)
                foreach (var name in root.GetSubKeyNames())
                    using (var key = root.OpenSubKey(name))
                        if (key is not null) WalkBinaries(key, $@"HARDWARE\ACPI\{name}", Add, 0);
        }
        catch (Exception e)
        {
            Log.Error("reading ACPI tables from the registry", e);
        }

        return tables.OrderBy(t => t.Signature switch { "FACP" => 0, "DSDT" => 1, "SSDT" => 2, _ => 3 })
            .ThenBy(t => t.Signature, StringComparer.Ordinal).ToList();
    }

    static void WalkBinaries(RegistryKey key, string path, Action<byte[], string> add, int depth)
    {
        if (depth > 6) return;
        foreach (var value in key.GetValueNames())
            if (key.GetValueKind(value) == RegistryValueKind.Binary && key.GetValue(value) is byte[] data)
                add(data, $@"{path}\{value}");
        foreach (var name in key.GetSubKeyNames())
            using (var sub = key.OpenSubKey(name))
                if (sub is not null) WalkBinaries(sub, $@"{path}\{name}", add, depth + 1);
    }

    static List<DisplayEdid> ReadDisplays(List<DeviceInfo> devices, List<string> problems)
    {
        var internalIds = InternalDisplays();
        var result = new List<DisplayEdid>();
        foreach (var d in devices.Where(d => d.InstanceId.StartsWith(@"DISPLAY\", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{d.InstanceId}\Device Parameters");
                if (key?.GetValue("EDID") is byte[] edid && Edid.IsValid(edid))
                    result.Add(new DisplayEdid(d.InstanceId, edid, internalIds.Contains(d.InstanceId)));
            }
            catch (Exception e)
            {
                Log.Info($"EDID of {d.InstanceId}: {e.Message}");
            }
        }
        // A laptop with one display and nothing plugged in: that one is the panel.
        if (internalIds.Count == 0 && result.Count == 1) result[0] = result[0] with { Internal = true };
        if (result.Count == 0) problems.Add("No display EDID found.");
        Log.Info($"Displays: {string.Join(", ", result.Select(r => $"{Edid.PanelId(r.Edid)}{(r.Internal ? " (internal)" : "")}"))}");
        return result;
    }

    /// The instance IDs of built-in panels, from WMI's connection types:
    /// internal, embedded DisplayPort, embedded UDI or LVDS.
    static HashSet<string> InternalDisplays()
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var json = WindowsPlatform.PowerShell("""
                ConvertTo-Json -InputObject @(Get-CimInstance -Namespace root/wmi -ClassName WmiMonitorConnectionParams |
                  Select-Object InstanceName, VideoOutputTechnology)
                """, "Finding the built-in display");
            using var doc = JsonDocument.Parse(json);
            foreach (var m in doc.RootElement.EnumerateArray())
            {
                var tech = m.GetProperty("VideoOutputTechnology").GetInt64() & 0xFFFFFFFF;
                if (tech is 0x80000000 or 11 or 13 or 6 && m.GetProperty("InstanceName").GetString() is { } name)
                    ids.Add(System.Text.RegularExpressions.Regex.Replace(name, @"_\d+$", ""));
            }
        }
        catch (Exception e)
        {
            Log.Error("finding the built-in display", e);
        }
        return ids;
    }

    static string OsDescription()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var build = key?.GetValue("CurrentBuild") as string ?? "";
            var ubr = key?.GetValue("UBR") is int u ? $".{u}" : "";
            var display = key?.GetValue("DisplayVersion") as string;
            var name = int.TryParse(build, out var b) && b >= 22000 ? "Windows 11" : "Windows 10";
            var edition = key?.GetValue("EditionID") as string;
            return $"{name} {edition} {display} (build {build}{ubr}, {RuntimeInformation.OSArchitecture})".Replace("  ", " ");
        }
        catch
        {
            return RuntimeInformation.OSDescription;
        }
    }
}

[SupportedOSPlatform("windows")]
static class FirmwareTables
{
    public const uint Acpi = 0x41435049; // 'ACPI'
    public const uint Rsmb = 0x52534D42; // 'RSMB'

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern uint EnumSystemFirmwareTables(uint provider, byte[]? buffer, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern uint GetSystemFirmwareTable(uint provider, uint id, byte[]? buffer, uint size);

    public static IEnumerable<uint> List(uint provider)
    {
        var size = EnumSystemFirmwareTables(provider, null, 0);
        if (size == 0) throw new Win32Exception();
        var buffer = new byte[size];
        size = EnumSystemFirmwareTables(provider, buffer, size);
        for (var i = 0; i + 4 <= size; i += 4) yield return BitConverter.ToUInt32(buffer, i);
    }

    public static byte[] Get(uint provider, uint id)
    {
        var size = GetSystemFirmwareTable(provider, id, null, 0);
        if (size == 0) throw new Win32Exception();
        var buffer = new byte[size];
        var read = GetSystemFirmwareTable(provider, id, buffer, size);
        if (read == 0) throw new Win32Exception();
        if (read > size) throw new InvalidOperationException("the table grew while it was being read");
        return read == size ? buffer : buffer[..(int)read];
    }
}

/// Present devices and their properties, through cfgmgr32.
[SupportedOSPlatform("windows")]
static class DeviceList
{
    [StructLayout(LayoutKind.Sequential)]
    readonly struct DevPropKey(Guid fmtid, uint pid)
    {
        readonly Guid fmtid = fmtid;
        readonly uint pid = pid;
    }

    static readonly Guid DeviceGuid = new("a45c254e-df1c-4efd-8020-67d146a850e0");
    static readonly Guid DevNodeGuid = new("4340a6c5-93fa-4706-972c-7b648008a5a7");
    static readonly Guid DriverGuid = new("a8b865dd-2e3d-4094-ad97-e593a70c75d6");

    static readonly DevPropKey Name = new(new Guid("b725f130-47ef-101a-a5f1-02608c9eebac"), 10);
    static readonly DevPropKey HardwareIds = new(DeviceGuid, 3);
    static readonly DevPropKey CompatibleIds = new(DeviceGuid, 4);
    static readonly DevPropKey Service = new(DeviceGuid, 6);
    static readonly DevPropKey Class = new(DeviceGuid, 9);
    static readonly DevPropKey Manufacturer = new(DeviceGuid, 13);
    static readonly DevPropKey LocationPaths = new(DeviceGuid, 37);
    static readonly DevPropKey BiosDeviceName = new(new Guid("540b947e-8b40-45bc-a8a2-6a0b894cbda2"), 10);
    static readonly DevPropKey ProblemCode = new(DevNodeGuid, 3);
    static readonly DevPropKey Parent = new(DevNodeGuid, 8);
    static readonly DevPropKey DriverDate = new(DriverGuid, 2);
    static readonly DevPropKey DriverVersion = new(DriverGuid, 3);
    static readonly DevPropKey DriverInfPath = new(DriverGuid, 5);
    static readonly DevPropKey DriverInfSection = new(DriverGuid, 6);
    static readonly DevPropKey DriverProvider = new(DriverGuid, 9);

    const uint CM_GETIDLIST_FILTER_PRESENT = 0x100;
    const uint CR_SUCCESS = 0, CR_BUFFER_SMALL = 0x1A;
    const uint DEVPROP_TYPE_UINT32 = 0x07, DEVPROP_TYPE_FILETIME = 0x10, DEVPROP_TYPE_STRING = 0x12, DEVPROP_TYPE_STRING_LIST = 0x2012;

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern uint CM_Get_Device_ID_List_SizeW(out uint length, string? filter, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern uint CM_Get_Device_ID_ListW(string? filter, char[] buffer, uint length, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern uint CM_Locate_DevNodeW(out uint devInst, string id, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    static extern uint CM_Get_DevNode_PropertyW(uint devInst, in DevPropKey key, out uint type, byte[]? buffer, ref uint size, uint flags);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool SetupGetInfDriverStoreLocationW(string fileName, IntPtr platform, string? locale, char[]? buffer, uint size,
        out uint required);

    public static List<DeviceInfo> Present()
    {
        char[] buffer;
        uint cr;
        do
        {
            cr = CM_Get_Device_ID_List_SizeW(out var length, null, CM_GETIDLIST_FILTER_PRESENT);
            if (cr != CR_SUCCESS) throw new InvalidOperationException($"CM_Get_Device_ID_List_Size returned {cr:x}");
            buffer = new char[length];
            cr = CM_Get_Device_ID_ListW(null, buffer, length, CM_GETIDLIST_FILTER_PRESENT);
        } while (cr == CR_BUFFER_SMALL); // a device arrived in between
        if (cr != CR_SUCCESS) throw new InvalidOperationException($"CM_Get_Device_ID_List returned {cr:x}");

        var folders = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var result = new List<DeviceInfo>();
        foreach (var id in new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            if (CM_Locate_DevNodeW(out var dev, id, 0) != CR_SUCCESS) continue;
            var inf = Get(dev, DriverInfPath) as string;
            result.Add(new DeviceInfo
            {
                InstanceId = id,
                Name = Get(dev, Name) as string,
                Class = Get(dev, Class) as string,
                Manufacturer = Get(dev, Manufacturer) as string,
                HardwareIds = Get(dev, HardwareIds) as string[] ?? [],
                CompatibleIds = Get(dev, CompatibleIds) as string[] ?? [],
                Service = Get(dev, Service) as string,
                Parent = Get(dev, Parent) as string,
                BiosName = Get(dev, BiosDeviceName) as string,
                LocationPaths = Get(dev, LocationPaths) as string[] ?? [],
                Problem = Get(dev, ProblemCode) as uint?,
                Inf = inf,
                InfSection = Get(dev, DriverInfSection) as string,
                DriverProvider = Get(dev, DriverProvider) as string,
                DriverVersion = Get(dev, DriverVersion) as string,
                DriverDate = Get(dev, DriverDate) as string,
                DriverFolder = inf is null ? null : folders.TryGetValue(inf, out var f) ? f : folders[inf] = StoreFolder(inf),
            });
        }
        return result;
    }

    static object? Get(uint dev, DevPropKey key)
    {
        uint size = 0;
        if (CM_Get_DevNode_PropertyW(dev, key, out _, null, ref size, 0) != CR_BUFFER_SMALL) return null;
        var data = new byte[size];
        if (CM_Get_DevNode_PropertyW(dev, key, out var type, data, ref size, 0) != CR_SUCCESS) return null;
        return type switch
        {
            DEVPROP_TYPE_STRING => Encoding.Unicode.GetString(data, 0, (int)size).TrimEnd('\0'),
            DEVPROP_TYPE_STRING_LIST => Encoding.Unicode.GetString(data, 0, (int)size).Split('\0', StringSplitOptions.RemoveEmptyEntries),
            DEVPROP_TYPE_UINT32 when size >= 4 => BitConverter.ToUInt32(data, 0),
            DEVPROP_TYPE_FILETIME when size >= 8 => DateTime.FromFileTimeUtc(BitConverter.ToInt64(data, 0)).ToString("yyyy-MM-dd"),
            _ => null,
        };
    }

    /// The driver store folder an installed INF (oem12.inf) came from.
    static string? StoreFolder(string inf)
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF", inf);
        SetupGetInfDriverStoreLocationW(path, IntPtr.Zero, null, null, 0, out var required);
        if (required == 0) return null;
        var buffer = new char[required];
        if (!SetupGetInfDriverStoreLocationW(path, IntPtr.Zero, null, buffer, required, out _)) return null;
        var stored = new string(buffer).TrimEnd('\0');
        return Path.GetDirectoryName(stored);
    }
}
