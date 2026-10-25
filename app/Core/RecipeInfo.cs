using System.Text.Json;

namespace OmarchyArmInstaller.Core;

public sealed record RecipeInfoPackage(string Name, bool Replaces, string Description, string? Upstream);

/// A recipe repository's own description of itself (<repo>.recipe.json,
/// signed with the repository key): what each package does, which of
/// Omarchy's it replaces, and whether the repository is still needed.
public sealed record RecipeInfo
{
    public int Format { get; init; }
    public required string Repo { get; init; }
    public required string Name { get; init; }
    public string Summary { get; init; } = "";
    public RecipeMaintainer? Maintainer { get; init; }
    public string? Homepage { get; init; }
    public string Status { get; init; } = "active";
    public string? RetiredMessage { get; init; }
    public RecipeInfoPackage[] Packages { get; init; } = [];
    /// Official packages the recipe wants on the stick beyond what its packages depend on.
    public string[] Official { get; init; } = [];

    public bool IsRetired => Status.Equals("retired", StringComparison.OrdinalIgnoreCase);

    static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static RecipeInfo Parse(byte[] json, string repo)
    {
        var info = JsonSerializer.Deserialize<RecipeInfo>(json, Options)
            ?? throw new InvalidDataException("The repository's description is empty.");
        if (info.Format != 1) throw new InvalidDataException($"The repository's description is format {info.Format}, which this installer does not know.");
        if (info.Repo != repo) throw new InvalidDataException($"The description is for the {info.Repo} repository, not {repo}.");
        return info;
    }
}
