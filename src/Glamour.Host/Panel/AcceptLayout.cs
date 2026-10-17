namespace Glamour.Host.Panel;

/// <summary>
/// The Accept button on the client's match found popup (SPEC "Click layer": Accept ring), for drawing round it. Like <see cref="RosterLayout"/> the client
/// draws its 1280x720 base layout scaled to the window, so the button is the same fraction of the window at any size. Pure.
/// </summary>
public static class AcceptLayout
{
    // The button's gold frame, measured by its colour on the 1280x720 shot (ARAM Mayhem, cs-20261009-194005-READY_CHECK.png): a flat top on row 527.5
    // from x 550 to 729, sides slanting out to corners at x 530.5 / 748.5 (row ~566), and a bottom bowed down to row 592.5 at the middle, which fits a
    // circle of radius 243.8 (it passes 579 at x 560/720, 575.5 at 550.5, 571 at 540). Centre x 639.5.
    private const double CentreX = 639.5, Top = 527.5, TopHalf = 89.5, Bottom = 592.5, ArcRadius = 243.8;
    // Each side runs from the top corner (89.5 out) down to the bottom corner (109 out, 38.5 rows lower).
    private const double SideDx = 109 - 89.5, SideDy = 566 - 527.5;

    /// <summary>
    /// The frame's outline <paramref name="offset"/> base px outside the gold line, in screen pixels: clockwise from the middle of the top edge, the last
    /// point back at the first, so the halfway point by length is the middle of the bottom. Null when the window is not 16:9.
    /// </summary>
    public static IReadOnlyList<(double X, double Y)>? Outline(PixelRect client, double offset)
    {
        if (!RosterLayout.Trusted(client)) return null;
        var s = client.Height / 720.0;
        var pts = Base(offset);
        return [.. pts.Select(p => (client.Left + p.X * s, client.Top + p.Y * s))];
    }

    /// <summary>The outline in base px (1280x720).</summary>
    internal static List<(double X, double Y)> Base(double d)
    {
        // The side line moved out by d along its normal, then cut by the top (moved up by d) and by the arc (radius + d, same centre).
        var len = Math.Sqrt(SideDx * SideDx + SideDy * SideDy);
        var (nx, ny) = (SideDy / len, -SideDx / len); // the right side's outward normal
        var (sx, sy) = (CentreX + TopHalf + nx * d, Top + ny * d); // a point on the moved right side
        var (ux, uy) = (SideDx / len, SideDy / len);
        var top = Top - d;
        var topRight = sx + (top - sy) / uy * ux;
        var (cy, r) = (Bottom - ArcRadius, ArcRadius + d);
        // Where the moved side meets the circle: |s + t*u - c|^2 = r^2.
        var (px, py) = (sx - CentreX, sy - cy);
        var b = px * ux + py * uy;
        var t = -b + Math.Sqrt(b * b - (px * px + py * py - r * r));
        var (cx1, cy1) = (sx + t * ux, sy + t * uy); // the right bottom corner
        var a0 = Math.Atan2(cy1 - cy, cx1 - CentreX); // from the circle's centre, below it

        var pts = new List<(double, double)> { (CentreX, top), (topRight, top), (cx1, cy1) };
        const int Steps = 24;
        for (var i = 1; i < Steps; i++) // round the bottom from the right corner to the left one
        {
            var a = a0 + (Math.PI - 2 * a0) * i / Steps;
            pts.Add((CentreX + r * Math.Cos(a), cy + r * Math.Sin(a)));
        }
        pts.Add((2 * CentreX - cx1, cy1));
        pts.Add((2 * CentreX - topRight, top));
        pts.Add((CentreX, top));
        return pts;
    }
}
