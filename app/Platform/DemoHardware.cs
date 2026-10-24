using SurfaceUsbCreator.Core.Hardware;

namespace SurfaceUsbCreator.Platform;

/// A hardware report's raw material read from Linux, for developing the
/// report away from Windows: the SMBIOS table (root-only in sysfs, so
/// SURFACE_CREATOR_DMI can point at a copy), ACPI tables where the machine
/// booted with them, EDIDs from DRM, and stand-in devices for the driver
/// store SURFACE_CREATOR_DRIVER_STORE points at.
public sealed class DemoHardware : IHardwareSource
{
    public Task<CollectedHardware> CollectAsync(IProgress<string> status, CancellationToken ct) => Task.Run(async () =>
    {
        var problems = new List<string>();

        status.Report("Reading the SMBIOS table");
        await Task.Delay(300, ct);
        var dmi = Environment.GetEnvironmentVariable("SURFACE_CREATOR_DMI") ?? "/sys/firmware/dmi/tables/DMI";
        byte[]? smbios = null;
        (byte, byte, byte) version = (3, 0, 0);
        try
        {
            smbios = File.ReadAllBytes(dmi);
            var entry = Path.Combine(Path.GetDirectoryName(dmi)!, "smbios_entry_point");
            if (File.Exists(entry) && File.ReadAllBytes(entry) is { Length: >= 10 } e && e.AsSpan(0, 5).SequenceEqual("_SM3_"u8))
                version = (e[7], e[8], e[9]);
        }
        catch (Exception e)
        {
            problems.Add($"SMBIOS table: {e.Message}");
        }

        status.Report("Reading the ACPI tables");
        await Task.Delay(300, ct);
        var acpi = new List<AcpiTable>();
        var acpiDir = Environment.GetEnvironmentVariable("SURFACE_CREATOR_ACPI") ?? "/sys/firmware/acpi/tables";
        if (Directory.Exists(acpiDir))
        {
            foreach (var file in Directory.EnumerateFiles(acpiDir, "*", SearchOption.AllDirectories).Order())
            {
                try
                {
                    var data = File.ReadAllBytes(file);
                    if (data.Length >= 36) acpi.Add(new AcpiTable(System.Text.Encoding.ASCII.GetString(data, 0, 4), data));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                }
            }
        }

        status.Report("Listing devices and their drivers");
        await Task.Delay(300, ct);
        var devices = StandIns();

        status.Report("Reading the displays' EDID");
        var displays = new List<DisplayEdid>();
        if (Directory.Exists("/sys/class/drm"))
        {
            foreach (var connector in Directory.EnumerateDirectories("/sys/class/drm", "card*-*"))
            {
                var path = Path.Combine(connector, "edid");
                if (!File.Exists(path)) continue;
                var edid = File.ReadAllBytes(path);
                if (!Edid.IsValid(edid)) continue;
                var name = Path.GetFileName(connector);
                displays.Add(new DisplayEdid(name, edid, name.Contains("eDP") || name.Contains("DSI") || name.Contains("LVDS")));
            }
        }

        return new CollectedHardware
        {
            Smbios = smbios,
            SmbiosVersion = version,
            Acpi = acpi,
            Displays = displays,
            Devices = devices,
            Os = $"Demo on {System.Runtime.InteropServices.RuntimeInformation.OSDescription}",
            Problems = problems,
        };
    }, ct);

    /// A few devices shaped like Windows' own, including ones the report must
    /// leave out or redact, and one per Qualcomm package in the driver store.
    static List<DeviceInfo> StandIns()
    {
        var list = new List<DeviceInfo>
        {
            new() { InstanceId = @"ACPI\QCOM0C1B\2&DABA3FF&0", Name = "Qualcomm(R) Adreno(TM) X1-85 GPU", Class = "Display",
                    HardwareIds = [@"ACPI\VEN_QCOM&DEV_0C1B", @"ACPI\QCOM0C1B"], BiosName = @"\_SB_.GPU0", Service = "qcdx" },
            new() { InstanceId = @"ACPI\MSHW0486\1", Name = "Surface Audio", Class = "MEDIA", BiosName = @"\_SB_.AUDD", Problem = 28 },
            new() { InstanceId = @"USB\VID_045E&PID_0C1E\0123ABCD9876", Name = "Surface Camera Front", Class = "Camera",
                    HardwareIds = [@"USB\VID_045E&PID_0C1E&REV_0100"], Parent = @"USB\ROOT_HUB30\4&1A2B3C&0&0" },
            new() { InstanceId = @"BTHENUM\{0000110B-0000-1000-8000-00805F9B34FB}_VID&0002004C_PID&2014\7&2A3B&0&A4C3F0D1E2B3_C00000000",
                    Name = "Someone's AirPods" },
            new() { InstanceId = @"SWD\MMDEVAPI\{0.0.0.00000000}.{8D1C5E3B-0000-0000-0000-000000000000}", Name = "Headphones (Someone's AirPods)" },
            new() { InstanceId = @"PCI\VEN_17CB&DEV_1107&SUBSYS_00000000&REV_01\4&3B5C6D7E&0&0010", Name = "Qualcomm FastConnect 7800 Wi-Fi 7",
                    Class = "Net", LocationPaths = [@"PCIROOT(0)#PCI(0000)#PCI(0000)", @"ACPI(_SB_)#ACPI(PCI4)#ACPI(RP1_)#ACPI(WLAN)"] },
        };
        var store = Environment.GetEnvironmentVariable("SURFACE_CREATOR_DRIVER_STORE");
        if (store is not null && Directory.Exists(store))
        {
            foreach (var folder in Directory.EnumerateDirectories(store, "qc*").Order().Take(40))
            {
                var inf = Path.GetFileName(folder).Split('_')[0];
                list.Add(new DeviceInfo
                {
                    InstanceId = $@"ACPI\{inf.ToUpperInvariant()}\0", Name = inf, Inf = inf, DriverProvider = "Qualcomm",
                    HardwareIds = [$@"ACPI\VEN_QCOM&DEV_{inf.ToUpperInvariant()}"], DriverFolder = folder,
                });
            }
        }
        return list;
    }
}
