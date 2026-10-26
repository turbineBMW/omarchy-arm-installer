using System.Text.Json.Serialization;

namespace OmarchyArmInstaller.Core.Hardware;

/// One ACPI table as the firmware handed it to the OS.
public sealed record AcpiTable(string Signature, byte[] Data);

/// A display's EDID; Internal is the laptop's own panel.
public sealed record DisplayEdid(string Device, byte[] Edid, bool Internal);

/// One present device, as Windows' device manager knows it. BiosName is its
/// ACPI namespace path (\_SB.I2C1.TPL0), which maps it to the DSDT.
public sealed record DeviceInfo
{
    public required string InstanceId { get; init; }
    public string? Name { get; init; }
    public string? Class { get; init; }
    public string? Manufacturer { get; init; }
    public string[] HardwareIds { get; init; } = [];
    public string[] CompatibleIds { get; init; } = [];
    public string? Service { get; init; }
    public string? Parent { get; init; }
    public string? BiosName { get; init; }
    public string[] LocationPaths { get; init; } = [];
    public uint? Problem { get; init; }
    public string? Inf { get; init; }
    public string? InfSection { get; init; }
    public string? DriverProvider { get; init; }
    public string? DriverVersion { get; init; }
    public string? DriverDate { get; init; }
    /// The driver package's folder in the driver store, when it has one.
    public string? DriverFolder { get; init; }
    /// The driver's key under Control\Class ({class guid}\0000), where the
    /// driver keeps its settings.
    public string? Driver { get; init; }
    /// Every property the configuration manager has for the device.
    public IReadOnlyList<DeviceProperty> Properties { get; init; } = [];
    /// The device's own key under Enum, with Device Parameters and LogConf.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RegistryDump? EnumKey { get; init; }
    /// The driver's settings key under Control\Class.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RegistryDump? DriverKey { get; init; }
}

/// Everything read from the machine, before redaction.
public sealed record CollectedHardware
{
    /// The SMBIOS structure table, and the SMBIOS version it follows.
    public byte[]? Smbios { get; init; }
    public (byte Major, byte Minor, byte Revision) SmbiosVersion { get; init; }
    public IReadOnlyList<AcpiTable> Acpi { get; init; } = [];
    public IReadOnlyList<DisplayEdid> Displays { get; init; } = [];
    public IReadOnlyList<DeviceInfo> Devices { get; init; } = [];
    /// The service (driver) keys of the present devices, by service name,
    /// where drivers keep machine-wide parameters.
    public IReadOnlyDictionary<string, RegistryDump> Services { get; init; } = new Dictionary<string, RegistryDump>();
    /// System-wide keys that describe the hardware, by their path under HKLM:
    /// HARDWARE\DESCRIPTION\System, DEVICEMAP, RESOURCEMAP, SystemInformation.
    public IReadOnlyDictionary<string, RegistryDump> Registry { get; init; } = new Dictionary<string, RegistryDump>();
    /// What Windows itself says about storage, battery, power, displays, the
    /// GPU and the driver store: one JSON document, gathered by PowerShell.
    public string? WindowsJson { get; init; }
    /// Names that identify this install rather than the model (the computer
    /// and user names), to take out of everything else.
    public IReadOnlyList<string> Secrets { get; init; } = [];
    public string Os { get; init; } = "";
    /// What could not be read, for the report and the person sending it.
    public IReadOnlyList<string> Problems { get; init; } = [];
}

public interface IHardwareSource
{
    Task<CollectedHardware> CollectAsync(IProgress<string> status, CancellationToken ct);
}
