using System.Text.Json;
using System.Text.Json.Nodes;

namespace Glamour.Core;

public sealed record ChampSelectAction(int Id, int ActorCellId, string Type, int ChampionId, bool Completed, bool IsInProgress);

/// <summary>One entry of positionSwaps, pickOrderSwaps or trades. Kind is position, pick-order or trade.</summary>
public sealed record ChampSelectSwap(string Kind, int Id, int CellId, string State);

public sealed record ChampSelectTimer(string Phase, long AdjustedTimeLeftInPhase, long InternalNowInEpochMs);

public sealed record ChampSelectSession(int LocalPlayerCellId, ChampSelectTimer Timer, IReadOnlyList<ChampSelectAction> Actions, IReadOnlySet<int>? MyTeamCellIds = null, IReadOnlyList<ChampSelectSwap>? Swaps = null, long LocalSummonerId = 0, string? MyPosition = null, ChampSelectBoard? Board = null, bool SimultaneousPicks = true)
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static ChampSelectSession? FromJson(JsonNode? json)
    {
        if (json is not JsonObject o || o["timer"] is null || o["actions"] is not JsonArray groups) return null;
        var timer = o["timer"].Deserialize<ChampSelectTimer>(Options);
        if (timer is null) return null;
        var actions = groups.OfType<JsonArray>().SelectMany(g => g).Select(a => a.Deserialize<ChampSelectAction>(Options)).OfType<ChampSelectAction>().ToList();
        var team = (o["myTeam"] as JsonArray)?.Select(p => p?["cellId"]?.GetValue<int>()).OfType<int>().ToHashSet();
        var local = o["localPlayerCellId"]?.GetValue<int>() ?? -1;
        var me = (o["myTeam"] as JsonArray)?.FirstOrDefault(p => p?["cellId"]?.GetValue<int>() == local);
        var swaps = ParseAllSwaps(o);
        return new ChampSelectSession(local, timer, actions, team, swaps, me?["summonerId"]?.GetValue<long>() ?? 0, me?["assignedPosition"]?.GetValue<string>(), ChampSelectBoard.FromJson(o, timer, DateTimeOffset.UtcNow),
            o["hasSimultaneousPicks"]?.GetValue<bool>() ?? true); // missing counts as simultaneous: no pick-order swaps then
    }

    public static List<ChampSelectSwap> ParseAllSwaps(JsonObject o) =>
        ParseSwaps(o, "positionSwaps", "position").Concat(ParseSwaps(o, "pickOrderSwaps", "pick-order")).Concat(ParseSwaps(o, "trades", "trade")).ToList();

    private static IEnumerable<ChampSelectSwap> ParseSwaps(JsonObject o, string key, string kind) =>
        (o[key] as JsonArray)?.Select(x => x is JsonObject s && s["id"] is { } id && s["cellId"] is { } cell && s["state"] is { } st
            ? new ChampSelectSwap(kind, id.GetValue<int>(), cell.GetValue<int>(), st.GetValue<string>()) : null).OfType<ChampSelectSwap>() ?? [];

    /// <summary>My assigned lane (TOP, JUNGLE, MIDDLE, BOTTOM, SUPPORT), or null when the mode has none or it is not assigned yet.</summary>
    public string? Lane => LaneNames.FromAssigned(MyPosition);

    /// <summary>True when a teammate (not me) has this champion hovered or locked as their pick.</summary>
    public bool IsHoveredByAlly(int championId) =>
        Actions.Any(a => a.Type == "pick" && a.ChampionId == championId && a.ActorCellId != LocalPlayerCellId
                         && (MyTeamCellIds is null || MyTeamCellIds.Contains(a.ActorCellId)));

    /// <summary>My next pick that is not done yet, live or not (planning shows it before the turn). Null when the mode has no picks.</summary>
    public ChampSelectAction? MyPendingPick => Actions.FirstOrDefault(a => a.ActorCellId == LocalPlayerCellId && a.Type == "pick" && !a.Completed);

    /// <summary>My unfinished action that is currently live, if any.</summary>
    public ChampSelectAction? MyActiveAction => Actions.FirstOrDefault(a => a.ActorCellId == LocalPlayerCellId && a.IsInProgress && !a.Completed);

    /// <summary>Time left in the turn, corrected for how old the snapshot is (LeagueAkari's phase-calibrated delay).</summary>
    public TimeSpan TimeLeft(DateTimeOffset now)
    {
        var ms = Timer.AdjustedTimeLeftInPhase - (now.ToUnixTimeMilliseconds() - Timer.InternalNowInEpochMs);
        return TimeSpan.FromMilliseconds(Math.Max(0, ms));
    }
}
