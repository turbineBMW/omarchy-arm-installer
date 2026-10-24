using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using SurfaceUsbCreator.Core;

namespace SurfaceUsbCreator.Platform;

/// The real thing, on the Surface's own Windows. Disk, partition and BitLocker
/// work goes through Windows' storage and CIM cmdlets; only the raw image write
/// is done here directly.
[SupportedOSPlatform("windows")]
public sealed class WindowsPlatform : IPlatform
{
    public bool IsDemo => false;

    public string DriverStore => Path.Combine(Environment.SystemDirectory, "DriverStore", "FileRepository");

    public string DownloadFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    public MachineInfo GetMachine()
    {
        var json = PowerShell("Get-CimInstance Win32_ComputerSystem | Select-Object Model, SystemSKUNumber | ConvertTo-Json", "Reading the model");
        using var doc = JsonDocument.Parse(json);
        var model = doc.RootElement.GetProperty("Model").GetString() ?? "";
        var sku = doc.RootElement.GetProperty("SystemSKUNumber").GetString() ?? "";
        var admin = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
        return new MachineInfo(model, sku, RuntimeInformation.OSArchitecture == Architecture.Arm64, admin);
    }

    public Task<IReadOnlyList<UsbDisk>> ListUsbDisksAsync(CancellationToken ct) => Task.Run<IReadOnlyList<UsbDisk>>(() =>
    {
        var json = PowerShell("""
            ConvertTo-Json -InputObject @(Get-Disk |
              Where-Object { $_.BusType -eq 'USB' -and -not $_.IsBoot -and -not $_.IsSystem -and $_.Size -gt 0 } |
              Select-Object Number, FriendlyName, Size, SerialNumber)
            """, "Listing USB sticks");
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateArray().Select(d => new UsbDisk(
            d.GetProperty("Number").GetInt32(),
            d.GetProperty("FriendlyName").GetString() ?? "USB disk",
            d.GetProperty("Size").GetInt64(),
            d.TryGetProperty("SerialNumber", out var s) ? s.GetString() ?? "" : "")).ToList();
    }, ct);

    const string EncryptableC = """
        $v = Get-CimInstance -Namespace root/CIMV2/Security/MicrosoftVolumeEncryption -ClassName Win32_EncryptableVolume -Filter "DriveLetter='C:'"
        """;

    public Task<BitLockerState> GetBitLockerAsync(CancellationToken ct) => Task.Run(() =>
    {
        var json = PowerShell(EncryptableC + """

            if (-not $v) { '{"on":false,"keys":[]}'; return }
            $status = (Invoke-CimMethod -InputObject $v -MethodName GetProtectionStatus).ProtectionStatus
            $ids = (Invoke-CimMethod -InputObject $v -MethodName GetKeyProtectors -Arguments @{ KeyProtectorType = [uint32]3 }).VolumeKeyProtectorID
            $keys = @(foreach ($id in $ids) {
              [pscustomobject]@{ id = $id; password = (Invoke-CimMethod -InputObject $v -MethodName GetKeyProtectorNumericalPassword -Arguments @{ VolumeKeyProtectorID = $id }).NumericalPassword }
            })
            [pscustomobject]@{ on = ($status -eq 1); keys = $keys } | ConvertTo-Json -Depth 3
            """, "Reading BitLocker on C:");
        using var doc = JsonDocument.Parse(json);
        var keys = doc.RootElement.GetProperty("keys");
        var list = keys.ValueKind == JsonValueKind.Array
            ? keys.EnumerateArray().Select(k => (k.GetProperty("id").GetString() ?? "", k.GetProperty("password").GetString() ?? "")).ToList()
            : [];
        return new BitLockerState(doc.RootElement.GetProperty("on").GetBoolean(), list);
    }, ct);

    public Task SuspendBitLockerAsync(int reboots, CancellationToken ct) => Task.Run(() =>
    {
        PowerShell(EncryptableC + $$"""

            $r = Invoke-CimMethod -InputObject $v -MethodName DisableKeyProtectors -Arguments @{ DisableCount = [uint32]{{reboots}} }
            if ($r.ReturnValue -ne 0) { throw "DisableKeyProtectors returned $($r.ReturnValue)" }
            """, "Pausing BitLocker");
    }, ct);

    public Task<SystemPartition> GetSystemPartitionAsync(CancellationToken ct) => Task.Run(() =>
    {
        var json = PowerShell("""
            $p = Get-Partition -DriveLetter C
            $s = Get-PartitionSupportedSize -DriveLetter C
            $d = Get-Disk -Number $p.DiskNumber
            [pscustomobject]@{ size = $p.Size; min = $s.SizeMin; free = $d.LargestFreeExtent } | ConvertTo-Json
            """, "Reading C: and its disk");
        using var doc = JsonDocument.Parse(json);
        return new SystemPartition(doc.RootElement.GetProperty("size").GetInt64(), doc.RootElement.GetProperty("min").GetInt64(),
            doc.RootElement.GetProperty("free").GetInt64());
    }, ct);

    public Task ShrinkSystemPartitionAsync(long bytes, CancellationToken ct) => Task.Run(() =>
    {
        PowerShell($$"""
            $p = Get-Partition -DriveLetter C
            $s = Get-PartitionSupportedSize -DriveLetter C
            $target = $p.Size - {{bytes}}
            if ($target -lt $s.SizeMin) { throw "C: can shrink by at most $([math]::Floor(($p.Size - $s.SizeMin) / 1GB)) GB" }
            Resize-Partition -DriveLetter C -Size $target
            """, "Shrinking C:");
    }, ct);

    public async Task WriteImageAsync(string image, UsbDisk disk, IProgress<double> progress, CancellationToken ct)
    {
        // Same checks as the list, against the disk as it is now, and the
        // volumes Windows mounted from whatever the stick held before.
        var json = await Task.Run(() => PowerShell($$"""
            $d = Get-Disk -Number {{disk.Number}}
            if ($d.BusType -ne 'USB' -or $d.IsBoot -or $d.IsSystem) { throw "Disk {{disk.Number}} is not a removable USB stick" }
            ConvertTo-Json -InputObject @(Get-Partition -DiskNumber {{disk.Number}} -ErrorAction SilentlyContinue |
              ForEach-Object { $_.AccessPaths } | Where-Object { $_ -like '\\?\Volume*' })
            """, "Checking the stick and listing its volumes"), ct);
        using var doc = JsonDocument.Parse(json);
        var volumes = doc.RootElement.ValueKind == JsonValueKind.Array
            ? doc.RootElement.EnumerateArray().Select(v => v.GetString() ?? "").Where(v => v.Length > 0).ToList()
            : [];

        var length = new FileInfo(image).Length;
        await Task.Run(() =>
        {
            // Locked and dismounted, as Rufus does, so Windows lets the raw
            // writes through and does not touch the old file systems meanwhile.
            var locked = new List<SafeFileHandle>();
            try
            {
                foreach (var volume in volumes) locked.Add(LockAndDismount(volume));

                using var target = Native.Open($@"\\.\PhysicalDrive{disk.Number}", write: true);
                // Forget the stick's partitions before writing over them. With an
                // earlier image's layout still registered, Windows refused the very
                // first write ("Incorrect function") even with every volume
                // dismounted; this is what deleting the partitions by hand does.
                if (Native.Ioctl(target, Native.IOCTL_DISK_DELETE_DRIVE_LAYOUT, throwOnError: false))
                {
                    Native.Ioctl(target, Native.IOCTL_DISK_UPDATE_PROPERTIES, throwOnError: false);
                    Log.Info("Old partition layout deleted");
                }
                else
                {
                    Log.Info($"Could not delete the old partition layout (Windows error {Marshal.GetLastWin32Error()}); writing anyway");
                }
                WriteAndVerify(target, image, length, disk.Size, progress, ct);
                // Let Windows read the new partition table.
                Native.Ioctl(target, Native.IOCTL_DISK_UPDATE_PROPERTIES);
            }
            finally
            {
                foreach (var handle in locked) handle.Dispose();
            }
        }, ct);

        await Task.Run(() => PowerShell($$"""
            Update-Disk -Number {{disk.Number}}
            """, "Rescanning the stick"), ct);
    }

    static SafeFileHandle LockAndDismount(string volumePath)
    {
        // \\?\Volume{GUID}\ -> \\.\Volume{GUID}, the device itself.
        var device = @"\\.\" + volumePath.TrimEnd('\\')[4..];
        var handle = Native.Open(device, write: true);
        var locked = false;
        for (var attempt = 0; attempt < 20 && !locked; attempt++)
        {
            locked = Native.Ioctl(handle, Native.FSCTL_LOCK_VOLUME, throwOnError: false);
            if (!locked) Thread.Sleep(250);
        }
        // Dismounting works without the lock too; something else then still
        // has a file open on it, which the write makes stale anyway.
        Native.Ioctl(handle, Native.FSCTL_DISMOUNT_VOLUME);
        Log.Info($"{device}: {(locked ? "locked and dismounted" : "dismounted (could not lock: a program has files open on it)")}");
        return handle;
    }

    static void WriteAndVerify(SafeFileHandle target, string image, long length, long diskSize, IProgress<double> progress,
        CancellationToken ct)
    {
        var buffer = new byte[4 << 20];
        using var source = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using (var input = File.OpenRead(image))
        {
            long offset = 0;
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                // Whole sectors: an ISO is a multiple of 2048 bytes.
                var count = (read + 511) & ~511;
                Array.Clear(buffer, read, count - read);
                RandomAccess.Write(target, buffer.AsSpan(0, count), offset);
                source.AppendData(buffer, 0, read);
                offset += read;
                progress.Report(0.8 * offset / length);
            }
        }

        // A partition table the stick held before keeps its backup copy in
        // the last sectors; clear it so nothing mistakes it for the image's.
        if (diskSize > length + (1 << 20))
        {
            Array.Clear(buffer, 0, 1 << 20);
            RandomAccess.Write(target, buffer.AsSpan(0, 1 << 20), (diskSize - (1 << 20)) & ~511L);
        }
        Log.Info($"Wrote {length} bytes");

        // Read it back: a stick that silently drops writes is common enough.
        using var written = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long verified = 0;
        while (verified < length)
        {
            ct.ThrowIfCancellationRequested();
            var count = (int)Math.Min(buffer.Length, length - verified);
            var got = RandomAccess.Read(target, buffer.AsSpan(0, (count + 511) & ~511), verified);
            written.AppendData(buffer, 0, Math.Min(got, count));
            verified += count;
            progress.Report(0.8 + 0.2 * verified / length);
        }
        if (!written.GetHashAndReset().AsSpan().SequenceEqual(source.GetHashAndReset()))
            throw new IOException("The stick did not keep what was written to it. Try another USB stick.");
        Log.Info("Read back intact");
    }

    public Task<string> MountFirmwareVolumeAsync(UsbDisk disk, CancellationToken ct) => Task.Run(() =>
    {
        // Windows takes a moment to find the partitions the image brought.
        var root = PowerShell($$"""
            for ($i = 0; $i -lt 30; $i++) {
              $part = Get-Partition -DiskNumber {{disk.Number}} -ErrorAction SilentlyContinue |
                Where-Object { (Get-Volume -Partition $_ -ErrorAction SilentlyContinue).FileSystemLabel -eq 'OMARCHY_FW' } |
                Select-Object -First 1
              if ($part) { break }
              Update-Disk -Number {{disk.Number}}
              Start-Sleep -Seconds 1
            }
            if (-not $part) { throw "The written stick has no OMARCHY_FW partition; is the image from this project?" }
            if (-not $part.DriveLetter -or $part.DriveLetter -eq [char]0) {
              $part | Add-PartitionAccessPath -AssignDriveLetter
              $part = Get-Partition -DiskNumber {{disk.Number}} -PartitionNumber $part.PartitionNumber
            }
            "$($part.DriveLetter):\"
            """, "Finding the stick's OMARCHY_FW volume").Trim();
        return root;
    }, ct);

    public Task EjectAsync(UsbDisk disk, CancellationToken ct) => Task.Run(() =>
    {
        PowerShell($$"""
            Get-Partition -DiskNumber {{disk.Number}} | Where-Object DriveLetter | ForEach-Object { Write-VolumeCache -DriveLetter $_.DriveLetter }
            """, "Flushing the stick");
    }, ct);

    /// Runs a Windows PowerShell script and returns its output. Every call
    /// goes to the log with its full output; a failure throws with the
    /// script's own error message. -EncodedCommand runs the script as one
    /// unit, which stdin (-Command -) does not for multi-line blocks.
    static string PowerShell(string script, string what)
    {
        var full = $$"""
            $ErrorActionPreference = 'Stop'
            $ProgressPreference = 'SilentlyContinue'
            try {
            {{script}}
            } catch {
              [Console]::Error.WriteLine("ERROR: " + $_.Exception.Message)
              [Console]::Error.WriteLine($_.InvocationInfo.PositionMessage)
              exit 1
            }
            """;
        var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(full));
        var info = new ProcessStartInfo("powershell.exe",
            $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -OutputFormat Text -EncodedCommand {encoded}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        Log.Info($"PowerShell: {what}");
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Could not start PowerShell");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        Log.Block($"  exit {process.ExitCode}, stdout", output.Result);
        if (error.Result.Trim().Length > 0) Log.Block("  stderr", error.Result);
        if (process.ExitCode != 0)
        {
            var message = error.Result.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("ERROR: "))?[7..]
                ?? FirstLine(error.Result) ?? FirstLine(output.Result) ?? $"PowerShell exited with {process.ExitCode}";
            throw new InvalidOperationException($"{what} failed: {message}");
        }
        return output.Result;
    }

    static string? FirstLine(string text) =>
        text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
}

/// The few Windows calls the storage cmdlets do not cover.
[SupportedOSPlatform("windows")]
static class Native
{
    public const uint FSCTL_LOCK_VOLUME = 0x00090018;
    public const uint FSCTL_DISMOUNT_VOLUME = 0x00090020;
    public const uint IOCTL_DISK_UPDATE_PROPERTIES = 0x00070140;
    public const uint IOCTL_DISK_DELETE_DRIVE_LAYOUT = 0x0007C100;

    const uint GenericRead = 0x80000000, GenericWrite = 0x40000000;
    const uint ShareReadWrite = 0x00000001 | 0x00000002;
    const uint OpenExisting = 3;
    const uint WriteThrough = 0x80000000;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition,
        uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DeviceIoControl(SafeFileHandle device, uint code, IntPtr input, uint inputSize, IntPtr output,
        uint outputSize, out uint returned, IntPtr overlapped);

    public static SafeFileHandle Open(string device, bool write)
    {
        var handle = CreateFileW(device, GenericRead | (write ? GenericWrite : 0), ShareReadWrite, IntPtr.Zero,
            OpenExisting, WriteThrough, IntPtr.Zero);
        if (handle.IsInvalid)
            throw new IOException($"Could not open {device} (Windows error {Marshal.GetLastWin32Error()})");
        return handle;
    }

    public static bool Ioctl(SafeFileHandle device, uint code, bool throwOnError = true)
    {
        if (DeviceIoControl(device, code, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero)) return true;
        if (throwOnError)
            throw new IOException($"DeviceIoControl 0x{code:x8} failed (Windows error {Marshal.GetLastWin32Error()})");
        return false;
    }
}
