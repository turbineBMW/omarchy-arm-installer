namespace SurfaceUsbCreator.Platform;

public sealed record MachineInfo(string Model, string Sku, bool IsArm64, bool IsAdministrator)
{
    public bool IsSurfacePro11 => Model.Contains("Surface Pro", StringComparison.OrdinalIgnoreCase) &&
                                  (Model.Contains("11th", StringComparison.OrdinalIgnoreCase) || Sku.Contains("11th", StringComparison.OrdinalIgnoreCase));

    /// The Snapdragon X Elite OLED model is the one tested; the LCD and X Plus
    /// models use other device trees.
    public bool IsTestedModel => Sku.Equals("Surface_Pro_11th_Edition_2076", StringComparison.OrdinalIgnoreCase);
}

public sealed record UsbDisk(int Number, string Model, long Size, string Serial)
{
    public string Description => $"{Model.Trim()}  ·  {Size / 1e9:0.#} GB  ·  disk {Number}";
}

/// BitLocker on C:. Recovery keys are the 48-digit numerical passwords.
public sealed record BitLockerState(bool On, IReadOnlyList<(string Id, string Password)> RecoveryKeys);

/// What C: can shrink to, so Omarchy can be installed into the free space,
/// and the largest stretch of unallocated space already on its disk.
public sealed record SystemPartition(long Size, long MinimumSize, long DiskFree)
{
    public long MaxShrink => Math.Max(0, Size - MinimumSize);
}

/// Everything that touches the machine: real on Windows, simulated elsewhere
/// so the interface can be developed and checked on Linux.
public interface IPlatform
{
    bool IsDemo { get; }
    MachineInfo GetMachine();
    string DriverStore { get; }
    string DownloadFolder { get; }

    Task<IReadOnlyList<UsbDisk>> ListUsbDisksAsync(CancellationToken ct);
    Task<BitLockerState> GetBitLockerAsync(CancellationToken ct);
    Task<SystemPartition> GetSystemPartitionAsync(CancellationToken ct);

    /// Shrinks C: by <bytes>, leaving unallocated space for Omarchy.
    Task ShrinkSystemPartitionAsync(long bytes, CancellationToken ct);

    /// Keeps BitLocker's keys in the clear for the next <reboots> starts, so
    /// turning Secure Boot off does not ask for the recovery key.
    Task SuspendBitLockerAsync(int reboots, CancellationToken ct);

    /// Erases the disk and writes the image to it sector by sector.
    Task WriteImageAsync(string image, UsbDisk disk, IProgress<double> progress, CancellationToken ct);

    /// The written image's OMARCHY_FW volume, mounted where it can be written.
    Task<string> MountFirmwareVolumeAsync(UsbDisk disk, CancellationToken ct);

    Task EjectAsync(UsbDisk disk, CancellationToken ct);
}
