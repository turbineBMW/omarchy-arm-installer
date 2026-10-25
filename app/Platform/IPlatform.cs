namespace OmarchyArmInstaller.Platform;

public sealed record MachineInfo(string Model, string Sku, bool IsArm64, bool IsAdministrator);

public sealed record UsbDisk(int Number, string Model, long Size, string Serial)
{
    public string Description => $"{Model.Trim()}  ·  {Size / 1e9:0.#} GB  ·  disk {Number}";
}

/// BitLocker on C:. Recovery keys are the 48-digit numerical passwords.
public sealed record BitLockerState(bool On, IReadOnlyList<(string Id, string Password)> RecoveryKeys);

/// C: and the largest stretch of unallocated space on its disk, where Omarchy
/// installs next to Windows. The user makes that space in Disk Management.
public sealed record SystemPartition(long Size, long DiskFree);

/// Everything that touches the machine: real on Windows, simulated elsewhere
/// so the interface can be developed and checked on Linux.
public interface IPlatform
{
    bool IsDemo { get; }
    MachineInfo GetMachine();
    string DriverStore { get; }
    string DownloadFolder { get; }

    /// Reads what a hardware report is made from.
    Core.Hardware.IHardwareSource Hardware { get; }

    Task<IReadOnlyList<UsbDisk>> ListUsbDisksAsync(CancellationToken ct);
    Task<BitLockerState> GetBitLockerAsync(CancellationToken ct);
    Task<SystemPartition> GetSystemPartitionAsync(CancellationToken ct);

    /// Opens Windows' Disk Management, where the user shrinks C: for Omarchy.
    void OpenDiskManagement();

    /// Keeps BitLocker's keys in the clear for the next <reboots> starts, so
    /// turning Secure Boot off does not ask for the recovery key.
    Task SuspendBitLockerAsync(int reboots, CancellationToken ct);

    /// Erases the disk and writes the image to it sector by sector.
    Task WriteImageAsync(string image, UsbDisk disk, IProgress<double> progress, CancellationToken ct);

    /// The written image's OMARCHY_FW volume, mounted where it can be written.
    Task<string> MountFirmwareVolumeAsync(UsbDisk disk, CancellationToken ct);

    Task EjectAsync(UsbDisk disk, CancellationToken ct);
}
