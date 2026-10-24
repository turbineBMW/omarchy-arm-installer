using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OmarchyArmInstaller.Core;

public sealed record RecipeRepo(string Name, string Server, string KeyFingerprint)
{
    /// The server for one architecture, as pacman expands $arch.
    public string ServerFor(string arch) => Server.Replace("$arch", arch);
}

public sealed record RecipeMaintainer(string Name, string? Github);

public sealed record RecipeFirmware(string Folder, string[] Files);

/// A device recipe (recipes/<id>/recipe.json): add-on packages for one model,
/// from a repository its maintainer signs, offered when the model's CHIDs match.
public sealed record Recipe
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Device { get; init; }
    public string Status { get; init; } = "untested";
    public required RecipeMaintainer Maintainer { get; init; }
    public string Description { get; init; } = "";
    public string[] Features { get; init; } = [];
    public required RecipeMatch Match { get; init; }
    public required RecipeRepo Repo { get; init; }
    public required string[] Packages { get; init; }
    public RecipeFirmware[] Firmware { get; init; } = [];
    public string[] Warnings { get; init; } = [];
    /// How to turn Secure Boot off and start from USB on this model.
    public string? SecureBoot { get; init; }
    public string? BootFromUsb { get; init; }

    /// The repository's public key, shipped with the recipe.
    [JsonIgnore] public string Key { get; init; } = "";
    /// recipe.json as written, which the stick carries for the installer.
    [JsonIgnore] public string Json { get; init; } = "";

    public bool IsTested => Status.Equals("tested", StringComparison.OrdinalIgnoreCase);
}

public sealed record RecipeMatch(string[] Chids);

/// A model systemd-stub can pick a device tree for (systemd's hwids).
public sealed record KnownModel(string File, string Name, string Compatible, string[] Hwids);

public static class Recipes
{
    static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    static readonly Lazy<IReadOnlyList<Recipe>> all = new(Load);
    static readonly Lazy<IReadOnlyList<KnownModel>> models = new(LoadModels);

    public static IReadOnlyList<Recipe> All => all.Value;
    public static IReadOnlyList<KnownModel> Models => models.Value;

    /// The first recipe one of whose CHIDs this machine has.
    public static Recipe? For(IEnumerable<Guid> chids)
    {
        var set = chids.Select(c => c.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return All.FirstOrDefault(r => r.Match.Chids.Any(set.Contains));
    }

    public static KnownModel? ModelFor(IEnumerable<Guid> chids)
    {
        var set = chids.Select(c => c.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Models.FirstOrDefault(m => m.Hwids.Any(set.Contains));
    }

    static IReadOnlyList<Recipe> Load()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var names = assembly.GetManifestResourceNames().Select(n => n.Replace('\\', '/')).ToList();
        var list = new List<Recipe>();
        foreach (var name in names.Where(n => n.StartsWith("recipes/") && n.EndsWith("/recipe.json")).Order())
        {
            var folder = name[..^"recipe.json".Length];
            var json = Read(assembly, name);
            var recipe = JsonSerializer.Deserialize<Recipe>(json, Options)
                ?? throw new InvalidDataException($"{name} is empty");
            var key = names.Contains(folder + "key.asc") ? Read(assembly, folder + "key.asc") : "";
            // Demo only, away from Windows: a local copy of the repository.
            if (!OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable("OMARCHY_DEMO_RECIPE_SERVER") is { } server)
                recipe = recipe with { Repo = recipe.Repo with { Server = server } };
            list.Add(recipe with { Json = json, Key = key });
        }
        return list;
    }

    static IReadOnlyList<KnownModel> LoadModels()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("models.json");
        if (stream is null) return [];
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.GetProperty("models").Deserialize<List<KnownModel>>(Options) ?? [];
    }

    static string Read(Assembly assembly, string name)
    {
        var actual = assembly.GetManifestResourceNames().First(n => n.Replace('\\', '/') == name);
        using var stream = assembly.GetManifestResourceStream(actual)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
