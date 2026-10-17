using System.Text.Json.Nodes;
using Glamour.Core;

namespace Glamour.Tests;

/// <summary>Shapes from the real Mayhem dump (tools/logs/cs-dump-1790943347862.json): benchEnabled, benchChampions, rerollsRemaining, trades.</summary>
public class GameModeTests
{
    private static ChampSelectBoard Board(int queueId, string extra, string actions = "") =>
        ChampSelectSession.FromJson(JsonNode.Parse($$"""
            {"localPlayerCellId":0,"queueId":{{queueId}},"timer":{"phase":"BAN_PICK","adjustedTimeLeftInPhase":12000,"internalNowInEpochMs":1},
             "myTeam":[{"cellId":0,"championId":0,"team":1}],"theirTeam":[],"bans":{"myTeamBans":[],"theirTeamBans":[]},"actions":[[{{actions}}]]{{extra}}}
            """))!.Board!;

    private const string Pick = """{"id":0,"actorCellId":0,"type":"pick","championId":0,"completed":false,"isInProgress":true}""";

    [Theory]
    [InlineData(2400, true, "", "mayhem")]
    [InlineData(450, true, "", "aram")]
    [InlineData(400, false, Pick, "draft")]
    [InlineData(0, false, "", "other")]
    public void Mode_comes_from_queue_bench_and_actions(int queue, bool bench, string actions, string expected) =>
        Assert.Equal(expected, Board(queue, $",\"benchEnabled\":{bench.ToString().ToLowerInvariant()}", actions).Mode);

    [Fact]
    public void Bench_and_rerolls_are_on_the_board_and_a_change_republishes()
    {
        var a = Board(2400, ""","benchEnabled":true,"allowRerolling":true,"rerollsRemaining":1,"benchChampions":[{"championId":7},{"championId":0}]""");
        Assert.Equal([7], a.Bench);
        Assert.True(a is { AllowRerolling: true, Rerolls: 1 });
        var b = Board(2400, ""","benchEnabled":true,"allowRerolling":true,"rerollsRemaining":1,"benchChampions":[{"championId":7},{"championId":9}]""");
        Assert.False(b.SameAs(a));
        Assert.True(Board(2400, ""","benchEnabled":true,"allowRerolling":true,"rerollsRemaining":1,"benchChampions":[{"championId":7},{"championId":0}]""").SameAs(a));
    }

    [Fact]
    public void Only_live_swap_entries_reach_the_board_across_all_three_kinds()
    {
        var b = Board(2400, ""","trades":[{"cellId":1,"id":12,"state":"INVALID"},{"cellId":2,"id":13,"state":"AVAILABLE"}],"positionSwaps":[{"cellId":3,"id":4,"state":"RECEIVED"}],"pickOrderSwaps":[{"cellId":3,"id":4,"state":"SENT"}]""");
        Assert.Equal([new ChampSelectSwap("pick-order", 4, 3, "SENT"), new ChampSelectSwap("position", 4, 3, "RECEIVED"), new ChampSelectSwap("trade", 13, 2, "AVAILABLE")], b.Swaps!.OrderBy(s => s.Kind).ToList());
    }
}

public class DealtModeAutomationTests
{
    [Theory]
    [InlineData(2400)]
    [InlineData(450)]
    public async Task Auto_lock_ban_and_pre_hover_do_nothing_in_a_dealt_champion_mode(int queueId)
    {
        // The real Mayhem dump: every pick action is live in BAN_PICK, and ~170 champions are "pickable".
        var lcu = new FakeLcu();
        lcu.Gets["/lol-champ-select/v1/pickable-champion-ids"] = JsonNode.Parse("[1,2,518]");
        var settings = new GlamourSettings { AutoLockChampionId = 518, AutoBanChampionId = 238, PreHoverChampionId = 518 };
        var feature = new AutoBanLockFeature(lcu, new Glamour.Core.ActionGateway(lcu, settings, Microsoft.Extensions.Logging.Abstractions.NullLogger<Glamour.Core.ActionGateway>.Instance),
            settings, TimeProvider.System, Microsoft.Extensions.Logging.Abstractions.NullLogger<AutoBanLockFeature>.Instance);
        var session = ChampSelectSession.FromJson(JsonNode.Parse($$"""
            {"localPlayerCellId":0,"queueId":{{queueId}},"benchEnabled":true,"timer":{"phase":"BAN_PICK","adjustedTimeLeftInPhase":12000,"internalNowInEpochMs":1},
             "myTeam":[{"cellId":0,"championId":0,"team":1}],"theirTeam":[],"bans":{"myTeamBans":[],"theirTeamBans":[]},
             "actions":[[{"id":0,"actorCellId":0,"type":"pick","championId":0,"completed":false,"isInProgress":true}]]}
            """))!;
        await feature.OnChampSelectAsync(session, default);
        Assert.Empty(lcu.Writes);
    }
}
