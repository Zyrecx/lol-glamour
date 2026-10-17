using System.Text.Json.Nodes;

namespace Glamour.Core;

/// <summary>
/// One player on the board. Champion is the locked pick, else the hover (ally `championPickIntent`, or the champion on the cell's own pick action).
/// Name is empty when the client hides it (enemies always, allies in some queues): the UI shows a placeholder.
/// PickTurn is the 1-based pick turn the player picks in (two players share a turn in draft), 0 when the mode has no pick for them.
/// </summary>
public sealed record BoardPlayer(int CellId, int ChampionId, bool Locked, string Lane, int Spell1Id, int Spell2Id, int SkinId, string Name, bool IsMe, int PickTurn = 0);

/// <summary>A live ban or pick turn. In draft all ten bans run at once, so several can be live together.</summary>
public sealed record BoardTurn(int CellId, string Type);

/// <summary>A completed ban and the cell that made it. Mine is the ban of my own team (the actor's cell is in my team).</summary>
public sealed record BoardBan(int CellId, int ChampionId, bool Mine);

/// <summary>
/// Who made each ban, by player rather than by cell. A ban action keeps the cell it was made from, but a pick-order swap moves players between cells
/// (LIVE: after one the panel said I banned the ban of the cell I moved into). So the first time a ban shows completed, the player in its cell is
/// remembered (puuid, else obfuscated puuid, else summoner id: they travel with the player), and later boards credit the cell that player sits in now.
/// A player with no readable id (an enemy whose ids are hidden) keeps the ban's own cell, which may be wrong after a swap on their side.
/// One instance per champ select.
/// </summary>
public sealed class BanOwners
{
    private readonly Dictionary<int, string> _byAction = [];

    public ChampSelectBoard Follow(ChampSelectBoard board, JsonObject session)
    {
        if (board.BanList is not { Count: > 0 }) return board;
        var who = new Dictionary<int, string>();
        foreach (var team in new[] { session["myTeam"], session["theirTeam"] })
            foreach (var p in (team as JsonArray)?.OfType<JsonObject>() ?? [])
                if (Identity(p) is { } id) who[p["cellId"]?.GetValue<int>() ?? -1] = id;
        var cellOf = who.GroupBy(w => w.Value).Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single().Key);
        var myCells = board.MyTeam.Select(p => p.CellId).ToHashSet();
        var bans = (session["actions"] as JsonArray)?.OfType<JsonArray>().SelectMany(g => g).OfType<JsonObject>()
            .Where(a => a["type"]?.GetValue<string>() == "ban" && a["completed"]?.GetValue<bool>() == true && (a["championId"]?.GetValue<int>() ?? 0) > 0)
            .OrderBy(a => a["id"]?.GetValue<int>() ?? 0).ToList() ?? [];
        var list = new List<BoardBan>();
        foreach (var a in bans)
        {
            var id = a["id"]?.GetValue<int>() ?? 0;
            var actor = a["actorCellId"]?.GetValue<int>() ?? -1;
            if (!_byAction.ContainsKey(id) && who.TryGetValue(actor, out var made)) _byAction[id] = made;
            var cell = _byAction.TryGetValue(id, out var player) && cellOf.TryGetValue(player, out var now) ? now : actor;
            list.Add(new BoardBan(cell, a["championId"]!.GetValue<int>(), myCells.Contains(cell)));
        }
        return board with { BanList = list };
    }

    private static string? Identity(JsonObject p)
    {
        var puuid = p["puuid"]?.GetValue<string>() ?? "";
        if (puuid.Trim('0', '-').Length > 0) return "p" + puuid;
        if (p["obfuscatedPuuid"]?.GetValue<string>() is { Length: > 0 } o) return "o" + o;
        return (p["summonerId"]?.GetValue<long>() ?? 0) is > 0 and var s ? $"s{s}" : null;
    }
}

/// <summary>What kind of champ select this is, so the UI shows only what applies. Mayhem is queue 2400 (gameMode KIWI); every other bench queue (ARAM 450, ARURF 900) is plain ARAM.</summary>
public static class ChampSelectMode
{
    public const string Draft = "draft", Aram = "aram", Mayhem = "mayhem", Other = "other";

    public static string From(int queueId, bool benchEnabled, bool hasPickOrBanActions) =>
        queueId == 2400 ? Mayhem : benchEnabled ? Aram : hasPickOrBanActions ? Draft : Other;
}

/// <summary>
/// Everything the champ select screen shows: both teams, bans, who is acting. Built from the session JSON; the countdown
/// (<see cref="TimeLeftMs"/>) is deliberately not part of <see cref="SameAs"/>, so timer ticks never republish it.
/// <see cref="Next"/> is who acts after the current turn; <see cref="PickTurnNow"/> the pick turn being played (0 before the picks start).
/// </summary>
public sealed record ChampSelectBoard(string Stage, int MyCellId, IReadOnlyList<BoardPlayer> MyTeam, IReadOnlyList<BoardPlayer> TheirTeam,
    IReadOnlyList<int> MyBans, IReadOnlyList<int> TheirBans, IReadOnlyList<BoardTurn> Acting, long TimeLeftMs, int QueueId = 0,
    string Mode = ChampSelectMode.Draft, IReadOnlyList<int>? Bench = null, int Rerolls = 0, bool AllowRerolling = false, IReadOnlyList<ChampSelectSwap>? Swaps = null,
    IReadOnlyList<int>? Offered = null, IReadOnlyList<BoardTurn>? Next = null, int PickTurnNow = 0, int MyPickActionId = 0, IReadOnlyList<BoardBan>? BanList = null,
    IReadOnlyList<int>? BanCells = null)
{
    public bool SameAs(ChampSelectBoard? o) =>
        o is not null && Stage == o.Stage && QueueId == o.QueueId && MyCellId == o.MyCellId && MyTeam.SequenceEqual(o.MyTeam) && TheirTeam.SequenceEqual(o.TheirTeam)
        && MyBans.SequenceEqual(o.MyBans) && TheirBans.SequenceEqual(o.TheirBans) && Acting.SequenceEqual(o.Acting)
        && Mode == o.Mode && Rerolls == o.Rerolls && AllowRerolling == o.AllowRerolling
        && (Offered ?? []).SequenceEqual(o.Offered ?? []) && (Bench ?? []).SequenceEqual(o.Bench ?? []) && (Swaps ?? []).SequenceEqual(o.Swaps ?? [])
        && (Next ?? []).SequenceEqual(o.Next ?? []) && PickTurnNow == o.PickTurnNow
        && MyPickActionId == o.MyPickActionId && (BanList ?? []).SequenceEqual(o.BanList ?? []) && (BanCells ?? []).SequenceEqual(o.BanCells ?? []);

    /// <summary>Shapes from real dumps: tools/logs/draft-dump-*.json and cs-dump-*.json. Null when the session has no teams yet.</summary>
    public static ChampSelectBoard? FromJson(JsonObject o, ChampSelectTimer timer, DateTimeOffset now)
    {
        if (o["myTeam"] is not JsonArray mine) return null;
        var local = o["localPlayerCellId"]?.GetValue<int>() ?? -1;
        var actions = (o["actions"] as JsonArray)?.OfType<JsonArray>().SelectMany(g => g).OfType<JsonObject>().ToList() ?? [];
        var order = Order.From(o);
        var myCells = mine.OfType<JsonObject>().Select(p => Int(p, "cellId")).ToHashSet();

        var bench = (o["benchChampions"] as JsonArray)?.OfType<JsonObject>().Select(b => Int(b, "championId")).Where(i => i > 0).ToList() ?? [];
        var mode = ChampSelectMode.From(Int(o, "queueId"), Bool(o, "benchEnabled"), actions.Any(a => Str(a, "type") is "pick" or "ban"));
        var board = new ChampSelectBoard(timer.Phase, local,
            Players(mine, actions, local, order), Players(o["theirTeam"] as JsonArray, actions, local, order),
            Bans(o, actions, myCells, mine: true), Bans(o, actions, myCells, mine: false),
            actions.Where(a => Bool(a, "isInProgress") && !Bool(a, "completed") && Str(a, "type") is "pick" or "ban")
                .Select(a => new BoardTurn(Int(a, "actorCellId"), Str(a, "type"))).OrderBy(t => t.CellId).ToList(),
            (long)new ChampSelectSession(local, timer, []).TimeLeft(now).TotalMilliseconds, Int(o, "queueId"),
            mode, bench, Int(o, "rerollsRemaining"), Bool(o, "allowRerolling"),
            ChampSelectSession.ParseAllSwaps(o).Where(w => w.State is "AVAILABLE" or "SENT" or "RECEIVED").ToList(),
            Next: order.Next, PickTurnNow: order.PickTurnNow,
            MyPickActionId: actions.Where(a => Str(a, "type") == "pick" && Int(a, "actorCellId") == local && !Bool(a, "completed")).Select(a => Int(a, "id")).FirstOrDefault(),
            BanList: actions.Where(a => Str(a, "type") == "ban" && Bool(a, "completed") && Int(a, "championId") > 0).OrderBy(a => Int(a, "id"))
                .Select(a => new BoardBan(Int(a, "actorCellId"), Int(a, "championId"), myCells.Contains(Int(a, "actorCellId")))).ToList(),
            // Ban boxes stand beside the cards only while bans are still being made; in the pick part they are in the top bar (LIVE, ranked shot).
            BanCells: actions.Any(a => Str(a, "type") == "ban" && !Bool(a, "completed"))
                ? actions.Where(a => Str(a, "type") == "ban").Select(a => Int(a, "actorCellId")).Distinct().Order().ToList()
                : []);
        return board;
    }

    /// <summary>
    /// The turn order from the session's action groups: each group is one turn, played together (all ten draft bans, or two picks).
    /// Groups without a pick or ban (`ten_bans_reveal`) are not turns. Pick-order swaps change the groups, so this follows them.
    /// </summary>
    private sealed record Order(Dictionary<int, int> PickTurns, List<BoardTurn> Next, int PickTurnNow)
    {
        public static Order From(JsonObject o)
        {
            var turns = (o["actions"] as JsonArray)?.OfType<JsonArray>()
                .Select(g => g.OfType<JsonObject>().Where(a => Str(a, "type") is "pick" or "ban").ToList()).Where(g => g.Count > 0).ToList() ?? [];
            var pickTurns = new Dictionary<int, int>();
            var picks = turns.Where(t => t.Any(a => Str(a, "type") == "pick")).ToList();
            for (var i = 0; i < picks.Count; i++)
                foreach (var a in picks[i].Where(a => Str(a, "type") == "pick")) pickTurns.TryAdd(Int(a, "actorCellId"), i + 1);

            static bool Live(JsonObject a) => Bool(a, "isInProgress") && !Bool(a, "completed");
            var current = turns.FindIndex(t => t.Any(Live));
            var next = turns.Skip(current + 1).FirstOrDefault(t => t.Any(a => !Bool(a, "completed")));
            var nextTurns = next?.Where(a => !Bool(a, "completed")).Select(a => new BoardTurn(Int(a, "actorCellId"), Str(a, "type"))).OrderBy(t => t.CellId).ToList() ?? [];
            var pickNow = current >= 0 ? picks.IndexOf(turns[current]) + 1 : 0;
            return new Order(pickTurns, nextTurns, pickNow);
        }
    }

    private static List<BoardPlayer> Players(JsonArray? team, List<JsonObject> actions, int local, Order order) =>
        team?.OfType<JsonObject>().Select(p =>
        {
            var cell = Int(p, "cellId");
            var pick = actions.LastOrDefault(a => Str(a, "type") == "pick" && Int(a, "actorCellId") == cell);
            var locked = Int(p, "championId");
            var champion = locked > 0 ? locked : Int(p, "championPickIntent") > 0 ? Int(p, "championPickIntent") : pick is null ? 0 : Int(pick, "championId");
            var name = Str(p, "gameName") is { Length: > 0 } g ? (Str(p, "tagLine") is { Length: > 0 } t ? $"{g}#{t}" : g) : "";
            return new BoardPlayer(cell, champion, locked > 0 || (pick is not null && Bool(pick, "completed") && Int(pick, "championId") > 0),
                LaneNames.FromAssigned(Str(p, "assignedPosition")) ?? "", Int(p, "spell1Id"), Int(p, "spell2Id"), Int(p, "selectedSkinId"), name, cell == local,
                order.PickTurns.GetValueOrDefault(cell));
        }).OrderBy(p => p.CellId).ToList() ?? [];

    /// <summary>Completed ban actions, split by who acted. The session's own `bans` object is empty in some modes, so it is only the fallback.</summary>
    private static List<int> Bans(JsonObject o, List<JsonObject> actions, HashSet<int> myCells, bool mine)
    {
        var fromActions = actions.Where(a => Str(a, "type") == "ban" && Bool(a, "completed") && Int(a, "championId") > 0 && myCells.Contains(Int(a, "actorCellId")) == mine)
            .OrderBy(a => Int(a, "id")).Select(a => Int(a, "championId")).ToList();
        if (fromActions.Count > 0) return fromActions;
        return (o["bans"]?[mine ? "myTeamBans" : "theirTeamBans"] as JsonArray)?.Select(x => x?.GetValue<int>() ?? 0).Where(x => x > 0).ToList() ?? [];
    }

    private static int Int(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<int>(out var i) ? i : 0;
    private static bool Bool(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
    private static string Str(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
}
