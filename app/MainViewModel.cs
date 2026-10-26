using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using OmarchyArmInstaller.Core;
using OmarchyArmInstaller.Core.Hardware;
using OmarchyArmInstaller.Platform;

namespace OmarchyArmInstaller;

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
    public enum Steps { Start, Machine, Firmware, Extras, Installer, Stick, Windows, Create, Done, Collect, Share }

    static readonly Dictionary<Steps, string> Titles = new()
    {
        [Steps.Start] = "What would you like to do?",
        [Steps.Machine] = "This computer",
        [Steps.Firmware] = "Firmware",
        [Steps.Extras] = "Extras",
        [Steps.Installer] = "Installer",
        [Steps.Stick] = "USB stick",
        [Steps.Windows] = "Windows",
        [Steps.Create] = "Create",
        [Steps.Done] = "Next steps",
        [Steps.Collect] = "Hardware report",
        [Steps.Share] = "Share it",
    };

    /// The two things the tool does, each its own run of steps.
    static readonly Steps[] InstallFlow = [Steps.Machine, Steps.Firmware, Steps.Installer, Steps.Stick, Steps.Windows, Steps.Create, Steps.Done];
    /// With a recipe for this model, its extras get a step of their own.
    static readonly Steps[] RecipeFlow = [Steps.Machine, Steps.Firmware, Steps.Extras, Steps.Installer, Steps.Stick, Steps.Windows, Steps.Create, Steps.Done];
    static readonly Steps[] ReportFlow = [Steps.Collect, Steps.Share];

    readonly IPlatform platform;
    CancellationTokenSource? work;

    public MainViewModel(IPlatform platform)
    {
        this.platform = platform;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    void ChangedAll() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));

    public bool IsDemo => platform.IsDemo;
    public ObservableCollection<StepItem> StepItems { get; } = [];

    Steps[] flow = [];
    Steps current = Steps.Start;
    public Steps Current
    {
        get => current;
        set
        {
            current = value;
            StepItems.Clear();
            for (var i = 0; i < flow.Length; i++) StepItems.Add(new StepItem(Titles[flow[i]], i, this));
            ChangedAll();
        }
    }

    /// The position in the chosen run of steps; -1 on the start screen.
    public int Step
    {
        get => Array.IndexOf(flow, current);
        set => Current = flow[value];
    }

    public bool IsStartStep => Current == Steps.Start;
    public bool IsMachineStep => Current == Steps.Machine;
    public bool IsFirmwareStep => Current == Steps.Firmware;
    public bool IsExtrasStep => Current == Steps.Extras;
    public bool IsInstallerStep => Current == Steps.Installer;
    public bool IsStickStep => Current == Steps.Stick;
    public bool IsWindowsStep => Current == Steps.Windows;
    public bool IsCreateStep => Current == Steps.Create;
    public bool IsDoneStep => Current == Steps.Done;
    public bool IsCollectStep => Current == Steps.Collect;
    public bool IsShareStep => Current == Steps.Share;
    public string StepTitle => IsStartStep ? StartTitle : Titles[Current];
    public bool IsReport => flow == ReportFlow;
    public string Subtitle => IsStartStep ? "for Windows on Arm laptops" : IsReport ? "hardware report" : "installer";

    public bool CanGoBack => !IsStartStep && !Busy && Current switch
    {
        Steps.Create => CreateLog.Count == 0 || HasError,
        Steps.Done => false,
        // A laptop Linux knows was sent straight here; there is no start screen behind it.
        Steps.Machine => Detected != Detection.Known,
        _ => true,
    };
    public bool CanGoNext => !Busy && Current switch
    {
        Steps.Machine => MachineOk,
        Steps.Firmware => FirmwareOk && FirmwareSize <= StickRoom,
        Steps.Extras => !UseRecipe || !StickTooSmall,
        Steps.Installer => ImagePath is not null && (!UseRecipe || (imageFromRelease && official is not null)),
        Steps.Stick => SelectedDisk is not null,
        Steps.Windows => (!BitLocker.On || KeySaved) && !(KeepWindows && DiskFreeLow),
        Steps.Collect => Report is not null,
        _ => false,
    };
    public bool ShowNext => Current is not (Steps.Start or Steps.Create or Steps.Done or Steps.Share);
    public bool ShowClose => Current is Steps.Done or Steps.Share || NotArm;

    public async Task StartAsync(bool report)
    {
        flow = report ? ReportFlow : InstallFlow;
        Error = null;
        Step = 0;
        await EnterAsync();
    }

    bool busy;
    public bool Busy { get => busy; set { busy = value; ChangedAll(); } }

    string? error;
    public string? Error { get => error; set { error = value; Changed(); Changed(nameof(HasError)); } }
    public bool HasError => !string.IsNullOrEmpty(Error);

    // ---------- Looking at this computer ----------

    /// What opening the installer found out, which decides what the start
    /// screen is. A laptop Linux knows goes straight to making the stick; one
    /// it does not is told what that means and offered the report; when the
    /// computer could not be read, the person chooses.
    public enum Detection { Looking, Known, NoDeviceTree, NotArm, Failed }

    public Detection Detected { get; private set; } = Detection.Looking;
    public bool IsLooking => IsStartStep && Detected == Detection.Looking;
    public bool NoDeviceTree => IsStartStep && Detected == Detection.NoDeviceTree;
    public bool NotArm => IsStartStep && Detected == Detection.NotArm;
    public bool DetectFailed => IsStartStep && Detected == Detection.Failed;

    string StartTitle => Detected switch
    {
        Detection.Looking => "Looking at this computer",
        Detection.NoDeviceTree => "Linux doesn't know this computer yet",
        Detection.NotArm => "Not a Windows on Arm computer",
        _ => "What would you like to do?",
    };

    /// The list a laptop is recognised from goes stale with the installer.
    public string ModelsNote =>
        $"This installer ({Log.Version}) recognises the {Recipes.Models.Count} models in systemd's list when it was released, " +
        $"and the {Recipes.All.Count} with a recipe. A newer release may know more: github.com/{ImageSource.Repository}/releases.";

    public async Task DetectAsync()
    {
        Detected = Detection.Looking;
        await RunAsync(async ct =>
        {
            ProgressText = "Reading this computer";
            machine ??= await Task.Run(platform.GetMachine, ct);
            if (!machine.IsArm64) { Detected = Detection.NotArm; return; }
            await ReadMachineAsync(ct);
            Detected = Model is not null || Recipe is not null ? Detection.Known : Detection.NoDeviceTree;
        });
        if (HasError) Detected = Detection.Failed;
        Log.Info($"Start: {Detected}");
        if (Detected == Detection.Known) await StartAsync(report: false);
        else ChangedAll();
    }

    // ---------- This computer ----------

    public ObservableCollection<Check> MachineChecks { get; } = [];
    public bool MachineOk => MachineChecks.Count > 0 && MachineChecks.All(c => c.Ok);
    public bool MachineUnsupported { get; private set; }
    public string MachineName { get; private set; } = "";
    public string MachineNote { get; private set; } = "";
    public bool HasMachineNote => MachineNote.Length > 0;
    /// A recipe matched this model: said on its own line, with its icon.
    public bool HasRecipeNote { get; private set; }
    public string RecipeNoteDetail => Recipe is { IsTested: false } ? "It has not been tested on this model yet." : "";
    public bool HasRecipeNoteDetail => RecipeNoteDetail.Length > 0;

    MachineInfo? machine;
    CollectedHardware? hardware;
    public Recipe? Recipe { get; private set; }
    public KnownModel? Model { get; private set; }

    /// Reads the computer once: SMBIOS, displays and devices, and from those
    /// the CHIDs that say whether Linux has a device tree for it and whether a
    /// recipe matches. The start screen does this; the steps reuse it.
    async Task ReadMachineAsync(CancellationToken ct)
    {
        machine ??= await Task.Run(platform.GetMachine, ct);
        if (hardware is not null) return;
        var m = machine;
        hardware = await platform.Hardware.CollectAsync(new Progress<string>(t => { ProgressText = t; Changed(nameof(ProgressText)); }), ct);
        var identity = hardware.Smbios is { Length: > 0 } table ? Smbios.Identity(Smbios.Parse(table)) : null;
        var panel = hardware.Displays.OrderByDescending(d => d.Internal).Select(d => Edid.PanelId(d.Edid)).FirstOrDefault();
        var chids = identity is null ? [] : Chid.Calculate(identity, panel).Select(c => c.Value).ToList();
        Recipe = Recipes.For(chids);
        Model = Recipes.ModelFor(chids);
        MachineName = identity is null ? m.Model
            : string.Join(" ", new[] { identity.Manufacturer, identity.ProductName }.Where(x => !string.IsNullOrWhiteSpace(x)))
              + (identity.Sku is { } sku ? $"  ·  {sku}" : "");
        Log.Info($"Machine: {MachineName}, arm64 {m.IsArm64}, administrator {m.IsAdministrator}, " +
                 $"device tree {Model?.Compatible ?? "none"}, recipe {Recipe?.Id ?? "none"}, CHIDs {string.Join(" ", chids)}");
    }

    public async Task CheckMachineAsync()
    {
        MachineChecks.Clear();
        MachineNote = "";
        HasRecipeNote = false;
        MachineUnsupported = false;
        await RunAsync(async ct =>
        {
            ProgressText = "Reading this computer";
            await ReadMachineAsync(ct);
            var m = machine!;

            MachineChecks.Add(new Check("Windows on Arm", m.IsArm64));
            MachineChecks.Add(new Check("Running as administrator", m.IsAdministrator,
                m.IsAdministrator ? "" : "Right-click the installer and choose Run as administrator"));
            var supported = Model is not null || Recipe is not null;
            MachineChecks.Add(new Check("Linux knows this model", supported,
                supported ? $"Device tree {Model?.Compatible ?? Recipe!.Device}" : "No device tree for it yet"));
            MachineUnsupported = !supported;

            MachineNote = !supported
                ? "Omarchy cannot start on this model yet: Linux needs a device tree for it, written by hand. " +
                  "Share a hardware report, and the people who write them have what they need."
                : Recipe is not null ? ""
                    : "Omarchy installs as it comes, with this computer's firmware. Not every part may work yet.";
            HasRecipeNote = supported && Recipe is not null;
            flow = Recipe is null ? InstallFlow : RecipeFlow;
            UseRecipe = Recipe is not null;
        });
        Current = Current; // the steps may have changed
    }

    public async Task SwitchToReportAsync() => await StartAsync(report: true);

    // ---------- Firmware ----------

    /// The stick's OMARCHY_FW partition, less what FAT32 and slack take.
    const long StickRoom = 1000L * 1000 * 1000;

    public ObservableCollection<FirmwarePackage> Firmware { get; } = [];
    public bool FirmwareOk => Firmware.Count > 0 && Firmware.All(p => p.Complete);
    public long FirmwareSize => FirmwareCollector.Size(Firmware);
    public string FirmwareSummary => Firmware.Count == 0 ? "No firmware found in " + platform.DriverStore :
        $"{Firmware.Sum(p => p.Found.Count)} files, {FirmwareSize / 1e6:0.0} MB from {platform.DriverStore}";

    public void FindFirmware()
    {
        Firmware.Clear();
        foreach (var p in FirmwareCollector.Find(platform.DriverStore, hardware?.Devices ?? [], Recipe))
        {
            Firmware.Add(p);
            Log.Info($"Firmware {p.Folder} ({p.Label}): {(p.Complete ? "complete" : "INCOMPLETE")}, " +
                     string.Join(", ", p.Found.Select(f => $"{f.Name} ({f.Size})").Take(6)) +
                     (p.Found.Count > 6 ? $" and {p.Found.Count - 6} more" : ""));
        }
        ChangedAll();
    }

    // ---------- Extras ----------

    bool useRecipe;
    /// Only a verified, still-active repository can be chosen.
    public bool UseRecipe { get => useRecipe && CanUseRecipe; set { useRecipe = value; ChangedAll(); } }
    public bool CanUseRecipe => verified is not null && !verified.Description.IsRetired;
    VerifiedRepo? verified;
    public RecipeInfo? RecipeInfo => verified?.Description;
    public List<RepoPackage>? RecipePackages => verified?.Packages;
    public string RecipeProblem { get; private set; } = "";
    public bool HasRecipeProblem => RecipeProblem.Length > 0;
    public bool RecipeLoading => Recipe is not null && verified is null && !HasRecipeProblem;
    public long RecipeSize => UseRecipe ? (RecipePackages?.Sum(p => p.Size) ?? 0) + (official?.Sum(p => p.Package.Size) ?? 0) : 0;

    /// Omarchy's and Arch's packages the extras need that the installer image
    /// does not carry; known once both the repository and the image are.
    List<OfficialPackage>? official;
    bool imageFromRelease;
    public string OfficialProblem { get; private set; } = "";
    public bool HasOfficialProblem => OfficialProblem.Length > 0;
    public bool ShowOfficialProblem => HasOfficialProblem && UseRecipe;
    public bool OfficialLoading { get; private set; }
    public bool HasOfficial => official is not null && UseRecipe;
    public string OfficialText => official is null ? "" : official.Count == 0
        ? "The installer image has everything the extras depend on."
        : $"The extras also need {official.Count} of Omarchy's and Arch Linux ARM's own packages that the installer image does not carry " +
          $"({official.Sum(p => p.Package.Size) / 1e6:0.0} MB): {string.Join(", ", official.Select(p => p.Package.Name))}. " +
          "They go on the stick too, with their own signatures, which Omarchy checks with its keyring.";

    /// The Extras page's first list: Omarchy's and Arch Linux ARM's own
    /// packages the recipe adds because the installer image lacks them.
    public IReadOnlyList<OfficialItem> OfficialItems => official?
        .OrderBy(p => p.Package.Name, StringComparer.Ordinal)
        .Select(p => new OfficialItem(p.Package.Name, $"{p.Repo.Name}  ·  {p.Package.Version}  ·  {Size(p.Package.Size)}"))
        .ToList() ?? [];
    public bool HasOfficialItems => official is { Count: > 0 };
    public string OfficialStatus =>
        OfficialLoading ? "Working out which of Omarchy's packages the recipe needs…"
        : HasOfficialProblem ? ""
        : official is null ? "Listed once the installer image is known, at the Installer step."
        : official.Count == 0 ? "None: the installer image already has everything the recipe needs from Omarchy."
        : $"Packages Omarchy already publishes, which the installer image does not carry. They come from Omarchy's and Arch Linux ARM's " +
          $"own repositories with their own signatures, and Omarchy checks them with its keyring ({official.Count} packages, {Size(official.Sum(p => p.Package.Size))}).";
    public bool HasOfficialStatus => OfficialStatus.Length > 0;

    /// The Extras page's second list: what comes from outside Omarchy, by
    /// the repository it comes from, each with a link to it.
    public IReadOnlyList<OutsideRepo> OutsideRepos => Recipe is null || RecipeInfo is not { } info ? [] :
    [
        new OutsideRepo(
            Recipe.Repo.Name,
            info.Summary,
            $"Maintained by {(info.Maintainer ?? Recipe.Maintainer).Name}  ·  signed with key " +
                string.Join(" ", Recipe.Repo.KeyFingerprint.Chunk(4).Select(c => new string(c))),
            Link(info.Homepage) ?? Link(Recipe.Repo.ServerFor(PacmanRepo.Arch)),
            info.Packages.Select(p => new OutsidePackage(p.Name, p.Replaces, p.Description, Link(p.Upstream))).ToList(),
            RecipePackages is { } packages ? $"{packages.Count} packages, {Size(packages.Sum(p => p.Size))}" : ""),
    ];
    public string OutsideNote
    {
        get
        {
            var replaced = RecipeInfoPackages.Where(p => p.Replaces).Select(p => p.Name).ToList();
            return (replaced.Count > 0 ? $"They replace Omarchy's own {string.Join(", ", replaced)}. " : "") +
                   "They keep updating from their repository until you remove them with omarchy-recipe remove in a terminal; " +
                   "when the maintainer retires it, once Omarchy has the same, Omarchy's updates say so.";
        }
    }

    static Uri? Link(string? url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps ? uri : null;
    static string Size(long bytes) => bytes >= 1_000_000 ? $"{bytes / 1e6:0.0} MB" : $"{Math.Max(1, bytes / 1000)} kB";

    public string RecipeTitle => RecipeInfo?.Name ?? Recipe?.Name ?? "";
    public string RecipeSummary => RecipeInfo?.Summary ?? "";
    public IReadOnlyList<RecipeInfoPackage> RecipeInfoPackages => RecipeInfo?.Packages ?? [];
    public bool RecipeRetired => RecipeInfo?.IsRetired == true;
    public string RecipeRetiredText => RecipeInfo is { IsRetired: true } info
        ? $"Its maintainer has retired this repository: {info.RetiredMessage ?? "Omarchy has everything it carried."} Install Omarchy as it comes."
        : "";

    public string RecipeDownload => !UseRecipe || RecipePackages is null ? "" :
        $"On the stick: {RecipePackages.Count} packages from outside Omarchy ({Size(RecipePackages.Sum(p => p.Size))})" +
        (official is { Count: > 0 } ? $" and {official.Count} of Omarchy's ({Size(official.Sum(p => p.Package.Size))})" : "") + ".";
    public bool StickTooSmall => FirmwareSize + RecipeSize > StickRoom;

    public async Task LoadRecipeAsync()
    {
        if (Recipe is null || verified is not null) return;
        RecipeProblem = "";
        ChangedAll();
        try
        {
            verified = await PacmanRepo.ReadAsync(Recipe, CancellationToken.None);
            Log.Info($"Recipe {Recipe.Id}: verified, {verified.Description.Status}, " +
                     string.Join(", ", verified.Packages.Select(p => $"{p.FileName} ({p.Size})")));
            official = null;
        }
        catch (Exception e)
        {
            Log.Error($"reading the {Recipe.Repo.Name} repository", e);
            RecipeProblem = e is InvalidDataException ? e.Message
                : $"Could not reach the extras' repository ({e.Message}). Leave them out, or go Back and try again.";
        }
        ChangedAll();
    }

    // ---------- Next steps ----------

    public string SecureBootText => Recipe?.SecureBoot ??
        "Turn Secure Boot off in this computer's firmware settings (often F2 or Del as it starts, or Windows' Advanced startup > UEFI Firmware Settings).";
    public string BootFromUsbText => Recipe?.BootFromUsb ??
        "Start from the stick: choose it in the firmware's boot menu (often F12 as it starts), or put it first in the boot order.";

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
            else await ResolveOfficialAsync();
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
            imageFromRelease = true;
        });
    }

    public void UseLocalImage(string path)
    {
        Log.Info($"Image: {path} ({new FileInfo(path).Length} bytes)");
        ImagePath = path;
        imageFromRelease = false;
        Error = null;
        if (UseRecipe)
            OfficialProblem = "With an ISO file of your own, this tool cannot tell what the image carries, so the extras' other " +
                              "dependencies cannot be put on the stick and the install would fail. Download the installer above, or leave the extras out.";
        ChangedAll();
    }

    /// Works out which official packages the stick must carry for the extras:
    /// what the recipe's packages depend on that the image's offline mirror
    /// (its published package list) and the recipe's repository lack.
    public async Task ResolveOfficialAsync()
    {
        official = null;
        OfficialProblem = "";
        if (Recipe is null || verified is null || release is null) { ChangedAll(); return; }
        if (release.OfflineDbUrl is null)
        {
            OfficialProblem = "This installer release does not say which packages it carries, so the extras cannot be completed " +
                              "offline. Leave the extras out, or wait for a newer installer.";
            ChangedAll();
            return;
        }
        OfficialLoading = true;
        ChangedAll();
        try
        {
            var ct = CancellationToken.None;
            var offline = await OfficialPackages.FetchOfflineDatabaseAsync(release.OfflineDbUrl, ct);
            var dbs = await OfficialPackages.FetchDatabasesAsync(ct);
            var wanted = Recipe.Packages.Concat(verified.Description.Official);
            official = OfficialPackages.Resolve(wanted, verified.Packages, offline, dbs);
            Log.Info($"Official packages for {Recipe.Id} beyond {release.IsoName}: " +
                     (official.Count == 0 ? "none" : string.Join(", ", official.Select(p => $"{p.Repo.Name}/{p.Package.FileName} ({p.Package.Size})"))));
        }
        catch (Exception e)
        {
            Log.Error("working out the extras' official packages", e);
            OfficialProblem = e is InvalidDataException ? e.Message
                : $"Could not read the package repositories ({e.Message}). Leave the extras out, or go Back and try again.";
        }
        OfficialLoading = false;
        ChangedAll();
    }

    /// At the Extras step: the published installer, looked up quietly (the
    /// Installer step says so if there is none), and the official packages
    /// the recipe needs beyond it, so the page can list them.
    async Task PrepareOfficialAsync()
    {
        if (verified is null) return;
        if (release is null && !releaseChecked)
        {
            try { release = await ImageSource.FindLatestAsync(CancellationToken.None); }
            catch (Exception e) { Log.Info($"Release lookup at the Extras step: {e.Message}"); }
        }
        if (release is not null && official is null && !HasOfficialProblem && !OfficialLoading) await ResolveOfficialAsync();
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

    public enum WindowsPlan { Keep, Replace }

    WindowsPlan plan = WindowsPlan.Keep;
    public WindowsPlan Plan { get => plan; set { plan = value; ChangedAll(); } }
    public bool KeepWindows { get => Plan == WindowsPlan.Keep; set { if (value) Plan = WindowsPlan.Keep; } }
    public bool ReplaceWindows { get => Plan == WindowsPlan.Replace; set { if (value) Plan = WindowsPlan.Replace; } }
    /// Windows stays, so Secure Boot going off is something it will notice.
    public bool WindowsStays => Plan != WindowsPlan.Replace;

    public SystemPartition? Partition { get; private set; }
    /// Omarchy's installer wants this much unallocated space for a free-space install.
    const double MinFree = 20e9;
    public bool DiskFreeLow => Partition is null || Partition.DiskFree < MinFree;
    public string DiskFreeText => Partition is null ? "Reading this computer's disk…" : Partition.DiskFree >= MinFree
        ? $"{Partition.DiskFree / 1e9:0} GB of unallocated space on this disk: enough for Omarchy."
        : Partition.DiskFree < 1e9
            ? "No unallocated space on this disk yet. Omarchy needs at least 20 GB."
            : $"Only {Partition.DiskFree / 1e9:0} GB of unallocated space on this disk. Omarchy needs at least 20 GB.";

    public BitLockerState BitLocker { get; private set; } = new(false, []);
    public string RecoveryKeys => string.Join("\n", BitLocker.RecoveryKeys.Select(k => k.Password));
    bool keySaved;
    public bool KeySaved { get => keySaved; set { keySaved = value; ChangedAll(); } }

    public async Task CheckWindowsAsync()
    {
        try
        {
            await CheckDiskAsync();
            BitLocker = await platform.GetBitLockerAsync(CancellationToken.None);
            Log.Info($"BitLocker on C:: {(BitLocker.On ? "on" : "off")}, {BitLocker.RecoveryKeys.Count} recovery key(s)");
        }
        catch (Exception e)
        {
            Fail("reading C: and BitLocker", e);
        }
        ChangedAll();
    }

    /// Reads the disk again, after the user has shrunk C: in Disk Management.
    public async Task CheckDiskAsync()
    {
        try
        {
            Partition = await platform.GetSystemPartitionAsync(CancellationToken.None);
            Log.Info($"C: {Partition.Size / 1e9:0.0} GB; {Partition.DiskFree / 1e9:0.0} GB unallocated on its disk");
        }
        catch (Exception e)
        {
            Fail("reading C: and its disk", e);
        }
        ChangedAll();
    }

    public void OpenDiskManagement()
    {
        Log.Info("Opening Disk Management");
        platform.OpenDiskManagement();
    }

    public string RecoveryKeyFileText =>
        "BitLocker recovery key for this computer's Windows (C:)\n" +
        $"Saved by the Omarchy Arm Installer on {DateTime.Now:yyyy-MM-dd HH:mm}\n\n" +
        string.Join("\n", BitLocker.RecoveryKeys.Select(k => $"Key ID {k.Id}\nRecovery key {k.Password}\n")) +
        "\nWindows asks for this if it starts after the firmware's security settings change.\n" +
        "Keep it somewhere other than this computer and the USB stick.\n";

    // ---------- Create ----------

    public ObservableCollection<LogLine> CreateLog { get; } = [];
    public string Summary => SelectedDisk is null ? "" :
        $"Erase {SelectedDisk.Description} and write {Path.GetFileName(ImagePath)} to it, with this computer's firmware" +
        (UseRecipe && Recipe is not null ? $" and the {Recipe.Name}" : "") + ".\n" +
        Plan switch
        {
            WindowsPlan.Keep => $"Leave Windows as it is; Omarchy installs into the {(Partition?.DiskFree ?? 0) / 1e9:0} GB of unallocated space next to it.",
            _ => "Leave Windows as it is; Omarchy's installer will erase this computer's disk.",
        } +
        (WindowsStays && BitLocker.On ? "\nPause BitLocker for the next 3 starts, while Secure Boot is turned off and Omarchy is installed." : "");

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
            Log.Info($"Creating: disk {disk.Number} ({disk.Model}, {disk.Size} bytes), image {image}, plan {Plan}, BitLocker {(BitLocker.On ? "on" : "off")}, recipe {(UseRecipe ? Recipe?.Id : "none")}");
            // Download first: a failed download must not leave a half-made stick.
            var recipe = UseRecipe ? Recipe : null;
            var recipeFolder = recipe is null ? null : Path.Combine(platform.DownloadFolder, "omarchy-recipes", recipe.Id);
            if (recipe is not null)
            {
                if (verified is null) throw new InvalidOperationException("The extras' repository was not read; go Back to Extras.");
                Note($"Downloading the {recipe.Name}");
                ProgressText = "Downloading and checking the extras";
                await PacmanRepo.DownloadAsync(recipe, verified, Path.Combine(recipeFolder!, "repo"),
                    new Progress<double>(p => Progress = p), ct);
                Done($"{recipe.Name} downloaded and checked");
                if (official is null) throw new InvalidOperationException("The extras' other dependencies were not worked out; go Back to Installer.");
                // Only this run's set goes on the stick: a stale file from an
                // earlier run would be installed too.
                var officialFolder = Path.Combine(recipeFolder!, "official");
                if (Directory.Exists(officialFolder)) Directory.Delete(officialFolder, recursive: true);
                if (official.Count > 0)
                {
                    Note($"Downloading {official.Count} of Omarchy's and Arch's packages the installer image lacks");
                    ProgressText = "Downloading and checking the official packages";
                    await OfficialPackages.DownloadAsync(official, officialFolder, new Progress<double>(p => Progress = p), ct);
                    Done($"{official.Count} official packages downloaded and checked");
                }
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

            Note("Copying this computer's firmware onto the stick");
            var volume = await platform.MountFirmwareVolumeAsync(disk, ct);
            await Task.Run(() => FirmwareCollector.CopyTo(Firmware, volume), ct);
            Done($"{Firmware.Sum(p => p.Found.Count)} firmware files copied to {volume}");

            if (recipe is not null)
            {
                Note($"Copying the {recipe.Name} onto the stick");
                await Task.Run(() => CopyRecipe(recipe, verified!, recipeFolder!, Path.Combine(volume, "recipe")), ct);
                Done($"{recipe.Name} copied; the installer adds them");
            }

            await platform.EjectAsync(disk, ct);
            Done("The stick is ready");
        });
        if (!HasError) Current = Steps.Done;
    }

    /// The stick's recipe folder, as the installer reads it: recipe.json,
    /// key.asc, the repository's signed description, the repository in repo/,
    /// and in official/ the official packages the image lacks.
    static void CopyRecipe(Recipe recipe, VerifiedRepo verified, string downloaded, string destination)
    {
        Directory.CreateDirectory(Path.Combine(destination, "repo"));
        File.WriteAllText(Path.Combine(destination, "recipe.json"), recipe.Json);
        File.WriteAllText(Path.Combine(destination, "key.asc"), recipe.Key);
        File.WriteAllBytes(Path.Combine(destination, "info.json"), verified.Info);
        File.WriteAllBytes(Path.Combine(destination, "info.json.sig"), verified.InfoSignature);
        foreach (var file in Directory.EnumerateFiles(Path.Combine(downloaded, "repo")))
            File.Copy(file, Path.Combine(destination, "repo", Path.GetFileName(file)), overwrite: true);
        var official = Path.Combine(downloaded, "official");
        if (!Directory.Exists(official)) return;
        Directory.CreateDirectory(Path.Combine(destination, "official"));
        foreach (var file in Directory.EnumerateFiles(official))
            File.Copy(file, Path.Combine(destination, "official", Path.GetFileName(file)), overwrite: true);
    }

    void Note(string text) => Lines.Add(new LogLine(text, false));

    void Done(string text)
    {
        if (Lines.Count > 0 && !Lines[^1].Ok) Lines[^1] = new LogLine(text, true);
        else Lines.Add(new LogLine(text, true));
    }

    /// Where Note and Done write: the list on the current step.
    ObservableCollection<LogLine> Lines => IsCollectStep ? ReportLog : CreateLog;

    // ---------- Hardware report ----------

    /// Where reports are sent, as issues with the zip attached.
    public const string ReportRepository = "turbineBMW/omarchy-hardware-reports";

    public ObservableCollection<LogLine> ReportLog { get; } = [];
    HardwareReport? report;
    public HardwareReport? Report { get => report; private set { report = value; ChangedAll(); } }
    public bool HasReport => Report is not null;
    public bool CanCollect => !Busy && Report is null;

    public string ReportFacts => Report is not { } r ? "" : string.Join("\n", new[]
    {
        r.Model,
        r.Identity.Sku is { } sku ? $"SKU {sku}" : null,
        r.Processor,
        r.Panel is { } panel ? $"Panel {panel}" : null,
        $"{r.Chids.Count} hardware IDs, {r.AcpiTables.Count} ACPI tables, {r.DeviceCount} devices" +
            (r.ProblemDevices > 0 ? $" ({r.ProblemDevices} without a driver)" : ""),
        $"{r.FirmwareFiles} firmware files in {r.FirmwarePackages} driver packages, listed by name and hash, and their {r.InfFiles} INF files",
        $"{r.RegistryKeys} registry keys: each device's settings and its driver's, the resources drivers were given, the processors and firmware" +
            (r.HasWindowsFacts ? ", and Windows' view of storage, battery, power states, displays and the driver store" : ""),
    }.Where(l => !string.IsNullOrWhiteSpace(l)));

    public string ReportLeftOut => Report is null ? "" : string.Join("\n", Report.Redactions.Select(r => $"\u00b7 {r}"));
    public string ReportProblems => Report is null ? "" : string.Join("\n", Report.Problems.Select(p => $"\u00b7 {p}"));
    public bool HasReportProblems => Report is { Problems.Count: > 0 };
    public string ReportPath => Report?.ZipPath ?? "";

    public async Task CollectAsync()
    {
        ReportLog.Clear();
        Report = null;
        await RunAsync(async ct =>
        {
            var status = new Progress<string>(text =>
            {
                if (ReportLog.Count > 0 && !ReportLog[^1].Ok) Done(ReportLog[^1].Text);
                Note(text);
            });
            var hardware = await platform.Hardware.CollectAsync(status, ct);
            Done(ReportLog.Count > 0 ? ReportLog[^1].Text : "Read the hardware");
            Note("Writing the report");
            var built = await Task.Run(() => ReportBuilder.Build(hardware, platform.DownloadFolder, Log.Version, null, ct), ct);
            Log.Info($"Report {built.Id}: {built.ZipPath}\n{built.Summary}");
            Done("Report saved");
            Report = built;
        });
    }

    /// A new issue on the reports repository with the form filled in from the
    /// report; the zip itself has to be dropped in by hand.
    public string ReportIssueUrl
    {
        get
        {
            if (Report is not { } r) return "";
            var id = r.Identity;
            var title = $"Hardware report: {r.Model}" + (id.Sku is { } sku ? $" ({sku})" : "");
            var fields = new Dictionary<string, string?>
            {
                ["template"] = "hardware-report.yml",
                ["title"] = title.Length > 120 ? title[..117] + "..." : title,
                ["manufacturer"] = id.Manufacturer,
                ["product"] = id.ProductName,
                ["sku"] = id.Sku,
                ["processor"] = r.Processor,
                ["panel"] = r.Panel,
                ["report"] = $"{r.Id} ({Path.GetFileName(r.ZipPath)})",
                ["chids"] = string.Join("\n", r.Chids.Select(c => $"{c.Type,4} {c.Fields} {c.Value}")),
            };
            return $"https://github.com/{ReportRepository}/issues/new?" + string.Join("&", fields
                .Where(f => !string.IsNullOrWhiteSpace(f.Value))
                .Select(f => $"{f.Key}={Uri.EscapeDataString(f.Value!)}"));
        }
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
            var machine = MachineName;
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

    public async Task BackAsync()
    {
        Error = null;
        if (Step > 0)
        {
            Step--;
        }
        else if (Detected == Detection.Known)
        {
            // The report was chosen from the stick's first step; go back there.
            await StartAsync(report: false);
        }
        else
        {
            flow = [];
            Current = Steps.Start;
        }
    }

    public async Task EnterAsync()
    {
        Log.Info($"Step: {StepTitle}");
        switch (Current)
        {
            case Steps.Machine: await CheckMachineAsync(); break;
            case Steps.Firmware: FindFirmware(); break;
            case Steps.Extras: await LoadRecipeAsync(); await PrepareOfficialAsync(); break;
            case Steps.Installer:
                if (release is null) await FindReleaseAsync();
                else if (UseRecipe && official is null && !HasOfficialProblem && !OfficialLoading) await ResolveOfficialAsync();
                break;
            case Steps.Stick: await ListDisksAsync(); break;
            case Steps.Windows: await CheckWindowsAsync(); break;
        }
    }
}

/// One of Omarchy's or Arch Linux ARM's packages a recipe adds.
public sealed record OfficialItem(string Name, string Detail);

/// A package from outside Omarchy, with where its fix is headed.
public sealed record OutsidePackage(string Name, bool Replaces, string Description, Uri? Upstream)
{
    public bool HasUpstream => Upstream is not null;
}

/// A repository outside Omarchy that a recipe takes packages from.
public sealed record OutsideRepo(string Name, string Summary, string Source, Uri? Link, IReadOnlyList<OutsidePackage> Packages, string Size)
{
    public bool HasLink => Link is not null;
    public bool HasSummary => Summary.Length > 0;
}
