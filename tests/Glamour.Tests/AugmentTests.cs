using System.Diagnostics;
using System.IO.Compression;
using Glamour.Core;
using Glamour.Host;
using Glamour.Host.Panel;

namespace Glamour.Tests;

/// <summary>
/// Augment tags (SPEC "PC app direction" 5). Screens: ARAM Mayhem Tracker's test screenshots (Copyright (c) 2026 Sygnano, MIT License, github.com/Sygnano/aram-mayhem-tracker), greyed by
/// `tools/gray.ps1`; data: aramkit's 16.20 files and CommunityDragon's augment lists, as fetched on 2026-10-10. Nothing here touches the network.
/// </summary>
public class AugmentTests
{
    private static GrayImage Screen(string name)
    {
        using var file = File.OpenRead(FixtureFiles.PathOf(name + ".gray.gz"));
        using var gz = new GZipStream(file, CompressionMode.Decompress);
        return GrayImage.Read(gz);
    }

    private static double ButtonScore(GrayImage screen, int shiftY = 0)
    {
        var client = new PixelRect(0, 0, screen.Width, screen.Height);
        var r = AugmentLayout.ButtonSearch(client);
        var (w, h) = AugmentLayout.ButtonSize(client);
        return AugmentButton.Score(screen.Crop(r.Left, r.Top + shiftY, r.Width, r.Height), AugmentButton.Template, w, h);
    }

    public static TheoryData<string> Offers => new()
    {
        "augments_1920x1080_gold", "augments_1920x1080_rerolls", "augments_2000x844_gold", "augments_1280x1024_rerolls", "augments_3440x1440_rerolls",
    };

    [Theory]
    [MemberData(nameof(Offers))]
    public void The_hide_button_is_found_on_every_offer_screen(string shot)
    {
        var screen = Screen(shot);
        Assert.True(AugmentLayout.Fits(new PixelRect(0, 0, screen.Width, screen.Height)));
        Assert.True(ButtonScore(screen) >= AugmentButton.Present, $"{shot}: {ButtonScore(screen):0.000}");
    }

    [Theory]
    [MemberData(nameof(Offers))]
    public void The_same_box_a_tenth_of_the_screen_higher_is_no_button(string shot)
    {
        var screen = Screen(shot);
        Assert.True(ButtonScore(screen, -screen.Height / 10) < AugmentButton.Present);
    }

    [Fact]
    public void No_button_in_champ_select()
    {
        Assert.True(ButtonScore(Screen("no-augments_1920x1080")) < AugmentButton.Present);
    }

    [Fact]
    public void A_tick_without_an_offer_costs_a_few_milliseconds()
    {
        var screen = Screen("no-augments_1920x1080");
        ButtonScore(screen);
        var watch = Stopwatch.StartNew();
        for (var i = 0; i < 20; i++) ButtonScore(screen);
        Assert.True(watch.Elapsed.TotalMilliseconds / 20 < 15, $"{watch.Elapsed.TotalMilliseconds / 20:0.0} ms");
    }

    private static string Json(string name) => File.ReadAllText(FixtureFiles.PathOf(name));
    private static readonly IReadOnlyList<AugmentInfo> Pool = AugmentDataSource.ParsePool(Json("cdragon-augment-lists.json"), Json("cdragon-cherry-augments.json"));
    private static readonly AugmentTable Lillia = AugmentDataSource.ParseTable(Json("aramkit-champion-details-876.json"), "16.20", champion: true)!;
    private static readonly AugmentTable Everyone = AugmentDataSource.ParseTable(Json("aramkit-augment-rankings.json"), "16.20", champion: false)!;
    private static int Id(string name) => Pool.First(a => a.Name == name).Id;

    [Fact]
    public void The_mayhem_pool_is_the_kiwi_list_with_names_and_rarities()
    {
        Assert.InRange(Pool.Count, 150, 300);
        Assert.Contains(Pool, a => a is { Name: "Shrink Engine", Id: 1373, Rarity: "gold" });
        Assert.Contains(Pool, a => a.Name == "Overflow");
        Assert.All(Pool, a => Assert.Contains(a.Rarity, new[] { "silver", "gold", "prismatic" }));
    }

    [Fact]
    public void Champion_details_keep_the_champions_win_rate_and_every_offer()
    {
        Assert.Equal(0.5667, Lillia.ChampionWinRate, 4);
        Assert.Equal(0.5, Everyone.ChampionWinRate);
        var shrink = Lillia.Augments[1373];
        Assert.Equal(new AugmentRow(67994, 0.5722), shrink.All);
        Assert.Equal([1, 2, 3, 4], shrink.Stages.Keys.Order());
    }

    [Theory]
    [InlineData("Overflow", "Overflow")]
    [InlineData("0verfIow", "Overflow")]            // OCR's usual swaps
    [InlineData("Ravenous  Bind.", "Ravenous Bind")]
    [InlineData("upgrade sheen", "Upgrade Sheen")]
    [InlineData("Shrlnk Engine", "Shrink Engine")]
    public void A_misread_title_still_names_its_augment(string read, string name) =>
        Assert.Equal(name, AugmentNames.Match(read, Pool)?.Name);

    [Theory]
    [InlineData("")]
    [InlineData("Damage")]
    [InlineData("xq")]
    public void Noise_names_nothing(string read) => Assert.Null(AugmentNames.Match(read, Pool));

    [Fact]
    public void A_wrapped_title_is_read_from_its_last_lines_up()
    {
        Assert.Equal("Shrink Engine", AugmentNames.MatchLines(["[End]", "Shrink Engine"], Pool)?.Name);
        var longest = Pool.Where(a => a.Name.Contains(' ')).MaxBy(a => a.Name.Length)!;
        var words = longest.Name.Split(' ');
        var half = words.Length / 2;
        Assert.Equal(longest.Name, AugmentNames.MatchLines([string.Join(" ", words[..half]), string.Join(" ", words[half..])], Pool)?.Name);
    }

    [Fact]
    public void A_card_reads_my_champion_at_this_offer_then_falls_back()
    {
        var shrink = AugmentAdvice.Resolve(1373, Lillia, Everyone, 1);
        Assert.Equal(AugmentSource.Stage, shrink.Source);
        var row = Lillia.Augments[1373].Stages[1];
        Assert.Equal(Math.Round((row.WinRate - Lillia.ChampionWinRate) * 100, 1), shrink.DeltaPoints);

        var unknown = Everyone.Augments.Keys.First(id => !Lillia.Augments.ContainsKey(id));
        Assert.Equal(AugmentSource.Global, AugmentAdvice.Resolve(unknown, Lillia, Everyone, 1).Source);
        Assert.Equal(AugmentSource.None, AugmentAdvice.Resolve(-5, Lillia, Everyone, 1).Source);
    }

    [Fact]
    public void A_thin_row_is_flagged_and_never_the_best()
    {
        var thin = new AugmentTable("t", 0.5, new Dictionary<int, AugmentEntry>
        {
            [1] = new(new AugmentRow(50, 0.9), new Dictionary<int, AugmentRow>()),
            [2] = new(new AugmentRow(5000, 0.52), new Dictionary<int, AugmentRow>()),
            [3] = new(new AugmentRow(5000, 0.49), new Dictionary<int, AugmentRow>()),
        });
        var (picks, best) = AugmentAdvice.Rank([1, 2, 3], thin, null, 2);
        Assert.True(picks[0]!.LowSample);
        Assert.Equal(1, best);
        Assert.Equal("A", picks[1]!.Grade);
        Assert.Equal("C", picks[2]!.Grade);
    }

    [Fact]
    public void The_best_card_is_among_my_champions_numbers_and_needs_two_to_compare()
    {
        var (_, best) = AugmentAdvice.Rank([Id("Shrink Engine"), null, null], Lillia, Everyone, 1);
        Assert.Equal(-1, best);
        var ids = Lillia.Augments.Where(a => a.Value.Stages.TryGetValue(1, out var r) && r.Samples >= 200).Select(a => a.Key).Take(3).ToArray();
        var (picks, top) = AugmentAdvice.Rank([.. ids.Select(i => (int?)i)], Lillia, Everyone, 1);
        Assert.Equal(picks.Max(p => p!.DeltaPoints), picks[top]!.DeltaPoints);
    }

    [Fact]
    public void Offers_are_numbered_by_new_cards_and_never_below_my_level()
    {
        var offers = new AugmentOffers();
        Assert.Equal(1, offers.StageOf([1, 2, 3], AugmentAdvice.StagesUnlocked(1)));
        Assert.Equal(1, offers.StageOf([1, 9, 3], AugmentAdvice.StagesUnlocked(3)));   // a reroll
        Assert.Equal(2, offers.StageOf([4, 5, 6], AugmentAdvice.StagesUnlocked(7)));
        Assert.Equal(2, offers.StageOf([4, 5, 6], AugmentAdvice.StagesUnlocked(11))); // shown again after the shop
        Assert.Equal(4, new AugmentOffers().StageOf([7, 8, 9], AugmentAdvice.StagesUnlocked(16))); // Glamour started late
    }

    [Fact]
    public void The_clients_patch_and_the_one_before_are_tried()
    {
        var paths = AugmentDataSource.DataPaths(Json("aramkit-versions.json"), "16.20");
        Assert.Equal(["16.20", "16.19"], paths.Select(p => p.Version));
        Assert.All(paths, p => Assert.StartsWith("data/", p.DataPath));
        Assert.Single(AugmentDataSource.DataPaths(Json("aramkit-versions.json"), "17.1"));
    }

    [Fact]
    public void Title_fingerprints_tell_a_changed_card_from_the_same_one()
    {
        var a = Screen("augments_1920x1080_gold");
        var b = Screen("augments_1920x1080_rerolls");
        var client = new PixelRect(0, 0, 1920, 1080);
        var t = AugmentLayout.Title(client, 0);
        var fa = AugmentButton.Fingerprint(a.Crop(t.Left, t.Top, t.Width, t.Height));
        Assert.False(AugmentButton.Differs(fa, AugmentButton.Fingerprint(a.Crop(t.Left, t.Top, t.Width, t.Height))));
        Assert.True(AugmentButton.Differs(fa, AugmentButton.Fingerprint(b.Crop(t.Left, t.Top, t.Width, t.Height))));
    }
}
