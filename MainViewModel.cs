using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using SurfaceUsbCreator.Core;
using SurfaceUsbCreator.Platform;

namespace SurfaceUsbCreator;

public sealed record Check(string Text, bool Ok, string Detail = "")
{
    public string Mark => Ok ? "\uf00c" : "\uf00d";
}

public sealed record StepItem(string Title, int Index, MainViewModel Owner)
{
    public bool IsCurrent => Owner.Step == Index;
    public bool IsDone => Owner.Step > Index;
    public string Mark => IsDone ? "\uf00c" : (Index + 1).ToString();
}

public sealed record LogLine(string Text, bool Ok);

public sealed class MainViewModel : INotifyPropertyChanged
{
    public enum Steps { Surface, Firmware, Installer, Stick, Windows, Create, Done }

    static readonly string[] Titles = ["This Surface", "Firmware", "Installer", "USB stick", "Windows", "Create", "Next steps"];

    readonly IPlatform platform;
    CancellationTokenSource? work;

    public MainViewModel(IPlatform platform)
    {
        this.platform = platform;
        StepItems = new ObservableCollection<StepItem>(Titles.Select((t, i) => new StepItem(t, i, this)));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    void ChangedAll() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));

    public bool IsDemo => platform.IsDemo;
    public ObservableCollection<StepItem> StepItems { get; }

    int step;
    public int Step
    {
        get => step;
        set
        {
            step = value;
            for (var i = 0; i < StepItems.Count; i++) StepItems[i] = new StepItem(Titles[i], i, this);
            ChangedAll();
        }
    }

    public bool IsSurfaceStep => Step == (int)Steps.Surface;
    public bool IsFirmwareStep => Step == (int)Steps.Firmware;
    public bool IsInstallerStep => Step == (int)Steps.Installer;
    public bool IsStickStep => Step == (int)Steps.Stick;
    public bool IsWindowsStep => Step == (int)Steps.Windows;
    public bool IsCreateStep => Step == (int)Steps.Create;
    public bool IsDoneStep => Step == (int)Steps.Done;
    public string StepTitle => Titles[Step];

    public bool CanGoBack => Step > 0 && !Busy && (Step < (int)Steps.Create || (Step == (int)Steps.Create && (CreateLog.Count == 0 || HasError)));
    public bool CanGoNext => !Busy && (Steps)Step switch
    {
        Steps.Surface => SurfaceOk,
        Steps.Firmware => FirmwareOk,
        Steps.Installer => ImagePath is not null,
        Steps.Stick => SelectedDisk is not null,
        Steps.Windows => (!BitLocker.On || KeySaved) && !(AlreadyDone && DiskFreeLow),
        _ => false,
    };
    public bool ShowNext => Step < (int)Steps.Create;

    bool busy;
    public bool Busy { get => busy; set { busy = value; ChangedAll(); } }

    string? error;
    public string? Error { get => error; set { error = value; Changed(); Changed(nameof(HasError)); } }
    public bool HasError => !string.IsNullOrEmpty(Error);

    // ---------- This Surface ----------

    public ObservableCollection<Check> SurfaceChecks { get; } = [];
    public bool SurfaceOk => SurfaceChecks.Count > 0 && SurfaceChecks.All(c => c.Ok);
    public string SurfaceNote { get; private set; } = "";

    public void CheckSurface()
    {
        SurfaceChecks.Clear();
        try
        {
            var m = platform.GetMachine();
            SurfaceChecks.Add(new Check("Surface Pro 11", m.IsSurfacePro11, $"{m.Model} ({m.Sku})"));
            SurfaceChecks.Add(new Check("Windows on Arm", m.IsArm64));
            SurfaceChecks.Add(new Check("Running as administrator", m.IsAdministrator,
                m.IsAdministrator ? "" : "Right-click the creator and choose Run as administrator"));
            Log.Info($"Machine: {m.Model} ({m.Sku}), arm64 {m.IsArm64}, administrator {m.IsAdministrator}");
            SurfaceNote = m.IsSurfacePro11 && !m.IsTestedModel
                ? "Only the Snapdragon X Elite model with the OLED screen has been tested. Others may not boot."
                : "";
        }
        catch (Exception e)
        {
            Log.Error("reading the model", e);
            SurfaceChecks.Add(new Check("Reading this computer's model", false, e.Message));
        }
        ChangedAll();
    }

    // ---------- Firmware ----------

    public ObservableCollection<FirmwarePackage> Firmware { get; } = [];
    public bool FirmwareOk => Firmware.Count > 0 && Firmware.All(p => p.Complete);
    public string FirmwareSummary => Firmware.Count == 0 ? "" :
        $"{Firmware.Sum(p => p.Found.Count)} files, {Firmware.Sum(p => p.Found.Sum(f => f.Size)) / 1e6:0.0} MB from {platform.DriverStore}";

    public void FindFirmware()
    {
        Firmware.Clear();
        foreach (var p in FirmwareCollector.Find(platform.DriverStore))
        {
            Firmware.Add(p);
            Log.Info($"Firmware {p.Pattern}: {(p.Complete ? "complete" : "INCOMPLETE")}, " +
                     string.Join(", ", p.Found.Select(f => $"{f.Folder}/{f.Name} ({f.Size})").Take(6)) +
                     (p.Found.Count > 6 ? $" and {p.Found.Count - 6} more" : ""));
        }
        ChangedAll();
    }

    // ---------- Installer ----------

    ReleasedImage? release;
    bool releaseChecked;
    public string ReleaseText => release is not null ? $"{release.IsoName}  ·  {release.Size / 1e9:0.0} GB"
        : releaseChecked ? "No published installer found" : "Looking for the latest installer…";
    public bool CanDownload => release is not null && !Busy;
    public string? ImagePath { get; private set; }
    public string ImageText => ImagePath is null ? "No installer image chosen yet" : $"Using {ImagePath}";

    double progress;
    public double Progress { get => progress; set { progress = value; Changed(); } }
    public string ProgressText { get; private set; } = "";

    public async Task FindReleaseAsync()
    {
        try
        {
            release = await ImageSource.FindLatestAsync(CancellationToken.None);
            if (release is null) Error = "No installer has been published yet. Choose an ISO file instead.";
        }
        catch (HttpRequestException e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            Error = "No installer has been published yet. Choose an ISO file instead.";
        }
        catch (Exception e)
        {
            Error = $"Could not reach GitHub ({e.Message}). Choose an ISO file instead.";
        }
        releaseChecked = true;
        ChangedAll();
    }

    public async Task DownloadAsync()
    {
        if (release is null) return;
        await RunAsync(async ct =>
        {
            Directory.CreateDirectory(platform.DownloadFolder);
            var path = Path.Combine(platform.DownloadFolder, release.IsoName);
            ProgressText = "Downloading and checking the installer";
            await ImageSource.DownloadAsync(release, path, new Progress<double>(p => Progress = p), ct);
            ImagePath = path;
        });
    }

    public void UseLocalImage(string path)
    {
        Log.Info($"Image: {path} ({new FileInfo(path).Length} bytes)");
        ImagePath = path;
        Error = null;
        ChangedAll();
    }

    // ---------- USB stick ----------

    public ObservableCollection<UsbDisk> Disks { get; } = [];
    UsbDisk? selectedDisk;
    public UsbDisk? SelectedDisk { get => selectedDisk; set { selectedDisk = value; ChangedAll(); } }
    public bool NoDisks => Disks.Count == 0;

    public async Task ListDisksAsync()
    {
        try
        {
            var disks = await platform.ListUsbDisksAsync(CancellationToken.None);
            Disks.Clear();
            foreach (var d in disks.Where(d => d.Size >= 8_000_000_000)) Disks.Add(d);
            if (SelectedDisk is not null && !Disks.Contains(SelectedDisk)) SelectedDisk = null;
            if (Disks.Count == 1) SelectedDisk = Disks[0];
            Log.Info($"USB sticks: {(disks.Count == 0 ? "none" : string.Join("; ", disks.Select(d => d.Description)))}");
        }
        catch (Exception e)
        {
            Fail("listing USB sticks", e);
        }
        ChangedAll();
    }

    // ---------- Windows ----------

    public enum WindowsPlan { Keep, AlreadyDone, Replace }

    WindowsPlan plan = WindowsPlan.Keep;
    public WindowsPlan Plan { get => plan; set { plan = value; ChangedAll(); } }
    public bool KeepWindows { get => Plan == WindowsPlan.Keep; set { if (value) Plan = WindowsPlan.Keep; } }
    public bool AlreadyDone { get => Plan == WindowsPlan.AlreadyDone; set { if (value) Plan = WindowsPlan.AlreadyDone; } }
    public bool ReplaceWindows { get => Plan == WindowsPlan.Replace; set { if (value) Plan = WindowsPlan.Replace; } }
    /// Windows stays, so Secure Boot going off is something it will notice.
    public bool WindowsStays => Plan != WindowsPlan.Replace;

    public string DiskFreeText => Partition is null ? "" : Partition.DiskFree >= 20e9
        ? $"{Partition.DiskFree / 1e9:0} GB of unallocated space found on this disk."
        : $"Only {Partition.DiskFree / 1e9:0} GB of unallocated space on this disk; Omarchy needs at least 20 GB.";
    public bool DiskFreeLow => Partition is not null && Partition.DiskFree < 20e9;

    public SystemPartition? Partition { get; private set; }
    public double MaxShrinkGb => Partition is null ? 0 : Math.Floor(Partition.MaxShrink / 1e9);
    double shrinkGb = 128;
    public double ShrinkGb { get => shrinkGb; set { shrinkGb = Math.Round(value); Changed(); Changed(nameof(ShrinkText)); } }
    public string ShrinkText => $"Make {ShrinkGb:0} GB of room for Omarchy (C: keeps {(Partition is null ? 0 : (Partition.Size / 1e9 - ShrinkGb)):0} GB)";
    public bool CanShrink => MaxShrinkGb >= 32;

    public BitLockerState BitLocker { get; private set; } = new(false, []);
    public string RecoveryKeys => string.Join("\n", BitLocker.RecoveryKeys.Select(k => k.Password));
    bool keySaved;
    public bool KeySaved { get => keySaved; set { keySaved = value; ChangedAll(); } }

    public async Task CheckWindowsAsync()
    {
        try
        {
            Partition = await platform.GetSystemPartitionAsync(CancellationToken.None);
            ShrinkGb = Math.Clamp(ShrinkGb, 32, Math.Max(32, MaxShrinkGb));
            if (!CanShrink && Plan == WindowsPlan.Keep) Plan = Partition!.DiskFree >= 20e9 ? WindowsPlan.AlreadyDone : WindowsPlan.Replace;
            Log.Info($"C: {Partition!.Size / 1e9:0.0} GB, shrinkable to {Partition.MinimumSize / 1e9:0.0} GB; {Partition.DiskFree / 1e9:0.0} GB unallocated");
            BitLocker = await platform.GetBitLockerAsync(CancellationToken.None);
            Log.Info($"BitLocker on C:: {(BitLocker.On ? "on" : "off")}, {BitLocker.RecoveryKeys.Count} recovery key(s)");
        }
        catch (Exception e)
        {
            Fail("reading C: and BitLocker", e);
        }
        ChangedAll();
    }

    public string RecoveryKeyFileText =>
        "BitLocker recovery key for this Surface's Windows (C:)\n" +
        $"Saved by the Surface Pro 11 for Omarchy USB creator on {DateTime.Now:yyyy-MM-dd HH:mm}\n\n" +
        string.Join("\n", BitLocker.RecoveryKeys.Select(k => $"Key ID {k.Id}\nRecovery key {k.Password}\n")) +
        "\nWindows asks for this if it starts after the firmware's security settings change.\n" +
        "Keep it somewhere other than this Surface and the USB stick.\n";

    // ---------- Create ----------

    public ObservableCollection<LogLine> CreateLog { get; } = [];
    public string Summary => SelectedDisk is null ? "" :
        $"Erase {SelectedDisk.Description} and write {Path.GetFileName(ImagePath)} to it, with this Surface's firmware.\n" +
        Plan switch
        {
            WindowsPlan.Keep => $"Shrink C: by {ShrinkGb:0} GB so Omarchy installs next to Windows.",
            WindowsPlan.AlreadyDone => "Leave C: as it is: Omarchy installs into the space you made.",
            _ => "Leave Windows as it is; Omarchy's installer will erase this Surface's disk.",
        } +
        (WindowsStays && BitLocker.On ? "\nPause BitLocker for the next 3 starts, while Secure Boot is turned off and Omarchy is installed." : "");

    bool shrunk;

    public async Task CreateAsync()
    {
        if (SelectedDisk is null || ImagePath is null) return;
        // Windows can renumber a stick that was unplugged and plugged back in.
        var chosen = SelectedDisk;
        UsbDisk? disk = null;
        try
        {
            disk = (await platform.ListUsbDisksAsync(CancellationToken.None))
                .FirstOrDefault(d => d.Serial == chosen.Serial && d.Size == chosen.Size);
        }
        catch (Exception e)
        {
            Fail("finding the USB stick again", e);
            return;
        }
        if (disk is null)
        {
            Error = "The USB stick is not plugged in any more. Plug it back in, wait a few seconds, go Back and choose Look again.";
            Log.Info($"Stick {chosen.Description} ({chosen.Serial}) not found before writing");
            return;
        }
        var image = ImagePath;
        CreateLog.Clear();
        await RunAsync(async ct =>
        {
            Log.Info($"Creating: disk {disk.Number} ({disk.Model}, {disk.Size} bytes), image {image}, plan {Plan}, shrink {ShrinkGb} GB, BitLocker {(BitLocker.On ? "on" : "off")}");
            // A retry after a failure must not shrink C: a second time.
            if (Plan == WindowsPlan.Keep && !shrunk)
            {
                Note($"Shrinking C: by {ShrinkGb:0} GB");
                await platform.ShrinkSystemPartitionAsync((long)(ShrinkGb * 1e9), ct);
                shrunk = true;
                Done($"C: shrunk; {ShrinkGb:0} GB free for Omarchy");
            }
            if (WindowsStays && BitLocker.On)
            {
                Note("Pausing BitLocker for 3 starts");
                await platform.SuspendBitLockerAsync(3, ct);
                Done("BitLocker paused for the next 3 starts");
            }

            Note($"Writing the installer to {disk.Model.Trim()}");
            ProgressText = "Writing and checking the stick";
            await platform.WriteImageAsync(image, disk, new Progress<double>(p => Progress = p), ct);
            Done("Installer written and read back intact");

            Note("Copying this Surface's firmware onto the stick");
            var volume = await platform.MountFirmwareVolumeAsync(disk, ct);
            await Task.Run(() => FirmwareCollector.CopyTo(Firmware, volume), ct);
            Done($"{Firmware.Sum(p => p.Found.Count)} firmware files copied to {volume}");

            await platform.EjectAsync(disk, ct);
            Done("The stick is ready");
        });
        if (!HasError) Step = (int)Steps.Done;
    }

    void Note(string text) => CreateLog.Add(new LogLine(text, false));

    void Done(string text)
    {
        if (CreateLog.Count > 0 && !CreateLog[^1].Ok) CreateLog[^1] = new LogLine(text, true);
        else CreateLog.Add(new LogLine(text, true));
    }

    async Task RunAsync(Func<CancellationToken, Task> action)
    {
        work = new CancellationTokenSource();
        Error = null;
        Progress = 0;
        Busy = true;
        try
        {
            await action(work.Token);
        }
        catch (OperationCanceledException)
        {
            Error = "Stopped.";
            Log.Info("Stopped by the user");
        }
        catch (Exception e)
        {
            Fail(StepTitle, e);
        }
        finally
        {
            Busy = false;
            ProgressText = "";
            ChangedAll();
        }
    }

    public void Cancel() => work?.Cancel();

    // ---------- Errors ----------

    string failedStep = "";

    void Fail(string context, Exception e)
    {
        Log.Error(context, e);
        failedStep = StepTitle;
        Error = Explain(e);
    }

    /// Windows' wording for the failures a stick most often meets, put plainly.
    static string Explain(Exception e) => (e.HResult & 0xFFFF) switch
    {
        // ERROR_DEV_NOT_EXIST, ERROR_DEVICE_NOT_CONNECTED, ERROR_NO_SUCH_DEVICE
        55 or 1167 or 433 => "The USB stick was disconnected while it was being written. Plug it back in, wait a few seconds, " +
                             "go Back, choose Look again, and create it again.",
        // ERROR_INVALID_FUNCTION, ERROR_ACCESS_DENIED, ERROR_WRITE_PROTECT
        1 or 5 or 19 when e is IOException => $"Windows would not let the creator write to the stick ({e.Message.TrimEnd('.')}). " +
                             "Unplug it, plug it back in and try again; if it keeps failing, open the log and report it.",
        _ => e.Message,
    };

    public string LogPath => Log.FilePath;

    /// A new issue on the repository, filled in with what the log says; the
    /// log file itself has to be attached by hand.
    public string IssueUrl
    {
        get
        {
            var machine = SurfaceChecks.FirstOrDefault()?.Detail ?? "";
            var title = $"USB creator: {Error}";
            if (title.Length > 120) title = title[..117] + "...";
            var body =
                $"**What happened**\n{Error}\n\n" +
                $"**Step**: {failedStep}\n" +
                $"**Creator**: {Log.Version}\n" +
                $"**Machine**: {machine}\n\n" +
                "**Log**\nPlease attach the log file (drag it into this box):\n" +
                $"`{Log.FilePath}`\n";
            return $"https://github.com/{ImageSource.Repository}/issues/new?title={Uri.EscapeDataString(title)}&body={Uri.EscapeDataString(body)}";
        }
    }

    // ---------- Navigation ----------

    public async Task NextAsync()
    {
        Error = null;
        Step++;
        await EnterAsync();
    }

    public void Back()
    {
        Error = null;
        Step--;
    }

    public async Task EnterAsync()
    {
        Log.Info($"Step: {StepTitle}");
        switch ((Steps)Step)
        {
            case Steps.Surface: CheckSurface(); break;
            case Steps.Firmware: FindFirmware(); break;
            case Steps.Installer: if (release is null) await FindReleaseAsync(); break;
            case Steps.Stick: await ListDisksAsync(); break;
            case Steps.Windows: await CheckWindowsAsync(); break;
        }
    }
}
