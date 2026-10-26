using System.Reflection;
using System.Text.Json;

namespace OmarchyArmInstaller.Core;

/// A model systemd-stub can pick a device tree for (systemd's hwids).
public sealed record KnownModel(string File, string Name, string Compatible, string[] Hwids);

/// The models Linux knows: systemd's hwids list, embedded as models.json.
public static class Models
{
    static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    static readonly Lazy<IReadOnlyList<KnownModel>> all = new(Load);

    public static IReadOnlyList<KnownModel> All => all.Value;

    /// The first model one of whose hardware IDs this machine has.
    public static KnownModel? For(IEnumerable<Guid> chids)
    {
        var set = chids.Select(c => c.ToString()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return All.FirstOrDefault(m => m.Hwids.Any(set.Contains));
    }

    static IReadOnlyList<KnownModel> Load()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("models.json");
        if (stream is null) return [];
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.GetProperty("models").Deserialize<List<KnownModel>>(Options) ?? [];
    }
}
