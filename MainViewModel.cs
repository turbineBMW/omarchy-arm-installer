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

    public bool CanGoBack => Step > 0 && !Busy && (Step < (int)Steps.Create || (Step == (int)Steps.Create && Log.Count == 0));
    public bool CanGoNext => !Busy && (Steps)Step switch
    {
        Steps.Surface => SurfaceOk,
        Steps.Firmware => FirmwareOk,
        Steps.Installer => ImagePath is not null,
        Steps.Stick => SelectedDisk is not null,
        Steps.Windows => !BitLocker.On || KeySaved,
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
            SurfaceNote = m.IsSurfacePro11 && !m.IsTestedModel
                ? "Only the Snapdragon X Elite model with the OLED screen has been tested. Others may not boot."
                : "";
        }
        catch (Exception e)
        {
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
        foreach (var p in FirmwareCollector.Find(platform.DriverStore)) Firmware.Add(p);
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
        }
        catch (Exception e)
        {
            Error = e.Message;
        }
        ChangedAll();
    }

    // ---------- Windows ----------

    bool keepWindows = true;
    public bool KeepWindows { get => keepWindows; set { keepWindows = value; ChangedAll(); } }
    public bool ReplaceWindows { get => !keepWindows; set => KeepWindows = !value; }

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
            if (!CanShrink) KeepWindows = false;
            BitLocker = await platform.GetBitLockerAsync(CancellationToken.None);
        }
        catch (Exception e)
        {
            Error = e.Message;
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

    public ObservableCollection<LogLine> Log { get; } = [];
    public string Summary => SelectedDisk is null ? "" :
        $"Erase {SelectedDisk.Description} and write {Path.GetFileName(ImagePath)} to it, with this Surface's firmware.\n" +
        (KeepWindows
            ? $"Shrink C: by {ShrinkGb:0} GB so Omarchy installs next to Windows." +
              (BitLocker.On ? "\nPause BitLocker for the next 3 starts, while Secure Boot is turned off and Omarchy is installed." : "")
            : "Leave Windows as it is; Omarchy's installer will erase this Surface's disk.");

    public async Task CreateAsync()
    {
        if (SelectedDisk is null || ImagePath is null) return;
        var disk = SelectedDisk;
        var image = ImagePath;
        Log.Clear();
        await RunAsync(async ct =>
        {
            if (KeepWindows)
            {
                Note($"Shrinking C: by {ShrinkGb:0} GB");
                await platform.ShrinkSystemPartitionAsync((long)(ShrinkGb * 1e9), ct);
                Done($"C: shrunk; {ShrinkGb:0} GB free for Omarchy");
                if (BitLocker.On)
                {
                    Note("Pausing BitLocker for 3 starts");
                    await platform.SuspendBitLockerAsync(3, ct);
                    Done("BitLocker paused for the next 3 starts");
                }
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

    void Note(string text) => Log.Add(new LogLine(text, false));

    void Done(string text)
    {
        if (Log.Count > 0 && !Log[^1].Ok) Log[^1] = new LogLine(text, true);
        else Log.Add(new LogLine(text, true));
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
        }
        catch (Exception e)
        {
            Error = e.Message;
        }
        finally
        {
            Busy = false;
            ProgressText = "";
            ChangedAll();
        }
    }

    public void Cancel() => work?.Cancel();

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
