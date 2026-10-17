using System.Text.Json.Nodes;

namespace Glamour.Core;

/// <summary>
/// One person in my lobby. The lobby payload carries no readable name (summonerName is empty live, INSIGHTS "Lobby"), so the UI resolves it
/// from <see cref="SummonerId"/> through the host. Positions are the client's words (TOP, JUNGLE, MIDDLE, BOTTOM, UTILITY, FILL, UNSELECTED or empty).
/// </summary>
public sealed record LobbyMember(string Puuid, long SummonerId, bool IsMe, bool IsLeader, bool IsBot, bool Ready, int IconId, int Level, string First, string Second)
{
    /// <summary>Members of a `/lol-lobby/v2/lobby` body; the local member is found by puuid. Empty when there is no lobby.</summary>
    public static IReadOnlyList<LobbyMember> FromLobby(JsonNode? lobby)
    {
        if (lobby?["members"] is not JsonArray members) return [];
        var me = lobby["localMember"]?["puuid"]?.GetValue<string>() ?? "";
        return members.OfType<JsonObject>().Select(m =>
        {
            var puuid = Str(m, "puuid");
            return new LobbyMember(puuid, Long(m, "summonerId"), me.Length > 0 && puuid == me, Bool(m, "isLeader"), Bool(m, "isBot"), Bool(m, "ready"),
                (int)Long(m, "summonerIconId"), (int)Long(m, "summonerLevel"), Str(m, "firstPositionPreference"), Str(m, "secondPositionPreference"));
        }).ToList();
    }

    private static string Str(JsonObject o, string k) => o[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
    private static long Long(JsonObject o, string k) => o[k] is JsonValue v && v.TryGetValue<long>(out var l) ? l : 0;
    private static bool Bool(JsonObject o, string k) => o[k] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
}
