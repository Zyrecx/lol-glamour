using System.Text.Json.Nodes;

namespace Glamour.Core;

/// <summary>One player I can honor. <see cref="Honor"/> is the type currently staged on the ballot for them (upper case), else null.</summary>
public sealed record HonorCandidate(string Puuid, long SummonerId, string Name, int ChampionId, bool Ally, bool IsFriend, string? Honor);

/// <summary>The post-game ballot as the UI needs it. <see cref="Votes"/> is how many honors I may give, <see cref="Staged"/> how many are on the ballot already.</summary>
public sealed record HonorBallotView(long GameId, int Votes, int Staged, IReadOnlyList<HonorCandidate> Candidates)
{
    public static readonly HonorBallotView Empty = new(0, 0, 0, []);

    public bool SameAs(HonorBallotView o) => GameId == o.GameId && Votes == o.Votes && Staged == o.Staged && Candidates.SequenceEqual(o.Candidates);

    /// <summary>The honor types the ballot API accepts. The client only offers HEART; COOL and SHOTCALLER are the hidden ones (INSIGHTS "Post-game").</summary>
    public static readonly string[] Types = ["HEART", "COOL", "SHOTCALLER"];

    /// <summary>A `/lol-honor-v2/v1/ballot` body (null or gameId 0: nothing to vote on) with the friends list `/lol-chat/v1/friends` used only to mark friends and prefer their Riot ID.</summary>
    public static HonorBallotView From(JsonNode? ballot, JsonNode? friends)
    {
        var gameId = ballot?["gameId"] is JsonValue g && g.TryGetValue<long>(out var id) ? id : 0;
        if (gameId == 0 || ballot is null) return Empty;

        var staged = new Dictionary<string, string>();
        if (ballot["honoredPlayers"] is JsonArray honored)
            foreach (var h in honored.OfType<JsonObject>())
                if (Str(h, "recipientPuuid") is { Length: > 0 } p) staged[p] = Str(h, "honorType").ToUpperInvariant();

        var friendNames = new Dictionary<string, string>();
        if (friends is JsonArray fl)
            foreach (var f in fl.OfType<JsonObject>())
                if (Str(f, "puuid") is { Length: > 0 } p) friendNames[p] = Str(f, "gameName") is { Length: > 0 } n ? (Str(f, "gameTag") is { Length: > 0 } t ? $"{n}#{t}" : n) : Str(f, "name");

        var people = Side(ballot["eligibleAllies"], true).Concat(Side(ballot["eligibleOpponents"], false)).ToList();
        var candidates = people.Where(p => p.puuid.Length > 0).Select(p => new HonorCandidate(
            p.puuid, p.summonerId, friendNames.TryGetValue(p.puuid, out var fn) && fn.Length > 0 ? fn : p.name, p.championId, p.ally, friendNames.ContainsKey(p.puuid),
            staged.GetValueOrDefault(p.puuid))).ToList();
        var votes = ballot["votePool"]?["votes"] is JsonValue v && v.TryGetValue<int>(out var n2) ? n2 : 0;
        return new HonorBallotView(gameId, votes, candidates.Count(c => c.Honor is not null), candidates);
    }

    private static IEnumerable<(string puuid, long summonerId, string name, int championId, bool ally)> Side(JsonNode? list, bool ally) =>
        list is JsonArray a
            ? a.OfType<JsonObject>().Select(o => (Str(o, "puuid"), o["summonerId"] is JsonValue s && s.TryGetValue<long>(out var sid) ? sid : 0, Str(o, "summonerName"),
                o["championId"] is JsonValue c && c.TryGetValue<int>(out var cid) ? cid : 0, ally))
            : [];

    private static string Str(JsonObject o, string k) => o[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
}
