using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace OmarchyArmInstaller.Core;

/// An installer image on GitHub: parts that join into one ISO, and its SHA-256.
public sealed record ReleasedImage(string Tag, string IsoName, IReadOnlyList<(string Name, string Url, long Size)> Parts,
    string ChecksumUrl, string? OfflineDbUrl)
{
    public long Size => Parts.Sum(p => p.Size);
}

/// Finds and downloads the installer from the repository's releases. GitHub
/// limits a release asset to 2 GiB, so the ISO is published in .part-aa,
/// .part-ab, ... pieces beside a .sha256 of the whole image.
public static class ImageSource
{
    public const string Repository = "turbineBMW/omarchy-arm-installer";
    const string TagPrefix = "installer-";

    static readonly HttpClient Http = CreateClient();

    static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("OmarchyArmInstaller", "1.0"));
        return client;
    }

    public static async Task<ReleasedImage?> FindLatestAsync(CancellationToken ct)
    {
        using var response = await Http.GetAsync($"https://api.github.com/repos/{Repository}/releases?per_page=30", ct);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        foreach (var release in document.RootElement.EnumerateArray())
        {
            var tag = release.GetProperty("tag_name").GetString() ?? "";
            if (!tag.StartsWith(TagPrefix) || release.GetProperty("draft").GetBoolean()) continue;

            var assets = release.GetProperty("assets").EnumerateArray()
                .Select(a => (Name: a.GetProperty("name").GetString() ?? "",
                              Url: a.GetProperty("browser_download_url").GetString() ?? "",
                              Size: a.GetProperty("size").GetInt64()))
                .ToList();
            var checksum = assets.FirstOrDefault(a => a.Name.EndsWith(".iso.sha256"));
            if (checksum.Name is null or "") continue;
            var isoName = checksum.Name[..^".sha256".Length];
            var parts = assets.Where(a => a.Name.StartsWith(isoName + ".part-")).OrderBy(a => a.Name, StringComparer.Ordinal).ToList();
            if (parts.Count == 0) continue;
            // The offline mirror's database: what the image installs without the
            // network, so a recipe's other dependencies can go on the stick.
            var offlineDb = assets.FirstOrDefault(a => a.Name.EndsWith(".offline.db"));
            return new ReleasedImage(tag, isoName, parts, checksum.Url, offlineDb.Name is null or "" ? null : offlineDb.Url);
        }
        return null;
    }

    /// Downloads and joins the parts into <destination>, then checks the whole
    /// image against the published SHA-256. A finished, matching file is reused.
    public static async Task DownloadAsync(ReleasedImage image, string destination, IProgress<double> progress, CancellationToken ct)
    {
        var expected = await ReadExpectedChecksumAsync(image.ChecksumUrl, ct);
        if (File.Exists(destination) && new FileInfo(destination).Length == image.Size &&
            await Sha256Async(destination, progress, ct) == expected)
        {
            return;
        }

        var partial = destination + ".download";
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long done = 0;
        var buffer = new byte[1 << 20];
        await using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, true))
        {
            foreach (var part in image.Parts)
            {
                using var response = await Http.GetAsync(part.Url, HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();
                await using var input = await response.Content.ReadAsStreamAsync(ct);
                int read;
                while ((read = await input.ReadAsync(buffer, ct)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                    hash.AppendData(buffer, 0, read);
                    done += read;
                    progress.Report((double)done / image.Size);
                }
            }
        }
        var actual = Convert.ToHexStringLower(hash.GetHashAndReset());
        if (actual != expected)
        {
            File.Delete(partial);
            throw new InvalidDataException($"The download is damaged (SHA-256 {actual}, expected {expected}). Try again.");
        }
        File.Move(partial, destination, overwrite: true);
    }

    static async Task<string> ReadExpectedChecksumAsync(string url, CancellationToken ct)
    {
        var text = await Http.GetStringAsync(url, ct);
        var value = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        if (value.Length != 64) throw new InvalidDataException("The published checksum file is not a SHA-256.");
        return value.ToLowerInvariant();
    }

    public static async Task<string> Sha256Async(string path, IProgress<double> progress, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, true);
        var buffer = new byte[1 << 20];
        long done = 0, size = input.Length;
        int read;
        while ((read = await input.ReadAsync(buffer, ct)) > 0)
        {
            hash.AppendData(buffer, 0, read);
            done += read;
            progress.Report(size == 0 ? 1 : (double)done / size);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}
