using System.Globalization;

namespace Glamour.Host.Panel;

/// <summary>One bench slot in the client's "AVAILABLE CHAMPIONS" strip, in screen pixels: its square's top-left corner and side.</summary>
public readonly record struct BenchSlot(int Index, double X, double Y, double Size);

/// <summary>
/// Where the client draws the bench in ARAM-style champ select (SPEC "PC app direction" 5, INSIGHTS "Champ select: ARAM Mayhem"), for marking it with
/// bench advice. Pure, like <see cref="RosterLayout"/>: the client draws its 1280x720 base layout scaled to the window.
/// </summary>
public static class BenchLayout
{
    // Mayhem, 1280x720, BAN_PICK and FINALIZATION: 10 slots of 50x50 px, frame rows 10-59, the left frame edges below (58 or 59 px apart), filled left to
    // right in `benchChampions` order; a champion swapped in keeps its slot [LIVE] (shots bench-20261010-1841*).
    private static readonly int[] Lefts = [352, 410, 469, 527, 586, 645, 703, 762, 821, 879];
    private const double Top = 10, Side = 50, Base = 720;
    public const int Count = 10;

    /// <summary>The bench slots on screen, left to right; null where the strip is not measured (ARAM 450 not captured yet) or the window is not 16:9.</summary>
    public static IReadOnlyList<BenchSlot>? Slots(PixelRect client, string mode)
    {
        if (mode != "mayhem" || !RosterLayout.Trusted(client)) return null;
        var scale = client.Height / Base;
        return [.. Enumerable.Range(0, Count).Select(i => new BenchSlot(i, client.Left + Lefts[i] * scale, client.Top + Top * scale, Side * scale))];
    }
}

/// <summary>A bench champion's number for its slot: a win rate ("52.4%") or a tier ("S+"), and whether it clearly beats my champion's.</summary>
public readonly record struct BenchTag(string Text, bool Better)
{
    /// <summary>
    /// The page's "bench:" list: one item per slot in bench order, "52.4" (a win rate), "S+" (a tier) or empty, "^" first when better than mine.
    /// Anything else is no tag; at most <see cref="BenchLayout.Count"/> items.
    /// </summary>
    public static BenchTag?[] ParseAll(string list) =>
        list.Length == 0 ? [] : [.. list.Split(',').Take(BenchLayout.Count).Select(item =>
        {
            var better = item.StartsWith('^');
            var value = better ? item[1..] : item;
            if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var wr) && wr is >= 0 and <= 100)
                return new BenchTag(FormattableString.Invariant($"{wr:0.0}%"), better);
            return value is "S+" or "S" or "A" or "B" or "C" or "D" ? new BenchTag(value, better) : (BenchTag?)null;
        })];
}
