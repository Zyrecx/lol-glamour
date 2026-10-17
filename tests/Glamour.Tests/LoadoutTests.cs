using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Glamour.Core;
using Glamour.Host;
using Glamour.Lcu;
using Microsoft.Extensions.DependencyInjection;

namespace Glamour.Tests;

/// <summary>A client that answers canned GETs, records writes, and hands back an id for a created rune page.</summary>
public sealed class ScriptedLcu : ILcuClient, ILcuAssets
{
    public Dictionary<string, JsonNode?> Gets { get; } = [];
    public List<(HttpMethod Method, string Path, string? Body)> Writes { get; } = [];
    public int NextPageId { get; set; } = 77;
    public List<string> AssetRequests { get; } = [];

    public Task<LcuResponse> SendAsync(HttpMethod method, string path, object? body = null, CancellationToken ct = default)
    {
        if (method == HttpMethod.Get) return Task.FromResult(new LcuResponse(Gets.ContainsKey(path) ? 200 : 404, Gets.GetValueOrDefault(path)));
        Writes.Add((method, path, body is null ? null : JsonSerializer.Serialize(body)));
        return Task.FromResult(method == HttpMethod.Post && path == "/lol-perks/v1/pages" ? new LcuResponse(200, new JsonObject { ["id"] = NextPageId++ }) : new LcuResponse(204, null));
    }

    public Task<LcuAsset> GetAssetAsync(string path, CancellationToken ct = default)
    {
        AssetRequests.Add(path);
        return Task.FromResult(new LcuAsset(200, [1, 2, 3], "image/jpeg"));
    }
}

public class LoadoutTests : IDisposable
{
    private readonly TestHost _host = new();
    private readonly ScriptedLcu _lcu = new();
    private readonly HttpClient _pc;

    public LoadoutTests()
    {
        _pc = _host.Client(TestHost.Loopback);
        _host.Services.GetRequiredService<SwappableLcuClient>().Current = _lcu;
    }

    public void Dispose() => _host.Dispose();

    private static JsonNode Fixture(string file) => JsonNode.Parse(File.ReadAllText(FixtureFiles.PathOf(file)))!;

    private async Task EnterChampSelectAsync() =>
        await _host.Services.GetRequiredService<Engine>().HandleAsync(new LcuEvent("/lol-gameflow/v1/gameflow-phase", "Update", JsonValue.Create("ChampSelect")), default);

    private void RunesAvailable()
    {
        _lcu.Gets["/lol-perks/v1/recommended-champion-positions"] = JsonNode.Parse("""{"32":{"recommendedPositions":["JUNGLE","UTILITY"]}}""");
        _lcu.Gets["/lol-perks/v1/recommended-pages/champion/32/position/JUNGLE/map/11"] = Fixture("recommended-32-JUNGLE.json")["raw"]!["body"]!.DeepClone();
    }

    [Fact]
    public void Skins_keep_owned_skins_and_chromas_only_and_the_real_dump_parses()
    {
        var skins = Skins.OwnedFrom(Fixture("skin-carousel.json"));
        Assert.Contains(skins, s => s.IsBase && s.Id == 157000 && s.Tile.StartsWith("/lol-game-data/assets/"));
        Assert.DoesNotContain(skins.SelectMany(s => s.Chromas), c => c.Id == 157004); // not owned
        Assert.All(skins, s => Assert.True(s.Id != 0));
    }

    [Fact]
    public async Task Skins_of_another_champion_left_in_the_carousel_are_not_listed()
    {
        _lcu.Gets["/lol-champ-select/v1/skin-carousel-skins"] = Fixture("skin-carousel.json"); // Yasuo (157)
        await EnterChampSelectAsync();
        Assert.NotEqual(0, (await _pc.GetFromJsonAsync<JsonElement>("/api/champselect/skins?championId=157")).GetArrayLength());
        Assert.Equal(0, (await _pc.GetFromJsonAsync<JsonElement>("/api/champselect/skins?championId=99")).GetArrayLength());
    }

    [Theory]
    [InlineData("/lol-game-data/assets/ASSETS/Characters/Yasuo/Skins/Base/Images/yasuo_splash_tile_0.jpg", true)]
    [InlineData("/lol-game-data/assets/v1/champion-chroma-images/157/157004.png", true)]
    [InlineData("/lol-game-data/assets/../../lol-summoner/v1/current-summoner", false)]
    [InlineData("/lol-game-data/assets/%2e%2e/x.png", false)]
    [InlineData("/lol-summoner/v1/current-summoner", false)]
    [InlineData("/lol-game-data/assets/x.json", false)]
    [InlineData("/lol-game-data/assets/x.png?a=1", false)]
    [InlineData("", false)]
    public void Asset_route_only_serves_game_data_images(string path, bool allowed) => Assert.Equal(allowed, LoadoutEndpoints.IsAssetPath(path));

    [Fact]
    public async Task Asset_route_relays_an_image_and_refuses_other_paths()
    {
        var ok = await _pc.GetAsync("/api/lcu-asset?path=" + Uri.EscapeDataString("/lol-game-data/assets/v1/champion-chroma-images/157/157005.png"));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("image/jpeg", ok.Content.Headers.ContentType?.MediaType);
        var bad = await _pc.GetAsync("/api/lcu-asset?path=" + Uri.EscapeDataString("/lol-summoner/v1/current-summoner"));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Single(_lcu.AssetRequests);
    }

    [Fact]
    public async Task Everything_is_refused_outside_champ_select_and_writes_nothing()
    {
        RunesAvailable();
        Assert.Equal(HttpStatusCode.BadRequest, (await _pc.PutAsJsonAsync("/api/champselect/skin", new { skinId = 157000 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _pc.PostAsJsonAsync("/api/champselect/runes/apply", new { championId = 32, index = 0 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _pc.GetAsync("/api/champselect/runes?championId=32")).StatusCode);
        Assert.Empty(_lcu.Writes);
    }

    [Fact]
    public async Task Skin_change_is_refused_for_an_unowned_skin_and_sent_for_an_owned_one()
    {
        _lcu.Gets["/lol-champ-select/v1/skin-carousel-skins"] = Fixture("skin-carousel.json");
        await EnterChampSelectAsync();
        var unowned = await _pc.PutAsJsonAsync("/api/champselect/skin", new { skinId = 157004 });
        Assert.Equal(HttpStatusCode.BadRequest, unowned.StatusCode);
        Assert.Empty(_lcu.Writes);

        var owned = await _pc.PutAsJsonAsync("/api/champselect/skin", new { skinId = 157000 });
        Assert.True(owned.IsSuccessStatusCode);
        Assert.Equal(("PATCH", "/lol-champ-select/v1/session/my-selection", """{"selectedSkinId":157000}"""), (_lcu.Writes[0].Method.Method, _lcu.Writes[0].Path, _lcu.Writes[0].Body));
    }

    [Fact]
    public async Task Recommended_runes_are_listed_with_the_lane_found_for_the_champion()
    {
        RunesAvailable();
        await EnterChampSelectAsync();
        var json = await _pc.GetFromJsonAsync<JsonElement>("/api/champselect/runes?championId=32");
        Assert.Equal("JUNGLE", json.GetProperty("position").GetString());
        var first = json.GetProperty("pages")[0];
        Assert.Equal("Aftershock", first.GetProperty("keystone").GetString());
        Assert.Equal(9, first.GetProperty("perks").GetArrayLength());
    }

    [Fact]
    public async Task Apply_creates_a_temporary_current_page_from_a_fresh_fetch_and_replaces_only_its_own_previous_page()
    {
        RunesAvailable();
        await EnterChampSelectAsync();

        var first = await _pc.PostAsJsonAsync("/api/champselect/runes/apply", new { championId = 32, index = 0 });
        Assert.True(first.IsSuccessStatusCode);
        var post = _lcu.Writes.Single();
        Assert.Equal("/lol-perks/v1/pages", post.Path);
        var body = JsonNode.Parse(post.Body!)!;
        Assert.True(body["isTemporary"]!.GetValue<bool>());
        Assert.True(body["current"]!.GetValue<bool>());
        Assert.Equal(8400, body["primaryStyleId"]!.GetValue<int>());
        Assert.Equal(9, body["selectedPerkIds"]!.AsArray().Count);
        Assert.StartsWith("Glamour", body["name"]!.GetValue<string>());

        // Second apply: the page we made (77) is listed as ours; another app's temporary page (5) and the user's own (3) are left alone.
        _lcu.Gets["/lol-perks/v1/pages"] = JsonNode.Parse("""[{"id":3,"isTemporary":false,"name":"Mine"},{"id":5,"isTemporary":true,"name":"Blitz"},{"id":77,"isTemporary":true,"name":"Glamour Aftershock"}]""");
        await _pc.PostAsJsonAsync("/api/champselect/runes/apply", new { championId = 32, index = 1 });
        Assert.Equal(["DELETE /lol-perks/v1/pages/77", "POST /lol-perks/v1/pages"], _lcu.Writes.Skip(1).Select(w => $"{w.Method.Method} {w.Path}"));
    }

    [Fact]
    public async Task Apply_never_deletes_a_page_that_is_no_longer_a_glamour_temporary_page()
    {
        RunesAvailable();
        await EnterChampSelectAsync();
        await _pc.PostAsJsonAsync("/api/champselect/runes/apply", new { championId = 32, index = 0 });
        // The user turned our page into their own (or the id was reused): it must survive.
        _lcu.Gets["/lol-perks/v1/pages"] = JsonNode.Parse("""[{"id":77,"isTemporary":false,"name":"Glamour Aftershock"}]""");
        await _pc.PostAsJsonAsync("/api/champselect/runes/apply", new { championId = 32, index = 0 });
        Assert.DoesNotContain(_lcu.Writes, w => w.Method == HttpMethod.Delete);
    }

    [Fact]
    public async Task Apply_with_a_recommendation_that_does_not_exist_writes_nothing()
    {
        RunesAvailable();
        await EnterChampSelectAsync();
        var r = await _pc.PostAsJsonAsync("/api/champselect/runes/apply", new { championId = 32, index = 9 });
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        Assert.Empty(_lcu.Writes);
    }
}
