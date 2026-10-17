using System.Text.Json.Nodes;
using Glamour.Core;
using Glamour.Host.Panel;

namespace Glamour.Tests;

public class LoadingPartiesTests
{
    private static readonly PixelRect Screen = new(0, 0, 1920, 1080);

    // The Mayhem game of 2026-10-11 21:37 (tools/logs/lcd-playerlist-mayhem.json, the `parties` event in glamour.log): an ally 4-stack, an enemy 4-stack,
    // me solo on ORDER and one enemy with a hidden Riot ID.
    private static readonly LivePlayer[] Players =
    [
        new("ORDER", "Quillon#MOSS", "Twitch"), new("ORDER", "TÃ¶rÃ¶kmÃ©zË‡fÃ¼#5668", "Shaco"), new("ORDER", "VÃ¡rkapuË‡rÃ©t#EUNE", "Rengar"),
        new("ORDER", "KÃ¶dfoltË‡gÃ¡zlÃ³#EUNE", "Jinx"), new("ORDER", "SÃ¡slÃ¡pË‡dÃ©r#EUNE", "Yuumi"),
        new("CHAOS", "#", "Aphelios"), new("CHAOS", "Marrowind#3518", "Nilah"), new("CHAOS", "Glowmoth#1999", "Ahri"), new("CHAOS", "KeStReL#EUNE", "Lillia"), new("CHAOS", "Dunlin#EUNE", "Caitlyn"),
    ];

    private static readonly Dictionary<int, string> Keys = new() { [107] = "Rengar", [222] = "Jinx", [350] = "Yuumi", [35] = "Shaco", [895] = "Nilah", [51] = "Caitlyn", [103] = "Ahri", [876] = "Lillia", [523] = "Aphelios" };

    private static PartyPlayer P(string name, int champ) => new("", champ, name, false);

    private static readonly GameParties Game = new(7000000101,
    [
        new(true, [P("VÃ¡rkapuË‡rÃ©t#EUNE", 107), P("KÃ¶dfoltË‡gÃ¡zlÃ³#EUNE", 222), P("SÃ¡slÃ¡pË‡dÃ©r#EUNE", 350), P("TÃ¶rÃ¶kmÃ©zË‡fÃ¼#5668", 35)]),
        new(false, [P("Marrowind#3518", 895), P("Dunlin#EUNE", 51), P("Glowmoth#1999", 103), P("KeStReL#EUNE", 876)]),
    ]);

    [Fact]
    public void Party_members_land_on_their_cards_in_playerlist_order()
    {
        var cards = LoadingCards.Match(Game, Players, id => Keys.GetValueOrDefault(id));
        Assert.Equal([(true, 2), (true, 3), (true, 4), (true, 1)], cards.Where(c => c.Party == 0).Select(c => (c.Top, c.Slot)));
        Assert.Equal([(false, 1), (false, 4), (false, 2), (false, 3)], cards.Where(c => c.Party == 1).Select(c => (c.Top, c.Slot)));
        Assert.All(cards, c => Assert.Equal(4, c.Size));
        Assert.Equal("4-stack", LoadingCards.Label(4));
        Assert.Equal("Duo", LoadingCards.Label(2));
    }

    // The game of 2026-10-12 21:23 (glamour.log, loading-20261012-212317-2.png): the list puts me first on ORDER, the screen shows me third
    // (Nasus, Soraka, Singed, Talon, Varus); the Nasus + Soraka duo was drawn on Soraka and me (user).
    private static readonly LivePlayer[] Listed =
    [
        new("ORDER", "Quillon#MOSS", "Singed"), new("ORDER", "Hé yún#EUNE", "Nasus"), new("ORDER", "OakhollowPrime#EUNE", "Soraka"),
        new("ORDER", "quiet tide guy#EUNE", "Talon"), new("ORDER", "BrisketWee#EUNE", "Varus"),
        new("CHAOS", "fellrunner#EUNE", "Ziggs"), new("CHAOS", "CORVANES#EUNE", "Shaco"), new("CHAOS", "PaleHarborKid#ZODI", "Lucian"),
        new("CHAOS", "Cold Soup#nah", "Vayne"), new("CHAOS", "ritzbitsu#EUNE", "Akshan"),
    ];

    private static readonly GameParties Listed_game = new(7000000106,
    [
        new(true, [P("Hé yún#EUNE", 75), P("OakhollowPrime#EUNE", 16)]),
        new(false, [P("PaleHarborKid#ZODI", 236), P("Cold Soup#nah", 67), P("ritzbitsu#EUNE", 166), P("CORVANES#EUNE", 35), P("fellrunner#EUNE", 115)]),
    ]);

    [Fact]
    public void My_row_is_placed_by_my_card_on_screen_and_left_unmarked_until_it_is_found()
    {
        var at = LoadingCards.Match(Listed_game, Listed, _ => null, "Quillon#MOSS", (true, 2));
        Assert.Equal([(true, 0), (true, 1)], at.Where(c => c.Party == 0).Select(c => (c.Top, c.Slot)));
        Assert.Equal([2, 3, 4, 1, 0], at.Where(c => c.Party == 1).Select(c => c.Slot));

        var unknown = LoadingCards.Match(Listed_game, Listed, _ => null, "Quillon#MOSS", null);
        Assert.DoesNotContain(unknown, c => c.Top);
        Assert.Equal(5, unknown.Count);

        var wrongRow = LoadingCards.Match(Listed_game, Listed, _ => null, "Quillon#MOSS", (false, 2)); // my gold name on the other row: trust neither
        Assert.DoesNotContain(wrongRow, c => c.Top);
    }

    [Theory]
    [InlineData("me-top3", true, 2)]
    [InlineData("me-top2", true, 1)]
    [InlineData("me-top1", true, 0)]
    [InlineData("me-still-loading", null, null)] // my card still shows the profile: no name line yet
    public void My_card_is_the_one_with_the_gold_name(string shot, bool? top, int? slot)
    {
        using var file = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", "loading-names-1920x1080.json.gz"));
        using var gz = new System.IO.Compression.GZipStream(file, System.IO.Compression.CompressionMode.Decompress);
        var boxes = JsonNode.Parse(gz)![shot]!.AsArray().Select(b => (X: (int)b!["x"]!, Y: (int)b["y"]!, W: (int)b["w"]!, H: (int)b["h"]!, Rgb: Convert.FromHexString((string)b["rgb"]!))).ToList();
        (int, int, int) At(int x, int y)
        {
            foreach (var b in boxes)
                if (x >= b.X && x < b.X + b.W && y >= b.Y && y < b.Y + b.H)
                {
                    var i = ((y - b.Y) * b.W + x - b.X) * 3;
                    return (b.Rgb[i], b.Rgb[i + 1], b.Rgb[i + 2]);
                }
            throw new KeyNotFoundException($"{x},{y}");
        }
        var mine = LoadingLayout.MyCard(Screen, At);
        Assert.Equal(top, mine?.Top);
        Assert.Equal(slot, mine?.Slot);
    }

    [Fact]
    public void A_hidden_Riot_ID_is_found_by_champion_and_a_mirror_pick_by_name_only()
    {
        var hidden = new GameParties(1, [new(false, [P("", 523), P("Marrowind#3518", 895)])]);
        Assert.Equal([(false, 0), (false, 1)], LoadingCards.Match(hidden, Players, id => Keys.GetValueOrDefault(id)).Select(c => (c.Top, c.Slot)));

        LivePlayer[] mirror = [.. Players[..5], new("CHAOS", "#", "Twitch"), .. Players[6..]];
        var duo = new GameParties(1, [new(false, [P("", 29), P("Marrowind#3518", 895)])]); // Twitch on both teams, the enemy's name hidden
        Assert.Empty(LoadingCards.Match(duo, mirror, id => id == 29 ? "Twitch" : Keys.GetValueOrDefault(id))); // one card alone is not marked
    }

    [Fact]
    public void Not_the_two_row_screen_gives_nothing()
    {
        LivePlayer[] arena = [.. Enumerable.Range(0, 16).Select(i => new LivePlayer("", $"p{i}#1", $"C{i}"))];
        Assert.Empty(LoadingCards.Match(Game, arena, id => Keys.GetValueOrDefault(id)));
    }

    [Fact]
    public void Cards_sit_on_the_measured_frames_and_scale_with_the_height()
    {
        Assert.Equal(new PixelRect(237, 68, 492, 529), LoadingLayout.Card(Screen, true, 0));
        Assert.Equal(new PixelRect(1421, 591, 1676, 1052), LoadingLayout.Card(Screen, false, 4));
        var small = LoadingLayout.Card(new PixelRect(100, 50, 1380, 770), true, 1); // 1280x720 at 100,50
        Assert.Equal(new PixelRect(100 + 355, 50 + 45, 100 + 525, 50 + 353), small);
        Assert.True(LoadingLayout.Fits(Screen));
        Assert.False(LoadingLayout.Fits(new PixelRect(0, 0, 1000, 1080)));
    }

    [Theory]
    [InlineData("loading-6s", true)]
    [InlineData("loading-15s", true)]
    [InlineData("draft-ranked", true)] // rank borders cover the plate's lower part
    [InlineData("draft-printwindow", true)]
    [InlineData("draft-flex-printwindow", true)]
    [InlineData("loading-3s-flying", false)] // the top row still flying in
    [InlineData("not-yet", false)]
    [InlineData("game-30s", false)]
    [InlineData("game-90s", false)]
    [InlineData("game-shop", false)]
    public void The_loading_screen_is_told_by_its_card_plates(string shot, bool loading)
    {
        var all = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "loading-plates-1920x1080.json")))!;
        var pixels = all[shot]!.AsObject();
        (int, int, int) At(int x, int y) => pixels[$"{x},{y}"] is JsonArray c ? ((int)c[0]!, (int)c[1]!, (int)c[2]!) : throw new KeyNotFoundException($"{x},{y}");
        Assert.Equal(loading, LoadingLayout.PlatesShown(Screen, At) >= LoadingLayout.PlatesNeeded);
    }
}
