using System.Text.RegularExpressions;
using OmarchyArmInstaller.Core.Hardware;

namespace OmarchyArmInstaller.Core;

/// One file the stick's OMARCHY_FW partition carries, found in the driver store.
/// Name keeps any subfolder within the package.
public sealed record FirmwareFile(string Folder, string Name, long Size, string SourcePath);

/// A driver package and the firmware the stick takes from it. Missing lists
/// what a recipe needs from it that was not found.
public sealed record FirmwarePackage(string Folder, string Label, IReadOnlyList<FirmwareFile> Found, IReadOnlyList<string> Missing)
{
    public string Details => Missing.Count > 0
        ? $"Missing {string.Join(", ", Missing)}"
        : Found.Count <= 3
            ? string.Join("  ", Found.Select(f => f.Name))
            : $"{Found.Count} files, {Found.Sum(f => f.Size) / 1e6:0.0} MB";

    public bool Complete => Missing.Count == 0 && Found.Count > 0;
}

/// Finds the firmware Linux needs in the Windows DriverStore\FileRepository:
/// every file that is not a driver or Windows plumbing, from the driver
/// packages of the devices this machine has (other than Microsoft's own), plus
/// whatever the machine's recipe names. Files keep their package folder: the
/// installer's qcom-firmware-extract matches each DSP device tree to the
/// firmware beside it.
public static class FirmwareCollector
{
    const long LargestFile = 256L << 20;

    public static IReadOnlyList<FirmwarePackage> Find(string fileRepository, IReadOnlyList<DeviceInfo> devices, Recipe? recipe)
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
            var found = Files(folder, _ => true);
            if (found.Count == 0) continue;
            var label = group.Select(d => d.Name).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)) ?? Path.GetFileName(folder);
            packages[Path.GetFileName(folder)] = new FirmwarePackage(Path.GetFileName(folder), label, found, []);
        }

        // What the recipe needs, from every version of the package Windows has.
        foreach (var need in recipe?.Firmware ?? [])
        {
            var pattern = new Regex("^" + Regex.Escape(need.Folder).Replace(@"\*", ".*") + "$", RegexOptions.IgnoreCase);
            var folders = Directory.EnumerateDirectories(store).Where(f => pattern.IsMatch(Path.GetFileName(f))).ToList();
            var all = need.Files.Contains("*");
            bool Wanted(string name) => all || need.Files.Any(w => w.Equals(Path.GetFileName(name), StringComparison.OrdinalIgnoreCase));
            var seen = new List<FirmwareFile>();
            foreach (var folder in folders)
            {
                var name = Path.GetFileName(folder);
                var found = Files(folder, Wanted);
                seen.AddRange(found);
                if (packages.TryGetValue(name, out var existing))
                    packages[name] = existing with { Found = existing.Found.UnionBy(found, f => f.Name, StringComparer.OrdinalIgnoreCase).ToList() };
                else if (found.Count > 0)
                    packages[name] = new FirmwarePackage(name, name, found, []);
            }
            List<string> missing = all
                ? (seen.Count == 0 ? [need.Folder] : [])
                : need.Files.Where(w => !seen.Any(f => Path.GetFileName(f.Name).Equals(w, StringComparison.OrdinalIgnoreCase))).ToList();
            if (missing.Count > 0)
                packages[need.Folder] = new FirmwarePackage(need.Folder, need.Folder, [], missing);
        }

        return packages.Values.OrderBy(p => p.Complete).ThenBy(p => p.Label, StringComparer.OrdinalIgnoreCase).ToList();
    }

    static bool IsMicrosoft(string? provider) =>
        provider is not null && provider.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase);

    static List<FirmwareFile> Files(string folder, Func<string, bool> wanted)
    {
        var name = Path.GetFileName(folder);
        var list = new List<FirmwareFile>();
        foreach (var path in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            if (ReportBuilder.NotFirmware.Contains(Path.GetExtension(path))) continue;
            var relative = Path.GetRelativePath(folder, path).Replace('\\', '/');
            if (!wanted(relative)) continue;
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
