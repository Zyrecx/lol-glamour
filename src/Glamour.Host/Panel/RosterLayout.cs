namespace Glamour.Host.Panel;

public enum RosterTeam { Mine, Theirs }

/// <summary>One player's card in the client's own champ select roster, as fractions of the client window (0..1 from its top-left corner).</summary>
/// <param name="Index">Position from the top of that team's column; the client lists each team by cell (the order is [UNVERIFIED], SPEC "Click layer").</param>
/// <summary>An enemy's round champion portrait on its card in the client, in screen pixels: centre and radius.</summary>
public readonly record struct RosterPortrait(int Index, double X, double Y, double R);

public readonly record struct RosterSlot(RosterTeam Team, int Index, double X, double Y, double W, double H)
{
    public bool Contains(double fx, double fy) => fx >= X && fx < X + W && fy >= Y && fy < Y + H;
}

/// <summary>
/// Where a click on the League client lands in its champ select roster (SPEC "Click layer on the client's roster"). The client draws its 1280x720 base
/// layout scaled to the window, so a slot is the same fraction of the window at any size. Pure: the desktop app feeds it the client rectangle and the click.
/// </summary>
public static class RosterLayout
{
    // 5v5 draft, measured on the 1280x720 client in PLANNING, BAN_PICK and FINALIZATION (the rows do not move between them): five 80 px rows from
    // y 95 under each other, allies in the left 300 px, enemies from x 1000 to the right edge. Top to bottom is cell order on both sides [LIVE].
    // An ally card counts only from its portrait's left edge (x 55 in BAN_PICK and FINALIZATION) and in its upper 5-55 px: left of that sit the summoner
    // spells (a click there opens the client's spell picker), and in FINALIZATION the chat grows over the last row from y 472.
    private const double Base = 720, RowTop = 95 / Base, RowHeight = 80 / Base;
    private static readonly RosterSlot[] Draft5v5 =
    [
        .. Enumerable.Range(0, 5).Select(i => new RosterSlot(RosterTeam.Mine, i, 55 / 1280.0, RowTop + i * RowHeight + 5 / Base, 245 / 1280.0, 50 / Base)),
        .. Enumerable.Range(0, 5).Select(i => new RosterSlot(RosterTeam.Theirs, i, 1000 / 1280.0, RowTop + i * RowHeight, 280 / 1280.0, RowHeight)),
    ];

    // The round portrait on each enemy card, measured on the 1280x720 shots: centre y 134.5 and 80 px further per row, radius 30 (the client's red ring
    // is its outer 2 px). Its centre x is 1223.5, or 1193.5 in BAN_PICK on the card of an enemy who bans while bans are still being made (a ban box sits
    // to its right; bots have none, custom shots cs-20261009-150934 and -221818). In the pick part the bans move to the top bar and the portrait is back at
    // 1223.5 (ranked shot); `board.BanCells` is empty then.
    private const double PortraitTop = 134.5 / Base, PortraitRadius = 30 / Base;
    private static double PortraitX(string stage, bool bans) => (stage == "BAN_PICK" && bans ? 1193.5 : 1223.5) / 1280;

    /// <summary>
    /// Where the enemies' portraits are on screen in this timer stage, top to bottom (cell order), for marking them in the client (SPEC "Click layer on the
    /// client's roster"); null where no layout is measured, the window is not 16:9, or in PLANNING (the enemy column is empty then).
    /// <paramref name="bans"/>: per row, whether that enemy has a ban action (null: none do).
    /// </summary>
    public static IReadOnlyList<RosterPortrait>? EnemyPortraits(PixelRect client, string mode, int myTeamSize, int theirTeamSize, string stage, IReadOnlyList<bool>? bans = null)
    {
        if (For(mode, myTeamSize, theirTeamSize) is null || !Trusted(client) || stage == "PLANNING") return null;
        return [.. Enumerable.Range(0, 5).Select(i => new RosterPortrait(i, client.Left + PortraitX(stage, bans is not null && i < bans.Count && bans[i]) * client.Width,
            client.Top + (PortraitTop + i * RowHeight) * client.Height, PortraitRadius * client.Height))];
    }

    /// <summary>Every size the client offers is 16:9 and scales the same layout, so a window of another shape (minimized, mid-resize, a future layout) is not trusted.</summary>
    public static bool Trusted(PixelRect client) => client.Width > 0 && client.Height > 0 && Math.Abs(client.Width / (double)client.Height - 16 / 9.0) <= 0.02;

    /// <summary>The measured slots for a kind of champ select, or null where nothing is measured (ARAM, Arena, uneven teams): the click layer then does nothing.</summary>
    public static IReadOnlyList<RosterSlot>? For(string mode, int myTeamSize, int theirTeamSize) =>
        mode == "draft" && myTeamSize == 5 && theirTeamSize == 5 ? Draft5v5 : null;

    /// <summary>The click as fractions of the client rectangle, or null when it is outside or the window is not <see cref="Trusted"/>.</summary>
    public static (double X, double Y)? Fraction(PixelRect client, int x, int y)
    {
        if (!Trusted(client) || x < client.Left || x >= client.Right || y < client.Top || y >= client.Bottom) return null;
        return ((x - client.Left) / (double)client.Width, (y - client.Top) / (double)client.Height);
    }

    /// <summary>The slot under a click at screen pixel (x, y), or null.</summary>
    public static RosterSlot? HitTest(PixelRect client, int x, int y, IReadOnlyList<RosterSlot> slots)
    {
        if (Fraction(client, x, y) is not var (fx, fy)) return null;
        foreach (var s in slots)
            if (s.Contains(fx, fy)) return s;
        return null;
    }
}
