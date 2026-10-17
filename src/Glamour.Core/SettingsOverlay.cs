using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Glamour.Core;

/// <summary>
/// Per-account settings: shared defaults with sparse overrides laid over them. An override replaces the whole top-level field
/// (<c>lanes</c> and the honor list are one field each). A JSON <c>null</c> is a real value (auto-ban off for this account), so
/// "inherit again" is an explicit clear, never a null. Keys are camelCase, the same names the HTTP API uses.
/// </summary>
public static class SettingsOverlay
{
    /// <summary>Same options as the HTTP API (camelCase, string enums), so an override key always matches a settings key.</summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    /// <summary>Device or infrastructure settings that are the same for every account, the global automation switch and the Play shortcuts.</summary>
    public static readonly string[] SharedKeys = ["ntfyEnabled", "ntfyServer", "ntfyTopic", "webPushEnabled", "mutedNotifications", "dryRun", "automationEnabled", "pinnedQueueIds"];

    private static readonly HashSet<string> Known =
        JsonSerializer.SerializeToNode(new GlamourSettings(), Json)!.AsObject().Select(p => p.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>The canonical key for a settings field (any casing), or null when there is no such field.</summary>
    public static string? Canonical(string key) => Known.FirstOrDefault(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));

    public static bool IsShared(string key) => SharedKeys.Contains(key, StringComparer.OrdinalIgnoreCase);

    /// <summary>The settings in effect: the defaults with the overrides laid over them. Shared keys in the overrides are ignored.</summary>
    public static GlamourSettings Apply(GlamourSettings defaults, JsonObject? overrides)
    {
        var merged = JsonSerializer.SerializeToNode(defaults, Json)!.AsObject();
        if (overrides is not null)
            foreach (var (key, value) in overrides)
                if (Canonical(key) is { } k && !IsShared(k)) merged[k] = value?.DeepClone();
        return JsonSerializer.Deserialize<GlamourSettings>(merged, Json)!;
    }
}
