using Glamour.Host.Panel;

namespace Glamour.Tests;

public class AcceptLayoutTests
{
    private static readonly PixelRect Base = new(0, 0, 1280, 720);

    [Fact]
    public void Outline_at_no_offset_follows_the_measured_gold_frame()
    {
        var o = AcceptLayout.Outline(Base, 0)!;
        Assert.Equal((639.5, 527.5), o[0]);
        Assert.Equal(o[0], o[^1]);
        Assert.Equal(729, o[1].X, tolerance: 0.5); // top right corner
        Assert.Equal(748.5, o[2].X, tolerance: 1); // bottom right corner
        Assert.Equal(566, o[2].Y, tolerance: 1);
        var low = o.MaxBy(p => p.Y);
        Assert.Equal(592.5, low.Y, tolerance: 0.5); // the bottom's middle
        Assert.Equal(639.5, low.X, tolerance: 1);
        // Symmetric about the centre.
        var xs = o.Select(p => p.X).ToList();
        Assert.Equal(639.5, (xs.Min() + xs.Max()) / 2, tolerance: 0.01);
    }

    [Fact]
    public void Offset_moves_every_edge_out()
    {
        var o = AcceptLayout.Outline(Base, 4)!;
        Assert.Equal(523.5, o[0].Y, tolerance: 0.01);
        Assert.Equal(596.5, o.Max(p => p.Y), tolerance: 0.5);
        Assert.True(o[2].X > 748.5 + 3);
    }

    [Fact]
    public void Scales_with_the_window_and_follows_it()
    {
        var o = AcceptLayout.Outline(new PixelRect(100, 50, 100 + 1920, 50 + 1080), 0)!;
        Assert.Equal((100 + 639.5 * 1.5, 50 + 527.5 * 1.5), o[0]);
    }

    [Fact]
    public void Not_16_by_9_is_not_trusted() => Assert.Null(AcceptLayout.Outline(new PixelRect(0, 0, 1280, 800), 0));
}
