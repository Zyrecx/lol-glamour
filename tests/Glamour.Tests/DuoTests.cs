using Glamour.Core;
using Glamour.Host;

namespace Glamour.Tests;

/// <summary>
/// Duo synergy in bot lane (SPEC "Champ select advice"): the partner's ally table, the picks that do best with it and Best counter's tie-break.
/// Fixtures: real Lolalytics `build-team` answers for Jinx bottom and Thresh support, 16.20. Nothing here touches the network or a client.
/// </summary>
public class DuoTests
{
    private const int Jinx = 222, Thresh = 412, Braum = 201, Blitzcrank = 53, Rakan = 526;

    private static ChampionAllies JinxTable() =>
        LolalyticsMatchupSource.ParseAllies(Jinx, "BOTTOM", LolalyticsMatchupSource.TrimTeam(File.ReadAllText(FixtureFiles.PathOf("lolalytics-team-jinx-bottom.json"))))!;

    [Fact]
    public void A_team_answer_is_trimmed_to_rows_per_lane()
    {
        var raw = File.ReadAllText(FixtureFiles.PathOf("lolalytics-team-jinx-bottom.json"));
        var trimmed = LolalyticsMatchupSource.TrimTeam(raw);
        Assert.True(trimmed.Length < raw.Length);
        var jinx = LolalyticsMatchupSource.ParseAllies(Jinx, "BOTTOM", trimmed)!;
        Assert.Equal(["JUNGLE", "MIDDLE", "SUPPORT", "TOP"], jinx.With.Keys.Order());
        Assert.Equal(new MatchupRow(Thresh, 56.31, 12834), jinx.With["SUPPORT"].Single(r => r.OpponentId == Thresh));
        Assert.Null(LolalyticsMatchupSource.ParseAllies(Jinx, "BOTTOM", LolalyticsMatchupSource.TrimTeam("""{"status":404}""")));
    }

    [Fact]
    public void Both_tables_of_a_pair_agree()
    {
        var thresh = LolalyticsMatchupSource.ParseAllies(Thresh, "SUPPORT", LolalyticsMatchupSource.TrimTeam(File.ReadAllText(FixtureFiles.PathOf("lolalytics-team-thresh-support.json"))))!;
        var fromThresh = thresh.With["BOTTOM"].Single(r => r.OpponentId == Jinx).WinRate;
        var fromJinx = JinxTable().With["SUPPORT"].Single(r => r.OpponentId == Thresh).WinRate;
        Assert.InRange(Math.Abs(fromThresh - fromJinx), 0, 0.5); // 56.24 and 56.31
    }

    [Fact]
    public void The_best_partners_come_from_the_partners_table_thin_pairs_pulled_to_its_mean()
    {
        Assert.Equal([Braum, Blitzcrank, Thresh], DuoPicker.Best(JinxTable(), "SUPPORT"));
        // Rakan (56.65 over 1,647) has a higher raw rate than Thresh (56.31 over 12,834) but fewer games behind it.
        Assert.Equal([Blitzcrank, Thresh, Rakan], DuoPicker.Best(JinxTable(), "SUPPORT", taken: [Braum]));
        Assert.Equal([Thresh, Rakan], DuoPicker.Best(JinxTable(), "SUPPORT", playable: [Thresh, Rakan, 1]));
        Assert.Empty(DuoPicker.Best(JinxTable(), "BOTTOM")); // no rows for its own lane
    }

    [Theory]
    [InlineData("BOTTOM", "SUPPORT")]
    [InlineData("SUPPORT", "BOTTOM")]
    [InlineData("MIDDLE", null)]
    [InlineData("JUNGLE", null)]
    public void Only_bot_lane_has_a_partner(string lane, string? partner) => Assert.Equal(partner, DuoPicker.PartnerLane(lane));

    // Best counter's tie-break.

    private static CounterPickView Lane(int[] duo, params CounterSuggestion[] picks) => new("ready", "SUPPORT", 99, picks, Duo: new DuoView(Jinx, duo));

    [Fact]
    public void A_duo_pick_within_a_point_of_the_top_goes_first()
    {
        var view = Lane([2], new CounterSuggestion(1, 55, 3000, false, false), new CounterSuggestion(2, 54.2, 3000, false, false), new CounterSuggestion(3, 54.5, 3000, false, false));
        Assert.Equal([2, 1, 3], BestCounterPick.Order(view));
    }

    [Fact]
    public void A_duo_pick_further_behind_stays_where_it_is()
    {
        var view = Lane([2], new CounterSuggestion(1, 55, 3000, false, false), new CounterSuggestion(2, 53.5, 3000, false, false));
        Assert.Equal([1, 2], BestCounterPick.Order(view));
    }

    [Fact]
    public void A_duo_pick_never_crosses_the_pool_or_few_games_tiers()
    {
        // The pool champion leads; the duo pick is close but not in the pool.
        Assert.Equal([1, 2], BestCounterPick.Order(Lane([2], new CounterSuggestion(1, 54, 3000, false, true), new CounterSuggestion(2, 54.5, 3000, false, false))));
        // A few-games duo pick stays behind the rows with enough games.
        Assert.Equal([1, 2], BestCounterPick.Order(Lane([2], new CounterSuggestion(1, 55, 3000, false, false), new CounterSuggestion(2, 56, 120, true, false))));
    }

    [Fact]
    public void Without_a_partner_the_order_is_unchanged()
    {
        var view = new CounterPickView("ready", "SUPPORT", 99, [new CounterSuggestion(1, 55, 3000, false, false), new CounterSuggestion(2, 54.5, 3000, false, false)]);
        Assert.Equal([1, 2], BestCounterPick.Order(view));
    }

    [Fact]
    public void Team_picks_get_the_tie_break_too()
    {
        var view = new CounterPickView("waiting", "SUPPORT", TeamPicks: [new TeamSuggestion(1, 53, 2, 3000, false), new TeamSuggestion(2, 52.5, 2, 3000, false)], Duo: new DuoView(Jinx, [2]));
        Assert.Equal([2, 1], BestCounterPick.Order(view));
    }

    // The counter view.

    private static ChampSelectBoard Board(string myLane, bool locked = false, int partnerChampion = Jinx, string partnerLane = "BOTTOM") =>
        new("BAN_PICK", 0, [new BoardPlayer(0, 0, locked, myLane, 0, 0, 0, "me", true), new BoardPlayer(1, partnerChampion, false, partnerLane, 0, 0, 0, "", false)],
            [], [Braum], [], [], 0);

    [Fact]
    public async Task The_view_names_my_partner_and_its_best_picks_left_out_once_taken()
    {
        var view = await CounterPick.ViewAsync(Board("SUPPORT"), new GlamourSettings(), new NoTables(), default, allies: new Allies(JinxTable()));
        Assert.Equal(Jinx, view.Duo!.PartnerId);
        Assert.Equal([Blitzcrank, Thresh, Rakan], view.Duo.Best); // Braum is banned
    }

    [Fact]
    public async Task No_duo_outside_bot_lane_without_a_partner_champion_or_after_my_lock()
    {
        var allies = new Allies(JinxTable());
        Assert.Null((await CounterPick.ViewAsync(Board("MIDDLE", partnerLane: "TOP"), new GlamourSettings(), new NoTables(), default, allies: allies)).Duo);
        Assert.Null((await CounterPick.ViewAsync(Board("SUPPORT", partnerChampion: 0), new GlamourSettings(), new NoTables(), default, allies: allies)).Duo);
        Assert.Null((await CounterPick.ViewAsync(Board("SUPPORT", locked: true), new GlamourSettings(), new NoTables(), default, allies: allies)).Duo);
    }

    private sealed class Allies(ChampionAllies table) : IAllySource
    {
        public Task<ChampionAllies?> GetAlliesAsync(int championId, string lane, CancellationToken ct) =>
            Task.FromResult(championId == table.ChampionId && lane == table.Lane ? table : null);
    }

    private sealed class NoTables : IMatchupSource
    {
        public Task<ChampionMatchups?> GetAsync(int championId, string lane, CancellationToken ct) => Task.FromResult<ChampionMatchups?>(null);
    }
}
