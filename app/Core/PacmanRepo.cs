using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace OmarchyArmInstaller.Core;

public sealed record RepoPackage(string Name, string Version, string FileName, long Size, string Sha256);

/// A recipe's repository as read and checked: its database and description,
/// each with a signature by the key the recipe pins.
public sealed record VerifiedRepo(byte[] Database, byte[] DatabaseSignature, byte[] Info, byte[] InfoSignature,
    RecipeInfo Description, List<RepoPackage> Packages);

/// Copies a recipe's pacman repository onto the stick, so the installer can
/// install from it offline: the database and every package in it, each
/// checked against the database's SHA-256. The database, the repository's
/// description and every package are trusted only with a signature by the
/// recipe's key; the signatures go on the stick, where pacman checks them again.
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

    /// Fetches and checks the database and the repository's description.
    public static async Task<VerifiedRepo> ReadAsync(Recipe recipe, CancellationToken ct)
    {
        var repo = recipe.Repo;
        var server = repo.ServerFor(Arch);
        var database = await FetchAsync($"{server}/{repo.Name}.db", "package database", ct);
        var databaseSignature = await FetchAsync($"{server}/{repo.Name}.db.sig", "package database's signature", ct);
        if (!OpenPgp.Verify(database, databaseSignature, recipe.Key, repo.KeyFingerprint))
            throw new InvalidDataException($"The {repo.Name} package database is not signed by the key its recipe trusts. Leave the extras out.");
        var info = await FetchAsync($"{server}/{repo.Name}.recipe.json", "description", ct);
        var infoSignature = await FetchAsync($"{server}/{repo.Name}.recipe.json.sig", "description's signature", ct);
        if (!OpenPgp.Verify(info, infoSignature, recipe.Key, repo.KeyFingerprint))
            throw new InvalidDataException($"The {repo.Name} repository's description is not signed by the key its recipe trusts. Leave the extras out.");

        using var stream = new MemoryStream(database);
        var packages = ReadDatabase(stream);
        if (recipe.Packages.FirstOrDefault(p => packages.All(r => r.Name != p)) is { } absent)
            throw new InvalidDataException($"The {repo.Name} repository has no {absent}, which its recipe installs.");
        return new VerifiedRepo(database, databaseSignature, info, infoSignature, RecipeInfo.Parse(info, repo.Name), packages);
    }

    static async Task<byte[]> FetchAsync(string url, string what, CancellationToken ct)
    {
        using var response = await Http.GetAsync(url, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            throw new InvalidDataException($"The extras' repository has no {what} ({url}).");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    /// Writes <folder>/<name>.db and every package, each with its signature, into <folder>.
    public static async Task DownloadAsync(Recipe recipe, VerifiedRepo verified, string folder,
        IProgress<double> progress, CancellationToken ct)
    {
        var repo = recipe.Repo;
        var packages = verified.Packages;
        Directory.CreateDirectory(folder);
        await File.WriteAllBytesAsync(Path.Combine(folder, $"{repo.Name}.db"), verified.Database, ct);
        await File.WriteAllBytesAsync(Path.Combine(folder, $"{repo.Name}.db.sig"), verified.DatabaseSignature, ct);
        var total = Math.Max(1, packages.Sum(p => p.Size));
        long done = 0;
        var buffer = new byte[1 << 20];
        foreach (var package in packages)
        {
            if (package.FileName.Contains('/') || package.FileName.Contains('\\') || package.FileName.StartsWith('.'))
                throw new InvalidDataException($"The {repo.Name} repository lists an odd file name: {package.FileName}");
            var path = Path.Combine(folder, package.FileName);
            var signature = await FetchAsync($"{repo.ServerFor(Arch)}/{Uri.EscapeDataString(package.FileName)}.sig",
                $"signature for {package.FileName}", ct);
            await File.WriteAllBytesAsync(path + ".sig", signature, ct);
            if (File.Exists(path) && await Sha256Async(path, ct) == package.Sha256 && await SignedAsync(path, signature, recipe))
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
            if (!await SignedAsync(path, signature, recipe))
            {
                File.Delete(path);
                throw new InvalidDataException($"{package.FileName} is not signed by the key the recipe trusts. Leave the extras out.");
            }
            Log.Info($"Recipe package {package.FileName} ({package.Size} bytes) checked");
        }
    }

    static Task<bool> SignedAsync(string path, byte[] signature, Recipe recipe) => Task.Run(() =>
    {
        using var input = File.OpenRead(path);
        return OpenPgp.Verify(input, signature, recipe.Key, recipe.Repo.KeyFingerprint);
    });

    static async Task<string> Sha256Async(string path, CancellationToken ct)
    {
        await using var input = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(input, ct));
    }
}
