using System.Text.Json.Nodes;

namespace Glamour.Core;

/// <summary>One player in a party. <see cref="Name"/> is the Riot ID, filled by the engine (the gameflow session leaves it blank).</summary>
/// <param name="Lane">The position the queue gave them (`selectedPosition`, one of <see cref="LaneNames.All"/>), "" when the queue has none.
/// Filled in ranked champ select for both teams (one sample; the enemy's never checked against where they played, [UNVERIFIED]).</param>
public sealed record PartyPlayer(string Puuid, int ChampionId, string Name, bool IsMe, string Lane = "");

/// <summary>Two or more players queued together, on my team or the enemy's.</summary>
public sealed record GameParty(bool Ally, IReadOnlyList<PartyPlayer> Players);

/// <summary>
/// Who queued with whom in the running game, from <c>gameData.teamOne/teamTwo[].teamParticipantId</c> of <c>/lol-gameflow/v1/session</c>:
/// players who share one queued together (LIVE: my duo and I shared one in ranked, every stranger had their own; INSIGHTS "Parties").
/// Champ select has the same groups in the session already (LIVE, ranked, enemies included), so they show from there, but with no names
/// and no champions: the client hides those there, and Glamour does not look them up (SPEC "Parties"); each player's queue position comes along.
/// The gameflow's own `championId` is NOT used in champ select: it disagreed with the champ select session in the one sample. From GameStart on, names too.
/// </summary>
public sealed record GameParties(long GameId, IReadOnlyList<GameParty> Parties)
{
    public static readonly GameParties Empty = new(0, []);

    public static readonly HashSet<string> InGamePhases = ["GameStart", "InProgress", "Reconnect", "WaitingForStats", "PreEndOfGame", "EndOfGame"];

    /// <summary>Phases with parties: champ select (groups only) and the game.</summary>
    public static bool Shown(string phase) => phase == "ChampSelect" || InGamePhases.Contains(phase);

    public bool SameAs(GameParties o) =>
        GameId == o.GameId && Parties.Count == o.Parties.Count
        && Parties.Zip(o.Parties).All(p => p.First.Ally == p.Second.Ally && p.First.Players.SequenceEqual(p.Second.Players));

    /// <summary>The parties in <paramref name="session"/>, names blank (and champions 0 in champ select); null outside champ select and the game, before the teams
    /// are filled, and in champ select off Summoner's Rift.</summary>
    public static GameParties? From(JsonNode? session, string myPuuid)
    {
        if (session?["phase"]?.GetValue<string>() is not { } phase || !Shown(phase)) return null;
        var picking = phase == "ChampSelect";
        var data = session["gameData"];
        var gameId = data?["gameId"] is JsonValue g && g.TryGetValue<long>(out var id) ? id : 0;
        var one = Players(data?["teamOne"]);
        var two = Players(data?["teamTwo"]);
        if (gameId == 0 || one.Count + two.Count == 0) return null;
        // Custom games reuse the field for lobby slots (LIVE: five bots shared one id, my team split 2+3 with no duo), so no parties there.
        var custom = data?["isCustomGame"] is JsonValue c && c.TryGetValue<bool>(out var isCustom) && isCustom
            || data?["queue"]?["id"] is JsonValue q && q.TryGetValue<int>(out var queueId) && queueId == 3100;
        if (custom) return new GameParties(gameId, []);
        // Off Summoner's Rift the champ select groups are not the parties (LIVE, ARAM: an ally 4-stack and an enemy duo there, an ally duo and an enemy
        // 3-stack at game start), so none until the game; and the queue still sends each player's last position (ARAM Mayhem): no lanes there.
        if (data?["queue"]?["mapId"] is JsonValue m && m.TryGetValue<int>(out var mapId) && mapId != 11)
        {
            if (picking) return null;
            (one, two) = (one.Select(p => p with { Lane = "" }).ToList(), two.Select(p => p with { Lane = "" }).ToList());
        }
        var mineIsTwo = two.Any(p => p.Puuid == myPuuid);
        var parties = new List<GameParty>();
        foreach (var (team, ally) in new[] { (one, !mineIsTwo), (two, mineIsTwo) })
            foreach (var group in team.Where(p => p.Tp > 0).GroupBy(p => p.Tp).Where(g => g.Count() > 1).OrderBy(g => g.Key))
                parties.Add(new GameParty(ally, group.Select(p => new PartyPlayer(p.Puuid, picking ? 0 : p.Champ, "", p.Puuid == myPuuid, p.Lane)).ToList()));
        // My party first, then the rest of my team's, then the enemy's.
        return new GameParties(gameId, parties.OrderByDescending(p => p.Players.Any(x => x.IsMe)).ThenByDescending(p => p.Ally).ToList());
    }

    private static List<(string Puuid, int Champ, long Tp, string Lane)> Players(JsonNode? team) =>
        (team as JsonArray)?.Select(p => (
            p?["puuid"]?.GetValue<string>() ?? "",
            p?["championId"] is JsonValue c && c.TryGetValue<int>(out var champ) ? champ : 0,
            p?["teamParticipantId"] is JsonValue t && t.TryGetValue<long>(out var tp) ? tp : 0,
            LaneNames.FromAssigned(p?["selectedPosition"] is JsonValue sp && sp.TryGetValue<string>(out var pos) ? pos : null) ?? ""))
        .Where(p => p.Item1 != "").ToList() ?? [];
}
