using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Glamour.Core;
using Glamour.Host;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Glamour.Tests;

public class SettingsPatchTests : IDisposable
{
    private readonly TestHost _host = new();
    private readonly HttpClient _pc;

    public SettingsPatchTests() => _pc = _host.Client(TestHost.Loopback);

    public void Dispose() => _host.Dispose();

    private Task<HttpResponseMessage> PatchAsync(string json) =>
        _pc.PatchAsync("/api/settings", new StringContent(json, Encoding.UTF8, "application/json"));

    private async Task<GlamourSettings> CurrentAsync() =>
        (await _pc.GetFromJsonAsync<GlamourSettings>("/api/settings", new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } }))!;

    [Fact]
    public async Task Patch_changes_only_the_fields_sent()
    {
        await _pc.PutAsJsonAsync("/api/settings", new GlamourSettings { AutoAccept = AutoAcceptMode.Always, AutoBanChampionId = 238 });

        var r = await PatchAsync("""{"autoLockChampionId":266,"lockTiming":"LastSecond"}""");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);

        var s = await CurrentAsync();
        Assert.Equal(AutoAcceptMode.Always, s.AutoAccept);   // untouched
        Assert.Equal(238, s.AutoBanChampionId);              // untouched
        Assert.Equal(266, s.AutoLockChampionId);
        Assert.Equal(ActionTiming.LastSecond, s.LockTiming);
    }

    [Fact]
    public async Task Two_devices_patching_different_fields_do_not_overwrite_each_other()
    {
        await PatchAsync("""{"autoHonorEnabled":true}""");
        await PatchAsync("""{"autoAccept":"Phone"}""");

        var s = await CurrentAsync();
        Assert.True(s.AutoHonorEnabled);
        Assert.Equal(AutoAcceptMode.Phone, s.AutoAccept);
    }

    [Fact]
    public async Task Patch_with_null_clears_a_champion()
    {
        await PatchAsync("""{"autoBanChampionId":238}""");
        await PatchAsync("""{"autoBanChampionId":null}""");
        Assert.Null((await CurrentAsync()).AutoBanChampionId);
    }

    [Theory]
    [InlineData("""{"banTiming":"Never"}""")]
    [InlineData("""{"autoAccept":42}""")]
    [InlineData("""{"lastSecondMarginSeconds":99}""")]
    [InlineData("""{"lastSecondMarginSeconds":-1}""")]
    [InlineData("""{"ntfyServer":"http://insecure.example"}""")]
    public async Task Invalid_patch_is_refused_and_changes_nothing(string json)
    {
        await PatchAsync("""{"autoBanChampionId":238}""");
        var before = await CurrentAsync();

        Assert.Equal(HttpStatusCode.BadRequest, (await PatchAsync(json)).StatusCode);

        var after = await CurrentAsync();
        Assert.Equal(before.AutoBanChampionId, after.AutoBanChampionId);
        Assert.Equal(before.BanTiming, after.BanTiming);
        Assert.Equal(before.AutoAccept, after.AutoAccept);
        Assert.Equal(before.LastSecondMarginSeconds, after.LastSecondMarginSeconds);
        Assert.Equal(before.NtfyServer, after.NtfyServer);
    }

    [Fact]
    public async Task Patch_enabling_notifications_generates_a_topic_once()
    {
        await PatchAsync("""{"ntfyEnabled":true}""");
        var topic = (await CurrentAsync()).NtfyTopic;
        Assert.False(string.IsNullOrWhiteSpace(topic));

        await PatchAsync("""{"autoHonorEnabled":true}""");
        Assert.Equal(topic, (await CurrentAsync()).NtfyTopic);
    }

    [Fact]
    public async Task Patch_needs_a_paired_device_from_the_lan()
    {
        var r = await _host.Client().PatchAsync("/api/settings", new StringContent("""{"dryRun":true}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode);
    }
}

public class ChampionsTests : IDisposable
{
    private readonly TestHost _host = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "glamour-champs-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        _host.Dispose();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private ChampionCatalog Catalog(FakeChampionSource source, FakeTimeProvider time) =>
        new(source, time, Path.Combine(_dir, "champions.json"), NullLogger<ChampionCatalog>.Instance);

    [Fact]
    public async Task Endpoint_returns_the_list_and_fetches_only_once()
    {
        var pc = _host.Client(TestHost.Loopback);
        var json = await pc.GetFromJsonAsync<JsonElement>("/api/champions");
        Assert.Equal("16.1.1", json.GetProperty("version").GetString());
        Assert.Equal(238, json.GetProperty("champions")[1].GetProperty("id").GetInt32());
        await pc.GetAsync("/api/champions");
        Assert.Equal(1, _host.Champions.Calls);
    }

    [Fact]
    public async Task Endpoint_says_503_when_offline_and_nothing_is_cached()
    {
        _host.Champions.Fail = true;
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await _host.Client(TestHost.Loopback).GetAsync("/api/champions")).StatusCode);
    }

    [Fact]
    public async Task Endpoint_needs_a_paired_device_from_the_lan()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _host.Client().GetAsync("/api/champions")).StatusCode);
    }

    [Fact]
    public async Task A_new_patch_is_fetched_within_the_hour_and_an_unchanged_one_costs_only_the_version_check()
    {
        var source = new FakeChampionSource();
        var time = new FakeTimeProvider();
        var catalog = Catalog(source, time);

        await catalog.GetAsync(default);
        time.Advance(TimeSpan.FromMinutes(59));
        await catalog.GetAsync(default);
        Assert.Equal((1, 0), (source.Calls, source.VersionCalls));

        time.Advance(TimeSpan.FromMinutes(2));
        await catalog.GetAsync(default); // same patch: version checked, list kept
        Assert.Equal((1, 1), (source.Calls, source.VersionCalls));

        time.Advance(TimeSpan.FromHours(1));
        source.Version = "16.2.1";
        Assert.Equal("16.2.1", (await catalog.GetAsync(default))!.Version);
        Assert.Equal(2, source.Calls);
    }

    [Fact]
    public async Task The_whole_list_is_fetched_again_after_twelve_hours_even_on_the_same_patch()
    {
        var source = new FakeChampionSource();
        var time = new FakeTimeProvider();
        var catalog = Catalog(source, time);

        await catalog.GetAsync(default);
        time.Advance(TimeSpan.FromHours(13));
        await catalog.GetAsync(default);
        Assert.Equal(2, source.Calls);
    }

    [Fact]
    public async Task A_failed_refresh_keeps_the_last_good_list_and_waits_before_retrying()
    {
        var source = new FakeChampionSource();
        var time = new FakeTimeProvider();
        var catalog = Catalog(source, time);
        await catalog.GetAsync(default);

        time.Advance(TimeSpan.FromHours(13));
        source.Fail = true;
        Assert.Equal("16.1.1", (await catalog.GetAsync(default))!.Version);
        Assert.Equal(2, source.Calls);

        await catalog.GetAsync(default); // within the 5 minute back-off: no new request
        Assert.Equal(2, source.Calls);

        time.Advance(TimeSpan.FromMinutes(6));
        await catalog.GetAsync(default);
        Assert.Equal(3, source.Calls);
    }

    [Fact]
    public async Task A_new_process_offline_falls_back_to_the_list_saved_on_disk()
    {
        var time = new FakeTimeProvider();
        await Catalog(new FakeChampionSource(), time).GetAsync(default);

        var offline = Catalog(new FakeChampionSource { Fail = true }, time);
        var list = await offline.GetAsync(default);
        Assert.Equal(2, list!.Champions.Count);
    }

    [Fact]
    public void Data_dragon_entries_become_sorted_champions_with_icon_urls()
    {
        using var doc = JsonDocument.Parse("""
            {"Zed":{"id":"Zed","key":"238","name":"Zed"},"Aatrox":{"id":"Aatrox","key":"266","name":"Aatrox"},"MonkeyKing":{"id":"MonkeyKing","key":"62","name":"Wukong"}}
            """);
        var list = DataDragonChampionSource.Parse("16.1.1", doc.RootElement);
        Assert.Equal(["Aatrox", "Wukong", "Zed"], list.Champions.Select(c => c.Name));
        Assert.Equal(62, list.Champions[1].Id);
        Assert.Equal("https://ddragon.leagueoflegends.com/cdn/16.1.1/img/champion/MonkeyKing.png", list.Champions[1].Icon);
        Assert.Equal("https://ddragon.leagueoflegends.com/cdn/img/champion/splash/MonkeyKing_{num}.jpg", list.Champions[1].Splash);
        Assert.Equal("https://ddragon.leagueoflegends.com/cdn/img/champion/loading/MonkeyKing_{num}.jpg", list.Champions[1].Portrait);
        Assert.All(list.Champions, c => Assert.Empty(c.Positions!)); // no lane source given
    }

    [Fact]
    public void Client_lanes_override_downloaded_ones_and_utility_means_support()
    {
        var lanes = ClientLanes.Parse(System.Text.Json.Nodes.JsonNode.Parse("""{"238":{"recommendedPositions":["JUNGLE","UTILITY"]},"x":{}}"""));
        var list = ClientLanes.Overlay(new("1", [new(238, "Zed", "i", ["MIDDLE"]), new(266, "Aatrox", "i", ["TOP"])]), lanes);
        Assert.Equal(["JUNGLE", "SUPPORT"], list.Champions[0].Positions);
        Assert.Equal(["TOP"], list.Champions[1].Positions);
    }

    [Fact]
    public void Lanes_are_joined_by_champion_id()
    {
        using var doc = JsonDocument.Parse("""{"Zed":{"id":"Zed","key":"238","name":"Zed"},"Newbie":{"id":"Newbie","key":"999","name":"Newbie"}}""");
        var lanes = DataDragonChampionSource.ParseLanes("""{"Zed":{"id":238,"key":"Zed","positions":["JUNGLE","MIDDLE"]},"Odd":{"id":5}}""");
        var list = DataDragonChampionSource.Parse("16.1.1", doc.RootElement, lanes);
        Assert.Equal(["JUNGLE", "MIDDLE"], list.Champions.Single(c => c.Name == "Zed").Positions);
        Assert.Empty(list.Champions.Single(c => c.Name == "Newbie").Positions!);
    }
}
