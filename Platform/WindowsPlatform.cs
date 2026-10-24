using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;

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
        var json = PowerShell("Get-CimInstance Win32_ComputerSystem | Select-Object Model, SystemSKUNumber | ConvertTo-Json");
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
            """);
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
            """);
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
            """);
    }, ct);

    public Task<SystemPartition> GetSystemPartitionAsync(CancellationToken ct) => Task.Run(() =>
    {
        var json = PowerShell("""
            $p = Get-Partition -DriveLetter C
            $s = Get-PartitionSupportedSize -DriveLetter C
            [pscustomobject]@{ size = $p.Size; min = $s.SizeMin } | ConvertTo-Json
            """);
        using var doc = JsonDocument.Parse(json);
        return new SystemPartition(doc.RootElement.GetProperty("size").GetInt64(), doc.RootElement.GetProperty("min").GetInt64());
    }, ct);

    public Task ShrinkSystemPartitionAsync(long bytes, CancellationToken ct) => Task.Run(() =>
    {
        PowerShell($$"""
            $p = Get-Partition -DriveLetter C
            $s = Get-PartitionSupportedSize -DriveLetter C
            $target = $p.Size - {{bytes}}
            if ($target -lt $s.SizeMin) { throw "C: can shrink by at most $([math]::Floor(($p.Size - $s.SizeMin) / 1GB)) GB" }
            Resize-Partition -DriveLetter C -Size $target
            """);
    }, ct);

    public async Task WriteImageAsync(string image, UsbDisk disk, IProgress<double> progress, CancellationToken ct)
    {
        // Same checks as the list, against the disk as it is now.
        await Task.Run(() => PowerShell($$"""
            $d = Get-Disk -Number {{disk.Number}}
            if ($d.BusType -ne 'USB' -or $d.IsBoot -or $d.IsSystem) { throw "Disk {{disk.Number}} is not a removable USB stick" }
            if ($d.PartitionStyle -ne 'RAW') { Clear-Disk -Number {{disk.Number}} -RemoveData -RemoveOEM -Confirm:$false }
            try { Set-Disk -Number {{disk.Number}} -IsOffline $true } catch { }
            """), ct);

        var length = new FileInfo(image).Length;
        var buffer = new byte[4 << 20];
        using var source = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await Task.Run(() =>
        {
            using var target = File.OpenHandle($@"\\.\PhysicalDrive{disk.Number}", FileMode.Open, FileAccess.ReadWrite,
                FileShare.ReadWrite, FileOptions.WriteThrough);
            using var input = File.OpenRead(image);
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

            // Read it back: a stick that silently drops writes is common enough.
            using var written = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            offset = 0;
            while (offset < length)
            {
                ct.ThrowIfCancellationRequested();
                var count = (int)Math.Min(buffer.Length, length - offset);
                var got = RandomAccess.Read(target, buffer.AsSpan(0, (count + 511) & ~511), offset);
                written.AppendData(buffer, 0, Math.Min(got, count));
                offset += count;
                progress.Report(0.8 + 0.2 * offset / length);
            }
            if (!written.GetHashAndReset().AsSpan().SequenceEqual(source.GetHashAndReset()))
                throw new IOException("The stick did not keep what was written to it. Try another USB stick.");
        }, ct);

        await Task.Run(() => PowerShell($$"""
            try { Set-Disk -Number {{disk.Number}} -IsOffline $false } catch { }
            Update-Disk -Number {{disk.Number}}
            """), ct);
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
            """).Trim();
        return root;
    }, ct);

    public Task EjectAsync(UsbDisk disk, CancellationToken ct) => Task.Run(() =>
    {
        PowerShell($$"""
            Get-Partition -DiskNumber {{disk.Number}} | Where-Object DriveLetter | ForEach-Object { Write-VolumeCache -DriveLetter $_.DriveLetter }
            """);
    }, ct);

    /// Runs a Windows PowerShell script, returning its output; a failure throws
    /// with the script's error text. -EncodedCommand runs the script as one
    /// unit, which stdin (-Command -) does not for multi-line blocks.
    static string PowerShell(string script)
    {
        var full = "$ErrorActionPreference = 'Stop'; $ProgressPreference = 'SilentlyContinue'\n" + script;
        var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(full));
        var info = new ProcessStartInfo("powershell.exe",
            $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Could not start PowerShell");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException(FirstLine(error.Result) ?? $"PowerShell exited with {process.ExitCode}");
        return output.Result;
    }

    static string? FirstLine(string text) =>
        text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
}
