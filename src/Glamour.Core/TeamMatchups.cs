namespace Glamour.Core;

/// <summary>A champion's damage to champions per game, by type (the source's averages for that champion and lane).</summary>
public sealed record DamageSplit(double Physical, double Magic, double True)
{
    public double Total => Physical + Magic + True;
}

/// <summary>
/// What a stats source knows about one champion played in <see cref="Lane"/>: its damage and its win rate (percent, lean taken off) against the champions
/// played in each lane (<see cref="Against"/>, keys from <see cref="LaneNames.All"/>).
/// </summary>
public sealed record ChampionProfile(int ChampionId, string Lane, DamageSplit? Damage, IReadOnlyDictionary<string, IReadOnlyList<MatchupRow>> Against);

/// <summary>A stats source for champion profiles. Null when it has nothing (unreachable, unknown champion or lane, no data for the patch yet).</summary>
public interface IChampionProfileSource
{
    Task<ChampionProfile?> GetProfileAsync(int championId, string lane, CancellationToken ct);
}

/// <summary>
/// A champion to suggest against the whole enemy team. <see cref="WinRate"/> is its team score: the average over the picked enemies of its win rate
/// against each one, pulled toward 50 when the pair has few games (<see cref="TeamPicker"/>). <see cref="Covered"/> of the enemies have any games
/// against it, <see cref="Games"/> in all. <see cref="Lane"/> is its win rate against my lane opponent when one is known.
/// </summary>
public sealed record TeamSuggestion(int ChampionId, double WinRate, int Covered, int Games, bool InPool, double? Lane = null);

/// <summary>The picked enemies' damage added up (per-game averages), and each one's own split for the tooltip.</summary>
public sealed record TeamDamage(double Physical, double Magic, double True, IReadOnlyDictionary<int, DamageSplit> ByChampion)
{
    public static TeamDamage? From(IEnumerable<ChampionProfile> enemies)
    {
        var known = enemies.Where(p => p.Damage is { Total: > 0 }).GroupBy(p => p.ChampionId).ToDictionary(g => g.Key, g => g.First().Damage!);
        if (known.Count == 0) return null;
        return new(Math.Round(known.Values.Sum(d => d.Physical)), Math.Round(known.Values.Sum(d => d.Magic)), Math.Round(known.Values.Sum(d => d.True)), known);
    }
}

public static class TeamPicker
{
    /// <summary>
    /// A pair's win rate counts as if it had this many more games at 50%: a 106-game pair at 60% scores 52.6, a 3,000-game one at 60% scores 59.1.
    /// Cross-lane pairs are thin (100-800 games on a patch, INSIGHTS "Matchup data"), so without it one lucky pair decides the list.
    /// </summary>
    public const double PriorGames = CounterPicker.FewGamesBelow;

    /// <summary>
    /// Team score per champion played in <paramref name="myLane"/>: for each picked enemy (its profile in its own lane) the champion's win rate against
    /// it, 100 minus the enemy's, shrunk toward 50 by <see cref="PriorGames"/>; an enemy with no games against it counts 50. Then the average.
    /// </summary>
    public static Dictionary<int, (double Score, int Covered, int Games)> Scores(IReadOnlyList<ChampionProfile> enemies, string myLane)
    {
        var rows = enemies.Select(e => e.Against.TryGetValue(myLane, out var r)
            ? r.Where(x => x.Games > 0 && x.OpponentId != e.ChampionId).GroupBy(x => x.OpponentId).ToDictionary(g => g.Key, g => g.First())
            : []).ToList();
        if (rows.Count == 0) return [];
        return rows.SelectMany(r => r.Keys).Distinct().ToDictionary(id => id, id =>
        {
            double sum = 0; var covered = 0; var games = 0;
            foreach (var r in rows)
            {
                if (!r.TryGetValue(id, out var row)) { sum += 50; continue; }
                sum += 50 + (50 - row.WinRate) * row.Games / (row.Games + PriorGames);
                covered++; games += row.Games;
            }
            return (Math.Round(sum / rows.Count, 2), covered, games);
        });
    }

    /// <summary>
    /// A champion outside my pool joins the team picks only when at least this share (percent) of its games are in my lane: a rare pick's win rate is
    /// inflated, since mostly its specialists play it there (Zilean top: 6.5% of his games, 56% win rate).
    /// </summary>
    public const double OffMetaBelow = 10;

    /// <summary>
    /// Each champion's share (percent) of its games played in <paramref name="myLane"/>: its games against the picked enemies (their profiles) in every lane.
    /// Close to the site's own lane share (Zilean top on 16.20: 6.6 here, 6.5 there).
    /// </summary>
    public static Dictionary<int, double> LaneShares(IReadOnlyList<ChampionProfile> enemies, string myLane)
    {
        var mine = new Dictionary<int, double>();
        var all = new Dictionary<int, double>();
        foreach (var e in enemies)
            foreach (var (lane, rows) in e.Against)
                foreach (var row in rows.Where(r => r.Games > 0 && r.OpponentId != e.ChampionId).DistinctBy(r => r.OpponentId))
                {
                    all[row.OpponentId] = all.GetValueOrDefault(row.OpponentId) + row.Games;
                    if (lane == myLane) mine[row.OpponentId] = mine.GetValueOrDefault(row.OpponentId) + row.Games;
                }
        return all.ToDictionary(a => a.Key, a => Math.Round(100 * mine.GetValueOrDefault(a.Key) / a.Value, 1));
    }

    /// <summary>
    /// Best team score first. Like <see cref="CounterPicker.Rank"/>: taken champions never, only <paramref name="playable"/> ones when known, the pool always
    /// (when the data has it), others only as one of the <paramref name="top"/> best with at least <see cref="CounterPicker.FewGamesBelow"/> games in all
    /// and, when <paramref name="shares"/> are known, at least <see cref="OffMetaBelow"/> of their games in my lane (any share when none has that much).
    /// <paramref name="lane"/> is each champion's win rate against my lane opponent, when known.
    /// </summary>
    public static IReadOnlyList<TeamSuggestion> Rank(IReadOnlyDictionary<int, (double Score, int Covered, int Games)> scores, IReadOnlyCollection<int> pool, int top = 5,
        IReadOnlyCollection<int>? taken = null, IReadOnlyCollection<int>? playable = null, IReadOnlyDictionary<int, double>? lane = null,
        IReadOnlyDictionary<int, double>? shares = null)
    {
        var all = scores.Where(s => taken?.Contains(s.Key) != true && playable?.Contains(s.Key) != false)
            .Select(s => new TeamSuggestion(s.Key, s.Value.Score, s.Value.Covered, s.Value.Games, pool.Contains(s.Key), lane?.TryGetValue(s.Key, out var l) == true ? l : null))
            .ToList();
        var others = all.Where(s => !s.InPool && s.Games >= CounterPicker.FewGamesBelow).ToList();
        var onLane = others.Where(s => shares?.TryGetValue(s.ChampionId, out var share) != true || share >= OffMetaBelow).ToList();
        return [.. all.Where(s => s.InPool)
            .Concat((onLane.Count > 0 ? onLane : others).OrderByDescending(s => s.WinRate).Take(top))
            .OrderByDescending(s => s.WinRate)];
    }
}

/// <summary>
/// "Best counter" as the lane's auto-lock (<see cref="LaneSetup.BestCounter"/>): the champions to lock, best first, from what the panel shows.
/// Against my lane opponent when one is known, else against the whole team. A pool champion with enough games comes before the rest; then the
/// others with enough games; champions with few games last.
/// </summary>
public static class BestCounterPick
{
    public static IReadOnlyList<int> Order(CounterPickView view)
    {
        var duo = view.Duo?.Best ?? [];
        if (view.State == "ready" && view.Picks is { Count: > 0 } picks)
            return Order(picks.Select(p => (p.ChampionId, p.WinRate, p.InPool, p.FewGames)), duo);
        if (view.State is "waiting" or "ready" or "nodata" && view.TeamPicks is { Count: > 0 } team)
            return Order(team.Select(p => (p.ChampionId, p.WinRate, p.InPool, p.Games < CounterPicker.FewGamesBelow)), duo);
        return [];
    }

    /// <summary>A duo pick (<see cref="DuoView.Best"/>) goes first when it is within <see cref="DuoPicker.TieMargin"/> of the top row and in its tier (few games, pool).</summary>
    private static List<int> Order(IEnumerable<(int Id, double Score, bool InPool, bool Few)> rows, IReadOnlyList<int> duo)
    {
        var ordered = rows.OrderBy(r => r.Few).ThenByDescending(r => r.InPool).ThenByDescending(r => r.Score).DistinctBy(r => r.Id).ToList();
        if (ordered.Count > 1 && !duo.Contains(ordered[0].Id))
        {
            var head = ordered[0];
            var pair = ordered.Skip(1).TakeWhile(r => r.Few == head.Few && r.InPool == head.InPool)
                .FirstOrDefault(r => duo.Contains(r.Id) && r.Score >= head.Score - DuoPicker.TieMargin);
            if (pair.Id > 0)
            {
                ordered.Remove(pair);
                ordered.Insert(0, pair);
            }
        }
        return [.. ordered.Select(r => r.Id)];
    }
}

/// <summary>Who the user chose to face (a click on an enemy card, "Who you face" in the panel or phone), kept for the auto-lock's best counter. Ends with the champ select.</summary>
public sealed class CounterChoice : IFeature
{
    private int _opponent; // 0: none, the host's lane guess
    public int? Opponent { get => Volatile.Read(ref _opponent) is var id and > 0 ? id : null; set => Volatile.Write(ref _opponent, value ?? 0); }

    public Task OnPhaseAsync(string phase, CancellationToken ct)
    {
        if (phase != "ChampSelect") Opponent = null;
        return Task.CompletedTask;
    }

    public Task OnChampSelectAsync(ChampSelectSession session, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>The champions "Best counter" would lock for this board, best first (empty: no data, nothing to lock).</summary>
public interface ICounterChooser
{
    Task<IReadOnlyList<int>> ChooseAsync(ChampSelectBoard board, CancellationToken ct);
}

/// <summary>
/// What a stats source knows about one champion played in <see cref="Lane"/> with allies: its win rate (percent) with the champions played in each other
/// lane (<see cref="With"/>, keys from <see cref="LaneNames.All"/>). Every row carries the champion's own strength, so rows compare only within one table.
/// </summary>
public sealed record ChampionAllies(int ChampionId, string Lane, IReadOnlyDictionary<string, IReadOnlyList<MatchupRow>> With);

/// <summary>A stats source for ally tables. Null when it has nothing (unreachable, unknown champion or lane, no data for the patch yet).</summary>
public interface IAllySource
{
    Task<ChampionAllies?> GetAlliesAsync(int championId, string lane, CancellationToken ct);
}

/// <summary>My lane partner's champion (hovered or locked) and the champions that do best with it in my lane, best first (SPEC "Champ select advice", duo synergy).</summary>
public sealed record DuoView(int PartnerId, IReadOnlyList<int> Best);

public static class DuoPicker
{
    /// <summary>Best counter takes a duo pick over the top pick when it is at most this many points behind it.</summary>
    public const double TieMargin = 1.0;

    /// <summary>Bot lane only: the two share the lane all game; elsewhere a pair moves the win rate by noise (SPEC).</summary>
    public static string? PartnerLane(string lane) => lane switch { "BOTTOM" => "SUPPORT", "SUPPORT" => "BOTTOM", _ => null };

    /// <summary>
    /// The <paramref name="top"/> champions in <paramref name="myLane"/> with the best win rate with the partner, from the partner's own table: each row pulled
    /// toward the table's mean by <see cref="TeamPicker.PriorGames"/>, at least <see cref="CounterPicker.FewGamesBelow"/> games, taken champions never,
    /// only <paramref name="playable"/> ones when known.
    /// </summary>
    public static IReadOnlyList<int> Best(ChampionAllies partner, string myLane, int top = 3, IReadOnlyCollection<int>? taken = null, IReadOnlyCollection<int>? playable = null)
    {
        if (!partner.With.TryGetValue(myLane, out var rows)) return [];
        var all = rows.Where(r => r.Games > 0 && r.OpponentId != partner.ChampionId).GroupBy(r => r.OpponentId).Select(g => g.First()).ToList();
        var games = all.Sum(r => r.Games);
        if (games == 0) return [];
        var mean = all.Sum(r => r.WinRate * r.Games) / games;
        return [.. all.Where(r => r.Games >= CounterPicker.FewGamesBelow && taken?.Contains(r.OpponentId) != true && playable?.Contains(r.OpponentId) != false)
            .OrderByDescending(r => mean + (r.WinRate - mean) * r.Games / (r.Games + TeamPicker.PriorGames)).Take(top).Select(r => r.OpponentId)];
    }
}
