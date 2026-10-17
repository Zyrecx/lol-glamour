namespace Glamour.Core;

/// <summary>
/// One opponent in a champion's matchup table: the champion's own win rate against it in this lane (percent) over <see cref="Games"/> games.
/// <see cref="OlderGames"/> of them come from the previous patch <see cref="OlderPatch"/> (<see cref="MatchupBlend"/>), 0 when all are from the current one.
/// </summary>
public sealed record MatchupRow(int OpponentId, double WinRate, int Games, int OlderGames = 0, string? OlderPatch = null);

/// <summary>What a stats source knows about one champion: how often it plays each lane (percent, keys from <see cref="LaneNames.All"/>) and its win rate against each opponent in <see cref="Lane"/>.</summary>
public sealed record ChampionMatchups(int ChampionId, string Lane, IReadOnlyDictionary<string, double> LaneShares, IReadOnlyList<MatchupRow> Rows);

/// <summary>A stats source for matchups. Null when it has nothing (unreachable, unknown champion or lane, no data for the patch yet).</summary>
public interface IMatchupSource
{
    Task<ChampionMatchups?> GetAsync(int championId, string lane, CancellationToken ct);
}

/// <summary>
/// Every champion's win rate in one mode (ARAM, Mayhem): each row's <see cref="MatchupRow.OpponentId"/> is the champion, <see cref="MatchupRow.Games"/> 0 when the
/// source gives no counts. <see cref="Patch"/> is the newest patch counted (the client's naming), <see cref="Source"/> the site. A source with tiers only
/// leaves <see cref="Rows"/> empty and fills <see cref="Tiers"/> (champion id to a letter of <see cref="ModeTiers.Order"/>).
/// </summary>
public sealed record ModeWinRates(string Mode, string Patch, IReadOnlyList<MatchupRow> Rows, string Source, IReadOnlyDictionary<int, string>? Tiers = null);

public static class ModeTiers
{
    /// <summary>Tier letters best first, as u.gg names them.</summary>
    public static readonly IReadOnlyList<string> Order = ["S+", "S", "A", "B", "C", "D"];
}

/// <summary>A stats source for whole modes, read for bench advice. Null when it has nothing (unreachable, mode it does not know).</summary>
public interface IModeWinRateSource
{
    Task<ModeWinRates?> GetModeAsync(string mode, CancellationToken ct);
}

public static class MatchupBlend
{
    /// <summary>A matchup with fewer games than this on the current patch also counts the previous patch's games.</summary>
    public const int BlendBelow = 1000;

    /// <summary>
    /// A new patch fills slowly (INSIGHTS "Matchup data"), so a thin row gets the previous patch's games added, weighted by games, and says how many are older.
    /// Opponents only the previous patch knows come in whole; lane shares are the current patch's unless it has none.
    /// </summary>
    public static ChampionMatchups? Blend(ChampionMatchups? current, ChampionMatchups? previous, string previousPatch)
    {
        static MatchupRow Older(MatchupRow r, string patch) => r with { OlderGames = r.Games, OlderPatch = patch };
        if (previous is null) return current;
        if (current is null) return previous with { Rows = [.. previous.Rows.Select(r => Older(r, previousPatch))] };
        var old = previous.Rows.Where(r => r.Games > 0).GroupBy(r => r.OpponentId).ToDictionary(g => g.Key, g => g.First());
        var rows = current.Rows.Select(c =>
        {
            if (c.Games >= BlendBelow || !old.TryGetValue(c.OpponentId, out var p)) return c;
            var games = c.Games + p.Games;
            return new MatchupRow(c.OpponentId, Math.Round((c.WinRate * c.Games + p.WinRate * p.Games) / games, 2), games, p.Games, previousPatch);
        }).ToList();
        rows.AddRange(old.Values.Where(p => rows.All(r => r.OpponentId != p.OpponentId)).Select(p => Older(p, previousPatch)));
        return current with { LaneShares = current.LaneShares.Count > 0 ? current.LaneShares : previous.LaneShares, Rows = rows };
    }
}

public static class LaneAssigner
{
    /// <summary>
    /// Enemy picks come without lanes. Tries every way to give the champions distinct lanes and keeps the one whose lane shares multiply to the most;
    /// a champion without data counts as an even share. Returns lane per champion id (at most one champion per lane).
    /// </summary>
    public static Dictionary<int, string> Assign(IReadOnlyList<(int ChampionId, IReadOnlyDictionary<string, double>? Shares)> champions)
    {
        var best = new string[champions.Count];
        var bestScore = double.NegativeInfinity;
        var current = new string[champions.Count];
        var used = new HashSet<string>();

        void Walk(int i, double score)
        {
            if (i == champions.Count)
            {
                if (score > bestScore) { bestScore = score; Array.Copy(current, best, current.Length); }
                return;
            }
            foreach (var lane in LaneNames.All)
            {
                if (!used.Add(lane)) continue;
                current[i] = lane;
                var share = champions[i].Shares is { } s && s.Count > 0 ? (s.TryGetValue(lane, out var v) ? v : 0) : 20;
                Walk(i + 1, score + Math.Log(Math.Max(share, 0.01) / 100));
                used.Remove(lane);
            }
        }

        if (champions.Count is > 0 and <= 5) Walk(0, 0);
        return champions.Count is > 0 and <= 5 ? champions.Select((c, i) => (c.ChampionId, best[i])).ToDictionary(x => x.ChampionId, x => x.Item2) : [];
    }

    /// <summary>
    /// How likely each champion is to play <paramref name="lane"/> (0 to 1): every way to give the champions distinct lanes, weighted by their lane shares multiplied,
    /// the same weights as <see cref="Assign"/>. With fewer than five picked, a split that leaves <paramref name="lane"/> open means a champion still to pick plays it.
    /// </summary>
    public static Dictionary<int, double> Chances(IReadOnlyList<(int ChampionId, IReadOnlyDictionary<string, double>? Shares)> champions, string lane)
    {
        if (champions.Count is 0 or > 5) return [];
        var inLane = new double[champions.Count];
        var total = 0.0;
        var used = new HashSet<string>();

        void Walk(int i, double weight, int holder)
        {
            if (i == champions.Count)
            {
                total += weight;
                if (holder >= 0) inLane[holder] += weight;
                return;
            }
            foreach (var l in LaneNames.All)
            {
                if (!used.Add(l)) continue;
                var share = champions[i].Shares is { } s && s.Count > 0 ? (s.TryGetValue(l, out var v) ? v : 0) : 20;
                Walk(i + 1, weight * Math.Max(share, 0.01) / 100, l == lane ? i : holder);
                used.Remove(l);
            }
        }

        Walk(0, 1, -1);
        return champions.Select((c, i) => (c.ChampionId, Chance: total > 0 ? inLane[i] / total : 0)).ToDictionary(x => x.ChampionId, x => x.Chance);
    }
}

/// <summary>A champion to suggest against the enemy laner. <see cref="WinRate"/> is its win rate against that enemy in this lane (percent).
/// <see cref="OlderGames"/> of the games are from the previous patch <see cref="OlderPatch"/>. <see cref="Team"/> is its team score (<see cref="TeamPicker"/>) when known.</summary>
public sealed record CounterSuggestion(int ChampionId, double WinRate, int Games, bool FewGames, bool InPool, int OlderGames = 0, string? OlderPatch = null, double? Team = null);

public static class CounterPicker
{
    public const int FewGamesBelow = 300;

    /// <summary>
    /// Champions in <paramref name="taken"/> (banned or locked in by anyone) are never suggested, not even from the pool.
    /// <paramref name="enemy"/> is the enemy champion's own table, so the suggestion's win rate is 100 minus the enemy's against it.
    /// The user's pool is always listed when the table knows it (marked "few games" when thin); other champions only come in as one of the
    /// <paramref name="top"/> best with at least <see cref="FewGamesBelow"/> games, which also drops off-lane noise. Best win rate first.
    /// With <paramref name="playable"/> (the champions I can pick: owned plus free rotation) nothing else is suggested.
    /// </summary>
    public static IReadOnlyList<CounterSuggestion> Rank(ChampionMatchups enemy, IReadOnlyCollection<int> pool, int top = 3, IReadOnlyCollection<int>? taken = null,
        IReadOnlyCollection<int>? playable = null)
    {
        var all = enemy.Rows
            .Where(r => r.OpponentId != enemy.ChampionId && r.Games > 0 && taken?.Contains(r.OpponentId) != true && playable?.Contains(r.OpponentId) != false)
            .Select(r => new CounterSuggestion(r.OpponentId, Math.Round(100 - r.WinRate, 2), r.Games, r.Games < FewGamesBelow, pool.Contains(r.OpponentId), r.OlderGames, r.OlderPatch))
            .ToList();
        return [.. all.Where(s => s.InPool)
            .Concat(all.Where(s => !s.InPool && !s.FewGames).OrderByDescending(s => s.WinRate).Take(top))
            .OrderByDescending(s => s.WinRate)];
    }
}

/// <summary>
/// What the panel shows: <see cref="State"/> is "none" (nothing to say: not a lane draft or no lane yet), "locked" (my pick is in: no suggestions, the enemies and
/// <see cref="Mine"/> still come), "waiting" (no enemy is likely in my lane yet), "nodata" (the opponent picked by hand has no table) or "ready"
/// (<see cref="EnemyId"/> is probably my lane opponent and <see cref="Picks"/> are the suggestions against it). <see cref="DataLane"/> is set when the picks
/// come from another lane than mine: the hand-picked opponent has no games in my lane, so its own main lane. <see cref="EnemyLanes"/> is the most likely lane
/// of each picked enemy champion (a guess from its lane shares, also when the opponent was picked by hand), for naming the champions in an enemy party.
/// <see cref="Threats"/> are the champions that beat my hovered or locked champion most in my lane, banned and locked ones left out (who to ban, or what to fear).
/// <see cref="TeamPicks"/> are the suggestions against all picked enemies (also while "waiting"), <see cref="Damage"/> the picked enemies' damage added up,
/// <see cref="AutoLock"/> the champion the lane's "Best counter" auto-lock would lock now (only when it is set up), <see cref="AllyDamage"/> my team's damage
/// (hovered and locked champions, me included, each in its own lane), <see cref="Duo"/> my bot lane partner and the picks that do best with it (before my lock).
/// <see cref="Chances"/> is how likely each picked enemy plays my lane (percent, <see cref="LaneAssigner.Chances"/>).
/// </summary>
public sealed record CounterPickView(string State, string? Lane = null, int EnemyId = 0, IReadOnlyList<CounterSuggestion>? Picks = null, IReadOnlyList<int>? Enemies = null,
    bool Forced = false, string? DataLane = null, MyMatchups? Mine = null, IReadOnlyDictionary<int, string>? EnemyLanes = null, IReadOnlyList<CounterSuggestion>? Threats = null,
    IReadOnlyList<TeamSuggestion>? TeamPicks = null, TeamDamage? Damage = null, int? AutoLock = null, TeamDamage? AllyDamage = null, DuoView? Duo = null,
    IReadOnlyDictionary<int, int>? Chances = null)
{
    public static readonly CounterPickView None = new("none");
}

/// <summary>My champion's win rate (percent) against one opponent in my lane; <see cref="OlderGames"/> of the games are from the previous patch <see cref="OlderPatch"/>.</summary>
public sealed record MatchupLine(int ChampionId, double WinRate, int Games, bool FewGames, int OlderGames = 0, string? OlderPatch = null);

/// <summary>
/// My hovered or locked champion against the enemies: <see cref="Picked"/> true lists the picked enemies that play my lane (my opponent first);
/// false means nobody is picked yet and the lines are its hardest common matchups it loses (none when it wins them all).
/// A picked enemy's line comes from that enemy's own table when it has my champion (100 minus its win rate), the same table the suggestions
/// use, so a champion shows the same number in both places; my table only fills in where the enemy's has no row.
/// </summary>
public sealed record MyMatchups(int ChampionId, bool Picked, IReadOnlyList<MatchupLine> Lines)
{
    public static MyMatchups? From(ChampionMatchups mine, IReadOnlyList<int> enemies, int opponentId, int hardest = 3, IReadOnlyList<ChampionMatchups?>? enemyTables = null)
    {
        var theirs = (enemyTables ?? []).OfType<ChampionMatchups>()
            .Select(t => (t.ChampionId, Row: t.Rows.FirstOrDefault(r => r.OpponentId == mine.ChampionId && r.Games > 0)))
            .Where(x => x.Row is not null).GroupBy(x => x.ChampionId).ToDictionary(g => g.Key, g => g.First().Row!);
        var rows = mine.Rows.Where(r => r.OpponentId != mine.ChampionId && r.Games > 0)
            .Select(r => theirs.TryGetValue(r.OpponentId, out var t) ? t with { OpponentId = r.OpponentId, WinRate = Math.Round(100 - t.WinRate, 2) } : r).ToList();
        rows.AddRange(theirs.Where(t => t.Key != mine.ChampionId && rows.All(r => r.OpponentId != t.Key)).Select(t => t.Value with { OpponentId = t.Key, WinRate = Math.Round(100 - t.Value.WinRate, 2) }));
        MatchupLine Line(MatchupRow r) => new(r.OpponentId, r.WinRate, r.Games, r.Games < CounterPicker.FewGamesBelow, r.OlderGames, r.OlderPatch);
        if (enemies.Count == 0)
            return new(mine.ChampionId, false, [.. rows.Where(r => r.Games >= CounterPicker.FewGamesBelow && r.WinRate < 50).OrderBy(r => r.WinRate).Take(hardest).Select(Line)]);
        // An enemy with few games against me in this lane rarely plays it; the opponent is kept whatever its count.
        var lines = rows.Where(r => enemies.Contains(r.OpponentId) && (r.Games >= CounterPicker.FewGamesBelow || r.OpponentId == opponentId))
            .OrderByDescending(r => r.OpponentId == opponentId).ThenByDescending(r => r.Games).Select(Line).ToList();
        return new(mine.ChampionId, true, lines);
    }
}

public static class CounterPick
{
    /// <summary>
    /// An enemy counts as my opponent from this chance (<see cref="LaneAssigner.Chances"/>) on. A single pick goes by its share in my lane (Naafiri 28% mid, Cho'Gath 31% top:
    /// not named); later picks settle the split (Swain, Sylas, Vayne and MF leave Vayne top, LIVE). Below it the lists and Best counter go by the team score.
    /// Backtest on my match history (`--lane-backtest`): a named opponent is right 91% of the time (the old rule 87%) and the chance matches how often it is right.
    /// </summary>
    public const double SureChance = 0.6;

    /// <summary>
    /// Counter suggestions for the current board. One table per picked enemy, fetched for my lane (the source caches); an enemy with no table for my lane
    /// cannot be my opponent. My pool for the lane is `GlamourSettings.PoolFor`. <paramref name="playable"/> limits the picks to champions I can pick
    /// (null = unknown, no limit); the threats to my champion are never limited.
    /// </summary>
    /// <summary>Champions nobody can pick any more: every ban and every locked-in champion on both teams.</summary>
    public static HashSet<int> Taken(ChampSelectBoard board) =>
        [.. board.MyBans.Concat(board.TheirBans).Concat((board.BanList ?? []).Select(b => b.ChampionId))
            .Concat(board.MyTeam.Concat(board.TheirTeam).Where(p => p.Locked).Select(p => p.ChampionId)).Where(id => id > 0)];

    public static async Task<CounterPickView> ViewAsync(ChampSelectBoard? board, GlamourSettings settings, IMatchupSource source, CancellationToken ct, int? opponentId = null,
        IReadOnlyCollection<int>? playable = null, IChampionProfileSource? profiles = null, IAllySource? allies = null)
    {
        if (board is not { Mode: ChampSelectMode.Draft } || board.MyTeam.FirstOrDefault(p => p.IsMe) is not { Lane: { Length: > 0 } lane } me) return CounterPickView.None;
        var enemies = board.TheirTeam.Select(p => p.ChampionId).Where(id => id > 0).Distinct().ToList();
        var tables = await Task.WhenAll(enemies.Select(id => source.GetAsync(id, lane, ct)));
        var view = await CountersAsync(board, settings, source, lane, enemies, tables, opponentId, playable, ct);
        if (profiles is not null && view.EnemyLanes is { Count: > 0 } lanes)
            view = await TeamAsync(view, board, settings, source, profiles, lane, lanes, me.Locked, playable, ct);
        if (profiles is not null) view = view with { AllyDamage = await AllyDamageAsync(board, profiles, ct) };
        if (allies is not null && !me.Locked) view = view with { Duo = await DuoAsync(board, lane, allies, playable, ct) };
        if (me.Locked) view = view with { State = "locked", Picks = null, DataLane = null, TeamPicks = null };
        else if (settings.ForLane(lane).LockChampionId == LaneSetup.BestCounter) view = view with { AutoLock = BestCounterPick.Order(view).FirstOrDefault() is var id and > 0 ? id : null };
        if (me.ChampionId > 0 && await source.GetAsync(me.ChampionId, lane, ct) is { } mine)
            view = view with { Mine = MyMatchups.From(mine, enemies, view.EnemyId, enemyTables: tables), Threats = CounterPicker.Rank(mine, [], top: 5, taken: Taken(board)) };
        return view;
    }

    /// <summary>My bot lane partner, once it shows a champion, and the champions that do best with it in my lane (none in other lanes).</summary>
    public static async Task<DuoView?> DuoAsync(ChampSelectBoard board, string lane, IAllySource allies, IReadOnlyCollection<int>? playable, CancellationToken ct)
    {
        if (Partner(board, lane) is not { } partner || await allies.GetAlliesAsync(partner.ChampionId, partner.Lane, ct) is not { } table) return null;
        return new(partner.ChampionId, DuoPicker.Best(table, lane, taken: Taken(board), playable: playable));
    }

    /// <summary>The ally in my lane's partner lane with a champion hovered or locked; null outside bot lane.</summary>
    public static BoardPlayer? Partner(ChampSelectBoard board, string lane) =>
        DuoPicker.PartnerLane(lane) is { } other ? board.MyTeam.FirstOrDefault(p => !p.IsMe && p.Lane == other && p.ChampionId > 0) : null;

    /// <summary>My team's damage: every ally with a champion (hovered or locked, me too) and an assigned lane, its profile in that lane.</summary>
    private static async Task<TeamDamage?> AllyDamageAsync(ChampSelectBoard board, IChampionProfileSource profiles, CancellationToken ct)
    {
        var allies = board.MyTeam.Where(p => p.ChampionId > 0 && LaneNames.All.Contains(p.Lane)).ToList();
        if (allies.Count == 0) return null;
        return TeamDamage.From((await Task.WhenAll(allies.Select(p => profiles.GetProfileAsync(p.ChampionId, p.Lane, ct)))).OfType<ChampionProfile>());
    }

    /// <summary>The enemies' damage, and (before my lock) the team scores: on the lane picks, and as a list of their own. Each enemy's profile is for its likely lane.</summary>
    private static async Task<CounterPickView> TeamAsync(CounterPickView view, ChampSelectBoard board, GlamourSettings settings, IMatchupSource source, IChampionProfileSource profiles,
        string lane, IReadOnlyDictionary<int, string> enemyLanes, bool locked, IReadOnlyCollection<int>? playable, CancellationToken ct)
    {
        var found = (await Task.WhenAll(enemyLanes.Select(e => profiles.GetProfileAsync(e.Key, e.Value, ct)))).OfType<ChampionProfile>().ToList();
        view = view with { Damage = TeamDamage.From(found) };
        if (locked || found.Count == 0) return view;
        var scores = TeamPicker.Scores(found, lane);
        // My win rate against the lane opponent beside each team pick: from the same table the lane picks come from.
        Dictionary<int, double>? vsLane = null;
        if (view is { State: "ready", DataLane: null } && await source.GetAsync(view.EnemyId, lane, ct) is { } table)
            vsLane = table.Rows.Where(r => r.Games > 0).GroupBy(r => r.OpponentId).ToDictionary(g => g.Key, g => Math.Round(100 - g.First().WinRate, 2));
        double? Team(int id) => scores.TryGetValue(id, out var s) ? s.Score : null;
        return view with
        {
            Picks = view.Picks?.Select(p => p with { Team = Team(p.ChampionId) }).ToList(),
            TeamPicks = TeamPicker.Rank(scores, [.. settings.PoolFor(lane)], taken: Taken(board), playable: playable, lane: vsLane,
                shares: TeamPicker.LaneShares(found, lane)),
        };
    }

    private static async Task<CounterPickView> CountersAsync(ChampSelectBoard board, GlamourSettings settings, IMatchupSource source, string lane, List<int> enemies,
        ChampionMatchups?[] tables, int? opponentId, IReadOnlyCollection<int>? playable, CancellationToken ct)
    {
        if (enemies.Count == 0) return new("waiting", lane);
        var known = enemies.Select((id, i) => (ChampionId: id, Table: tables[i])).ToList();
        List<(int, IReadOnlyDictionary<string, double>?)> shares = [.. known.Select(k => (k.ChampionId,
            k.Table?.LaneShares ?? (IReadOnlyDictionary<string, double>)LaneNames.All.ToDictionary(l => l, l => l == lane ? 0.0 : 20.0)))];
        var chances = LaneAssigner.Chances(shares, lane);
        var view = await PicksAsync(board, settings, source, lane, enemies, known, chances, opponentId, playable, ct);
        return view with { EnemyLanes = LaneAssigner.Assign(shares), Chances = chances.ToDictionary(c => c.Key, c => (int)Math.Round(c.Value * 100)) };
    }

    private static async Task<CounterPickView> PicksAsync(ChampSelectBoard board, GlamourSettings settings, IMatchupSource source, string lane, List<int> enemies,
        List<(int ChampionId, ChampionMatchups? Table)> known, Dictionary<int, double> chances, int? opponentId, IReadOnlyCollection<int>? playable, CancellationToken ct)
    {
        HashSet<int> pool = [.. settings.PoolFor(lane)];

        // The user picked who they face: that enemy's table, whatever the lane guess says. Off its lanes the table is empty (LIVE: shares, no counters),
        // so the picks come from its main lane then: who beats that champion in general.
        if (opponentId is { } chosen && enemies.Contains(chosen))
        {
            var table = await source.GetAsync(chosen, lane, ct);
            if (table is null) return new("nodata", lane, chosen, null, enemies, true);
            var picks = CounterPicker.Rank(table, pool, taken: Taken(board), playable: playable);
            if (picks.Count > 0) return new("ready", lane, chosen, picks, enemies, true);
            var main = table.LaneShares.Where(s => s.Key != lane && s.Value > 0).OrderByDescending(s => s.Value).Select(s => s.Key).FirstOrDefault();
            var other = main is null ? null : await source.GetAsync(chosen, main, ct);
            if (other is null) return new("nodata", lane, chosen, null, enemies, true);
            return new("ready", lane, chosen, CounterPicker.Rank(other, pool, taken: Taken(board), playable: playable), enemies, true, main);
        }

        // With all five picked one of them is my laner: the likeliest is right 92% of the time (backtest), better than no loadout.
        var least = enemies.Count >= 5 ? 0 : SureChance;
        var opponent = known.Where(k => k.Table is not null && chances.GetValueOrDefault(k.ChampionId) >= least).OrderByDescending(k => chances[k.ChampionId]).FirstOrDefault();
        if (opponent.Table is null) return new("waiting", lane, 0, null, enemies);
        return new("ready", lane, opponent.ChampionId, CounterPicker.Rank(opponent.Table, pool, taken: Taken(board), playable: playable), enemies);
    }
}
