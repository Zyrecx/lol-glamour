using System.Globalization;
using System.Text;

namespace Glamour.Core;

/// <summary>One augment's numbers in one table: games and win rate (0..1).</summary>
public readonly record struct AugmentRow(int Samples, double WinRate);

/// <summary>An augment's rows: over all offers and per offer (1-4, at levels 1, 7, 11, 15).</summary>
public sealed record AugmentEntry(AugmentRow? All, IReadOnlyDictionary<int, AugmentRow> Stages);

/// <summary>
/// aramkit's Mayhem augment numbers (INSIGHTS "Matchup data"): one champion's (<see cref="ChampionWinRate"/> is that champion's own) or every champion's
/// (<see cref="ChampionWinRate"/> 0.5), keyed by augment id (the ids of CommunityDragon `cherry-augments.json` and match history's `playerAugment`).
/// </summary>
public sealed record AugmentTable(string Patch, double ChampionWinRate, IReadOnlyDictionary<int, AugmentEntry> Augments);

/// <summary>A Mayhem augment: id, name as the game writes it, rarity (silver, gold, prismatic).</summary>
public sealed record AugmentInfo(int Id, string Name, string Rarity);

/// <summary>What a card's tag says: win-rate difference in points against my champion's own win rate (or 50% for <see cref="AugmentSource.Global"/>).</summary>
public enum AugmentSource { Stage, Champion, Global, None }

public sealed record AugmentPick(int Id, double? DeltaPoints, AugmentSource Source, bool LowSample)
{
    /// <summary>S at +4 points and over, A from +1, B within 1, C down to -4, D below (ARAM Mayhem Tracker's bands).</summary>
    public string Grade => DeltaPoints switch
    {
        null => "?",
        >= 4 => "S",
        >= 1 => "A",
        > -1 => "B",
        > -4 => "C",
        _ => "D",
    };
}

/// <summary>
/// Which offered augment to take (SPEC "PC app direction" 5): each card's number by the fallback ladder of ARAM Mayhem Tracker (Copyright (c) 2026 Sygnano, MIT License; `service/src/domain/augments.ts`):
/// my champion at this offer when it has 200 games, else my champion over all offers, else the thin champion row flagged, else every champion against 50%.
/// </summary>
public static class AugmentAdvice
{
    public const int MinSamples = 200;

    public static AugmentPick Resolve(int id, AugmentTable? champion, AugmentTable? global, int stage)
    {
        if (champion?.Augments.TryGetValue(id, out var e) == true)
        {
            var stageRow = e.Stages.TryGetValue(stage, out var s) ? s : (AugmentRow?)null;
            if (stageRow is { Samples: >= MinSamples } r1) return Pick(id, r1, champion.ChampionWinRate, AugmentSource.Stage);
            if (e.All is { Samples: >= MinSamples } r2) return Pick(id, r2, champion.ChampionWinRate, AugmentSource.Champion);
            if (stageRow is { } r3) return Pick(id, r3, champion.ChampionWinRate, AugmentSource.Stage);
            if (e.All is { } r4) return Pick(id, r4, champion.ChampionWinRate, AugmentSource.Champion);
        }
        if (global?.Augments.TryGetValue(id, out var g) == true && (g.Stages.TryGetValue(stage, out var gs) ? gs : g.All) is { } row)
            return Pick(id, row, 0.5, AugmentSource.Global);
        return new(id, null, AugmentSource.None, false);
    }

    private static AugmentPick Pick(int id, AugmentRow row, double baseline, AugmentSource source) =>
        new(id, Math.Round((row.WinRate - baseline) * 100, 1), source, row.Samples < MinSamples);

    /// <summary>
    /// The picks for an offer (null where a card was not read) and the best card's index, or -1. Champion numbers and all-champion ones measure
    /// against different baselines, so the best is taken among the champion ones when there are any; a thin row is never the best.
    /// </summary>
    public static (AugmentPick?[] Picks, int Best) Rank(IReadOnlyList<int?> offered, AugmentTable? champion, AugmentTable? global, int stage)
    {
        var picks = offered.Select(id => id is { } i ? Resolve(i, champion, global, stage) : null).ToArray();
        var pool = picks.Select((p, i) => (p, i)).Where(x => x.p is { DeltaPoints: not null, LowSample: false } && x.p.Source != AugmentSource.None).ToList();
        var own = pool.Where(x => x.p!.Source is AugmentSource.Stage or AugmentSource.Champion).ToList();
        var from = own.Count > 0 ? own : pool;
        if (from.Count < 2) return (picks, -1);
        var best = from.MaxBy(x => x.p!.DeltaPoints!.Value);
        return (picks, best.i);
    }

    /// <summary>Offers unlocked by my level: one at 1, then at 7, 11 and 15.</summary>
    public static int StagesUnlocked(int level) => level >= 15 ? 4 : level >= 11 ? 3 : level >= 7 ? 2 : 1;
}

/// <summary>
/// Numbers the offers of one game (ARAM Mayhem Tracker's `mayhem-core/src/augments.rs`): a new set of cards is the next offer, never below what my level
/// has unlocked (Glamour started mid-game); the same cards shown again (hidden and reshown, the shop closed) keep their offer. A reroll changes one card,
/// so an offer counts as the same while at least two of its cards stay.
/// </summary>
public sealed class AugmentOffers
{
    private int?[]? _last;
    private int _lastStage;

    public int Seen { get; private set; }

    public int StageOf(IReadOnlyList<int?> cards, int unlocked)
    {
        if (_last is not null && cards.Zip(_last, (a, b) => a is not null && a == b).Count(x => x) >= 2) { _last = [.. cards]; return _lastStage; }
        _lastStage = Math.Max(Math.Max(Seen + 1, unlocked), 1);
        Seen = Math.Max(Seen, _lastStage);
        _last = [.. cards];
        return _lastStage;
    }
}

/// <summary>Turns what OCR read off a card's title into one of the Mayhem pool's augments.</summary>
public static class AugmentNames
{
    /// <summary>Letters and digits only, lower case, accents dropped: OCR gets punctuation and spacing wrong far more than letters.</summary>
    public static string Key(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s.Normalize(NormalizationForm.FormD))
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    /// <summary>The pool's augment whose name is closest to <paramref name="read"/> (edit distance at most a quarter of the name, and clearly
    /// closer than the runner-up), or null. A line read off a wrapped title is matched too, so a two-line name needs both lines joined.</summary>
    public static AugmentInfo? Match(string read, IReadOnlyList<AugmentInfo> pool) => Best(read, pool).Info;

    /// <summary>The OCR'd lines of one title band, top to bottom: the best match of its last line, its last two joined, and so on (a wrapped title
    /// grows upwards; lines above it are the icon's edge or nothing).</summary>
    public static AugmentInfo? MatchLines(IReadOnlyList<string> lines, IReadOnlyList<AugmentInfo> pool) =>
        Enumerable.Range(1, lines.Count).Select(k => Best(string.Join(" ", lines.Skip(lines.Count - k)), pool))
            .Where(m => m.Info is not null).OrderByDescending(m => m.Score).FirstOrDefault().Info;

    private static (AugmentInfo? Info, double Score) Best(string read, IReadOnlyList<AugmentInfo> pool)
    {
        var key = Key(read);
        if (key.Length < 3) return (null, 0);
        // Names are compared once each: two augments may share a name (rarity variants), which is not a doubt about the reading. The first of them wins.
        (AugmentInfo? Info, double Score) best = (null, 0), second = (null, 0);
        foreach (var group in pool.GroupBy(a => Key(a.Name)))
        {
            if (group.Key.Length == 0) continue;
            var score = 1 - (double)Distance(key, group.Key) / Math.Max(key.Length, group.Key.Length);
            if (score > best.Score) { second = best; best = (group.First(), score); }
            else if (score > second.Score) second = (group.First(), score);
        }
        return best.Score >= 0.75 && best.Score - second.Score >= 0.05 ? best : (null, 0);
    }

    private static int Distance(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (var j = 1; j <= b.Length; j++)
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }

    /// <summary>A tag's text: "+3.2" / "-1.0" points, "?" with no number.</summary>
    public static string Points(double? delta) =>
        delta is { } d ? (d >= 0 ? "+" : "") + d.ToString("0.0", CultureInfo.InvariantCulture) : "?";
}
