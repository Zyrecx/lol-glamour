using System.Text.Json.Nodes;

namespace Glamour.Core;

/// <summary>A queue the user can start a lobby for. <see cref="Name"/> is what the client shows (Draft Pick, not its internal "Normal").</summary>
public sealed record PlayableQueue(int Id, string Name, int MapId, string MapName, string GameMode, string Category, bool IsRanked, bool ShowPositions);

/// <summary>The queue list for the phone's mode picker, shaped from the client's own lists so no mode is hardcoded (INSIGHTS "Lobby, queue and game modes").</summary>
public static class Queues
{
    /// <summary>
    /// `GET /lol-game-queues/v1/queues` filtered to queues the client itself offers: `queueAvailability` Available and `isVisible` not false
    /// (Clash, the tutorials and blank placeholder entries are Available but not visible), not custom, PvP or vs AI.
    /// With <paramref name="all"/> every queue the client lists comes back (advanced: the client may refuse to start some).
    /// Map names from `GET /lol-maps/v2/maps` when given (entries `{id, name}`, [UNVERIFIED] shape; without it the name is empty).
    /// Ranked first, then PvP, then vs AI, then by id.
    /// </summary>
    public static List<PlayableQueue> Playable(JsonNode? queues, JsonNode? maps, bool all = false)
    {
        var mapNames = new Dictionary<int, string>();
        if (maps is JsonArray ma)
            foreach (var m in ma.OfType<JsonObject>())
                if (Int(m, "id") is var id and > 0 && Str(m, "name") is { Length: > 0 } n) mapNames.TryAdd(id, n);

        return (queues as JsonArray)?.OfType<JsonObject>()
            .Where(q => all ? Int(q, "id") > 0 : Str(q, "queueAvailability") == "Available" && q["isVisible"]?.GetValue<bool>() != false && !Bool(q, "isCustom") && Str(q, "category") is "PvP" or "VersusAi")
            .Select(q => new PlayableQueue(Int(q, "id"), First(Str(q, "description"), Str(q, "name"), Str(q, "shortName"), $"Queue {Int(q, "id")}"),
                Int(q, "mapId"), mapNames.GetValueOrDefault(Int(q, "mapId"), ""), Str(q, "gameMode"), Str(q, "category"), Bool(q, "isRanked"), Bool(q, "showPositionSelector")))
            .OrderByDescending(q => q.IsRanked).ThenBy(q => q.Category == "PvP" ? 0 : 1).ThenBy(q => q.Id)
            .ToList() ?? [];
    }

    private static string First(params string[] values) => values.First(v => v.Length > 0);
    private static int Int(JsonObject o, string k) => o[k] is JsonValue v && v.TryGetValue<int>(out var i) ? i : 0;
    private static bool Bool(JsonObject o, string k) => o[k] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
    private static string Str(JsonObject o, string k) => o[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
}
