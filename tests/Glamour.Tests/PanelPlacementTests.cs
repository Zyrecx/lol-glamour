using Glamour.Host.Panel;

namespace Glamour.Tests;

public class PanelPlacementTests
{
    private static readonly PixelRect Screen = new(0, 0, 1920, 1040); // work area, taskbar excluded

    [Fact]
    public void Goes_against_the_right_edge_when_there_is_room()
    {
        var p = PanelPlacement.Compute(new(100, 50, 1380, 770), Screen, collapsed: false);
        Assert.Equal(PanelSide.Right, p.Side);
        Assert.Equal(new PixelRect(1380, 50, 1700, 770), p.Bounds);
    }

    [Fact]
    public void Tab_and_open_panel_stay_on_the_same_side()
    {
        var client = new PixelRect(640, 50, 1920, 770); // no room on the right
        var open = PanelPlacement.Compute(client, Screen, collapsed: false);
        var tab = PanelPlacement.Compute(client, Screen, collapsed: true);
        Assert.Equal(PanelSide.Left, tab.Side);
        Assert.Equal(open.Side, tab.Side);
        Assert.Equal(640, tab.Bounds.Right);
    }

    [Fact]
    public void Goes_left_when_the_client_sits_at_the_right_screen_edge()
    {
        var p = PanelPlacement.Compute(new(640, 50, 1920, 770), Screen, collapsed: false);
        Assert.Equal(PanelSide.Left, p.Side);
        Assert.Equal(new PixelRect(320, 50, 640, 770), p.Bounds);
    }

    [Fact]
    public void Size_follows_the_client_height_not_the_monitor()
    {
        // A 1600x900 client (the client's 125% size) on a big screen: 400 wide open, 50 as the rail.
        var big = new PixelRect(0, 0, 3840, 2100);
        var open = PanelPlacement.Compute(new(100, 50, 1700, 950), big, collapsed: false);
        Assert.Equal(1.25, open.Scale, 3);
        Assert.Equal(new PixelRect(1700, 50, 2100, 950), open.Bounds);
        Assert.Equal(50, PanelPlacement.Compute(new(100, 50, 1700, 950), big, collapsed: true).Bounds.Width);
    }

    [Theory]
    [InlineData(360, 0.75)] // very small client
    [InlineData(3000, 3)]
    public void Scale_is_clamped(int clientHeight, double expected)
    {
        var p = PanelPlacement.Compute(new(0, 0, 100, clientHeight), new(0, 0, 8000, 8000), collapsed: false);
        Assert.Equal(expected, p.Scale);
    }

    [Fact]
    public void Open_panel_with_no_room_beside_the_client_opens_inside_it()
    {
        // 1600x900 client centered on 1920x1080: 160 px each side, the open panel (400) fits nowhere outside.
        var screen = new PixelRect(0, 0, 1920, 1080);
        var client = new PixelRect(160, 90, 1760, 990);
        var open = PanelPlacement.Compute(client, screen, collapsed: false);
        Assert.True(open.Overlaps);
        Assert.False(open.Collapsed);
        Assert.Equal(new PixelRect(1360, 90, 1760, 990), open.Bounds); // right edge of the client, tie goes right
        var tab = PanelPlacement.Compute(client, screen, collapsed: true);
        Assert.False(tab.Overlaps);
        Assert.Equal(new PixelRect(1760, 90, 1810, 990), tab.Bounds); // the 50 px rail fits outside
    }

    [Fact]
    public void Small_laptop_screen_has_a_tab_only_until_asked()
    {
        // 1280x720 client on a 1366x768 screen: 43 px per side.
        var screen = new PixelRect(0, 0, 1366, 728);
        var client = new PixelRect(43, 0, 1323, 720);
        var tab = PanelPlacement.Compute(client, screen, collapsed: true);
        Assert.Equal(new PixelRect(1323, 0, 1363, 720), tab.Bounds);
        var open = PanelPlacement.Compute(client, screen, collapsed: false);
        Assert.True(open.Overlaps);
        Assert.Equal(320, open.Bounds.Width);
        Assert.Equal(1323, open.Bounds.Right);
    }

    [Fact]
    public void Tab_that_fits_nowhere_is_pushed_inside_the_screen()
    {
        var screen = new PixelRect(0, 0, 1280, 800);
        var p = PanelPlacement.Compute(new(0, 0, 1280, 720), screen, collapsed: true);
        Assert.Equal(1280, p.Bounds.Right);
        Assert.Equal(40, p.Bounds.Width);
        Assert.Equal(120, p.Bounds.Height);
        Assert.Equal(360, (p.Bounds.Top + p.Bounds.Bottom) / 2); // centered on the client (0..720)
    }

    [Fact]
    public void Fixed_left_side_is_kept_even_with_room_on_the_right()
    {
        var client = new PixelRect(400, 50, 1680, 770); // room on both sides
        var open = PanelPlacement.Compute(client, Screen, collapsed: false, PanelSide.Left);
        Assert.Equal(new PixelRect(80, 50, 400, 770), open.Bounds);
        var tab = PanelPlacement.Compute(client, Screen, collapsed: true, PanelSide.Left);
        Assert.Equal(new PixelRect(360, 50, 400, 770), tab.Bounds);
    }

    [Fact]
    public void Fixed_right_side_stays_outside_the_client_even_off_screen()
    {
        var client = new PixelRect(900, 50, 2180, 770); // dragged past the right screen edge; Auto would go left here
        var tab = PanelPlacement.Compute(client, Screen, collapsed: true, PanelSide.Right);
        Assert.Equal(new PixelRect(2180, 50, 2220, 770), tab.Bounds);
        var open = PanelPlacement.Compute(client, Screen, collapsed: false, PanelSide.Right);
        Assert.False(open.Overlaps);
        Assert.Equal(new PixelRect(2180, 50, 2500, 770), open.Bounds);
    }

    [Fact]
    public void Fixed_left_side_stays_outside_the_client_even_off_screen()
    {
        // Small laptop: 43 px each side; the rail fits outside the left edge, the open panel is cut by the screen edge.
        var client = new PixelRect(43, 0, 1323, 720);
        var screen = new PixelRect(0, 0, 1366, 728);
        Assert.Equal(new PixelRect(3, 0, 43, 720), PanelPlacement.Compute(client, screen, collapsed: true, PanelSide.Left).Bounds);
        var open = PanelPlacement.Compute(client, screen, collapsed: false, PanelSide.Left);
        Assert.False(open.Overlaps);
        Assert.Equal(new PixelRect(-277, 0, 43, 720), open.Bounds);
    }

    [Fact]
    public void Open_width_comes_from_the_page_but_the_side_from_the_widest_panel()
    {
        var client = new PixelRect(330, 50, 1610, 770); // 310 px free on the right: a 288 section would fit, the widest (320) would not
        var open = PanelPlacement.Compute(client, Screen, collapsed: false, openWidth: 288);
        Assert.Equal(PanelSide.Left, open.Side);
        Assert.Equal(new PixelRect(42, 50, 330, 770), open.Bounds);
        Assert.Equal(PanelSide.Left, PanelPlacement.Compute(client, Screen, collapsed: true).Side);
        Assert.Equal(320, PanelPlacement.Compute(new(100, 50, 1380, 770), Screen, collapsed: false, openWidth: 900).Bounds.Width); // clamped to the widest
    }

    [Fact]
    public void Height_follows_the_client_but_stays_inside_the_work_area()
    {
        var p = PanelPlacement.Compute(new(100, -20, 1380, 1200), Screen, collapsed: false);
        Assert.Equal(0, p.Bounds.Top);
        Assert.Equal(1040, p.Bounds.Bottom);
    }
}
