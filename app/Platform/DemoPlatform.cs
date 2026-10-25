namespace OmarchyArmInstaller.Platform;

/// Stands in for Windows when the installer runs on Linux: a machine with
/// BitLocker on and one USB stick, and every write only simulated. The
/// driver store is real when OMARCHY_DEMO_DRIVER_STORE points at one (the
/// Windows partition mounted from Linux), so the firmware step shows real files.
public sealed class DemoPlatform : IPlatform
{
    public bool IsDemo => true;

    public MachineInfo GetMachine() => new("Demo", "", true, true);

    public string DriverStore => Environment.GetEnvironmentVariable("OMARCHY_DEMO_DRIVER_STORE") ?? "/nonexistent";

    public string DownloadFolder => Path.Combine(Path.GetTempPath(), "omarchy-arm-installer");

    public Core.Hardware.IHardwareSource Hardware { get; } = new DemoHardware();

    public Task<IReadOnlyList<UsbDisk>> ListUsbDisksAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<UsbDisk>>([new UsbDisk(2, "SanDisk Ultra USB 3.0", 31_008_000_000, "4C530001")]);

    public Task<BitLockerState> GetBitLockerAsync(CancellationToken ct) =>
        Task.FromResult(new BitLockerState(true, [("{1A2B3C4D-0000-0000-0000-000000000000}", "123456-234567-345678-456789-567890-678901-789012-890123")]));

    /// OMARCHY_DEMO_FREE_GB is the unallocated space found (default 96);
    /// each check adds 64 GB, as if the user had just shrunk C:.
    long free = (long.TryParse(Environment.GetEnvironmentVariable("OMARCHY_DEMO_FREE_GB"), out var gb) ? gb : 96) * 1_000_000_000;
    public Task<SystemPartition> GetSystemPartitionAsync(CancellationToken ct)
    {
        var now = new SystemPartition(510_000_000_000, free);
        free += 64_000_000_000;
        return Task.FromResult(now);
    }

    public void OpenDiskManagement() => Core.Log.Info("Demo: would open Disk Management");

    public Task SuspendBitLockerAsync(int reboots, CancellationToken ct) => Task.Delay(500, ct);

    public async Task WriteImageAsync(string image, UsbDisk disk, IProgress<double> progress, CancellationToken ct)
    {
        // OMARCHY_DEMO_FAIL=1 shows what a failing write looks like.
        if (Environment.GetEnvironmentVariable("OMARCHY_DEMO_FAIL") == "1")
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
