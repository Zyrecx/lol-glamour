using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json.Nodes;
using Glamour.Core;
using Glamour.Host;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Glamour.Tests;

/// <summary>
/// "Best counter" as the auto-lock, the team score and the enemies' damage (SPEC "Champ select advice"). Fixtures: real Lolalytics `build-full` answers for
/// Zed mid (whole) and Lee Sin, Graves and Kayn jungle (trimmed), 16.20. Nothing here touches the network or a client.
/// </summary>
public class BestCounterTests
{
    private readonly FakeLcu _lcu = new();
    private readonly GlamourSettings _settings = new() { ActionDelaySeconds = 0 };
    private readonly FakeTimeProvider _time = new(DateTimeOffset.FromUnixTimeMilliseconds(1_000_000));
    private readonly Chooser _chooser = new();
    private readonly AutoBanLockFeature _feature;

    public BestCounterTests()
    {
        _feature = new AutoBanLockFeature(_lcu, new ActionGateway(_lcu, _settings, NullLogger<ActionGateway>.Instance), _settings, _time, NullLogger<AutoBanLockFeature>.Instance, counters: _chooser);
        _lcu.Gets["/lol-champ-select/v1/pickable-champion-ids"] = JsonNode.Parse("[1,2,518]");
        _settings.AutoLockChampionId = LaneSetup.BestCounter;
    }

    private sealed class Chooser : ICounterChooser
    {
        public Queue<Func<Task<IReadOnlyList<int>>>> Answers { get; } = [];
        public IReadOnlyList<int> Order { get; set; } = [];
        public int Calls { get; private set; }
        public Task<IReadOnlyList<int>> ChooseAsync(ChampSelectBoard board, CancellationToken ct)
        {
            Calls++;
            return Answers.TryDequeue(out var next) ? next() : Task.FromResult(Order);
        }
    }

    private static readonly ChampSelectBoard Board = new("BAN_PICK", 0, [new BoardPlayer(0, 0, false, "MIDDLE", 0, 0, 0, "me", true)], [], [], [], [], 0);

    /// <param name="allyHover">A teammate (cell 1) has this champion hovered.</param>
    private ChampSelectSession Turn(int championId = 0, int allyHover = 0, long timeLeftMs = 30_000) =>
        new(0, new ChampSelectTimer("BAN_PICK", timeLeftMs, _time.GetUtcNow().ToUnixTimeMilliseconds()),
            [new ChampSelectAction(7, 0, "pick", championId, false, true), new ChampSelectAction(8, 1, "pick", allyHover, false, false)], new HashSet<int> { 0, 1 }, Board: Board);

    /// <summary>The session the client answers when the last-second lock looks again: my action 7 holds <paramref name="championId"/>.</summary>
    private void Live(int championId) =>
        _lcu.Gets["/lol-champ-select/v1/session"] = JsonNode.Parse($$"""
            {"localPlayerCellId":0,"timer":{"phase":"BAN_PICK","adjustedTimeLeftInPhase":3000,"internalNowInEpochMs":1},
             "myTeam":[{"cellId":0,"assignedPosition":"middle"}],
             "actions":[[{"id":7,"actorCellId":0,"type":"pick","championId":{{championId}},"completed":false,"isInProgress":true}]]}
            """);

    private string[] Bodies => [.. _lcu.Writes.Select(w => w.Body ?? "")];

    private static async Task WaitFor(Func<bool> done)
    {
        for (var i = 0; i < 100 && !done(); i++) await Task.Delay(20);
        Assert.True(done());
    }

    [Fact]
    public async Task On_an_empty_pick_it_locks_the_first_champion_I_can_pick()
    {
        _chooser.Order = [5, 2, 1]; // 5 is not mine
        await _feature.OnChampSelectAsync(Turn(), default);
        Assert.Equal(2, _lcu.Writes.Count);
        Assert.All(Bodies, b => Assert.Contains("\"championId\":2", b));
        Assert.Contains("\"completed\":true", Bodies[1]);
    }

    [Fact]
    public async Task A_champion_a_teammate_hovers_is_passed_over()
    {
        _chooser.Order = [2, 1];
        await _feature.OnChampSelectAsync(Turn(allyHover: 2), default);
        Assert.Contains("\"championId\":1", Bodies[0]);
    }

    [Fact]
    public async Task The_users_own_hover_is_left_alone_and_no_data_locks_nothing()
    {
        _chooser.Order = [2];
        await _feature.OnChampSelectAsync(Turn(championId: 518), default);
        Assert.Empty(_lcu.Writes);
        Assert.Equal(0, _chooser.Calls);

        await _feature.OnPhaseAsync("None", default);
        _chooser.Order = [];
        await _feature.OnChampSelectAsync(Turn(), default);
        Assert.Empty(_lcu.Writes);
    }

    [Fact]
    public async Task At_the_last_second_it_asks_again_and_locks_the_new_best()
    {
        _settings.LockTiming = ActionTiming.LastSecond;
        _chooser.Order = [2];
        await _feature.OnChampSelectAsync(Turn(), default);
        Assert.Single(_lcu.Writes); // the hover, so the team sees it
        Assert.Contains("\"championId\":2", Bodies[0]);

        Live(2);
        _chooser.Order = [1]; // an enemy picked meanwhile and 1 counters the team better
        _time.Advance(TimeSpan.FromSeconds(25.4));
        await Task.Delay(100);
        Assert.Equal(1, _chooser.Calls); // not yet: it looks again at margin (3 s) + its own limit (1.5 s) left
        _time.Advance(TimeSpan.FromSeconds(0.2));
        await WaitFor(() => _chooser.Calls == 2);
        await Task.Delay(100);
        Assert.Single(_lcu.Writes); // answered at once: the lock still waits for the margin, not 1.5 s early (live 2026-10-10)
        _time.Advance(TimeSpan.FromSeconds(1.4));
        await WaitFor(() => _lcu.Writes.Count == 2);
        Assert.Contains("\"championId\":1", Bodies[1]);
        Assert.Contains("\"completed\":true", Bodies[1]);
    }

    [Fact]
    public async Task A_hover_the_user_makes_after_the_second_look_still_wins()
    {
        _settings.LockTiming = ActionTiming.LastSecond;
        _chooser.Order = [2];
        await _feature.OnChampSelectAsync(Turn(), default);
        Live(2);
        _time.Advance(TimeSpan.FromSeconds(25.6));
        await WaitFor(() => _chooser.Calls == 2);
        Live(518); // the user hovered Neeko in the last 1.5 s
        _time.Advance(TimeSpan.FromSeconds(1.5));
        await Task.Delay(150);
        Assert.Single(_lcu.Writes);
    }

    [Fact]
    public async Task At_the_last_second_a_hover_the_user_made_meanwhile_wins()
    {
        _settings.LockTiming = ActionTiming.LastSecond;
        _chooser.Order = [2];
        await _feature.OnChampSelectAsync(Turn(), default);
        Live(518); // the user hovered Neeko
        _time.Advance(TimeSpan.FromSeconds(27.1));
        await Task.Delay(150);
        Assert.Single(_lcu.Writes);
    }

    [Fact]
    public async Task A_slow_second_look_locks_the_champion_already_hovered()
    {
        _settings.LockTiming = ActionTiming.LastSecond;
        _chooser.Order = [2];
        await _feature.OnChampSelectAsync(Turn(), default);
        Live(2);
        var never = new TaskCompletionSource<IReadOnlyList<int>>();
        _chooser.Answers.Enqueue(() => never.Task);
        _time.Advance(TimeSpan.FromSeconds(25.6));
        await WaitFor(() => _chooser.Calls == 2);
        _time.Advance(AutoBanLockFeature.RechooseTimeout);
        await WaitFor(() => _lcu.Writes.Count == 2);
        Assert.Contains("\"championId\":2", Bodies[1]);
    }

    [Fact]
    public async Task A_first_look_that_never_answers_locks_nothing_and_is_not_asked_twice()
    {
        var never = new TaskCompletionSource<IReadOnlyList<int>>();
        _chooser.Answers.Enqueue(() => never.Task);
        var turn = _feature.OnChampSelectAsync(Turn(), default);
        await _feature.OnChampSelectAsync(Turn(), default); // an update while the first look waits
        Assert.Equal(1, _chooser.Calls);
        _time.Advance(AutoBanLockFeature.ChooseTimeout);
        await turn;
        await _feature.OnChampSelectAsync(Turn(), default);
        Assert.Equal(1, _chooser.Calls);
        Assert.Empty(_lcu.Writes);
    }

    // The order the auto-lock takes from the panel's view.

    [Fact]
    public void Against_a_known_opponent_the_lane_list_decides_pool_first_few_games_last()
    {
        var view = new CounterPickView("ready", "MIDDLE", 238, [
            new CounterSuggestion(1, 56, 3000, false, false), new CounterSuggestion(2, 53, 2000, false, true),
            new CounterSuggestion(3, 60, 120, true, true), new CounterSuggestion(4, 54, 3000, false, false)],
            TeamPicks: [new TeamSuggestion(9, 70, 3, 3000, false)]);
        Assert.Equal([2, 1, 4, 3], BestCounterPick.Order(view));
    }

    [Fact]
    public void Without_an_opponent_the_team_list_decides()
    {
        var view = new CounterPickView("waiting", "MIDDLE", TeamPicks: [new TeamSuggestion(1, 52, 2, 900, false), new TeamSuggestion(2, 55, 2, 1200, false), new TeamSuggestion(3, 58, 1, 100, true)]);
        Assert.Equal([2, 1, 3], BestCounterPick.Order(view));
        Assert.Empty(BestCounterPick.Order(new CounterPickView("locked", "MIDDLE")));
    }

    // Team score and damage.

    private static ChampionProfile Profile(int id, string lane, DamageSplit? damage, params (int Id, double Wr, int N)[] vsMiddle) =>
        new(id, lane, damage, new Dictionary<string, IReadOnlyList<MatchupRow>> { ["MIDDLE"] = [.. vsMiddle.Select(r => new MatchupRow(r.Id, r.Wr, r.N))] });

    [Fact]
    public void Team_score_averages_every_enemy_and_pulls_thin_pairs_toward_50()
    {
        var scores = TeamPicker.Scores([Profile(10, "TOP", null, (1, 40, 300), (2, 45, 3000)), Profile(20, "JUNGLE", null, (2, 50, 3000))], "MIDDLE");
        Assert.Equal((52.5, 1, 300), scores[1]);   // vs 10: 50 + 10 * 300/600 = 55; nothing vs 20 counts 50
        Assert.Equal((52.27, 2, 6000), scores[2]); // vs 10: 50 + 5 * 3000/3300 = 54.55; vs 20: 50
    }

    [Fact]
    public void Team_picks_follow_the_lane_list_rules()
    {
        var scores = new Dictionary<int, (double, int, int)> { [1] = (55, 2, 2000), [2] = (54, 2, 2000), [3] = (60, 1, 100), [4] = (53, 2, 2000), [5] = (51, 1, 50) };
        var picks = TeamPicker.Rank(scores, pool: [5], top: 2, taken: [2], playable: [1, 3, 4, 5], lane: new Dictionary<int, double> { [1] = 57.5 });
        Assert.Equal([1, 4, 5], picks.Select(p => p.ChampionId)); // 2 taken, 3 too few games, 5 from the pool
        Assert.Equal(57.5, picks[0].Lane);
        Assert.Null(picks[1].Lane);
    }

    [Fact]
    public void Lane_share_counts_a_champions_games_against_the_enemies_in_every_lane()
    {
        var a = new ChampionProfile(10, "TOP", null, new Dictionary<string, IReadOnlyList<MatchupRow>>
        {
            ["MIDDLE"] = [new(1, 50, 60), new(2, 50, 900)],
            ["SUPPORT"] = [new(1, 50, 900), new(10, 50, 5000)],
        });
        var b = new ChampionProfile(20, "JUNGLE", null, new Dictionary<string, IReadOnlyList<MatchupRow>> { ["MIDDLE"] = [new(1, 50, 40)], ["TOP"] = [new(2, 50, 100)] });
        var shares = TeamPicker.LaneShares([a, b], "MIDDLE");
        Assert.Equal(10, shares[1]);   // 100 of 1,000
        Assert.Equal(90, shares[2]);   // 900 of 1,000
        Assert.False(shares.ContainsKey(10)); // an enemy's own row is not a champion I could pick
    }

    [Fact]
    public void Team_picks_leave_out_off_meta_champions_unless_nothing_else_is_left()
    {
        var scores = new Dictionary<int, (double, int, int)> { [1] = (58, 2, 2000), [2] = (54, 2, 2000), [3] = (53, 2, 2000), [4] = (57, 2, 2000) };
        var shares = new Dictionary<int, double> { [1] = 6.5, [2] = 80, [3] = 10, [4] = 4 };
        Assert.Equal([2, 3], TeamPicker.Rank(scores, pool: [], shares: shares).Select(p => p.ChampionId));
        Assert.Equal([4, 2, 3], TeamPicker.Rank(scores, pool: [4], shares: shares).Select(p => p.ChampionId)); // the pool whatever its share
        Assert.Equal([1, 4], TeamPicker.Rank(scores, pool: [], taken: [2, 3], shares: shares).Select(p => p.ChampionId)); // none on the lane: all of them
    }

    [Fact]
    public void Damage_adds_up_the_enemies_and_keeps_each_ones_split()
    {
        var d = TeamDamage.From([Profile(10, "TOP", new(10_000, 2_000, 500)), Profile(20, "MIDDLE", new(1_000, 15_000, 0)), Profile(30, "BOTTOM", null)])!;
        Assert.Equal((11_000, 17_000, 500), (d.Physical, d.Magic, d.True));
        Assert.Equal([10, 20], d.ByChampion.Keys.Order());
        Assert.Null(TeamDamage.From([Profile(30, "BOTTOM", null)]));
    }

    // Parsing the real answers.

    private static string Fixture(string name) => File.ReadAllText(FixtureFiles.PathOf(name));

    [Fact]
    public void A_full_answer_is_trimmed_to_damage_lean_and_rows_and_read_with_the_lean_off()
    {
        var raw = Fixture("lolalytics-full-zed-mid.json");
        var trimmed = LolalyticsMatchupSource.TrimFull(raw);
        Assert.True(trimmed.Length < raw.Length / 4);
        var zed = LolalyticsMatchupSource.ParseProfile(238, "MIDDLE", trimmed)!;
        Assert.Equal(new DamageSplit(25643.93, 1017, 2725.45), zed.Damage);
        Assert.Equal(LaneNames.All.Order(), zed.Against.Keys.Order());
        Assert.Equal(new MatchupRow(45, 56.09, 653), zed.Against["MIDDLE"].Single(r => r.OpponentId == 45)); // 57.58 in the answer, less the lean (avgWr 51.49; the header's 49.41 is another number)
        Assert.Equal(new MatchupRow(64, 51.32, 2691), zed.Against["JUNGLE"].Single(r => r.OpponentId == 64));
        Assert.Equal(167, zed.Against["MIDDLE"].Count); // the counter table has only those with ~100 games or more (59)
        Assert.Null(LolalyticsMatchupSource.ParseProfile(238, "MIDDLE", LolalyticsMatchupSource.TrimFull("""{"status":404}""")));
    }

    [Fact]
    public void Across_lanes_both_tables_of_a_pair_agree_once_the_lean_is_off()
    {
        var zed = LolalyticsMatchupSource.ParseProfile(238, "MIDDLE", LolalyticsMatchupSource.TrimFull(Fixture("lolalytics-full-zed-mid.json")))!;
        foreach (var (slug, id) in new[] { ("leesin", 64), ("graves", 104), ("kayn", 141) })
        {
            var jungler = LolalyticsMatchupSource.ParseProfile(id, "JUNGLE", Fixture($"lolalytics-full-{slug}-jungle.json"))!;
            var sum = zed.Against["JUNGLE"].Single(r => r.OpponentId == id).WinRate + jungler.Against["MIDDLE"].Single(r => r.OpponentId == 238).WinRate;
            Assert.InRange(sum, 100, 101.5); // 103.7 to 104.2 before
        }
    }

    [Fact]
    public async Task The_counter_view_adds_team_picks_and_damage_and_names_what_best_counter_locks()
    {
        var lee = LolalyticsMatchupSource.ParseProfile(64, "JUNGLE", Fixture("lolalytics-full-leesin-jungle.json"))!;
        var graves = LolalyticsMatchupSource.ParseProfile(104, "JUNGLE", Fixture("lolalytics-full-graves-jungle.json"))!;
        var profiles = new Profiles { [(64, "JUNGLE")] = lee, [(104, "TOP")] = graves with { Lane = "TOP" } };
        var matchups = new Tables
        {
            [(64, "MIDDLE")] = new(64, "MIDDLE", new Dictionary<string, double> { ["JUNGLE"] = 95, ["TOP"] = 5 }, []),
            [(104, "MIDDLE")] = new(104, "MIDDLE", new Dictionary<string, double> { ["JUNGLE"] = 80, ["TOP"] = 20 }, []),
        };
        var board = new ChampSelectBoard("BAN_PICK", 0, [new BoardPlayer(0, 0, false, "MIDDLE", 0, 0, 0, "me", true)],
            [new BoardPlayer(5, 64, false, "", 0, 0, 0, "", false), new BoardPlayer(6, 104, false, "", 0, 0, 0, "", false)], [], [], [], 0);
        var settings = new GlamourSettings { AutoLockChampionId = LaneSetup.BestCounter };

        var view = await CounterPick.ViewAsync(board, settings, matchups, default, profiles: profiles);
        Assert.Equal("waiting", view.State); // two junglers, nobody likely mid
        Assert.Equal(5, view.TeamPicks!.Count);
        Assert.True(view.TeamPicks.Zip(view.TeamPicks.Skip(1)).All(x => x.First.WinRate >= x.Second.WinRate));
        Assert.All(view.TeamPicks, p => Assert.Equal(2, p.Covered));
        Assert.Equal(view.TeamPicks[0].ChampionId, view.AutoLock);
        Assert.Equal(lee.Damage!.Physical + graves.Damage!.Physical, view.Damage!.Physical, 0);

        var locked = await CounterPick.ViewAsync(board with { MyTeam = [board.MyTeam[0] with { Locked = true, ChampionId = 238 }] }, settings, matchups, default, profiles: profiles);
        Assert.Null(locked.TeamPicks);
        Assert.Null(locked.AutoLock);
        Assert.NotNull(locked.Damage); // still worth knowing what to build against
    }

    [Fact]
    public async Task My_teams_damage_counts_every_ally_with_a_champion_in_its_own_lane()
    {
        var lee = LolalyticsMatchupSource.ParseProfile(64, "JUNGLE", Fixture("lolalytics-full-leesin-jungle.json"))!;
        var zed = LolalyticsMatchupSource.ParseProfile(238, "MIDDLE", LolalyticsMatchupSource.TrimFull(Fixture("lolalytics-full-zed-mid.json")))!;
        var profiles = new Profiles { [(64, "JUNGLE")] = lee, [(238, "MIDDLE")] = zed };
        var board = new ChampSelectBoard("BAN_PICK", 0, [
            new BoardPlayer(0, 238, false, "MIDDLE", 0, 0, 0, "me", true), // my hover counts
            new BoardPlayer(1, 64, true, "JUNGLE", 0, 0, 0, "", false),
            new BoardPlayer(2, 0, false, "TOP", 0, 0, 0, "", false), // nothing yet
            new BoardPlayer(3, 104, true, "", 0, 0, 0, "", false)], [], [], [], [], 0); // no lane: no profile to read

        var view = await CounterPick.ViewAsync(board, new GlamourSettings(), new Tables(), default, profiles: profiles);
        Assert.Equal([64, 238], view.AllyDamage!.ByChampion.Keys.Order());
        Assert.Equal(lee.Damage!.Magic + zed.Damage!.Magic, view.AllyDamage.Magic, 0);
        Assert.Null(view.Damage); // no enemy picked

        var none = await CounterPick.ViewAsync(board with { MyTeam = [board.MyTeam[0] with { ChampionId = 0 }] }, new GlamourSettings(), new Tables(), default, profiles: profiles);
        Assert.Null(none.AllyDamage);
    }

    private sealed class Profiles : Dictionary<(int, string), ChampionProfile>, IChampionProfileSource
    {
        public Task<ChampionProfile?> GetProfileAsync(int championId, string lane, CancellationToken ct) => Task.FromResult(this.GetValueOrDefault((championId, lane)));
    }

    private sealed class Tables : Dictionary<(int, string), ChampionMatchups>, IMatchupSource
    {
        public Task<ChampionMatchups?> GetAsync(int championId, string lane, CancellationToken ct) => Task.FromResult(this.GetValueOrDefault((championId, lane)));
    }
}

/// <summary>The counter view and the auto-lock's chooser in the real composition (in-memory host, no client).</summary>
public class CounterPickHostTests
{
    [Fact]
    public async Task The_endpoint_answers_through_the_chooser_and_remembers_the_opponent_asked_for()
    {
        using var host = new TestHost();
        var r = await host.Client(TestHost.Loopback).GetAsync("/api/counter-pick?enemy=238");
        Assert.Equal(System.Net.HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("none", (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("state").GetString()); // no champ select
        var services = host.Services;
        Assert.Equal(238, services.GetRequiredService<CounterChoice>().Opponent);
        Assert.IsType<CounterChooser>(services.GetRequiredService<ICounterChooser>());
        Assert.Same(services.GetRequiredService<IMatchupSource>(),
            services.GetRequiredService<IChampionProfileSource>());
        Assert.NotNull(services.GetRequiredService<AutoBanLockFeature>()); // no cycle through the chooser
    }
}
