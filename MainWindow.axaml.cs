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
            if (model.IsDemo && int.TryParse(Environment.GetEnvironmentVariable("SURFACE_CREATOR_STEP"), out var step))
            {
                for (var i = 0; i < step; i++) { model.Step = i; await model.EnterAsync(); }
                model.Step = step;
            }
            await model.EnterAsync();
        };
    }

    async void Next(object? sender, RoutedEventArgs e) => await model.NextAsync();
    void Back(object? sender, RoutedEventArgs e) => model.Back();
    async void Download(object? sender, RoutedEventArgs e) => await model.DownloadAsync();
    async void Refresh(object? sender, RoutedEventArgs e) => await model.ListDisksAsync();
    async void Create(object? sender, RoutedEventArgs e) => await model.CreateAsync();
    void Cancel(object? sender, RoutedEventArgs e) => model.Cancel();
    void Close(object? sender, RoutedEventArgs e) => Close();

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
