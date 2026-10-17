using Glamour.Core;
using Glamour.Host;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Glamour.Tests;

/// <summary>Fixture: a real Lolalytics answer for Zed mid (tools/logs/2026-10-06-source-comparison.md). Nothing here touches the network.</summary>
public class MatchupTests
{
    private static string ZedJson => File.ReadAllText(FixtureFiles.PathOf("lolalytics-counter-zed-mid.json"));

    private sealed class Handler(Func<string, string> answer, Task? gate = null) : HttpMessageHandler
    {
        public List<string> Urls { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            lock (Urls) Urls.Add(request.RequestUri!.ToString()); // both patches are fetched at once
            if (gate is not null) await gate; // held open so callers overlap
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(answer(request.RequestUri.ToString())) };
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler);
    }

    private static LolalyticsMatchupSource Source(Handler handler, string dir)
    {
        var catalog = new ChampionCatalog(new FakeChampionSource { Version = "16.19.1" }, new FakeTimeProvider(), Path.Combine(dir, "champions.json"), NullLogger<ChampionCatalog>.Instance);
        return new(new Factory(handler), catalog, dir, NullLogger<LolalyticsMatchupSource>.Instance);
    }

    private static int Current(Handler handler) => handler.Urls.Count(u => u.Contains("patch=16.19&"));

    private static string TempDir() => Directory.CreateTempSubdirectory("glamour-matchups").FullName;

    [Fact]
    public void Parse_reads_lane_shares_and_the_enemy_win_rate_per_opponent()
    {
        var m = LolalyticsMatchupSource.Parse(238, "MIDDLE", ZedJson)!;
        Assert.Equal(69.1, m.LaneShares["MIDDLE"]);
        Assert.Equal(22.7, m.LaneShares["JUNGLE"]);
        var draven = m.Rows.Single(r => r.OpponentId == 202);
        Assert.Equal((65.33, 176), (draven.WinRate, draven.Games)); // Zed's own win rate against it, 67.05, less the table's lean (avgWr 51.72)
        Assert.True(m.Rows.Count > 100);
    }

    private static string Fixture(string name) => File.ReadAllText(FixtureFiles.PathOf(name));

    [Fact]
    public void Each_table_leans_toward_its_own_champion_and_parse_takes_the_lean_off()
    {
        // Real 16.20 answers, ~1,030 games each way: Garen's table says 54.24 against Darius, Darius's says Garen wins 48.7 (100 - 51.3). Both have avgWr 51.47.
        var garen = LolalyticsMatchupSource.Parse(86, "TOP", Fixture("lolalytics-counter-garen-top.json"))!.Rows.Single(r => r.OpponentId == 122);
        var darius = LolalyticsMatchupSource.Parse(122, "TOP", Fixture("lolalytics-counter-darius-top.json"))!.Rows.Single(r => r.OpponentId == 86);
        Assert.Equal(52.77, garen.WinRate);
        Assert.Equal(49.83, darius.WinRate);
        Assert.InRange(garen.WinRate + darius.WinRate, 100, 103); // 105.5 before; what is left is noise
    }

    [Fact]
    public async Task My_hover_and_the_suggestion_row_give_the_same_number_for_the_same_pair()
    {
        var darius = LolalyticsMatchupSource.Parse(122, "TOP", Fixture("lolalytics-counter-darius-top.json"))!;
        var garen = LolalyticsMatchupSource.Parse(86, "TOP", Fixture("lolalytics-counter-garen-top.json"))!;
        var source = new FakeSource(new() { [(122, "TOP")] = darius, [(86, "TOP")] = garen });
        var board = Draft("TOP", false, 122);
        board = board with { MyTeam = [board.MyTeam[0] with { ChampionId = 86 }] };
        var view = await CounterPick.ViewAsync(board, new GlamourSettings { PreHoverChampionId = 86 }, source, default);
        var suggested = view.Picks!.Single(p => p.ChampionId == 86);
        var mine = view.Mine!.Lines.Single(l => l.ChampionId == 122);
        Assert.Equal((suggested.WinRate, suggested.Games), (mine.WinRate, mine.Games));
        Assert.Equal(50.17, mine.WinRate);
    }

    [Fact]
    public async Task A_cached_file_of_the_current_patch_is_fetched_again_after_a_day_and_kept_when_that_fails()
    {
        var dir = TempDir();
        var online = true;
        var handler = new Handler(_ => online ? ZedJson : throw new HttpRequestException("offline"));
        var source = Source(handler, dir);
        Assert.NotNull(await source.GetAsync(238, "MIDDLE", default));
        var file = Path.Combine(dir, "16.19", "zed-middle.json");
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow - LolalyticsMatchupSource.Refresh - TimeSpan.FromMinutes(1));
        Assert.NotNull(await source.GetAsync(238, "MIDDLE", default));
        Assert.Equal(2, Current(handler));
        Assert.True(DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < TimeSpan.FromMinutes(1)); // written again

        online = false;
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow - LolalyticsMatchupSource.Refresh - TimeSpan.FromMinutes(1));
        Assert.NotNull(await source.GetAsync(238, "MIDDLE", default)); // the cached answer
        Assert.NotNull(await source.GetAsync(238, "MIDDLE", default));
        Assert.Equal(3, Current(handler)); // one failed try, then the day starts over
    }

    [Fact]
    public void Parse_gives_nothing_for_an_error_answer() =>
        Assert.Null(LolalyticsMatchupSource.Parse(238, "MIDDLE", """{"status":404}"""));

    [Fact]
    public void A_counter_win_rate_is_one_hundred_minus_the_enemys()
    {
        var m = LolalyticsMatchupSource.Parse(238, "MIDDLE", ZedJson)!;
        var picks = CounterPicker.Rank(m, [103]);
        var ahri = picks.Single(p => p.ChampionId == 103);
        Assert.Equal(49.32, ahri.WinRate); // Zed wins 52.4 against Ahri, 50.68 without the table's lean, so Ahri wins 49.32
        Assert.True(picks.Zip(picks.Skip(1)).All(x => x.First.WinRate >= x.Second.WinRate));
        Assert.All(picks.Where(p => !p.InPool), p => Assert.False(p.FewGames)); // off-lane noise (under 300 games) never comes in on its own
    }

    [Fact]
    public void Pool_champions_with_few_games_are_listed_and_marked()
    {
        var m = new ChampionMatchups(1, "MIDDLE", new Dictionary<string, double>(), [new(2, 40, 50), new(3, 45, 5000), new(4, 30, 4000)]);
        var picks = CounterPicker.Rank(m, [2], top: 1);
        Assert.Equal([4, 2], picks.Select(p => p.ChampionId)); // the best of the rest (70%) sorts above the pool pick (60%, 50 games)
        Assert.True(picks.Single(p => p.ChampionId == 2).FewGames);
    }

    [Fact]
    public void Lanes_are_assigned_to_the_most_likely_complete_split()
    {
        IReadOnlyDictionary<string, double> Shares(string main, double v) => new Dictionary<string, double> { [main] = v, ["JUNGLE"] = 100 - v };
        var lanes = LaneAssigner.Assign([
            (1, Shares("MIDDLE", 69)),   // Zed-like
            (2, new Dictionary<string, double> { ["JUNGLE"] = 90, ["TOP"] = 10 }),
            (3, new Dictionary<string, double> { ["MIDDLE"] = 55, ["TOP"] = 45 }), // also mid, but less
            (4, new Dictionary<string, double> { ["BOTTOM"] = 98 }),
            (5, new Dictionary<string, double> { ["SUPPORT"] = 95 })]);
        Assert.Equal("MIDDLE", lanes[1]);
        Assert.Equal("JUNGLE", lanes[2]);
        Assert.Equal("BOTTOM", lanes[4]);
        Assert.Equal("SUPPORT", lanes[5]);
        Assert.Equal("TOP", lanes[3]); // mid is taken, so the leftover lane
        Assert.Equal(5, lanes.Values.Distinct().Count());
    }

    [Fact]
    public void Fewer_than_five_picks_still_get_distinct_lanes_and_nothing_gives_nothing()
    {
        Assert.Equal(2, LaneAssigner.Assign([(1, new Dictionary<string, double> { ["TOP"] = 90 }), (2, null)]).Values.Distinct().Count());
        Assert.Empty(LaneAssigner.Assign([]));
    }

    [Fact]
    public void Lane_chances_count_every_split_and_leave_room_for_enemies_still_to_pick()
    {
        var zed = new Dictionary<string, double> { ["MIDDLE"] = 69, ["JUNGLE"] = 31 };
        Assert.Equal(0.69, LaneAssigner.Chances([(1, zed)], "MIDDLE")[1], 2); // alone: its own share
        var mid = new Dictionary<string, double> { ["MIDDLE"] = 55, ["TOP"] = 45 };
        var jungler = new Dictionary<string, double> { ["JUNGLE"] = 90, ["MIDDLE"] = 10 };
        var both = LaneAssigner.Chances([(1, zed), (2, mid), (3, jungler)], "MIDDLE");
        Assert.True(both[1] > 0.9 && both[2] < 0.01); // the jungler takes the jungle, so the 55% mid champion goes top
        Assert.True(both.Values.Sum() <= 1 + 1e-9); // at most one of them is mid
        Assert.Empty(LaneAssigner.Chances([], "MIDDLE"));
    }

    [Fact]
    public async Task A_first_pick_that_rarely_plays_my_lane_is_not_named_my_opponent()
    {
        // Naafiri, 72% jungle and 28% mid: the team list until the split is clearer.
        var naafiri = new ChampionMatchups(950, "MIDDLE", new Dictionary<string, double> { ["JUNGLE"] = 72, ["MIDDLE"] = 28 }, [new(1, 45, 3000)]);
        var view = await CounterPick.ViewAsync(Draft("MIDDLE", false, 950), new(), new FakeSource(new() { [(950, "MIDDLE")] = naafiri }), default);
        Assert.Equal(("waiting", 0, 28), (view.State, view.EnemyId, view.Chances![950]));
    }

    [Fact]
    public async Task With_all_five_picked_the_likeliest_is_named_even_when_unsure()
    {
        // Two flex mids: neither reaches the bar, but one of the five is my laner (the loadout needs a name).
        ChampionMatchups T(int id, double mid, double top, string other) =>
            new(id, "MIDDLE", new Dictionary<string, double> { ["MIDDLE"] = mid, ["TOP"] = top, [other] = 100 - mid - top }, [new(1, 45, 3000)]);
        var source = new FakeSource(new()
        {
            [(1, "MIDDLE")] = T(1, 50, 50, "JUNGLE"), [(2, "MIDDLE")] = T(2, 45, 55, "JUNGLE"),
            [(3, "MIDDLE")] = T(3, 0, 0, "JUNGLE"), [(4, "MIDDLE")] = T(4, 0, 0, "BOTTOM"), [(5, "MIDDLE")] = T(5, 0, 0, "SUPPORT"),
        });
        var four = await CounterPick.ViewAsync(Draft("MIDDLE", false, 1, 2, 3, 4), new(), source, default);
        Assert.Equal("waiting", four.State);
        var five = await CounterPick.ViewAsync(Draft("MIDDLE", false, 1, 2, 3, 4, 5), new(), source, default);
        Assert.Equal(("ready", 1), (five.State, five.EnemyId));
        Assert.True(five.Chances![1] < 60);
    }

    [Theory]
    [InlineData("16.19.1", "16.19,16.18")]
    [InlineData("16.1.1", "16.1")]
    [InlineData("garbage", "")]
    public void Patches_are_major_minor_with_the_one_before_as_fallback(string version, string expected) =>
        Assert.Equal(expected, string.Join(",", LolalyticsMatchupSource.Patches(version)));

    [Fact]
    public void Slug_is_the_data_dragon_file_name_lowercase_and_wukong_is_special()
    {
        Assert.Equal("kaisa", LolalyticsMatchupSource.Slug(new(145, "Kai'Sa", "https://x.test/img/champion/Kaisa.png")));
        Assert.Equal("wukong", LolalyticsMatchupSource.Slug(new(62, "Wukong", "https://x.test/img/champion/MonkeyKing.png")));
    }

    [Fact]
    public async Task Source_shares_one_fetch_between_callers_asking_at_once()
    {
        var dir = TempDir();
        var release = new TaskCompletionSource();
        var handler = new Handler(_ => ZedJson, release.Task);
        var source = Source(handler, dir);
        var calls = Enumerable.Range(0, 4).Select(_ => source.GetAsync(238, "MIDDLE", default)).ToList();
        await Task.Delay(100); // all four are waiting on the fetch
        release.SetResult();
        var all = await Task.WhenAll(calls);
        Assert.All(all, Assert.NotNull);
        Assert.Equal(1, Current(handler));
        Assert.Empty(Directory.GetFiles(dir, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Source_fetches_once_then_serves_from_the_disk_cache()
    {
        var dir = TempDir();
        var handler = new Handler(_ => ZedJson);
        var source = Source(handler, dir);
        Assert.NotNull(await source.GetAsync(238, "MIDDLE", default));
        Assert.Equal(["patch=16.18&c=zed&lane=middle", "patch=16.19&c=zed&lane=middle"], handler.Urls.Order().Select(u => u[(u.IndexOf("patch=", StringComparison.Ordinal))..(u.IndexOf("&tier", StringComparison.Ordinal))]));
        var again = Source(handler, dir); // a fresh instance, as after a restart
        Assert.NotNull(await again.GetAsync(238, "MIDDLE", default));
        Assert.Equal(2, handler.Urls.Count);
        Assert.True(File.Exists(Path.Combine(dir, "16.19", "zed-middle.json")));
        Assert.True(File.Exists(Path.Combine(dir, "16.18", "zed-middle.json")));
    }

    [Fact]
    public async Task Profiles_come_from_the_champion_page_cached_trimmed_beside_the_counter_table()
    {
        var dir = TempDir();
        var full = Fixture("lolalytics-full-zed-mid.json");
        var handler = new Handler(url => url.Contains("ep=build-full") ? (url.Contains("patch=16.19&") ? full : """{"status":404}""") : ZedJson);
        var source = Source(handler, dir);
        var zed = await source.GetProfileAsync(238, "MIDDLE", default);
        Assert.Equal(25643.93, zed!.Damage!.Physical);
        Assert.NotNull(await source.GetAsync(238, "MIDDLE", default));
        var file = Path.Combine(dir, "16.19", "zed-middle-full.json");
        Assert.True(new FileInfo(file).Length < full.Length / 4);
        Assert.True(File.Exists(Path.Combine(dir, "16.19", "zed-middle.json")));
        Assert.Equal(zed.Against["JUNGLE"], (await Source(handler, dir).GetProfileAsync(238, "MIDDLE", default))!.Against["JUNGLE"]); // read back from the cache
        Assert.Equal(3, handler.Urls.Count(u => u.Contains("ep=build-full"))); // 16.19 once; 16.18 has no data, so the second instance asks for it again
    }

    [Fact]
    public async Task A_patch_without_data_falls_back_to_the_previous_one()
    {
        var handler = new Handler(url => url.Contains("patch=16.19&") ? """{"status":404}""" : ZedJson);
        var m = await Source(handler, TempDir()).GetAsync(238, "MIDDLE", default);
        Assert.NotNull(m);
        Assert.Equal(2, handler.Urls.Count);
        Assert.All(m.Rows, r => Assert.Equal((r.Games, "16.18"), (r.OlderGames, r.OlderPatch)));
    }

    [Fact]
    public async Task The_previous_patch_failing_keeps_the_current_one()
    {
        var handler = new Handler(url => url.Contains("patch=16.18&") ? throw new HttpRequestException("offline") : ZedJson);
        var m = await Source(handler, TempDir()).GetAsync(238, "MIDDLE", default);
        Assert.NotNull(m);
        Assert.All(m.Rows, r => Assert.Equal(0, r.OlderGames));
    }

    [Fact]
    public void Blend_adds_the_previous_patch_only_to_thin_rows()
    {
        var shares = new Dictionary<string, double> { ["TOP"] = 90 };
        var current = new ChampionMatchups(1, "TOP", shares, [new(2, 60, 200), new(3, 40, 5000)]);
        var previous = new ChampionMatchups(1, "TOP", new Dictionary<string, double>(), [new(2, 50, 800), new(3, 50, 9000), new(4, 55, 3000)]);
        var m = MatchupBlend.Blend(current, previous, "16.18")!;
        Assert.Equal(new MatchupRow(2, 52, 1000, 800, "16.18"), m.Rows.Single(r => r.OpponentId == 2)); // (60*200 + 50*800) / 1000
        Assert.Equal(new MatchupRow(3, 40, 5000), m.Rows.Single(r => r.OpponentId == 3)); // enough games of its own
        Assert.Equal(new MatchupRow(4, 55, 3000, 3000, "16.18"), m.Rows.Single(r => r.OpponentId == 4)); // only the previous patch knows it
        Assert.Same(shares, m.LaneShares);
        Assert.Same(current, MatchupBlend.Blend(current, null, "16.18"));
    }

    [Fact]
    public void Older_games_reach_the_suggestions_and_my_lines()
    {
        var enemy = new ChampionMatchups(10, "TOP", new Dictionary<string, double> { ["TOP"] = 90 }, [new(20, 45, 1000, 800, "16.18")]);
        var pick = CounterPicker.Rank(enemy, [20]).Single();
        Assert.Equal((55.0, 800, "16.18"), (pick.WinRate, pick.OlderGames, pick.OlderPatch));
        var mine = new ChampionMatchups(20, "TOP", new Dictionary<string, double> { ["TOP"] = 90 }, [new(10, 58, 400)]);
        var line = MyMatchups.From(mine, [10], 10, enemyTables: [enemy])!.Lines.Single();
        Assert.Equal((55.0, 1000, 800, "16.18"), (line.WinRate, line.Games, line.OlderGames, line.OlderPatch));
    }

    [Fact]
    public async Task No_data_anywhere_or_an_unknown_champion_or_lane_is_null()
    {
        var none = new Handler(_ => "invalid end point");
        Assert.Null(await Source(none, TempDir()).GetAsync(238, "MIDDLE", default));
        Assert.Null(await Source(none, TempDir()).GetAsync(999999, "MIDDLE", default));
        Assert.Null(await Source(none, TempDir()).GetAsync(238, "NOWHERE", default));
    }

    [Fact]
    public async Task A_failing_connection_is_null_not_an_exception()
    {
        var handler = new Handler(_ => throw new HttpRequestException("offline"));
        Assert.Null(await Source(handler, TempDir()).GetAsync(238, "MIDDLE", default));
    }

    private sealed class FakeSource(Dictionary<(int, string), ChampionMatchups> tables) : IMatchupSource
    {
        public Task<ChampionMatchups?> GetAsync(int championId, string lane, CancellationToken ct) => Task.FromResult(tables.GetValueOrDefault((championId, lane)));
    }

    private static ChampSelectBoard Draft(string myLane, bool locked, params int[] enemies) => new("BAN_PICK", 0,
        [new BoardPlayer(0, 0, locked, myLane, 0, 0, 0, "me", true)], [.. enemies.Select((id, i) => new BoardPlayer(5 + i, id, false, "", 0, 0, 0, "", false))],
        [], [], [], 0);

    private static ChampionMatchups Table(int id, string lane, double laneShare, params (int Id, double Wr, int N)[] rows) =>
        new(id, lane, new Dictionary<string, double> { [lane] = laneShare, ["JUNGLE"] = 100 - laneShare }, [.. rows.Select(r => new MatchupRow(r.Id, r.Wr, r.N))]);

    [Fact]
    public async Task Counter_view_is_ready_when_an_enemy_is_likely_in_my_lane()
    {
        var settings = new GlamourSettings { PreHoverChampionId = 103 };
        var source = new FakeSource(new() { [(238, "MIDDLE")] = Table(238, "MIDDLE", 69, (103, 52.4, 9000), (75, 44, 3000)) });
        var view = await CounterPick.ViewAsync(Draft("MIDDLE", false, 238), settings, source, default);
        Assert.Equal(("ready", "MIDDLE", 238), (view.State, view.Lane, view.EnemyId));
        Assert.Equal([75, 103], view.Picks!.Select(p => p.ChampionId)); // 56% first, then the pool pick at 47.6%
    }

    [Fact]
    public async Task Counter_view_waits_when_no_enemy_fits_my_lane_or_none_has_picked()
    {
        var source = new FakeSource(new() { [(238, "MIDDLE")] = Table(238, "MIDDLE", 20, (75, 44, 3000)) }); // a jungler-ish pick
        Assert.Equal("waiting", (await CounterPick.ViewAsync(Draft("MIDDLE", false, 238), new(), source, default)).State);
        Assert.Equal("waiting", (await CounterPick.ViewAsync(Draft("MIDDLE", false), new(), source, default)).State);
        Assert.Equal("waiting", (await CounterPick.ViewAsync(Draft("MIDDLE", false, 777), new(), source, default)).State); // no table at all
    }

    [Fact]
    public async Task Counter_view_has_nothing_to_say_without_a_lane_or_outside_draft_and_no_picks_once_locked()
    {
        var source = new FakeSource(new() { [(238, "MIDDLE")] = Table(238, "MIDDLE", 69, (75, 44, 3000)) });
        var locked = await CounterPick.ViewAsync(Draft("MIDDLE", true, 238), new(), source, default);
        Assert.Equal(("locked", 238, true), (locked.State, locked.EnemyId, locked.Picks is null));
        Assert.Equal("none", (await CounterPick.ViewAsync(Draft("", false, 238), new(), source, default)).State);
        Assert.Equal("none", (await CounterPick.ViewAsync(null, new(), source, default)).State);
        Assert.Equal("none", (await CounterPick.ViewAsync(Draft("MIDDLE", false, 238) with { Mode = ChampSelectMode.Aram }, new(), source, default)).State);
    }

    [Fact]
    public async Task Picking_the_opponent_by_hand_overrides_the_lane_guess()
    {
        var source = new FakeSource(new()
        {
            [(238, "MIDDLE")] = Table(238, "MIDDLE", 69, (75, 44, 3000)),  // the guess
            [(103, "MIDDLE")] = Table(103, "MIDDLE", 5, (99, 40, 2000)),   // unlikely in mid, but the user says so
        });
        var board = Draft("MIDDLE", false, 238, 103, 777);
        var guessed = await CounterPick.ViewAsync(board, new(), source, default);
        Assert.Equal((238, false), (guessed.EnemyId, guessed.Forced));
        Assert.Equal([238, 103, 777], guessed.Enemies);

        var forced = await CounterPick.ViewAsync(board, new(), source, default, opponentId: 103);
        Assert.Equal(("ready", 103, true), (forced.State, forced.EnemyId, forced.Forced));
        Assert.Equal(99, forced.Picks![0].ChampionId);
        // The lane guess of every picked enemy comes along either way (it names the champions in an enemy party).
        Assert.Equal("MIDDLE", guessed.EnemyLanes![238]);
        Assert.Equal(guessed.EnemyLanes, forced.EnemyLanes);
        Assert.Equal(3, forced.EnemyLanes!.Count);

        Assert.Equal("nodata", (await CounterPick.ViewAsync(board, new(), source, default, opponentId: 777)).State); // picked, but no table for this lane
        Assert.Equal(238, (await CounterPick.ViewAsync(board, new(), source, default, opponentId: 555)).EnemyId);   // not on their team: ignored
    }

    [Fact]
    public async Task A_hand_picked_opponent_off_its_lanes_gets_the_counters_of_its_main_lane()
    {
        // Lolalytics answers an off-lane request with lane shares and no counters (LIVE, jinx-top).
        var offLane = new ChampionMatchups(222, "TOP", new Dictionary<string, double> { ["TOP"] = 0, ["MIDDLE"] = 0.1, ["BOTTOM"] = 99.8 }, []);
        var source = new FakeSource(new()
        {
            [(222, "TOP")] = offLane,
            [(222, "BOTTOM")] = Table(222, "BOTTOM", 99.8, (51, 46, 5000), (21, 49, 4000)),
        });
        var view = await CounterPick.ViewAsync(Draft("TOP", false, 222), new(), source, default, opponentId: 222);
        Assert.Equal(("ready", "TOP", "BOTTOM"), (view.State, view.Lane, view.DataLane));
        Assert.Equal([51, 21], view.Picks!.Select(p => p.ChampionId));
    }

    private static ChampSelectBoard Mine(int champion, bool locked, params int[] enemies)
    {
        var b = Draft("MIDDLE", locked, enemies);
        return b with { MyTeam = [b.MyTeam[0] with { ChampionId = champion }] };
    }

    [Fact]
    public async Task My_hover_shows_its_win_rate_against_the_picked_enemies_opponent_first()
    {
        var source = new FakeSource(new()
        {
            [(238, "MIDDLE")] = Table(238, "MIDDLE", 69, (103, 52.4, 9000)),
            [(103, "MIDDLE")] = Table(103, "MIDDLE", 80, (64, 55, 4000), (238, 47.6, 9000), (777, 50, 40)), // 777 almost never mid: dropped
        });
        var view = await CounterPick.ViewAsync(Mine(103, false, 64, 238, 777), new(), source, default);
        Assert.Equal(238, view.EnemyId);
        Assert.True(view.Mine!.Picked);
        Assert.Equal([238, 64], view.Mine.Lines.Select(l => l.ChampionId));
        Assert.Equal(47.6, view.Mine.Lines[0].WinRate);

        var locked = await CounterPick.ViewAsync(Mine(103, true, 64, 238), new(), source, default);
        Assert.Equal(("locked", 238), (locked.State, locked.Mine!.Lines[0].ChampionId));
    }

    [Fact]
    public async Task Before_any_enemy_pick_my_hover_shows_its_hardest_common_matchups()
    {
        var source = new FakeSource(new() { [(103, "MIDDLE")] = Table(103, "MIDDLE", 80, (1, 45, 3000), (2, 40, 100), (3, 47, 3000), (4, 53, 3000), (5, 46, 3000), (6, 49, 3000)) });
        var view = await CounterPick.ViewAsync(Mine(103, false), new(), source, default);
        Assert.Equal("waiting", view.State);
        Assert.False(view.Mine!.Picked);
        Assert.Equal([1, 5, 3], view.Mine.Lines.Select(l => l.ChampionId)); // 2 has too few games

        var strong = new FakeSource(new() { [(103, "MIDDLE")] = Table(103, "MIDDLE", 80, (4, 53, 3000)) });
        Assert.Empty((await CounterPick.ViewAsync(Mine(103, false), new(), strong, default)).Mine!.Lines); // wins them all: nothing to warn about
    }

    [Fact]
    public async Task Threats_are_who_beats_my_hover_most_without_bans()
    {
        var source = new FakeSource(new() { [(103, "MIDDLE")] = Table(103, "MIDDLE", 80, (1, 45, 3000), (2, 40, 100), (3, 47, 3000), (4, 53, 3000), (5, 46, 3000), (6, 42, 3000)) });
        var view = await CounterPick.ViewAsync(Mine(103, false) with { TheirBans = [6] }, new(), source, default);
        Assert.Equal([1, 5, 3, 4], view.Threats!.Select(t => t.ChampionId)); // 6 banned, 2 too few games
        Assert.Equal(55, view.Threats![0].WinRate); // their win rate against my champion
        Assert.Null((await CounterPick.ViewAsync(Draft("MIDDLE", false), new(), source, default)).Threats); // no hover: nothing to beat
    }

    [Fact]
    public async Task Banned_and_locked_champions_are_never_suggested_even_from_the_pool()
    {
        var settings = new GlamourSettings { PreHoverChampionId = 103 };
        var source = new FakeSource(new() { [(238, "MIDDLE")] = Table(238, "MIDDLE", 69, (103, 40, 9000), (75, 44, 3000), (84, 45, 3000), (1, 46, 3000), (2, 47, 3000)) });
        var board = Draft("MIDDLE", false, 238) with { MyBans = [75], TheirBans = [103] }; // 75 banned, 103 (my pool pick) banned
        var ids = (await CounterPick.ViewAsync(board, settings, source, default)).Picks!.Select(p => p.ChampionId);
        Assert.Equal([84, 1, 2], ids);
        var locked = board with { TheirTeam = [.. board.TheirTeam, new BoardPlayer(9, 2, true, "", 0, 0, 0, "", false)] };
        Assert.DoesNotContain(2, (await CounterPick.ViewAsync(locked, settings, source, default)).Picks!.Select(p => p.ChampionId));
    }
    [Fact]
    public async Task Only_playable_champions_are_suggested_but_threats_stay_whole()
    {
        var source = new FakeSource(new()
        {
            [(238, "MIDDLE")] = Table(238, "MIDDLE", 69, (75, 40, 3000), (84, 44, 3000), (1, 45, 3000), (2, 46, 3000), (3, 47, 3000)),
            [(103, "MIDDLE")] = Table(103, "MIDDLE", 80, (75, 45, 3000), (84, 46, 3000)),
        });
        var view = await CounterPick.ViewAsync(Mine(103, false, 238), new(), source, default, playable: [84, 2, 3, 103]);
        Assert.Equal([84, 2, 3], view.Picks!.Select(p => p.ChampionId)); // 75 and 1 not owned: the next playable ones fill the top 3
        Assert.Equal([75, 84], view.Threats!.Select(t => t.ChampionId));  // the enemy can play anything
        Assert.Equal([75, 84, 1], (await CounterPick.ViewAsync(Mine(103, false, 238), new(), source, default)).Picks!.Select(p => p.ChampionId)); // unknown: no limit
    }
    [Fact]
    public async Task With_four_enemies_picked_the_one_left_for_my_lane_counts_even_when_rare_there()
    {
        ChampionMatchups T(int id, double top, double mid, double bot, double sup, double jgl) =>
            new(id, "TOP", new Dictionary<string, double> { ["TOP"] = top, ["MIDDLE"] = mid, ["BOTTOM"] = bot, ["SUPPORT"] = sup, ["JUNGLE"] = jgl }, [new(1, 45, 3000)]);
        var tables = new Dictionary<(int, string), ChampionMatchups>
        {
            [(50, "TOP")] = T(50, 12, 60, 0, 28, 0), [(517, "TOP")] = T(517, 20, 75, 0, 0, 5), [(67, "TOP")] = T(67, 9, 0, 91, 0, 0), [(21, "TOP")] = T(21, 0, 0, 100, 0, 0),
        };
        var source = new FakeSource(tables);
        Assert.Equal("waiting", (await CounterPick.ViewAsync(Draft("TOP", false, 50, 517, 67), new(), source, default)).State); // three picked: nobody is likely top
        var view = await CounterPick.ViewAsync(Draft("TOP", false, 50, 517, 67, 21), new(), source, default);
        Assert.Equal("ready", view.State);
        Assert.Equal(67, view.EnemyId); // MF bottom, Swain support, Sylas mid leave Vayne top (9%): the most likely split
    }

    private static string AramJson => Fixture("lolalytics-list-aram.json");

    [Fact]
    public void The_aram_list_gives_every_champion_its_win_rate_and_games()
    {
        var aram = LolalyticsMatchupSource.ParseList(LolalyticsMatchupSource.TrimList(AramJson))!;
        Assert.Equal(173, aram.Rows.Count); // patch 16.20
        var annie = aram.Rows.Single(r => r.OpponentId == 1);
        Assert.Equal((47.73, 16615), (annie.WinRate, annie.Games));
    }

    [Fact]
    public void A_list_for_another_queue_is_no_aram_data()
    {
        // Unknown queue names answer ranked without an error (INSIGHTS "Matchup data").
        Assert.Null(LolalyticsMatchupSource.ParseList(LolalyticsMatchupSource.TrimList(AramJson.Replace("\"queue\":450", "\"queue\":420"))));
        Assert.Null(LolalyticsMatchupSource.ParseList(LolalyticsMatchupSource.TrimList("""{"status":404}""")));
    }

    [Fact]
    public async Task Aram_win_rates_blend_a_thin_current_patch_with_the_previous_one_and_are_cached_once_per_patch()
    {
        var dir = TempDir();
        // 16.19 thin: Annie 100 games at 60%; 16.18 the fixture (Annie 47.73 over 16,615).
        var thin = """{"queue":450,"cid":{"1":{"wr":60,"games":100},"2":{"wr":51,"games":5000}}}""";
        var handler = new Handler(u => u.Contains("patch=16.19&") ? thin : AramJson);
        var source = Source(handler, dir);
        var rates = (await source.GetModeAsync("aram", default))!;
        Assert.Equal("16.19", rates.Patch);
        var annie = rates.Rows.Single(r => r.OpponentId == 1);
        Assert.Equal((16715, 16615, "16.18"), (annie.Games, annie.OlderGames, annie.OlderPatch));
        Assert.Equal(51, rates.Rows.Single(r => r.OpponentId == 2).WinRate); // enough games of its own
        Assert.Equal(173, rates.Rows.Count);
        Assert.True(File.Exists(Path.Combine(dir, "16.19", "aram-list.json")));

        await source.GetModeAsync("aram", default);
        Assert.Equal(2, handler.Urls.Count);
        Assert.Null(await source.GetModeAsync("mayhem", default));
    }
}
