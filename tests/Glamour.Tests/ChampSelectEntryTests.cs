using System.Text.Json;
using System.Text.Json.Nodes;
using Glamour.Core;
using Glamour.Lcu;
using Microsoft.Extensions.Logging.Abstractions;

namespace Glamour.Tests;

/// <summary>
/// Champ select from its first second: the session's Create comes just before the phase changes (LIVE event order), and in PLANNING every ban is already
/// marked in progress (shape from tools/logs/livetest-1790967581988.jsonl, trimmed).
/// </summary>
public class ChampSelectEntryTests
{
    private const string SessionPath = "/lol-champ-select/v1/session";
    private readonly FakeLcu _lcu = new();
    private readonly Engine _engine;

    public ChampSelectEntryTests() => _engine = new Engine(_lcu, [], new EventHub(), NullLogger<Engine>.Instance);

    private static JsonNode Planning(int hover = 0) => JsonNode.Parse($$$"""
        {"localPlayerCellId":0,"queueId":400,"timer":{"phase":"PLANNING","adjustedTimeLeftInPhase":24000,"internalNowInEpochMs":1,"totalTimeInPhase":25000},
         "actions":[[{"actorCellId":0,"championId":0,"completed":false,"id":0,"isAllyAction":true,"isInProgress":true,"type":"ban"},
                     {"actorCellId":5,"championId":0,"completed":false,"id":5,"isAllyAction":false,"isInProgress":true,"type":"ban"}],
                    [{"actorCellId":0,"championId":0,"completed":false,"id":10,"isAllyAction":true,"isInProgress":false,"type":"pick"}]],
         "myTeam":[{"cellId":0,"championId":0,"championPickIntent":{{{hover}}},"assignedPosition":"top","summonerId":7,"team":1}],
         "theirTeam":[{"cellId":5,"championId":0,"team":2}],"bans":{"myTeamBans":[],"theirTeamBans":[]}}
        """)!;

    private JsonElement ChampSelect() => JsonSerializer.SerializeToElement(_engine.Tracker.Snapshot(), new JsonSerializerOptions(JsonSerializerDefaults.Web)).GetProperty("champSelect");

    private Task Phase(string phase) => _engine.HandleAsync(new LcuEvent("/lol-gameflow/v1/gameflow-phase", "Update", JsonValue.Create(phase)), default);

    [Fact]
    public async Task A_session_that_came_before_the_phase_is_read_on_entry()
    {
        _lcu.Gets[SessionPath] = Planning();
        await _engine.HandleAsync(new LcuEvent(SessionPath, "Create", Planning()), default); // still Matchmaking: ignored
        await Phase("ChampSelect");
        var cs = ChampSelect();
        Assert.Equal("PLANNING", cs.GetProperty("stage").GetString());
        Assert.Equal(JsonValueKind.Object, cs.GetProperty("board").ValueKind);
    }

    [Fact]
    public async Task Planning_is_nobodys_turn_even_with_the_ban_in_progress()
    {
        _lcu.Gets[SessionPath] = Planning();
        await Phase("ChampSelect");
        Assert.Equal(JsonValueKind.Null, ChampSelect().GetProperty("turn").ValueKind);
    }

    [Fact]
    public async Task A_planning_update_reaches_the_board()
    {
        await Phase("ChampSelect"); // no session yet (404)
        await _engine.HandleAsync(new LcuEvent(SessionPath, "Update", Planning(hover: 777)), default);
        var cs = ChampSelect();
        Assert.Equal("PLANNING", cs.GetProperty("stage").GetString());
        Assert.Equal(777, cs.GetProperty("board").GetProperty("myTeam")[0].GetProperty("championId").GetInt32());
    }
}
