using System.Text.Json;
using System.Text.Json.Nodes;
using Glamour.Core;
using Glamour.Host;
using Glamour.Lcu;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Glamour.Tests;

/// <summary>The loadout service against a client fake that keeps what is written (spells, the current rune page, item sets). No network.</summary>
public class MatchupLoadoutTests
{
    /// <summary>Answers like the client: a spells PATCH shows in the session, a page POST becomes the current page, item sets read back.</summary>
    private sealed class Client : ILcuClient
    {
        public (int, int) Spells = (4, 12);
        public int CurrentPage = 50; // the user's own page
        public bool PagesTemporary = true;
        public JsonObject Sets = (JsonObject)JsonNode.Parse("""{"accountId":1,"itemSets":[{"title":"Mine","uid":"u1"}],"timestamp":0}""")!;
        public List<(HttpMethod Method, string Path, string? Body)> Writes { get; } = [];
        private int _nextPage = 900;

        public Task<LcuResponse> SendAsync(HttpMethod method, string path, object? body = null, CancellationToken ct = default)
        {
            lock (Writes)
            {
                if (method != HttpMethod.Get) Writes.Add((method, path, body is null ? null : JsonSerializer.Serialize(body)));
                return Task.FromResult(Answer(method, path, body));
            }
        }

        private LcuResponse Answer(HttpMethod method, string path, object? body)
        {
            if (method == HttpMethod.Get) return path switch
            {
                "/lol-summoner/v1/current-summoner" => new(200, JsonNode.Parse("""{"summonerId":77}""")),
                "/lol-item-sets/v1/item-sets/77/sets" => new(200, Sets.DeepClone()),
                "/lol-perks/v1/currentpage" => new(200, new JsonObject { ["id"] = CurrentPage }),
                "/lol-perks/v1/pages" => new(200, JsonNode.Parse($$"""[{"id":{{CurrentPage}},"isTemporary":{{(CurrentPage >= 900 ? "true" : "false")}},"name":"Glamour vs Zed"}]""")),
                "/lol-champ-select/v1/session" => new(200, JsonNode.Parse($$"""
                    {"localPlayerCellId":0,"timer":{"phase":"FINALIZATION"},"myTeam":[{"cellId":0,"championId":266,"spell1Id":{{Spells.Item1}},"spell2Id":{{Spells.Item2}}}],
                     "theirTeam":[],"actions":[[{"id":1,"actorCellId":0,"type":"pick","championId":266,"completed":true}]]}
                    """)),
                _ => new(404, null),
            };
            var json = JsonSerializer.SerializeToNode(body);
            if (path == "/lol-champ-select/v1/session/my-selection") Spells = (json!["spell1Id"]!.GetValue<int>(), json["spell2Id"]!.GetValue<int>());
            if (path == "/lol-perks/v1/pages" && method == HttpMethod.Post) { CurrentPage = _nextPage++; return new(200, new JsonObject { ["id"] = CurrentPage, ["isTemporary"] = PagesTemporary }); }
            if (path == "/lol-item-sets/v1/item-sets/77/sets") { Sets = (JsonObject)json!; return new(201, null); }
            return new(204, null);
        }

        public List<string> WritesTo(string path) { lock (Writes) return [.. Writes.Where(w => w.Path == path).Select(w => w.Body ?? "")]; }
    }

    /// <summary>Every enemy plays my lane (Zed most of all), so the one picked is the guessed opponent.</summary>
    private sealed class Tables : IMatchupSource
    {
        public Task<ChampionMatchups?> GetAsync(int championId, string lane, CancellationToken ct) =>
            Task.FromResult<ChampionMatchups?>(new(championId, lane, new Dictionary<string, double> { ["TOP"] = championId == 7 ? 99 : 80, ["MIDDLE"] = championId == 7 ? 1 : 20 }, []));
    }

    private sealed class Builds : IMatchupBuildSource
    {
        public int Calls;
        public Task<MatchupBuild?> GetAsync(int championId, int enemyId, string lane, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult<MatchupBuild?>(new(championId, enemyId, lane, [new([4, 14], 500, 260)], [new(8000, 8400, [8010, 9111, 9104, 8299, 8444, 8242, 5005, 5008, 5001], 400, 210)],
                [new([1055, 2003], 500, 250)], [new([3047], 300, 150)], [new([6630], 400, 200), new([3053], 300, 150)], enemyId == 0 ? null : "tip", null));
        }
    }

    private readonly Client _lcu = new();
    private readonly GlamourSettings _settings = new();
    private readonly AutomationGate _gate;
    private readonly Builds _builds = new();
    private readonly MatchupLoadoutService _service;

    public MatchupLoadoutTests()
    {
        _gate = new AutomationGate(_settings, new EventHub(), TimeProvider.System);
        var dir = Directory.CreateTempSubdirectory("glamour-loadout").FullName;
        var catalog = new ChampionCatalog(new FakeChampionSource { Version = "16.19.1" }, new FakeTimeProvider(), Path.Combine(dir, "champions.json"), NullLogger<ChampionCatalog>.Instance);
        _service = new MatchupLoadoutService(_lcu, new ActionGateway(_lcu, _settings, NullLogger<ActionGateway>.Instance), _settings, new Tables(), _builds,
            new RunePageTracker(), catalog, _gate, TimeProvider.System, NullLogger<MatchupLoadoutService>.Instance) { ApplyDelay = TimeSpan.Zero };
    }

    private ChampSelectBoard Board(bool locked, params int[] enemies) => new("FINALIZATION", 0,
        [new BoardPlayer(0, 266, locked, "TOP", _lcu.Spells.Item1, _lcu.Spells.Item2, 0, "me", true)],
        [.. enemies.Select((id, i) => new BoardPlayer(5 + i, id, true, "", 0, 0, 0, "", false))],
        [], [], [], 0, 400, ChampSelectMode.Draft, [], 0, false, []);

    private ChampSelectSession Session(ChampSelectBoard board) => new(0, new ChampSelectTimer("FINALIZATION", 0, 0), [], Board: board);

    private static async Task Until(Func<bool> done)
    {
        for (var i = 0; i < 200 && !done(); i++) await Task.Delay(10);
        Assert.True(done());
    }

    [Fact]
    public async Task Apply_by_hand_writes_the_item_set_the_rune_page_and_the_spells()
    {
        var plan = await _service.PlanAsync(Board(false, 238), null, default);
        Assert.Equal(238, plan!.EnemyId);
        var result = await _service.ApplyByHandAsync(plan, default);
        Assert.Equal(new LoadoutResult("done", "done", "done"), result);
        Assert.Equal((4, 14), _lcu.Spells); // Flash stays on D
        var sets = _lcu.Sets["itemSets"]!.AsArray();
        Assert.Equal(["Mine", "Glamour: Aatrox vs Zed"], sets.Select(s => s!["title"]!.GetValue<string>()));
        var page = JsonNode.Parse(_lcu.WritesTo("/lol-perks/v1/pages").Single())!;
        Assert.Equal("Glamour vs Zed", page["name"]!.GetValue<string>());
        Assert.True(page["isTemporary"]!.GetValue<bool>());
        Assert.Equal((266, 238), _service.Applied);
    }

    [Fact]
    public async Task A_rune_page_the_client_did_not_make_temporary_is_deleted_at_once()
    {
        _lcu.PagesTemporary = false;
        var result = await _service.ApplyByHandAsync((await _service.PlanAsync(Board(true, 238), null, default))!, default);
        Assert.Equal("failed", result.Runes);
        Assert.Contains(_lcu.Writes, w => w.Method == HttpMethod.Delete && w.Path == "/lol-perks/v1/pages/900");
        Assert.Equal("done", result.ItemSet);
    }

    [Fact]
    public async Task Automatic_applies_once_locked_and_again_when_the_opponent_changes_keeping_what_the_user_changed()
    {
        _settings.MatchupLoadout = true;
        await _service.OnChampSelectAsync(Session(Board(false, 238)), default); // hovering: nothing yet
        await Task.Delay(50);
        Assert.Empty(_lcu.Writes);

        await _service.OnChampSelectAsync(Session(Board(true, 238)), default);
        await Until(() => _service.Applied == (266, 238));
        Assert.Equal((4, 14), _lcu.Spells);
        Assert.Single(_lcu.WritesTo("/lol-perks/v1/pages"));

        _lcu.Spells = (4, 6);  // the user takes Ghost
        _lcu.CurrentPage = 50; // and their own page
        await _service.OnChampSelectAsync(Session(Board(true, 238, 7)), default); // 7 is the top laner after all
        await Until(() => _service.Applied == (266, 7));
        Assert.Equal((4, 6), _lcu.Spells);
        Assert.Single(_lcu.WritesTo("/lol-perks/v1/pages"));
        Assert.Equal(2, _lcu.WritesTo("/lol-item-sets/v1/item-sets/77/sets").Count);
        Assert.Equal("Glamour: Aatrox vs #7", _lcu.Sets["itemSets"]![1]!["title"]!.GetValue<string>());
    }

    [Fact]
    public async Task Builds_are_fetched_ahead_once_per_champion_and_enemy_even_without_automatic()
    {
        await _service.OnChampSelectAsync(Session(Board(false)), default); // hovering, no enemy yet: the general build
        Assert.Equal(1, _builds.Calls);
        await _service.OnChampSelectAsync(Session(Board(false, 238)), default);
        await _service.OnChampSelectAsync(Session(Board(true, 238, 7)), default);
        await _service.OnChampSelectAsync(Session(Board(true, 238, 7)), default);
        Assert.Equal(3, _builds.Calls);
        Assert.Empty(_lcu.Writes);

        await _service.OnPhaseAsync("InProgress", default); // the next champ select fetches again (the source's cache keeps it cheap)
        await _service.OnChampSelectAsync(Session(Board(false)), default);
        Assert.Equal(4, _builds.Calls);
    }

    [Fact]
    public async Task Automatic_stays_out_after_apply_by_hand_and_while_stopped()
    {
        _settings.MatchupLoadout = true;
        await _gate.OnPhaseAsync("ChampSelect", default); // Stop only exists inside a champ select
        Assert.True(_gate.SetDraftStopped(true));
        await _service.OnChampSelectAsync(Session(Board(true, 238)), default);
        await Task.Delay(50);
        Assert.Empty(_lcu.Writes);

        _gate.SetDraftStopped(false);
        await _service.ApplyByHandAsync((await _service.PlanAsync(Board(true, 238), 238, default))!, default);
        var writes = _lcu.Writes.Count;
        await _service.OnChampSelectAsync(Session(Board(true, 238, 7)), default);
        await Task.Delay(50);
        Assert.Equal(writes, _lcu.Writes.Count);

        await _service.OnPhaseAsync("InProgress", default); // a new champ select starts clean
        Assert.Null(_service.Applied);
    }

    private ChampSelectSession Picking(ChampSelectBoard board) => new(0, new ChampSelectTimer("BAN_PICK", 30000, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), [], Board: board);

    private JsonNode? Loadout() => JsonSerializer.SerializeToNode(_gate.Snapshot(), new JsonSerializerOptions(JsonSerializerDefaults.Web))!["loadout"];

    [Fact]
    public async Task Automatic_counts_down_before_it_applies()
    {
        _settings.MatchupLoadout = true;
        _service.ApplyDelay = TimeSpan.FromMilliseconds(400);
        await _service.OnChampSelectAsync(Picking(Board(true, 238)), default);
        await Until(() => Loadout()?["state"]?.GetValue<string>() == "waiting");
        Assert.Equal(238, Loadout()!["enemyId"]!.GetValue<int>());
        Assert.InRange(Loadout()!["timeLeftMs"]!.GetValue<long>(), 1, 400);
        Assert.Empty(_lcu.Writes);
        await Until(() => Loadout()?["state"]?.GetValue<string>() == "done");
        Assert.Equal((266, 238), _service.Applied);
    }

    [Fact]
    public async Task Cancel_while_waiting_sends_nothing_for_the_rest_of_the_champ_select()
    {
        _settings.MatchupLoadout = true;
        _service.ApplyDelay = TimeSpan.FromMilliseconds(300);
        await _service.OnChampSelectAsync(Picking(Board(true, 238)), default);
        await Until(() => Loadout()?["state"]?.GetValue<string>() == "waiting");
        Assert.True(_service.CancelAuto());
        Assert.False(_service.CancelAuto());
        Assert.Equal("cancelled", Loadout()!["state"]!.GetValue<string>());
        await _service.OnChampSelectAsync(Picking(Board(true, 238, 7)), default); // a new opponent does not bring it back
        await Task.Delay(500);
        Assert.Empty(_lcu.Writes);
        Assert.Equal("cancelled", Loadout()!["state"]!.GetValue<string>());

        await _service.OnPhaseAsync("Lobby", default); // the next champ select starts clean
        Assert.False(_service.Cancelled);
    }

    [Fact]
    public async Task The_wait_never_runs_into_the_last_seconds_of_champ_select()
    {
        _settings.MatchupLoadout = true;
        _service.ApplyDelay = TimeSpan.FromSeconds(30);
        var session = new ChampSelectSession(0, new ChampSelectTimer("FINALIZATION", 3200, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), [], Board: Board(true, 238)); // 0.2 s before the margin
        await _service.OnChampSelectAsync(session, default);
        await Until(() => _service.Applied == (266, 238));
    }
}
