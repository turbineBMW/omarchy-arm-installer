using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using SurfaceUsbCreator.Platform;

namespace SurfaceUsbCreator;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            IPlatform platform = OperatingSystem.IsWindows() ? new WindowsPlatform() : new DemoPlatform();
            desktop.MainWindow = new MainWindow(new MainViewModel(platform));
        }
        base.OnFrameworkInitializationCompleted();
    }
}
