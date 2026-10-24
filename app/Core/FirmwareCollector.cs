using System.Reflection;
using System.Text.RegularExpressions;

namespace SurfaceUsbCreator.Core;

/// One file the stick's OMARCHY_FW partition carries, found in the driver store.
public sealed record FirmwareFile(string Folder, string Name, long Size, string SourcePath);

/// A driver package the list asks for, and what was found for it.
public sealed record FirmwarePackage(string Pattern, IReadOnlyList<string> Wanted, IReadOnlyList<FirmwareFile> Found)
{
    static readonly (string Prefix, string Label)[] Labels =
    [
        ("surfacepro_ext_adsp", "Audio DSP: sound, sensors and the battery gauge"),
        ("qcnspmcdm_ext_cdsp", "Compute DSP"),
        ("qcdx", "Graphics: the GPU's security shader"),
        ("surfacepro_snscfgcrd", "Sensor calibration"),
    ];

    public string Label => Labels.FirstOrDefault(l => Pattern.StartsWith(l.Prefix, StringComparison.OrdinalIgnoreCase)).Label ?? Pattern;

    public string Details => Found.Count == 0
        ? $"Not found ({Pattern})"
        : Found.Count <= 3
            ? string.Join("  ", Found.Select(f => f.Name))
            : $"{Found.Count} files, {Found.Sum(f => f.Size) / 1e3:0} KB";

    public bool Complete => Wanted.Contains("*")
        ? Found.Count > 0
        : Wanted.All(w => Found.Any(f => f.Name.Equals(w, StringComparison.OrdinalIgnoreCase)));
}

/// Reads iso/firmware-files.txt (embedded) and finds those files in a Windows
/// DriverStore\FileRepository, keeping each file in its package folder: the
/// installer's qcom-firmware-extract matches each DSP device tree to the
/// firmware beside it.
public static class FirmwareCollector
{
    public static IReadOnlyList<(string Pattern, string[] Files)> ReadList()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("firmware-files.txt")
            ?? throw new InvalidOperationException("firmware-files.txt is not embedded");
        using var reader = new StreamReader(stream);
        var list = new List<(string, string[])>();
        while (reader.ReadLine() is { } line)
        {
            line = line.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            list.Add((fields[0], fields[1..]));
        }
        return list;
    }

    public static IReadOnlyList<FirmwarePackage> Find(string fileRepository)
    {
        var packages = new List<FirmwarePackage>();
        foreach (var (pattern, wanted) in ReadList())
        {
            var found = new List<FirmwareFile>();
            if (Directory.Exists(fileRepository))
            {
                var folderPattern = new Regex("^" + Regex.Escape(pattern).Replace(@"\*", ".*") + "$", RegexOptions.IgnoreCase);
                foreach (var folder in Directory.EnumerateDirectories(fileRepository))
                {
                    var folderName = Path.GetFileName(folder);
                    if (!folderPattern.IsMatch(folderName)) continue;
                    foreach (var file in Directory.EnumerateFiles(folder))
                    {
                        var name = Path.GetFileName(file);
                        if (!wanted.Contains("*") && !wanted.Any(w => w.Equals(name, StringComparison.OrdinalIgnoreCase))) continue;
                        found.Add(new FirmwareFile(folderName, name, new FileInfo(file).Length, file));
                    }
                }
            }
            packages.Add(new FirmwarePackage(pattern, wanted, found));
        }
        return packages;
    }

    /// Copies everything found to <destination>\DriverStore\FileRepository.
    public static void CopyTo(IEnumerable<FirmwarePackage> packages, string destination)
    {
        var root = Path.Combine(destination, "DriverStore", "FileRepository");
        foreach (var file in packages.SelectMany(p => p.Found))
        {
            var folder = Path.Combine(root, file.Folder);
            Directory.CreateDirectory(folder);
            File.Copy(file.SourcePath, Path.Combine(folder, file.Name), overwrite: true);
        }
    }
}
