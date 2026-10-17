using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Glamour.Core;
using Glamour.Host;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Glamour.Tests;

/// <summary>Fixtures: arammayhem.com's six `get_tier_list` pages and u.gg's Mayhem tiers for 16.20, and the 16.20 champion list. Nothing here touches the network.</summary>
public class MayhemTests
{
    private static string Fixture(string name) => File.ReadAllText(FixtureFiles.PathOf(name));
    private static readonly JsonArray Pages = JsonNode.Parse(Fixture("arammayhem-tier-list.json"))!.AsArray();
    private static string UggJson => Fixture("ugg-mayhem-tiers.json");

    private static ChampionList Champions()
    {
        using var doc = JsonDocument.Parse(Fixture("champions-16.20.json"));
        return new(doc.RootElement.GetProperty("Version").GetString()!,
            [.. doc.RootElement.GetProperty("Champions").EnumerateArray().Select(c => new ChampionInfo(c.GetProperty("Id").GetInt32(), c.GetProperty("Name").GetString()!, c.GetProperty("Icon").GetString()!))]);
    }

    /// <summary>The kept form of the fixture's pages, as <see cref="MayhemSource"/> caches it.</summary>
    private static string Kept(string patch = "26.20") => new JsonObject
    {
        ["patch"] = patch,
        ["entries"] = new JsonArray([.. Pages.SelectMany(p => MayhemSource.PageOf(p!.ToJsonString())!["entries"]!.AsArray())
            .Select(e => (JsonNode)new JsonObject { ["championId"] = e!["championId"]!.DeepClone(), ["winRate"] = e["winRate"]!.DeepClone() })]),
    }.ToJsonString();

    /// <summary>Answers the MCP by the asked offset (as an event stream, like the site) and u.gg by URL; <see cref="Patch"/> rewrites the site's patch.</summary>
    private sealed class Handler : HttpMessageHandler
    {
        public List<string> Urls { get; } = [];
        public string Patch { get; set; } = "26.20";
        public bool Offline { get; set; }
        public HashSet<string> UggPatches { get; } = ["16_20"];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            Urls.Add(url);
            if (Offline) throw new HttpRequestException("offline");
            if (url.Contains("arammayhem.com"))
            {
                var offset = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!["params"]!["arguments"]!["offset"]!.GetValue<int>();
                var page = Pages[offset / 30]!.DeepClone();
                page["result"]!["structuredContent"]!["patch"] = Patch;
                return new(HttpStatusCode.OK) { Content = new StringContent($"event: message\ndata: {page.ToJsonString()}\n\n") };
            }
            return UggPatches.Any(p => url.Contains($"/{p}/")) ? new(HttpStatusCode.OK) { Content = new StringContent(UggJson) } : new(HttpStatusCode.NotFound);
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static (MayhemSource Source, Handler Handler, GlamourSettings Settings, string Dir) Source()
    {
        var dir = Directory.CreateTempSubdirectory("glamour-mayhem").FullName;
        var list = Champions();
        var catalog = new ChampionCatalog(new FakeChampionSource { Version = list.Version, Champions = list.Champions }, new FakeTimeProvider(), Path.Combine(dir, "champions.json"), NullLogger<ChampionCatalog>.Instance);
        var handler = new Handler();
        var settings = new GlamourSettings { MayhemWinRates = true };
        return (new MayhemSource(new Factory(handler), catalog, settings, dir, NullLogger<MayhemSource>.Instance), handler, settings, dir);
    }

    [Fact]
    public void Every_champion_of_the_site_is_found_in_the_champion_list_by_its_key()
    {
        var (patch, rows) = MayhemSource.ParseWinRates(Kept(), Champions())!.Value;
        Assert.Equal("26.20", patch);
        Assert.Equal(173, rows.Count); // MonkeyKing, KSante, Belveth, Nunu, Renata too
        Assert.Equal(56.75, rows.Single(r => r.OpponentId == 4).WinRate); // Twisted Fate
        Assert.All(rows, r => Assert.Equal(0, r.Games)); // the site gives no counts
    }

    [Theory]
    [InlineData("26.20", "16.20", true)]
    [InlineData("16.20", "16.20", true)]
    [InlineData("26.19", "16.20", false)]
    [InlineData("26.20", "16.21", false)]
    [InlineData("", "16.20", false)]
    public void The_sites_year_patch_is_the_clients_season_patch(string site, string client, bool same) =>
        Assert.Equal(same, MayhemSource.SamePatch(site, client));

    [Fact]
    public void Ugg_tiers_give_every_champion_a_letter()
    {
        var tiers = MayhemSource.ParseTiers(UggJson)!;
        Assert.Equal(173, tiers.Count);
        Assert.Equal("S+", tiers[804]);
        Assert.Equal("D", tiers[412]);
        Assert.Null(MayhemSource.ParseTiers("""{"status":404}"""));
    }

    [Fact]
    public async Task With_the_setting_on_the_win_rates_come_from_the_site_in_six_calls_and_are_cached()
    {
        var (source, handler, _, _) = Source();
        var rates = (await source.GetModeAsync("mayhem", default))!;
        Assert.Equal(("arammayhem.com", "16.20", 173), (rates.Source, rates.Patch, rates.Rows.Count));
        Assert.Equal(6, handler.Urls.Count);
        await source.GetModeAsync("mayhem", default);
        Assert.Equal(6, handler.Urls.Count);
        Assert.Null(await source.GetModeAsync("aram", default));
    }

    [Fact]
    public async Task With_the_setting_off_only_ugg_tiers_are_asked()
    {
        var (source, handler, settings, _) = Source();
        settings.MayhemWinRates = false;
        var rates = (await source.GetModeAsync("mayhem", default))!;
        Assert.Equal(("u.gg", "16.20", 0, 173), (rates.Source, rates.Patch, rates.Rows.Count, rates.Tiers!.Count));
        Assert.DoesNotContain(handler.Urls, u => u.Contains("arammayhem.com"));
    }

    [Fact]
    public async Task Site_data_for_an_older_patch_gives_way_to_tiers()
    {
        var (source, handler, _, _) = Source();
        handler.Patch = "26.19";
        Assert.Equal("u.gg", (await source.GetModeAsync("mayhem", default))!.Source);
    }

    [Fact]
    public async Task Without_tiers_for_the_new_patch_the_previous_patch_tiers_are_used()
    {
        var (source, handler, settings, _) = Source();
        settings.MayhemWinRates = false;
        handler.UggPatches.Clear();
        handler.UggPatches.Add("16_19");
        var rates = (await source.GetModeAsync("mayhem", default))!;
        Assert.Equal(("u.gg", "16.19"), (rates.Source, rates.Patch));
    }

    [Fact]
    public async Task Offline_a_cached_list_of_the_same_patch_still_answers()
    {
        var (source, handler, _, dir) = Source();
        await source.GetModeAsync("mayhem", default);
        var file = Path.Combine(dir, "mayhem", "arammayhem.json");
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow - LolalyticsMatchupSource.Refresh - TimeSpan.FromMinutes(1));
        handler.Offline = true;
        Assert.Equal("arammayhem.com", (await source.GetModeAsync("mayhem", default))!.Source);
    }
}
