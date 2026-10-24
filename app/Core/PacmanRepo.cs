using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace OmarchyArmInstaller.Core;

public sealed record RepoPackage(string Name, string Version, string FileName, long Size, string Sha256);

/// Copies a recipe's pacman repository onto the stick, so the installer can
/// install from it offline: the database and every package in it, each
/// checked against the database's SHA-256.
public static class PacmanRepo
{
    public const string Arch = "aarch64";

    static readonly HttpClient Http = CreateClient();

    static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("OmarchyArmInstaller", "1.0"));
        return client;
    }

    /// The packages a repository database (a gzip'd tar of <pkg>/desc files) lists.
    public static List<RepoPackage> ReadDatabase(Stream gzipped)
    {
        using var gzip = new GZipStream(gzipped, CompressionMode.Decompress);
        using var tar = new TarReader(gzip);
        var packages = new List<RepoPackage>();
        while (tar.GetNextEntry() is { } entry)
        {
            if (!entry.Name.EndsWith("/desc") || entry.DataStream is null) continue;
            using var reader = new StreamReader(entry.DataStream);
            var fields = new Dictionary<string, string>();
            string? key = null;
            while (reader.ReadLine() is { } line)
            {
                if (line.StartsWith('%') && line.EndsWith('%')) key = line.Trim('%');
                else if (key is not null && line.Length > 0 && !fields.ContainsKey(key)) fields[key] = line;
            }
            if (fields.TryGetValue("NAME", out var name) && fields.TryGetValue("FILENAME", out var file) &&
                fields.TryGetValue("SHA256SUM", out var sha))
            {
                packages.Add(new RepoPackage(name, fields.GetValueOrDefault("VERSION", ""), file,
                    long.TryParse(fields.GetValueOrDefault("CSIZE"), out var size) ? size : 0, sha.ToLowerInvariant()));
            }
        }
        return packages;
    }

    /// Fetches the database and lists its packages, to size the download.
    public static async Task<(byte[] Database, List<RepoPackage> Packages)> ReadAsync(RecipeRepo repo, CancellationToken ct)
    {
        var database = await Http.GetByteArrayAsync($"{repo.ServerFor(Arch)}/{repo.Name}.db", ct);
        using var stream = new MemoryStream(database);
        return (database, ReadDatabase(stream));
    }

    /// Writes <folder>/<name>.db and every package into <folder>.
    public static async Task DownloadAsync(RecipeRepo repo, byte[] database, IReadOnlyList<RepoPackage> packages, string folder,
        IProgress<double> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(folder);
        await File.WriteAllBytesAsync(Path.Combine(folder, $"{repo.Name}.db"), database, ct);
        var total = Math.Max(1, packages.Sum(p => p.Size));
        long done = 0;
        var buffer = new byte[1 << 20];
        foreach (var package in packages)
        {
            if (package.FileName.Contains('/') || package.FileName.Contains('\\') || package.FileName.StartsWith('.'))
                throw new InvalidDataException($"The {repo.Name} repository lists an odd file name: {package.FileName}");
            var path = Path.Combine(folder, package.FileName);
            if (File.Exists(path) && await Sha256Async(path, ct) == package.Sha256)
            {
                done += package.Size;
                progress.Report((double)done / total);
                continue;
            }
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using (var response = await Http.GetAsync($"{repo.ServerFor(Arch)}/{Uri.EscapeDataString(package.FileName)}",
                       HttpCompletionOption.ResponseHeadersRead, ct))
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
            var actual = Convert.ToHexStringLower(hash.GetHashAndReset());
            if (actual != package.Sha256)
            {
                File.Delete(path);
                throw new InvalidDataException($"{package.FileName} from the {repo.Name} repository is damaged. Try again.");
            }
            Log.Info($"Recipe package {package.FileName} ({package.Size} bytes) checked");
        }
    }

    static async Task<string> Sha256Async(string path, CancellationToken ct)
    {
        await using var input = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(input, ct));
    }
}
