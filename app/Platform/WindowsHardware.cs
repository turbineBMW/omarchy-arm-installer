using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using OmarchyArmInstaller.Core;
using OmarchyArmInstaller.Core.Hardware;

namespace OmarchyArmInstaller.Platform;

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

        ct.ThrowIfCancellationRequested();
        status.Report("Reading each device's settings and its driver's");
        devices = devices.Select(d => d with
        {
            EnumKey = WindowsRegistry.Dump($@"SYSTEM\CurrentControlSet\Enum\{d.InstanceId}", PropertyStore),
            DriverKey = d.Driver is null ? null : WindowsRegistry.Dump($@"SYSTEM\CurrentControlSet\Control\Class\{d.Driver}", PropertyStore),
        }).ToList();
        var services = new Dictionary<string, RegistryDump>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in devices.Select(d => d.Service).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            if (WindowsRegistry.Dump($@"SYSTEM\CurrentControlSet\Services\{name}", ServicePrivate) is { } dump) services[name!] = dump;
        }
        Log.Info($"Registry: {devices.Count(d => d.EnumKey is not null)} device keys, {devices.Count(d => d.DriverKey is not null)} driver keys, {services.Count} services");

        ct.ThrowIfCancellationRequested();
        status.Report("Reading the registry's hardware description");
        var registry = new Dictionary<string, RegistryDump>();
        foreach (var path in SystemKeys)
        {
            ct.ThrowIfCancellationRequested();
            if (WindowsRegistry.Dump(path) is { } dump) registry[path] = dump;
            else Log.Info($@"Registry: HKLM\{path} not read");
        }

        ct.ThrowIfCancellationRequested();
        status.Report("Asking Windows about storage, power, displays and drivers");
        string? windows = null;
        try
        {
            windows = WindowsPlatform.PowerShell(WindowsFacts, "Windows' view of the hardware");
            using var _ = JsonDocument.Parse(windows);
        }
        catch (Exception e)
        {
            Log.Error("asking Windows about the hardware", e);
            problems.Add($"Windows' view of storage, power and drivers: {e.Message}");
            windows = null;
        }

        return new CollectedHardware
        {
            Smbios = smbios,
            SmbiosVersion = version,
            Acpi = acpi,
            Displays = displays,
            Devices = devices,
            Services = services,
            Registry = registry,
            WindowsJson = windows,
            Secrets = new[] { Environment.MachineName, Environment.UserName, Environment.UserDomainName }
                .Where(n => n.Length >= 3).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            Os = OsDescription(),
            Problems = problems,
        };
    }, ct);

    /// The property store under a device or driver key repeats what the
    /// configuration manager already gave, as binary.
    static readonly HashSet<string> PropertyStore = new(StringComparer.OrdinalIgnoreCase) { "Properties" };
    /// A service's security descriptor and its device list say nothing about the hardware.
    static readonly HashSet<string> ServicePrivate = new(StringComparer.OrdinalIgnoreCase) { "Security", "Enum" };

    /// System-wide keys that describe the hardware: the processors, firmware
    /// and the resources drivers got; where ports and such map to devices;
    /// what Windows computed as the machine's CHIDs; Secure Boot's state.
    static readonly string[] SystemKeys =
    [
        @"HARDWARE\DESCRIPTION\System",
        @"HARDWARE\DEVICEMAP",
        @"HARDWARE\RESOURCEMAP",
        @"SYSTEM\CurrentControlSet\Control\SystemInformation",
        @"SYSTEM\CurrentControlSet\Control\SecureBoot\State",
    ];

    /// What Windows' own tools know that the firmware tables and the device
    /// list do not say: the storage bus, the battery, the power states the
    /// firmware offers, the GPU and displays as Windows drives them, and the
    /// driver packages in the store. Each part fails on its own.
    const string WindowsFacts = """
        function Try($block) { try { @(& $block) } catch { @{ error = $_.Exception.Message } } }
        function Day($d) { if ($d) { ([DateTime]$d).ToString('yyyy-MM-dd') } }
        $r = [ordered]@{}
        $r.computerSystem = Try { Get-CimInstance Win32_ComputerSystem | Select-Object Manufacturer, Model, SystemFamily, SystemSKUNumber, SystemType, PCSystemType, PCSystemTypeEx, TotalPhysicalMemory, NumberOfProcessors, NumberOfLogicalProcessors, HypervisorPresent, PowerSupplyState, ThermalState }
        $r.bios = Try { Get-CimInstance Win32_BIOS | Select-Object Manufacturer, Name, Version, SMBIOSBIOSVersion, SMBIOSMajorVersion, SMBIOSMinorVersion, @{n='ReleaseDate';e={Day $_.ReleaseDate}}, BIOSVersion, EmbeddedControllerMajorVersion, EmbeddedControllerMinorVersion, SystemBiosMajorVersion, SystemBiosMinorVersion }
        $r.processors = Try { Get-CimInstance Win32_Processor | Select-Object DeviceID, Name, Manufacturer, Description, Caption, Architecture, Family, Level, Revision, Stepping, ProcessorType, NumberOfCores, NumberOfEnabledCore, NumberOfLogicalProcessors, ThreadCount, MaxClockSpeed, CurrentClockSpeed, ExtClock, L2CacheSize, L3CacheSize, DataWidth, AddressWidth, Characteristics, VirtualizationFirmwareEnabled, SecondLevelAddressTranslationExtensions }
        $r.firmwareType = Try { (Get-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Control' -Name PEFirmwareType).PEFirmwareType }
        $r.secureBoot = Try { Confirm-SecureBootUEFI }
        $r.physicalDisks = Try { Get-PhysicalDisk | Select-Object DeviceId, FriendlyName, Manufacturer, Model, BusType, MediaType, Size, AllocatedSize, FirmwareVersion, PhysicalSectorSize, LogicalSectorSize, SpindleSpeed, HealthStatus, OperationalStatus, Usage }
        $r.disks = Try { Get-Disk | Select-Object Number, FriendlyName, Manufacturer, Model, BusType, PartitionStyle, Size, AllocatedSize, LogicalSectorSize, PhysicalSectorSize, NumberOfPartitions, IsBoot, IsSystem, BootFromDisk, FirmwareVersion, HealthStatus, OperationalStatus }
        $r.battery = Try { Get-CimInstance Win32_Battery | Select-Object Name, Description, Chemistry, DesignVoltage, DesignCapacity, FullChargeCapacity, BatteryStatus, EstimatedChargeRemaining, Availability, PowerManagementSupported }
        $r.batteryStatic = Try { Get-CimInstance -Namespace root/wmi -ClassName BatteryStaticData | Select-Object DeviceName, ManufactureName, ManufactureDate, Chemistry, DesignedCapacity, DefaultAlert1, DefaultAlert2, CriticalBias, Capabilities, Granularity1, Granularity2 }
        $r.batteryFullChargedCapacity = Try { Get-CimInstance -Namespace root/wmi -ClassName BatteryFullChargedCapacity | Select-Object FullChargedCapacity }
        $r.batteryCycleCount = Try { Get-CimInstance -Namespace root/wmi -ClassName BatteryCycleCount | Select-Object CycleCount }
        $r.video = Try { Get-CimInstance Win32_VideoController | Select-Object Name, VideoProcessor, AdapterCompatibility, AdapterRAM, AdapterDACType, DriverVersion, @{n='DriverDate';e={Day $_.DriverDate}}, VideoModeDescription, CurrentHorizontalResolution, CurrentVerticalResolution, CurrentRefreshRate, MinRefreshRate, MaxRefreshRate, CurrentBitsPerPixel, VideoArchitecture, VideoMemoryType, PNPDeviceID, Status }
        $r.monitorConnections = Try { Get-CimInstance -Namespace root/wmi -ClassName WmiMonitorConnectionParams | Select-Object InstanceName, VideoOutputTechnology, Active }
        $r.monitorParams = Try { Get-CimInstance -Namespace root/wmi -ClassName WmiMonitorBasicDisplayParams | Select-Object InstanceName, MaxHorizontalImageSize, MaxVerticalImageSize, VideoInputType, Active }
        $r.monitorModes = Try { Get-CimInstance -Namespace root/wmi -ClassName WmiMonitorListedSupportedSourceModes | ForEach-Object { [pscustomobject]@{ InstanceName = $_.InstanceName; PreferredSourceModeIndex = $_.PreferredSourceModeIndex; Modes = @($_.MonitorSourceModes | ForEach-Object { [pscustomobject]@{ HorizontalActivePixels = $_.HorizontalActivePixels; VerticalActivePixels = $_.VerticalActivePixels; VerticalRefreshRateNumerator = $_.VerticalRefreshRateNumerator; VerticalRefreshRateDenominator = $_.VerticalRefreshRateDenominator; HorizontalRefreshRateNumerator = $_.HorizontalRefreshRateNumerator; HorizontalRefreshRateDenominator = $_.HorizontalRefreshRateDenominator; PixelClockRate = $_.PixelClockRate; VideoStandardType = $_.VideoStandardType; Origin = $_.Origin } }) } } }
        $r.sound = Try { Get-CimInstance Win32_SoundDevice | Select-Object Name, Manufacturer, ProductName, PNPDeviceID, Status, StatusInfo }
        $r.usbControllers = Try { Get-CimInstance Win32_USBController | Select-Object Name, Manufacturer, PNPDeviceID, ProtocolSupported, Status }
        $r.networkAdapters = Try { Get-NetAdapter | Select-Object Name, InterfaceDescription, PnPDeviceID, DriverDescription, DriverVersionString, DriverProvider, DriverFileName, @{n='DriverDate';e={Day $_.DriverDate}}, LinkSpeed, MediaType, PhysicalMediaType, MediaConnectionState, Status, Virtual, Hidden, NdisVersion }
        $r.thermalZones = Try { Get-CimInstance -Namespace root/wmi -ClassName MSAcpi_ThermalZoneTemperature | Select-Object InstanceName, Active, CurrentTemperature, CriticalTripPoint, PassiveTripPoint, ActiveTripPoint, ActiveTripPointCount, SamplingPeriod, ThermalConstant1, ThermalConstant2, Reserved }
        $r.powerCapabilities = Try { @(& powercfg.exe /a 2>&1 | ForEach-Object { "$_".TrimEnd() }) }
        $r.powerSchemes = Try { @(& powercfg.exe /list 2>&1 | ForEach-Object { "$_".TrimEnd() }) }
        $r.tpm = Try { Get-Tpm | Select-Object TpmPresent, TpmReady, TpmEnabled, TpmActivated, ManufacturerId, ManufacturerIdTxt, ManufacturerVersion, ManufacturerVersionFull20, ManagedAuthLevel }
        $r.os = Try { Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion' | Select-Object ProductName, EditionID, InstallationType, DisplayVersion, ReleaseId, CurrentBuild, UBR, BuildLabEx, CurrentMajorVersionNumber, CurrentMinorVersionNumber, BuildBranch }
        $r.driverStore = Try { Get-WindowsDriver -Online | Select-Object Driver, OriginalFileName, ProviderName, ClassName, ClassGuid, ClassDescription, Version, @{n='Date';e={Day $_.Date}}, BootCritical, Inbox, DriverSignature }
        ConvertTo-Json -InputObject $r -Depth 6 -Compress
        """;

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
        public Guid Fmtid => fmtid;
        public uint Pid => pid;
    }

    static readonly Guid DeviceGuid = new("a45c254e-df1c-4efd-8020-67d146a850e0");
    static readonly Guid DevNodeGuid = new("4340a6c5-93fa-4706-972c-7b648008a5a7");
    static readonly Guid DriverGuid = new("a8b865dd-2e3d-4094-ad97-e593a70c75d6");

    static readonly DevPropKey Name = new(new Guid("b725f130-47ef-101a-a5f1-02608c9eebac"), 10);
    static readonly DevPropKey HardwareIds = new(DeviceGuid, 3);
    static readonly DevPropKey CompatibleIds = new(DeviceGuid, 4);
    static readonly DevPropKey Service = new(DeviceGuid, 6);
    static readonly DevPropKey Class = new(DeviceGuid, 9);
    static readonly DevPropKey Driver = new(DeviceGuid, 11);
    static readonly DevPropKey Security = new(DeviceGuid, 25);
    static readonly DevPropKey SecuritySds = new(DeviceGuid, 26);
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

    [DllImport("cfgmgr32.dll")]
    static extern uint CM_Get_DevNode_Property_Keys(uint devInst, [Out] DevPropKey[]? keys, ref uint count, uint flags);

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
                Driver = Get(dev, Driver) as string,
                Properties = AllProperties(dev),
            });
        }
        return result;
    }

    /// Every property the device has, decoded; security descriptors left out.
    static List<DeviceProperty> AllProperties(uint dev)
    {
        var result = new List<DeviceProperty>();
        uint count = 0;
        if (CM_Get_DevNode_Property_Keys(dev, null, ref count, 0) != CR_BUFFER_SMALL || count == 0) return result;
        var keys = new DevPropKey[count];
        if (CM_Get_DevNode_Property_Keys(dev, keys, ref count, 0) != CR_SUCCESS) return result;
        foreach (var key in keys.Take((int)count))
        {
            if (key.Equals(Security) || key.Equals(SecuritySds)) continue;
            uint size = 0;
            if (CM_Get_DevNode_PropertyW(dev, key, out _, null, ref size, 0) != CR_BUFFER_SMALL) continue;
            var data = new byte[size];
            if (CM_Get_DevNode_PropertyW(dev, key, out var type, data, ref size, 0) != CR_SUCCESS) continue;
            var (typeName, value) = DevProp.Decode(type, data, (int)size);
            result.Add(new DeviceProperty(DevProp.KeyName(key.Fmtid, key.Pid), typeName, value));
        }
        return result.OrderBy(p => p.Key, StringComparer.Ordinal).ToList();
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
