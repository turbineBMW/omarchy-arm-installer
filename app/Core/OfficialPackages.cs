using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace OmarchyArmInstaller.Core;

/// One of Omarchy's or Arch Linux ARM's repositories.
public sealed record OfficialRepo(string Name, string Server);

/// An official package the stick carries for the recipe, and where it comes from.
public sealed record OfficialPackage(OfficialRepo Repo, RepoPackage Package);

/// The official packages a recipe needs that the installer image does not
/// carry. The image's offline mirror has only what every install needs; a
/// recipe's packages depend on more of Omarchy's and Arch's own packages, and
/// nothing on the laptop can fetch them during the install. So the stick
/// carries them, each with its upstream signature, and the installer checks
/// those against the system keyring (Omarchy's and Arch's keys): the recipe's
/// key vouches for nothing but the recipe's own packages. bin/pacmandb.py
/// does the same from Linux.
public static class OfficialPackages
{
    /// In the order pacman.conf has them on an installed Omarchy; the first with a name wins.
    public static readonly OfficialRepo[] Repos =
    [
        new("omarchy", $"https://pkgs.omarchy.org/edge/{PacmanRepo.Arch}"),
        new("core", $"https://arm-mirror.omarchy.org/{PacmanRepo.Arch}/core"),
        new("extra", $"https://arm-mirror.omarchy.org/{PacmanRepo.Arch}/extra"),
        new("alarm", $"https://arm-mirror.omarchy.org/{PacmanRepo.Arch}/alarm"),
    ];

    static readonly HttpClient Http = CreateClient();

    static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("OmarchyArmInstaller", "1.0"));
        return client;
    }

    /// A dependency's name without its version constraint: libssc>=0.4 -> libssc.
    public static string Bare(string dependency)
    {
        var end = dependency.IndexOfAny(['<', '>', '=']);
        return end < 0 ? dependency : dependency[..end];
    }

    static HashSet<string> Provided(IEnumerable<RepoPackage> packages)
    {
        var names = new HashSet<string>();
        foreach (var p in packages)
        {
            names.Add(p.Name);
            foreach (var v in p.Provides) names.Add(Bare(v));
        }
        return names;
    }

    /// Everything the recipe's packages need, directly or not, that neither
    /// the image's offline mirror nor the recipe's repository has, from the
    /// first official repository that has it. A dependency nothing provides
    /// is an InvalidDataException naming it.
    public static List<OfficialPackage> Resolve(IEnumerable<string> wanted, List<RepoPackage> recipeDb,
        List<RepoPackage> offlineDb, IReadOnlyList<(OfficialRepo Repo, List<RepoPackage> Db)> official)
    {
        var have = Provided(offlineDb);
        have.UnionWith(Provided(recipeDb));
        var recipe = recipeDb.ToDictionary(p => p.Name);
        var chosen = new Dictionary<string, OfficialPackage>();
        var seen = new HashSet<string>();
        var queue = new Queue<string>(wanted);
        while (queue.TryDequeue(out var dependency))
        {
            var name = Bare(dependency);
            if (!seen.Add(name)) continue;
            if (recipe.TryGetValue(name, out var own))
            {
                foreach (var d in own.Depends) queue.Enqueue(d);
                continue;
            }
            if (have.Contains(name) || chosen.ContainsKey(name)) continue;
            OfficialPackage? found = null;
            foreach (var (repo, db) in official)
            {
                var package = db.FirstOrDefault(p => p.Name == name)
                    ?? db.FirstOrDefault(p => p.Provides.Any(v => Bare(v) == name));
                if (package is null) continue;
                found = new OfficialPackage(repo, package);
                break;
            }
            if (found is null)
                throw new InvalidDataException($"The extras need {name}, which neither the installer image, their repository, " +
                                               "nor Omarchy's and Arch's repositories provide. Leave the extras out.");
            if (!chosen.TryAdd(found.Package.Name, found)) continue;
            foreach (var v in found.Package.Provides) have.Add(Bare(v));
            foreach (var d in found.Package.Depends) queue.Enqueue(d);
        }
        return chosen.Values.OrderBy(c => c.Package.Name, StringComparer.Ordinal).ToList();
    }

    /// The official repositories' databases, in order.
    public static async Task<List<(OfficialRepo Repo, List<RepoPackage> Db)>> FetchDatabasesAsync(CancellationToken ct)
    {
        var dbs = new List<(OfficialRepo, List<RepoPackage>)>();
        foreach (var repo in Repos)
        {
            using var stream = new MemoryStream(await FetchAsync($"{repo.Server}/{repo.Name}.db", $"{repo.Name} database", ct));
            dbs.Add((repo, PacmanRepo.ReadDatabase(stream)));
        }
        return dbs;
    }

    /// The image's offline mirror database, published beside the ISO.
    public static async Task<List<RepoPackage>> FetchOfflineDatabaseAsync(string url, CancellationToken ct)
    {
        using var stream = new MemoryStream(await FetchAsync(url, "offline mirror database", ct));
        return PacmanRepo.ReadDatabase(stream);
    }

    static async Task<byte[]> FetchAsync(string url, string what, CancellationToken ct)
    {
        using var response = await Http.GetAsync(url, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            throw new InvalidDataException($"No {what} at {url}.");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    /// Writes every package and its upstream signature into <folder>, each
    /// checked against its repository database's SHA-256. The signatures are
    /// for the installer: it checks them with the system keyring.
    public static async Task DownloadAsync(IReadOnlyList<OfficialPackage> packages, string folder,
        IProgress<double> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(folder);
        var total = Math.Max(1, packages.Sum(p => p.Package.Size));
        long done = 0;
        var buffer = new byte[1 << 20];
        foreach (var (repo, package) in packages)
        {
            if (package.FileName.Contains('/') || package.FileName.Contains('\\') || package.FileName.StartsWith('.'))
                throw new InvalidDataException($"The {repo.Name} repository lists an odd file name: {package.FileName}");
            var path = Path.Combine(folder, package.FileName);
            var url = $"{repo.Server}/{Uri.EscapeDataString(package.FileName)}";
            await File.WriteAllBytesAsync(path + ".sig", await FetchAsync(url + ".sig", $"signature for {package.FileName}", ct), ct);
            if (File.Exists(path) && await Sha256Async(path, ct) == package.Sha256)
            {
                done += package.Size;
                progress.Report((double)done / total);
                continue;
            }
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using (var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                await using var input = await response.Content.ReadAsStreamAsync(ct);
                await using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, true);
                int read;
                while ((read = await input.ReadAsync(buffer, ct)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                    hash.AppendData(buffer, 0, read);
                    done += read;
                    progress.Report(Math.Min(1, (double)done / total));
                }
            }
            if (Convert.ToHexStringLower(hash.GetHashAndReset()) != package.Sha256)
            {
                File.Delete(path);
                throw new InvalidDataException($"{package.FileName} from the {repo.Name} repository is damaged. Try again.");
            }
            Log.Info($"Official package {repo.Name}/{package.FileName} ({package.Size} bytes) checked");
        }
    }

    static async Task<string> Sha256Async(string path, CancellationToken ct)
    {
        await using var input = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(input, ct));
    }
}
