using Glamour.Host.Panel;

namespace Glamour.Tests;

public class RosterLayoutTests
{
    // A made-up layout: two columns of two cards each.
    private static readonly RosterSlot[] Slots =
    [
        new(RosterTeam.Mine, 0, 0.0, 0.2, 0.2, 0.1),
        new(RosterTeam.Mine, 1, 0.0, 0.3, 0.2, 0.1),
        new(RosterTeam.Theirs, 0, 0.8, 0.2, 0.2, 0.1),
        new(RosterTeam.Theirs, 1, 0.8, 0.3, 0.2, 0.1),
    ];

    private static readonly PixelRect Square = new(0, 0, 1600, 900);

    [Fact]
    public void Same_slot_at_any_client_size_and_position()
    {
        var small = new PixelRect(100, 50, 1124, 626); // 1024x576
        var big = new PixelRect(-1920, 0, 0, 1080); // 1920x1080 on a monitor left of the main one
        Assert.Equal(new RosterSlot(RosterTeam.Theirs, 1, 0.8, 0.3, 0.2, 0.1), RosterLayout.HitTest(small, 100 + 900, 50 + 200, Slots));
        Assert.Equal(new RosterSlot(RosterTeam.Theirs, 1, 0.8, 0.3, 0.2, 0.1), RosterLayout.HitTest(big, -1920 + 1700, 380, Slots));
    }

    [Fact]
    public void Stacked_cards_split_at_their_shared_edge()
    {
        Assert.Equal(1, RosterLayout.HitTest(Square, 10, 272, Slots)?.Index); // y = 0.3 is the edge (270)
        Assert.Equal(0, RosterLayout.HitTest(Square, 10, 268, Slots)?.Index);
    }

    [Fact]
    public void Nothing_between_the_columns_or_outside_the_client()
    {
        Assert.Null(RosterLayout.HitTest(Square, 800, 225, Slots));
        Assert.Null(RosterLayout.HitTest(Square, 1600, 225, Slots)); // right edge is outside
        Assert.Null(RosterLayout.HitTest(Square, -1, 225, Slots));
        Assert.Null(RosterLayout.Fraction(new PixelRect(0, 0, 0, 0), 0, 0));
    }

    [Fact]
    public void A_window_that_is_not_16_by_9_is_not_trusted()
    {
        Assert.Null(RosterLayout.Fraction(new PixelRect(0, 0, 1000, 1000), 500, 500));
        Assert.Null(RosterLayout.Fraction(new PixelRect(0, 0, 160, 28), 10, 10)); // minimized or mid-resize
        Assert.NotNull(RosterLayout.Fraction(new PixelRect(0, 0, 1281, 720), 10, 10)); // a pixel off from rounding is fine
    }

    [Fact]
    public void Draft_layout_only_for_five_against_five()
    {
        Assert.Equal(10, RosterLayout.For("draft", 5, 5)?.Count);
        Assert.Null(RosterLayout.For("aram", 5, 5));
        Assert.Null(RosterLayout.For("draft", 3, 3));
    }

    // The clicks the user made on the real 1280x720 client in a custom draft (glamour.log 2026-10-09 13:40), each on a card the screenshot shows there.
    [Theory]
    [InlineData(0.9367, 0.1736, 0)] // Varus
    [InlineData(0.9523, 0.2556, 1)] // Tryndamere, top of its row
    [InlineData(0.9664, 0.3306, 1)] // Tryndamere, bottom of its row
    [InlineData(0.9586, 0.4083, 2)] // Kog'Maw
    [InlineData(0.8828, 0.3958, 2)] // Kog'Maw, left of the name
    [InlineData(0.9563, 0.5431, 3)] // Urgot
    [InlineData(0.9547, 0.6778, 4)] // Ashe
    public void Live_clicks_on_enemy_cards_land_on_that_enemy(double fx, double fy, int enemy)
    {
        var slots = RosterLayout.For("draft", 5, 5)!;
        foreach (var client in new[] { new PixelRect(0, 0, 1280, 720), new PixelRect(300, 200, 1324, 776), new PixelRect(-1600, 0, 0, 900) })
        {
            var hit = RosterLayout.HitTest(client, client.Left + (int)(fx * client.Width), client.Top + (int)(fy * client.Height), slots);
            Assert.Equal((RosterTeam.Theirs, enemy), (hit?.Team, hit?.Index));
        }
    }

    [Theory]
    [InlineData(0.0742, 0.1847, 0)]
    [InlineData(0.0758, 0.3111, 1)]
    [InlineData(0.0703, 0.4125, 2)]
    [InlineData(0.0773, 0.5347, 3)]
    [InlineData(0.0719, 0.6292, 4)]
    public void Live_clicks_on_ally_cards_land_on_that_ally(double fx, double fy, int ally)
    {
        var hit = RosterLayout.HitTest(new PixelRect(0, 0, 1280, 720), (int)(fx * 1280), (int)(fy * 720), RosterLayout.For("draft", 5, 5)!);
        Assert.Equal((RosterTeam.Mine, ally), (hit?.Team, hit?.Index));
    }

    [Fact]
    public void Champion_grid_ban_button_and_quit_are_not_cards()
    {
        var slots = RosterLayout.For("draft", 5, 5)!;
        var client = new PixelRect(0, 0, 1280, 720);
        Assert.Null(RosterLayout.HitTest(client, 720, 478, slots)); // 0.5625,0.6639: a champion in the grid
        Assert.Null(RosterLayout.HitTest(client, 637, 613, slots)); // the Ban button
        Assert.Null(RosterLayout.HitTest(client, 1072, 683, slots)); // Quit
        Assert.Null(RosterLayout.HitTest(client, 1227, 15, slots)); // the client's settings cog
    }

    [Fact]
    public void Enemy_portraits_scale_with_the_client()
    {
        // At 1280x720 the measured pixels: Twisted Fate's portrait centre and red ring in the FINALIZATION shot, then 80 px per row.
        var at720 = RosterLayout.EnemyPortraits(new PixelRect(0, 0, 1280, 720), "draft", 5, 5, "FINALIZATION")!;
        Assert.Equal(5, at720.Count);
        Assert.Equal((1223.5, 134.5, 30.0), (Math.Round(at720[0].X, 3), Math.Round(at720[0].Y, 3), Math.Round(at720[0].R, 3)));
        Assert.Equal(134.5 + 4 * 80, at720[4].Y, 3);
        // Twice the size on a monitor left of the main one: the same spot, doubled, from that window's corner.
        var big = RosterLayout.EnemyPortraits(new PixelRect(-2560, 100, 0, 1540), "draft", 5, 5, "FINALIZATION")!;
        Assert.Equal((-2560 + 2447.0, 100 + 269.0, 60.0), (Math.Round(big[0].X, 3), Math.Round(big[0].Y, 3), Math.Round(big[0].R, 3)));
    }

    [Fact]
    public void No_portraits_without_a_layout_or_on_a_window_that_is_not_16_by_9()
    {
        Assert.Null(RosterLayout.EnemyPortraits(new PixelRect(0, 0, 1280, 720), "aram", 5, 5, "BAN_PICK"));
        Assert.Null(RosterLayout.EnemyPortraits(new PixelRect(0, 0, 1000, 1000), "draft", 5, 5, "BAN_PICK"));
        Assert.Null(RosterLayout.EnemyPortraits(new PixelRect(0, 0, 1280, 720), "draft", 5, 5, "PLANNING"));
    }

    [Fact]
    public void Enemy_portraits_sit_left_of_the_ban_boxes_in_ban_pick()
    {
        // The red ring in the BAN_PICK shot (logs\shots\cs-20261009-135409-BAN_PICK.png): x 1164-1223, y 105-164 on the first row.
        var p = RosterLayout.EnemyPortraits(new PixelRect(0, 0, 1280, 720), "draft", 5, 5, "BAN_PICK", [true, true, true, true, true])!;
        Assert.Equal((1193.5, 134.5, 30.0), (Math.Round(p[0].X, 3), Math.Round(p[0].Y, 3), Math.Round(p[0].R, 3)));
    }

    [Fact]
    public void Enemies_who_do_not_ban_keep_their_portrait_in_place_in_ban_pick()
    {
        // Bots in a custom have no ban box (cs-20261009-221818-BAN_PICK-picks.png): portrait where FINALIZATION has it.
        var bots = RosterLayout.EnemyPortraits(new PixelRect(0, 0, 1280, 720), "draft", 5, 5, "BAN_PICK")!;
        Assert.All(bots, p => Assert.Equal(1223.5, p.X, 3));
        var mixed = RosterLayout.EnemyPortraits(new PixelRect(0, 0, 1280, 720), "draft", 5, 5, "BAN_PICK", [false, true, false, false, false])!;
        Assert.Equal([1223.5, 1193.5, 1223.5, 1223.5, 1223.5], mixed.Select(p => Math.Round(p.X, 3)));
    }

    [Theory]
    [InlineData(32, 135)] // first ally's summoner spells (FINALIZATION) or ban box (BAN_PICK)
    [InlineData(100, 480)] // the chat over the last row in FINALIZATION
    public void Spells_and_chat_beside_the_ally_cards_are_not_cards(int x, int y) =>
        Assert.Null(RosterLayout.HitTest(new PixelRect(0, 0, 1280, 720), x, y, RosterLayout.For("draft", 5, 5)!));
}
