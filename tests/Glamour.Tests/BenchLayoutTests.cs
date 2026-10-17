using Glamour.Host.Panel;

namespace Glamour.Tests;

public class BenchLayoutTests
{
    [Fact]
    public void Mayhem_slots_sit_on_the_measured_frames_at_1280x720()
    {
        var slots = BenchLayout.Slots(new PixelRect(0, 0, 1280, 720), "mayhem")!;
        Assert.Equal(10, slots.Count);
        // Left frame edges read off the live shots (bench-20261010-184138.png, row 35).
        Assert.Equal([352, 410, 469, 527, 586, 645, 703, 762, 821, 879], slots.Select(s => (int)s.X));
        Assert.All(slots, s => Assert.Equal((10, 50), (s.Y, s.Size)));
    }

    [Fact]
    public void The_strip_scales_with_the_client_and_follows_its_position()
    {
        var slot = BenchLayout.Slots(new PixelRect(-1920, 100, 0, 1180), "mayhem")![9]; // 1920x1080 on a monitor left of the main one
        Assert.Equal((-1920 + 879 * 1.5, 100 + 15.0, 75.0), (slot.X, slot.Y, slot.Size));
    }

    [Fact]
    public void Nothing_where_the_strip_is_not_measured()
    {
        Assert.Null(BenchLayout.Slots(new PixelRect(0, 0, 1280, 720), "aram")); // ARAM 450 not captured yet
        Assert.Null(BenchLayout.Slots(new PixelRect(0, 0, 1280, 720), "draft"));
        Assert.Null(BenchLayout.Slots(new PixelRect(0, 0, 1280, 1024), "mayhem"));
    }

    [Fact]
    public void Bench_tags_read_win_rates_tiers_and_the_better_mark_by_slot()
    {
        var tags = BenchTag.ParseAll("52.4,^55,,S+,^A,x,101");
        Assert.Equal([new BenchTag("52.4%", false), new BenchTag("55.0%", true), null, new BenchTag("S+", false), new BenchTag("A", true), null, null], tags);
        Assert.Empty(BenchTag.ParseAll(""));
        Assert.Equal(10, BenchTag.ParseAll(string.Join(",", Enumerable.Repeat("50", 12))).Length);
    }
}
