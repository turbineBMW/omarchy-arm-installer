namespace SurfaceUsbCreator.Platform;

/// Stands in for Windows when the creator runs on Linux: a Surface Pro 11
/// with BitLocker on and one USB stick, and every write only simulated. The
/// driver store is real when SURFACE_CREATOR_DRIVER_STORE points at one (the
/// Windows partition mounted from Linux), so the firmware step shows real files.
public sealed class DemoPlatform : IPlatform
{
    public bool IsDemo => true;

    public MachineInfo GetMachine() => new("Surface Pro, 11th Edition", "Surface_Pro_11th_Edition_2076", true, true);

    public string DriverStore => Environment.GetEnvironmentVariable("SURFACE_CREATOR_DRIVER_STORE") ?? "/nonexistent";

    public string DownloadFolder => Path.Combine(Path.GetTempPath(), "surface-usb-creator");

    public Core.Hardware.IHardwareSource Hardware { get; } = new DemoHardware();

    public Task<IReadOnlyList<UsbDisk>> ListUsbDisksAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<UsbDisk>>([new UsbDisk(2, "SanDisk Ultra USB 3.0", 31_008_000_000, "4C530001")]);

    public Task<BitLockerState> GetBitLockerAsync(CancellationToken ct) =>
        Task.FromResult(new BitLockerState(true, [("{1A2B3C4D-0000-0000-0000-000000000000}", "123456-234567-345678-456789-567890-678901-789012-890123")]));

    public Task<SystemPartition> GetSystemPartitionAsync(CancellationToken ct) =>
        Task.FromResult(new SystemPartition(510_000_000_000, 180_000_000_000, 96_000_000_000));

    public Task ShrinkSystemPartitionAsync(long bytes, CancellationToken ct) => Task.Delay(1500, ct);

    public Task SuspendBitLockerAsync(int reboots, CancellationToken ct) => Task.Delay(500, ct);

    public async Task WriteImageAsync(string image, UsbDisk disk, IProgress<double> progress, CancellationToken ct)
    {
        // SURFACE_CREATOR_FAIL=1 shows what a failing write looks like.
        if (Environment.GetEnvironmentVariable("SURFACE_CREATOR_FAIL") == "1")
        {
            await Task.Delay(800, ct);
            throw new InvalidOperationException("Checking the stick and listing its volumes failed: Access is denied.");
        }
        for (var i = 1; i <= 100; i++)
        {
            await Task.Delay(40, ct);
            progress.Report(i / 100.0);
        }
    }

    public Task<string> MountFirmwareVolumeAsync(UsbDisk disk, CancellationToken ct)
    {
        var path = Path.Combine(DownloadFolder, "OMARCHY_FW");
        Directory.CreateDirectory(path);
        return Task.FromResult(path);
    }

    public Task EjectAsync(UsbDisk disk, CancellationToken ct) => Task.CompletedTask;
}
