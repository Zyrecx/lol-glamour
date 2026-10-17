namespace Glamour.Host.Panel;

/// <summary>
/// Where the game's loading screen draws its ten player cards (SPEC "Parties on the loading screen"). Measured on Mayhem and ranked draft loading screens at
/// 1920x1080 (borderless): two rows of five, centred; scaled here by the window height like <see cref="AugmentLayout"/> (other sizes [UNVERIFIED]).
/// Every card ends in a flat plate (13,16,19) under the player's name; while most plates are there, the loading screen is. My own name is the gold one.
/// </summary>
public static class LoadingLayout
{
    // At 1080p: cards 255 px wide every 296 px from x 237, rows at y 68 and 591, 461 px tall; the plate sampled 415 px under a card's top, 12 px in
    // (a ranked border covers it from 440 px down, LIVE: drafts sampled lower read no plates at all).
    private const double H = 1080, FirstCenter = (237 + 127.5 - 960) / H, Pitch = 296 / H, CardWidth = 255 / H, CardHeight = 461 / H, TopRow = 68 / H, BottomRow = 591 / H;
    private const double PlateDown = 415 / H, PlateIn = 12 / H;
    public static readonly (int R, int G, int B) Plate = (13, 16, 19);
    private const int PlateTolerance = 6;
    /// <summary>Plates that must show for the loading screen to count (one card is drawn highlighted, the cards fly in for the first seconds).</summary>
    public const int PlatesNeeded = 7;

    public static PixelRect Card(PixelRect client, bool top, int slot)
    {
        double h = client.Height, mid = client.Left + client.Width / 2.0, cx = FirstCenter + slot * Pitch;
        var y = client.Top + (top ? TopRow : BottomRow) * h;
        return new((int)Math.Round(mid + (cx - CardWidth / 2) * h), (int)Math.Round(y), (int)Math.Round(mid + (cx + CardWidth / 2) * h), (int)Math.Round(y + CardHeight * h));
    }

    /// <summary>The two plate pixels of a card, one near each side.</summary>
    public static (int X, int Y)[] PlatePoints(PixelRect client, bool top, int slot)
    {
        var c = Card(client, top, slot);
        int y = (int)Math.Round(c.Top + PlateDown * client.Height), inset = Math.Max(2, (int)Math.Round(PlateIn * client.Height));
        return [(c.Left + inset, y), (c.Right - 1 - inset, y)];
    }

    /// <summary>How many of the ten cards show their plate, given the screen's colour at a point.</summary>
    public static int PlatesShown(PixelRect client, Func<int, int, (int R, int G, int B)> pixel)
    {
        var shown = 0;
        foreach (var top in new[] { true, false })
            for (var slot = 0; slot < 5; slot++)
                if (PlatePoints(client, top, slot).All(p => IsPlate(pixel(p.X, p.Y)))) shown++;
        return shown;
    }

    // My own name is drawn in gold, everyone else's in white: 137 gold pixels on my card and none on the others in four games' shots (LIVE). The name's
    // line runs 420-438 px under a card's top at 1080p, within 80 px of its middle; a card still loading shows a profile there instead (no gold).
    private const double NameTop = 420 / H, NameBottom = 438 / H, NameHalf = 80 / H;

    /// <summary>The box a card's player name is read in.</summary>
    public static PixelRect NameBox(PixelRect client, bool top, int slot)
    {
        var c = Card(client, top, slot);
        double h = client.Height, mid = (c.Left + c.Right) / 2.0;
        return new((int)Math.Round(mid - NameHalf * h), (int)Math.Round(c.Top + NameTop * h), (int)Math.Round(mid + NameHalf * h) + 1, (int)Math.Round(c.Top + NameBottom * h) + 1);
    }

    /// <summary>
    /// My card: the one card whose name is gold (sampled every other pixel). Null when none or more than one is: a card still loading, or not the
    /// loading screen.
    /// </summary>
    public static (bool Top, int Slot)? MyCard(PixelRect client, Func<int, int, (int R, int G, int B)> pixel)
    {
        var needed = Math.Max(4, (int)Math.Round(12 * Math.Pow(client.Height / H, 2))); // ~34 at 1080p, a quarter of the name's gold pixels
        (bool, int)? mine = null;
        foreach (var top in new[] { true, false })
            for (var slot = 0; slot < 5; slot++)
            {
                var box = NameBox(client, top, slot);
                var gold = 0;
                for (var y = box.Top; y < box.Bottom; y += 2)
                    for (var x = box.Left; x < box.Right; x += 2)
                        if (IsGold(pixel(x, y))) gold++;
                if (gold < needed) continue;
                if (mine is not null) return null;
                mine = (top, slot);
            }
        return mine;
    }

    private static bool IsGold((int R, int G, int B) c) => c.R > 180 && c.G > 140 && c.B < 110 && c.R - c.B > 110;

    private static bool IsPlate((int R, int G, int B) c) =>
        Math.Abs(c.R - Plate.R) <= PlateTolerance && Math.Abs(c.G - Plate.G) <= PlateTolerance && Math.Abs(c.B - Plate.B) <= PlateTolerance;

    /// <summary>The cards fit the window (a narrow window would push the outer ones out).</summary>
    public static bool Fits(PixelRect client) =>
        client.Height >= 400 && Card(client, true, 0).Left >= client.Left && Card(client, true, 4).Right <= client.Right;
}
