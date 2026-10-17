namespace Glamour.Host.Panel;

/// <summary>A rectangle in physical screen pixels (left/top inclusive, right/bottom exclusive, as Windows reports them).</summary>
public readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
}

public enum PanelSide { Right, Left }

/// <param name="Scale">The panel's size relative to its design size: the client's height over 720, the height of the client's own 1280x720 base layout.</param>
/// <param name="Overlaps">The open panel sits inside the client (no room beside it): it covers part of the client, so it opens only when asked.</param>
public readonly record struct PanelPlacement(PixelRect Bounds, PanelSide Side, bool Collapsed, double Scale = 1, bool Overlaps = false)
{
    /// <summary>Widest open panel and the collapsed rail at the client's 720 px height (design units). The page picks its open width up to the widest.</summary>
    public const int MaxOpenWidth = 320, RailWidth = 40, OverlapTabHeight = 120, ClientBaseHeight = 720;
    public const double MinScale = 0.75, MaxScale = 3;

    /// <summary>
    /// Where the side panel goes next to the League client: against its right edge, or its left edge when the right has no room on the client's
    /// monitor. Same height as the client, kept inside the work area. Everything scales with the client's height (physical pixels), so the panel keeps
    /// its proportion to the client at any client size, monitor resolution or Windows scale; the page is zoomed by <see cref="Scale"/> over the monitor scale.
    /// With no room for the open panel beside the client it opens inside the client's nearer edge (<see cref="Overlaps"/>); the tab then sits outside where it fits,
    /// else short at the screen edge, over the client.
    /// A <paramref name="fixedSide"/> (the user's choice) always sits outside the client on that side, even where the screen edge cuts it off; it never covers the client.
    /// <paramref name="openWidth"/> is what the open section asked for (design units); the side is chosen by the widest open panel, so switching sections never moves it.
    /// </summary>
    public static PanelPlacement Compute(PixelRect client, PixelRect workArea, bool collapsed, PanelSide? fixedSide = null, int openWidth = MaxOpenWidth)
    {
        var scale = Math.Clamp(client.Height / (double)ClientBaseHeight, MinScale, MaxScale);
        var widest = (int)Math.Round(MaxOpenWidth * scale);
        var tabWidth = (int)Math.Round(RailWidth * scale);
        var asked = (int)Math.Round(Math.Clamp(openWidth, RailWidth, MaxOpenWidth) * scale);
        var top = Math.Max(client.Top, workArea.Top);
        var bottom = Math.Min(client.Bottom, workArea.Bottom);
        if (bottom <= top) (top, bottom) = (workArea.Top, workArea.Bottom);

        PanelPlacement At(int left, int width, PanelSide side, bool tab, bool overlaps = false) => new(new PixelRect(left, top, left + width, bottom), side, tab, scale, overlaps);

        var width = collapsed ? tabWidth : asked;
        if (fixedSide == PanelSide.Right) return At(client.Right, width, PanelSide.Right, collapsed);
        if (fixedSide == PanelSide.Left) return At(client.Left - width, width, PanelSide.Left, collapsed);
        // The side is chosen by the WIDEST open width, so the rail and every open section are always on the same side of the client.
        if (client.Right + widest <= workArea.Right) return At(client.Right, width, PanelSide.Right, collapsed);
        if (client.Left - widest >= workArea.Left) return At(client.Left - width, width, PanelSide.Left, collapsed);
        // No room for the open panel: the side with more room around the client.
        var goRight = workArea.Right - client.Right >= client.Left - workArea.Left;
        // Asked to open, it goes inside the client at that edge.
        if (!collapsed)
            return goRight
                ? At(Math.Max(client.Left, client.Right - asked), Math.Min(asked, client.Width), PanelSide.Right, false, true)
                : At(client.Left, Math.Min(asked, client.Width), PanelSide.Left, false, true);
        if (client.Right + tabWidth <= workArea.Right) return At(client.Right, tabWidth, PanelSide.Right, true);
        if (client.Left - tabWidth >= workArea.Left) return At(client.Left - tabWidth, tabWidth, PanelSide.Left, true);
        // The tab does not fit either: it hugs that screen edge, short and centered so it does not cover the client's window buttons.
        var height = Math.Min(bottom - top, (int)Math.Round(OverlapTabHeight * scale));
        var mid = (top + bottom) / 2;
        PanelPlacement Short(int x, PanelSide side) => new(new PixelRect(x, mid - height / 2, x + tabWidth, mid - height / 2 + height), side, true, scale);
        return goRight
            ? Short(Math.Max(workArea.Left, workArea.Right - tabWidth), PanelSide.Right)
            : Short(workArea.Left, PanelSide.Left);
    }
}
