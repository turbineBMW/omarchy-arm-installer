using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace SurfaceUsbCreator;

public partial class MainWindow : Window
{
    readonly MainViewModel model;

    public MainWindow() : this(new MainViewModel(new Platform.DemoPlatform())) { }

    public MainWindow(MainViewModel model)
    {
        InitializeComponent();
        DataContext = this.model = model;
        Opened += async (_, _) =>
        {
            // Demo only: start on a later step, for screenshots of each one.
            // SURFACE_CREATOR_MODE is install or report; STEP counts within it.
            var mode = model.IsDemo ? Environment.GetEnvironmentVariable("SURFACE_CREATOR_MODE") : null;
            if (mode is null) return;
            await model.StartAsync(mode == "report");
            if (int.TryParse(Environment.GetEnvironmentVariable("SURFACE_CREATOR_STEP"), out var step) && step > 0)
            {
                if (Environment.GetEnvironmentVariable("SURFACE_CREATOR_IMAGE") is { } image) model.UseLocalImage(image);
                if (Enum.TryParse<MainViewModel.WindowsPlan>(Environment.GetEnvironmentVariable("SURFACE_CREATOR_PLAN"), out var plan))
                    model.Plan = plan;
                if (model.IsReport) await model.CollectAsync();
                for (var i = 1; i < step; i++) { model.Step = i; await model.EnterAsync(); }
                model.Step = step;
                model.Error = null;
                await model.EnterAsync();
            }
            if (model.IsCreateStep && Environment.GetEnvironmentVariable("SURFACE_CREATOR_CREATE") == "1")
                await model.CreateAsync();
            if (model.IsCollectStep && Environment.GetEnvironmentVariable("SURFACE_CREATOR_CREATE") == "1")
                await model.CollectAsync();
        };
    }

    async void StartInstall(object? sender, RoutedEventArgs e) => await model.StartAsync(report: false);
    async void StartReport(object? sender, RoutedEventArgs e) => await model.StartAsync(report: true);
    async void Collect(object? sender, RoutedEventArgs e) => await model.CollectAsync();
    async void OpenReportForm(object? sender, RoutedEventArgs e) => await Launcher.LaunchUriAsync(new Uri(model.ReportIssueUrl));

    async void ShowReport(object? sender, RoutedEventArgs e)
    {
        if (OperatingSystem.IsWindows())
            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{model.ReportPath}\"");
        else
            await Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(Path.GetDirectoryName(model.ReportPath)!));
    }

    async void Next(object? sender, RoutedEventArgs e) => await model.NextAsync();
    void Back(object? sender, RoutedEventArgs e) => model.Back();
    async void Download(object? sender, RoutedEventArgs e) => await model.DownloadAsync();
    async void Refresh(object? sender, RoutedEventArgs e) => await model.ListDisksAsync();
    async void Create(object? sender, RoutedEventArgs e) => await model.CreateAsync();
    void Cancel(object? sender, RoutedEventArgs e) => model.Cancel();
    void Close(object? sender, RoutedEventArgs e) => Close();

    async void OpenLog(object? sender, RoutedEventArgs e) => await Launcher.LaunchFileInfoAsync(new FileInfo(model.LogPath));

    async void ReportProblem(object? sender, RoutedEventArgs e) => await Launcher.LaunchUriAsync(new Uri(model.IssueUrl));

    async void ChooseIso(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose the Omarchy installer ISO",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Installer image") { Patterns = ["*.iso"] }],
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path) model.UseLocalImage(path);
    }

    async void SaveKey(object? sender, RoutedEventArgs e)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save the BitLocker recovery key",
            SuggestedFileName = "BitLocker recovery key.txt",
            DefaultExtension = "txt",
        });
        if (file is null) return;
        await using var stream = await file.OpenWriteAsync();
        await using var writer = new StreamWriter(stream);
        await writer.WriteAsync(model.RecoveryKeyFileText);
    }
}
