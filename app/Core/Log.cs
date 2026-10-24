using System.Reflection;
using System.Runtime.InteropServices;

namespace SurfaceUsbCreator.Core;

/// One log file per run, for the "Open log" button and for issue reports:
/// every step, every PowerShell call with its full output, and every error.
public static class Log
{
    static readonly object Gate = new();

    public static string Folder { get; } = OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SurfaceUsbCreator", "Logs")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "state", "surface-usb-creator", "logs");

    public static string FilePath { get; } = System.IO.Path.Combine(Folder, $"creator-{DateTime.Now:yyyyMMdd-HHmmss}.log");

    public static string Version { get; } =
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "?";

    static Log()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            Write($"Surface Pro 11 for Omarchy USB Creator {Version}\n" +
                  $"{RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture}), .NET {Environment.Version}");
        }
        catch
        {
            // A log that cannot be written must not stop the creator.
        }
    }

    public static void Info(string text) => Write(text);

    public static void Block(string title, string text) =>
        Write(text.Length == 0 ? $"{title}: (empty)" : $"{title}:\n{Indent(text.TrimEnd())}");

    public static void Error(string context, Exception e) => Write($"ERROR during {context}:\n{Indent(e.ToString())}");

    static string Indent(string text) => "    " + text.Replace("\n", "\n    ");

    static void Write(string text)
    {
        lock (Gate)
        {
            try
            {
                File.AppendAllText(FilePath, $"[{DateTime.Now:HH:mm:ss.fff}] {text}\n");
            }
            catch
            {
            }
        }
    }
}
