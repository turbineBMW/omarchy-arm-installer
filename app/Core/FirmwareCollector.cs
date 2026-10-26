using OmarchyArmInstaller.Core.Hardware;

namespace OmarchyArmInstaller.Core;

/// One file the stick's OMARCHY_FW partition carries, found in the driver store.
/// Name keeps any subfolder within the package.
public sealed record FirmwareFile(string Folder, string Name, long Size, string SourcePath);

/// A driver package and the firmware the stick takes from it.
public sealed record FirmwarePackage(string Folder, string Label, IReadOnlyList<FirmwareFile> Found)
{
    public string Details => Found.Count <= 3
        ? string.Join("  ", Found.Select(f => f.Name))
        : $"{Found.Count} files, {Found.Sum(f => f.Size) / 1e6:0.0} MB";

    public bool Complete => Found.Count > 0;
}

/// Finds the firmware Linux needs in the Windows DriverStore\FileRepository:
/// every file that is not a driver or Windows plumbing, from the driver
/// packages of the devices this machine has (other than Microsoft's own).
/// Files keep their package folder: the installer's qcom-firmware-extract
/// matches each DSP device tree to the firmware beside it.
public static class FirmwareCollector
{
    const long LargestFile = 256L << 20;

    public static IReadOnlyList<FirmwarePackage> Find(string fileRepository, IReadOnlyList<DeviceInfo> devices)
    {
        var packages = new Dictionary<string, FirmwarePackage>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(fileRepository)) return [];
        var store = Path.GetFullPath(fileRepository).TrimEnd('\\', '/');

        foreach (var group in devices.Where(d => d.DriverFolder is not null && !IsMicrosoft(d.DriverProvider))
                     .GroupBy(d => d.DriverFolder!, StringComparer.OrdinalIgnoreCase))
        {
            var folder = Path.GetFullPath(group.Key).TrimEnd('\\', '/');
            if (!string.Equals(Path.GetDirectoryName(folder), store, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(folder))
                continue;
            var found = Files(folder);
            if (found.Count == 0) continue;
            var label = group.Select(d => d.Name).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)) ?? Path.GetFileName(folder);
            packages[Path.GetFileName(folder)] = new FirmwarePackage(Path.GetFileName(folder), label, found);
        }

        return packages.Values.OrderBy(p => p.Label, StringComparer.OrdinalIgnoreCase).ToList();
    }

    static bool IsMicrosoft(string? provider) =>
        provider is not null && provider.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase);

    static List<FirmwareFile> Files(string folder)
    {
        var name = Path.GetFileName(folder);
        var list = new List<FirmwareFile>();
        foreach (var path in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            if (ReportBuilder.NotFirmware.Contains(Path.GetExtension(path))) continue;
            var relative = Path.GetRelativePath(folder, path).Replace('\\', '/');
            var size = new FileInfo(path).Length;
            if (size > LargestFile) continue;
            list.Add(new FirmwareFile(name, relative, size, path));
        }
        return list;
    }

    public static long Size(IEnumerable<FirmwarePackage> packages) => packages.SelectMany(p => p.Found).Sum(f => f.Size);

    /// Copies everything found to <destination>\DriverStore\FileRepository.
    public static void CopyTo(IEnumerable<FirmwarePackage> packages, string destination)
    {
        var root = Path.Combine(destination, "DriverStore", "FileRepository");
        foreach (var file in packages.SelectMany(p => p.Found))
        {
            var target = Path.Combine(root, file.Folder, file.Name.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file.SourcePath, target, overwrite: true);
        }
    }
}
