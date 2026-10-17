using System.Text.Json;
using System.Text.Json.Nodes;
using Glamour.Core;

namespace Glamour.Tests;

/// <summary>Shapes copied from real draft and custom dumps (Fixtures/draft-dump-*.json, cs-dump-*.json).</summary>
public class BoardTests
{
    private static string Player(int cell, int team, string name = "", string tag = "", int champ = 0, int intent = 0, string pos = "", int s1 = 0, int s2 = 0, int skin = 0) =>
        $$"""{"assignedPosition":"{{pos}}","cellId":{{cell}},"championId":{{champ}},"championPickIntent":{{intent}},"gameName":"{{name}}","tagLine":"{{tag}}","nameVisibilityType":"VISIBLE","selectedSkinId":{{skin}},"spell1Id":{{s1}},"spell2Id":{{s2}},"summonerId":1,"team":{{team}}}""";

    private static string Act(int id, int cell, string type, int champ, bool done, bool live) =>
        $$"""{"id":{{id}},"actorCellId":{{cell}},"type":"{{type}}","championId":{{champ}},"completed":{{done.ToString().ToLowerInvariant()}},"isInProgress":{{live.ToString().ToLowerInvariant()}},"isAllyAction":true}""";

    private static ChampSelectBoard Board(string actions, string mine, string theirs, string bans = """{"myTeamBans":[],"theirTeamBans":[],"numBans":0}""", string phase = "BAN_PICK") =>
        ChampSelectSession.FromJson(JsonNode.Parse($$"""
            {"localPlayerCellId":5,"timer":{"phase":"{{phase}}","adjustedTimeLeftInPhase":12000,"internalNowInEpochMs":1},
             "myTeam":[{{mine}}],"theirTeam":[{{theirs}}],"bans":{{bans}},"actions":[[{{actions}}]]}
            """))!.Board!;

    [Fact]
    public void Team_hover_lock_spells_skin_and_names()
    {
        var b = Board(Act(1, 5, "pick", 0, false, true) + "," + Act(2, 6, "pick", 99, false, false),
            Player(5, 2, "yarrowby", "EUE", intent: 145, pos: "bottom", s1: 4, s2: 21, skin: 145001) + "," + Player(6, 2, champ: 22, pos: "utility"),
            Player(0, 1) + "," + Player(1, 1, champ: 238));
        var me = b.MyTeam.Single(p => p.IsMe);
        Assert.Equal((145, false, "BOTTOM", 4, 21, 145001, "yarrowby#EUE"), (me.ChampionId, me.Locked, me.Lane, me.Spell1Id, me.Spell2Id, me.SkinId, me.Name));
        Assert.True(b.MyTeam[1] is { ChampionId: 22, Locked: true, Lane: "SUPPORT" });
        Assert.Equal("", b.TheirTeam[0].Name); // hidden enemy: placeholder in the UI
        Assert.Equal(238, b.TheirTeam[1].ChampionId);
    }

    [Fact]
    public void Hover_falls_back_to_the_pick_action_when_no_intent()
    {
        var b = Board(Act(1, 5, "pick", 99, false, true), Player(5, 2), Player(0, 1));
        Assert.True(b.MyTeam[0] is { ChampionId: 99, Locked: false });
    }

    [Fact]
    public void Draft_bans_run_in_parallel_so_several_turns_are_live_and_bans_split_by_team()
    {
        var b = Board(Act(1, 5, "ban", 0, false, true) + "," + Act(2, 6, "ban", 0, false, true) + "," + Act(3, 0, "ban", 238, true, false) + "," + Act(4, 1, "ban", 0, true, false),
            Player(5, 2) + "," + Player(6, 2), Player(0, 1) + "," + Player(1, 1));
        Assert.Equal([new BoardTurn(5, "ban"), new BoardTurn(6, "ban")], b.Acting);
        Assert.Equal([238], b.TheirBans); // actor cell 0 is on their team; the empty ban (championId 0, a skipped ban) is not a ban
        Assert.Empty(b.MyBans);
    }

    [Fact]
    public void Bans_object_is_only_the_fallback()
    {
        var b = Board("", Player(5, 2), Player(0, 1), """{"myTeamBans":[10,0],"theirTeamBans":[20],"numBans":2}""");
        Assert.Equal([10], b.MyBans);
        Assert.Equal([20], b.TheirBans);
    }

    [Fact]
    public void Real_dumps_parse()
    {
        foreach (var f in new[] { "cs-dump-1790943347862.json", "draft-dump-1790939689729.json" })
        {
            var dump = JsonNode.Parse(File.ReadAllText(FixtureFiles.PathOf(f)))!["/lol-champ-select/v1/session"]!["body"];
            var board = ChampSelectSession.FromJson(dump)!.Board!;
            Assert.Equal(5, board.MyTeam.Count);
            Assert.Equal(5, board.TheirTeam.Count);
            Assert.NotEmpty(board.Acting);
        }
    }

    private static ChampSelectBoard? Dump(string file)
    {
        return ChampSelectSession.FromJson(JsonNode.Parse(File.ReadAllText(FixtureFiles.PathOf(file)))!["/lol-champ-select/v1/session"]!["body"])!.Board;
    }

    [Fact]
    public void Real_draft_gives_pick_turns_and_the_next_turn_after_the_bans()
    {
        var b = Dump("draft-dump-1790939666491.json")!; // PLANNING: ten bans live, then 0 | 5,6 | 1,2 | 7,8 | 3,4 | 9
        var turns = b.MyTeam.Concat(b.TheirTeam).ToDictionary(p => p.CellId, p => p.PickTurn);
        Assert.Equal(new Dictionary<int, int> { [0] = 1, [5] = 2, [6] = 2, [1] = 3, [2] = 3, [7] = 4, [8] = 4, [3] = 5, [4] = 5, [9] = 6 }, turns);
        Assert.Equal([new BoardTurn(0, "pick")], b.Next); // ten_bans_reveal is not a turn
        Assert.Equal(0, b.PickTurnNow);
    }

    [Fact]
    public void Mayhem_everyone_picks_in_turn_one_and_nobody_is_next()
    {
        var b = Dump("cs-dump-1790943347862.json")!;
        Assert.All(b.MyTeam, p => Assert.Equal(1, p.PickTurn));
        Assert.Empty(b.Next!);
        Assert.Equal(1, b.PickTurnNow);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)] // a pick-order swap exchanged cells 6 and 1: the order follows the groups
    public void Mid_draft_order_follows_the_groups(bool swapped)
    {
        var (a, c) = swapped ? (1, 6) : (6, 1);
        var b = ChampSelectSession.FromJson(JsonNode.Parse($$"""
            {"localPlayerCellId":1,"timer":{"phase":"BAN_PICK","adjustedTimeLeftInPhase":20000,"internalNowInEpochMs":1},
             "myTeam":[{"cellId":0},{"cellId":1},{"cellId":2}],"theirTeam":[{"cellId":5},{"cellId":6}],
             "actions":[[{"id":0,"actorCellId":0,"type":"ban","championId":1,"completed":true,"isInProgress":false}],
                        [{"id":10,"actorCellId":0,"type":"pick","championId":2,"completed":true,"isInProgress":false}],
                        [{"id":11,"actorCellId":5,"type":"pick","championId":0,"completed":false,"isInProgress":true},
                         {"id":12,"actorCellId":{{a}},"type":"pick","championId":0,"completed":false,"isInProgress":true}],
                        [{"id":13,"actorCellId":{{c}},"type":"pick","championId":0,"completed":false,"isInProgress":false},
                         {"id":14,"actorCellId":2,"type":"pick","championId":0,"completed":false,"isInProgress":false}]]}
            """))!.Board!;
        Assert.Equal(2, b.PickTurnNow);
        Assert.Equal(swapped ? 2 : 3, b.MyTeam.Single(p => p.IsMe).PickTurn);
        Assert.Equal([new BoardTurn(Math.Min(c, 2), "pick"), new BoardTurn(Math.Max(c, 2), "pick")], b.Next);
        Assert.False(b.SameAs(b with { Next = [] }));
        Assert.False(b.SameAs(b with { PickTurnNow = 3 }));
    }

    [Fact]
    public void Board_names_my_pending_pick_and_who_made_each_ban()
    {
        var b = ChampSelectSession.FromJson(JsonNode.Parse("""
            {"localPlayerCellId":1,"timer":{"phase":"PLANNING","adjustedTimeLeftInPhase":20000,"internalNowInEpochMs":1},
             "myTeam":[{"cellId":0},{"cellId":1}],"theirTeam":[{"cellId":5},{"cellId":6}],
             "actions":[[{"id":0,"actorCellId":0,"type":"ban","championId":10,"completed":true,"isInProgress":false},
                         {"id":1,"actorCellId":5,"type":"ban","championId":20,"completed":true,"isInProgress":false},
                         {"id":2,"actorCellId":6,"type":"ban","championId":0,"completed":false,"isInProgress":true}],
                        [{"id":7,"actorCellId":1,"type":"pick","championId":0,"completed":false,"isInProgress":false}]]}
            """))!.Board!;
        Assert.Equal(7, b.MyPickActionId);
        Assert.Equal([new BoardBan(0, 10, true), new BoardBan(5, 20, false)], b.BanList);
        Assert.Equal([0, 5, 6], b.BanCells); // bans still running: who has a ban box on their card, done or not (cell 1 has none)
        Assert.False(b.SameAs(b with { MyPickActionId = 0 }));
        Assert.False(b.SameAs(b with { BanCells = [0] }));
    }

    [Fact]
    public void A_ban_stays_with_the_player_who_made_it_after_a_pick_order_swap()
    {
        static JsonObject Session(int local, long cell0, long cell1, string enemyPuuid) => (JsonObject)JsonNode.Parse($$"""
            {"localPlayerCellId":{{local}},"timer":{"phase":"BAN_PICK","adjustedTimeLeftInPhase":20000,"internalNowInEpochMs":1},
             "myTeam":[{"cellId":0,"summonerId":{{cell0}},"puuid":"00000000-0000-0000-0000-000000000000"},{"cellId":1,"summonerId":{{cell1}},"puuid":""}],
             "theirTeam":[{"cellId":5,"puuid":"{{enemyPuuid}}","summonerId":0}],
             "actions":[[{"id":0,"actorCellId":0,"type":"ban","championId":75,"completed":true,"isInProgress":false},
                         {"id":1,"actorCellId":1,"type":"ban","championId":777,"completed":true,"isInProgress":false},
                         {"id":2,"actorCellId":5,"type":"ban","championId":238,"completed":true,"isInProgress":false}]]}
            """)!;
        var owners = new BanOwners();
        var before = Session(0, 100, 200, "");  // I am in cell 0 and ban Nasus (75), cell 1 bans Yone (777); the enemy's id is hidden
        Assert.Equal([new BoardBan(0, 75, true), new BoardBan(1, 777, true), new BoardBan(5, 238, false)],
            owners.Follow(ChampSelectSession.FromJson(before)!.Board!, before).BanList);
        var after = Session(1, 200, 100, ""); // swapped: I sit in cell 1 now
        var board = owners.Follow(ChampSelectSession.FromJson(after)!.Board!, after);
        Assert.Equal([new BoardBan(1, 75, true), new BoardBan(0, 777, true), new BoardBan(5, 238, false)], board.BanList);
        Assert.Equal(75, board.BanList!.Single(b => b.CellId == board.MyCellId).ChampionId);
    }

    [Fact]
    public void Ban_boxes_leave_the_cards_once_every_ban_is_made()
    {
        var b = ChampSelectSession.FromJson(JsonNode.Parse("""
            {"localPlayerCellId":1,"timer":{"phase":"BAN_PICK","adjustedTimeLeftInPhase":20000,"internalNowInEpochMs":1},
             "myTeam":[{"cellId":0},{"cellId":1}],"theirTeam":[{"cellId":5},{"cellId":6}],
             "actions":[[{"id":0,"actorCellId":0,"type":"ban","championId":10,"completed":true,"isInProgress":false},
                         {"id":1,"actorCellId":5,"type":"ban","championId":20,"completed":true,"isInProgress":false}],
                        [{"id":7,"actorCellId":5,"type":"pick","championId":30,"completed":false,"isInProgress":true}]]}
            """))!.Board!;
        Assert.Empty(b.BanCells!);
    }

    [Fact]
    public void SameAs_ignores_the_countdown_only()
    {
        var a = Board("", Player(5, 2), Player(0, 1));
        Assert.True(a.SameAs(a with { TimeLeftMs = 1 }));
        Assert.False(a.SameAs(a with { Stage = "FINALIZATION" }));
        Assert.False(a.SameAs(a with { TheirBans = [1] }));
    }

    [Fact]
    public async Task Tracker_publishes_board_on_change_not_on_timer_ticks_and_clears_at_end()
    {
        var hub = new EventHub();
        var t = new EventTracker(hub);
        using var sub = hub.Subscribe();
        ChampSelectSession Cs(int timeLeft, int champ) => ChampSelectSession.FromJson(JsonNode.Parse($$"""
            {"localPlayerCellId":5,"timer":{"phase":"BAN_PICK","adjustedTimeLeftInPhase":{{timeLeft}},"internalNowInEpochMs":1},
             "myTeam":[{{Player(5, 2, intent: champ)}}],"theirTeam":[],"actions":[[]]}
            """))!;
        t.OnPhase("ChampSelect");
        t.OnChampSelect(Cs(9000, 0));
        t.OnChampSelect(Cs(8000, 0)); // only the timer moved
        t.OnChampSelect(Cs(8000, 145)); // I hovered
        Assert.NotNull(Get(t.Snapshot(), "champSelect", "board"));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        var count = 0;
        try { await foreach (var e in sub.ReadAllAsync(cts.Token)) if (e.Type == "board") count++; } catch (OperationCanceledException) { }
        Assert.Equal(2, count);
        t.OnPhase("None");
        Assert.Null(Get(t.Snapshot(), "champSelect", "board"));
    }

    private static object? Get(object o, params string[] path)
    {
        foreach (var p in path) o = o.GetType().GetProperty(p)!.GetValue(o)!;
        return o;
    }
}
