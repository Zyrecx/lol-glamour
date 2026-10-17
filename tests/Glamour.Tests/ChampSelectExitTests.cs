using System.Text.Json.Nodes;
using Glamour.Core;
using Microsoft.Extensions.Logging.Abstractions;

namespace Glamour.Tests;

/// <summary>The panel's Dodge: the client's dodge; in a custom one leaving the lobby before my lock and the champ select's quit after it; refused once the game is starting.</summary>
public class ChampSelectExitTests
{
    private readonly FakeLcu _lcu = new();
    private readonly ActionGateway _gateway;

    public ChampSelectExitTests() => _gateway = new ActionGateway(_lcu, new GlamourSettings(), NullLogger<ActionGateway>.Instance);

    private void Client(bool custom, string stage = "BAN_PICK", bool locked = false, bool leftoverSearch = false)
    {
        _lcu.Gets["/lol-champ-select/v1/session"] = JsonNode.Parse($$"""
            {"localPlayerCellId":2,"timer":{"phase":"{{stage}}","adjustedTimeLeftInPhase":20000,"internalNowInEpochMs":1},
             "actions":[[{"id":10,"actorCellId":2,"type":"pick","championId":{{(locked ? 518 : 0)}},"completed":{{(locked ? "true" : "false")}},"isInProgress":{{(locked ? "false" : "true")}}}]]}
            """);
        _lcu.Gets["/lol-lobby/v2/lobby"] = new JsonObject { ["gameConfig"] = new JsonObject { ["isCustom"] = custom } };
        if (leftoverSearch) _lcu.Gets["/lol-matchmaking/v1/search"] = JsonNode.Parse("""{"searchState":"Found"}""");
    }

    private string[] Writes => _lcu.Writes.Select(w => $"{w.Method} {w.Path}").ToArray();

    [Theory]
    [InlineData("BAN_PICK", false)]
    [InlineData("FINALIZATION", true)]
    public async Task Matchmade_is_the_clients_dodge_even_after_locking(string stage, bool locked)
    {
        Client(custom: false, stage, locked);
        var o = await ChampSelectExit.LeaveAsync(_lcu, _gateway, "ChampSelect", default);
        Assert.Null(o.Error);
        Assert.Equal(["POST /lol-gameflow/v1/session/dodge"], Writes);
    }

    [Fact]
    public async Task Custom_before_locking_leaves_the_lobby_and_clears_the_leftover_search()
    {
        Client(custom: true, leftoverSearch: true);
        var o = await ChampSelectExit.LeaveAsync(_lcu, _gateway, "ChampSelect", default);
        Assert.Null(o.Error);
        Assert.Equal(["DELETE /lol-lobby/v2/lobby", "DELETE /lol-matchmaking/v1/search"], Writes);
    }

    [Theory]
    [InlineData("BAN_PICK", true)]       // my pick is locked: leaving the lobby would still launch the game
    [InlineData("FINALIZATION", false)]
    public async Task Custom_after_locking_is_the_champ_selects_quit(string stage, bool locked)
    {
        Client(custom: true, stage, locked);
        var o = await ChampSelectExit.LeaveAsync(_lcu, _gateway, "ChampSelect", default);
        Assert.Null(o.Error);
        Assert.Equal(["POST /lol-lobby-team-builder/champ-select/v1/session/quit"], Writes);
    }

    [Fact]
    public async Task Custom_after_locking_falls_back_to_the_dodge_then_says_to_use_the_clients_quit()
    {
        Client(custom: true, "FINALIZATION", locked: true);
        _lcu.WriteStatus["/lol-lobby-team-builder/champ-select/v1/session/quit"] = 404;
        Assert.Null((await ChampSelectExit.LeaveAsync(_lcu, _gateway, "ChampSelect", default)).Error);
        Assert.Equal(["POST /lol-lobby-team-builder/champ-select/v1/session/quit", "POST /lol-gameflow/v1/session/dodge"], Writes);

        _lcu.WriteStatus["/lol-gameflow/v1/session/dodge"] = 400; // as live in a custom after the lock
        var o = await ChampSelectExit.LeaveAsync(_lcu, _gateway, "ChampSelect", default);
        Assert.Equal(400, o.Status);
        Assert.Equal("Couldn't leave. Use Quit in the client.", o.Error);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Refused_once_the_game_is_starting(bool custom)
    {
        Client(custom, "GAME_STARTING", locked: true);
        var o = await ChampSelectExit.LeaveAsync(_lcu, _gateway, "ChampSelect", default);
        Assert.Equal(409, o.Status);
        Assert.Empty(Writes);
    }

    [Fact]
    public async Task Refused_outside_champ_select()
    {
        Client(custom: false);
        Assert.Equal(409, (await ChampSelectExit.LeaveAsync(_lcu, _gateway, "Lobby", default)).Status);
        Assert.Empty(Writes);
    }
}
