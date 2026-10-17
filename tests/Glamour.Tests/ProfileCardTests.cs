using System.Text.Json.Nodes;
using Glamour.Core;
using Glamour.Host.Profiles;
using Glamour.Lcu;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Glamour.Tests;

public sealed class FakeCardSource : IProfileCardSource
{
    public ProfileCardRead? Next { get; set; }
    public int Reads { get; private set; }
    public bool Throws { get; set; }
    public Task<ProfileCardRead?> ReadAsync(DateTimeOffset now, CancellationToken ct)
    {
        Reads++;
        if (Throws) throw new HttpRequestException("client is closing");
        return Task.FromResult(Next is null ? null : Next with { Card = Next.Card with { At = now } });
    }
}

public class ProfileCardTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "glamour-cards-" + Guid.NewGuid().ToString("N"));
    private readonly FakeRiotClient _client = new();
    private readonly FakeCardSource _cards = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero));
    private readonly ProfileStore _store;
    private readonly ProfileService _svc;
    private string Live => Path.Combine(_dir, "riot", "RiotGamesPrivateSettings.yaml");

    public ProfileCardTests()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Live)!);
        _store = new ProfileStore(Path.Combine(_dir, "profiles"), NullLogger<ProfileStore>.Instance);
        _svc = new ProfileService(_store, _client, () => "None", Live, _time, new EventHub(), null, _cards);
    }

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    private static ProfileCard Card(int icon = 29, int level = 312, RankInfo? solo = null) =>
        new(icon, level, 40, solo, null, "default", "ranked", "Season2020", 3, "1", "2", default);

    private async Task<string> SaveAsync(string name, string session, string? riotId)
    {
        File.WriteAllText(Live, $"refresh_token: tok{session}\nrefresh_tokens_session_id: {session}\n");
        _client.Identity = riotId is null ? null : new RiotIdentity(riotId, "EUN1");
        await _svc.SaveCurrentAsync(name, null);
        return _store.Data.Profiles.Single(p => p.Name == name).Id;
    }

    [Fact]
    public async Task Stores_the_card_and_the_icon_on_the_active_profile()
    {
        var id = await SaveAsync("main", "s1", "Saffron Tide#EUNE");
        _cards.Next = new("Saffron Tide#EUNE", Card(solo: new RankInfo("GOLD", "II", 55, 10, 8)), [1, 2, 3]);
        await _svc.RefreshCardAsync(default);
        var p = _store.Data.Profiles.Single();
        Assert.Equal(312, p.Card!.Level);
        Assert.Equal("GOLD", p.Card.Solo!.Tier);
        Assert.Equal([1, 2, 3], File.ReadAllBytes(_store.IconPath(id)));
    }

    [Fact]
    public async Task Another_accounts_card_is_never_put_on_the_active_profile()
    {
        await SaveAsync("main", "s1", "Saffron Tide#EUNE");
        _cards.Next = new("Someone#ELSE", Card(), [1]);
        await _svc.RefreshCardAsync(default);
        Assert.Null(_store.Data.Profiles.Single().Card);
    }

    [Fact]
    public async Task Does_nothing_while_the_client_does_not_answer()
    {
        await SaveAsync("main", "s1", "Saffron Tide#EUNE");
        await _svc.RefreshCardAsync(default);
        Assert.Null(_store.Data.Profiles.Single().Card);
    }

    [Fact]
    public async Task An_unchanged_card_is_not_rewritten_but_a_changed_one_is()
    {
        await SaveAsync("main", "s1", "Saffron Tide#EUNE");
        _cards.Next = new("Saffron Tide#EUNE", Card(), [1]);
        await _svc.RefreshCardAsync(default);
        var first = _store.Data.Profiles.Single().Card!.At;
        _time.Advance(TimeSpan.FromMinutes(10));
        await _svc.RefreshCardAsync(default);
        Assert.Equal(first, _store.Data.Profiles.Single().Card!.At);
        _cards.Next = new("Saffron Tide#EUNE", Card(level: 313), [1]);
        await _svc.RefreshCardAsync(default);
        Assert.Equal(313, _store.Data.Profiles.Single().Card!.Level);
    }

    [Fact]
    public async Task The_card_survives_a_switch_and_a_re_sign_in_and_the_icon_goes_with_a_removal()
    {
        var a = await SaveAsync("a", "s1", "A#1");
        _cards.Next = new("A#1", Card(), [9]);
        await _svc.RefreshCardAsync(default);
        var b = await SaveAsync("b", "s2", "B#2");
        Assert.NotNull(_store.Data.Profiles.Single(p => p.Id == a).Card); // switching away keeps what was read
        File.WriteAllText(Live, "refresh_token: tokNEW\nrefresh_tokens_session_id: s1b\n");
        await _svc.SaveCurrentAsync(null, a);
        Assert.NotNull(_store.Data.Profiles.Single(p => p.Id == a).Card);
        await _svc.RemoveAsync(a);
        Assert.False(File.Exists(_store.IconPath(a)));
        Assert.Equal(b, _store.Data.Profiles.Single().Id);
    }

    [Fact]
    public async Task The_card_is_read_one_last_time_before_the_client_is_closed_for_a_switch()
    {
        var a = await SaveAsync("a", "s1", "A#1");
        var b = await SaveAsync("b", "s2", "B#2");
        await _svc.SwitchAsync(a, true, default); // now a is active, b was captured
        _client.RunningNames = ["LeagueClientUx"];
        _cards.Next = new("A#1", Card(level: 99), [1]);
        await _svc.SwitchAsync(b, true, default);
        Assert.Equal(99, _store.Data.Profiles.Single(p => p.Id == a).Card!.Level);
    }

    [Fact]
    public async Task The_snapshot_carries_the_card_and_whether_an_icon_is_cached()
    {
        await SaveAsync("main", "s1", "Saffron Tide#EUNE");
        _cards.Next = new("Saffron Tide#EUNE", Card(), [1]);
        await _svc.RefreshCardAsync(default);
        var json = System.Text.Json.JsonSerializer.SerializeToNode(_svc.Snapshot(), new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
        var p = json["profiles"]![0]!;
        Assert.True(p["hasIcon"]!.GetValue<bool>());
        Assert.Equal(312, p["card"]!["level"]!.GetValue<int>());
    }

    [Fact]
    public async Task A_dying_client_cannot_break_a_switch_and_the_refresh_says_nothing_was_read()
    {
        var a = await SaveAsync("a", "s1", "A#1");
        var b = await SaveAsync("b", "s2", "B#2");
        await _svc.SwitchAsync(a, true, default);
        _cards.Throws = true;
        Assert.False(await _svc.RefreshCardAsync(default));
        await _svc.SwitchAsync(b, true, default); // must still switch
        Assert.Equal(b, _store.Data.ActiveId);
        _cards.Throws = false;
        Assert.False(await _svc.RefreshCardAsync(default)); // no answer yet (login not finished): the caller retries soon
        _cards.Next = new("B#2", Card(), [1]);
        Assert.True(await _svc.RefreshCardAsync(default));
    }

    [Fact]
    public async Task An_unnamed_profile_does_not_take_the_card_of_an_account_saved_under_another_profile()
    {
        await SaveAsync("a", "s1", "A#1");
        var b = await SaveAsync("b", "s2", null); // no Riot ID known
        _cards.Next = new("A#1", Card(), [1]);
        Assert.False(await _svc.RefreshCardAsync(default));
        Assert.Null(_store.Data.Profiles.Single(p => p.Id == b).Card);
    }

    private sealed class FakeLcu(Dictionary<string, (int, JsonNode?)> routes) : ILcuClient, ILcuAssets
    {
        public Task<LcuResponse> SendAsync(HttpMethod method, string path, object? body = null, CancellationToken ct = default) =>
            Task.FromResult(routes.TryGetValue(path, out var r) ? new LcuResponse(r.Item1, r.Item2) : new LcuResponse(404, null));
        public Task<LcuAsset> GetAssetAsync(string path, CancellationToken ct = default) => Task.FromResult(new LcuAsset(200, [7], "image/jpeg"));
    }

    [Fact]
    public async Task The_lcu_source_maps_summoner_rank_regalia_and_flag()
    {
        var lcu = new FakeLcu(new()
        {
            ["/lol-summoner/v1/current-summoner"] = (200, JsonNode.Parse("""{"profileIconId":29,"summonerLevel":312,"percentCompleteForNextLevel":40,"gameName":"Saffron Tide","tagLine":"EUNE"}""")),
            ["/lol-ranked/v1/current-ranked-stats"] = (200, JsonNode.Parse("""{"queueMap":{"RANKED_SOLO_5x5":{"tier":"GOLD","division":"II","leaguePoints":55,"wins":10,"losses":8},"RANKED_FLEX_SR":{"tier":"","division":"NA","leaguePoints":0,"wins":0,"losses":0}}}""")),
            ["/lol-regalia/v2/current-summoner/regalia"] = (200, JsonNode.Parse("""{"bannerType":"lastSeasonHighest","crestType":"ranked"}""")),
            ["/lol-chat/v1/me"] = (200, JsonNode.Parse("""{"lol":{"bannerIdSelected":"301","playerTitleSelected":"12"}}""")),
            ["/lol-banners/v1/current-summoner/flags/equipped"] = (200, JsonNode.Parse("""{"theme":"Season2020","level":3,"itemId":1}""")),
        });
        var read = await new LcuProfileCardSource(lcu, lcu).ReadAsync(_time.GetUtcNow(), default);
        Assert.Equal("Saffron Tide#EUNE", read!.RiotId);
        Assert.Equal(29, read.Card.IconId);
        Assert.Equal(("GOLD", "II", 55), (read.Card.Solo!.Tier, read.Card.Solo.Division, read.Card.Solo.Lp));
        Assert.Null(read.Card.Flex); // empty tier = unranked
        Assert.Equal(("lastSeasonHighest", "ranked", "Season2020", 3), (read.Card.BannerType, read.Card.CrestType, read.Card.FlagTheme, read.Card.FlagLevel));
        Assert.Equal(("301", "12"), (read.Card.BannerId, read.Card.TitleId));
        Assert.Equal([(byte)7], read.Icon);
    }

    [Fact]
    public async Task The_lcu_source_returns_null_before_login_finishes_and_keeps_the_card_when_optional_parts_fail()
    {
        Assert.Null(await new LcuProfileCardSource(new FakeLcu([]), new FakeLcu([])).ReadAsync(_time.GetUtcNow(), default));
        var lcu = new FakeLcu(new() { ["/lol-summoner/v1/current-summoner"] = (200, JsonNode.Parse("""{"profileIconId":5,"summonerLevel":30}""")) });
        var read = await new LcuProfileCardSource(lcu, lcu).ReadAsync(_time.GetUtcNow(), default);
        Assert.Equal(30, read!.Card.Level);
        Assert.Null(read.Card.Solo);
        Assert.Null(read.RiotId);
    }
}
