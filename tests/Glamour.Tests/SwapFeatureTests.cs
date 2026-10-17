using System.Text.Json;
using System.Text.Json.Nodes;
using Glamour.Core;
using Glamour.Lcu;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Glamour.Tests;

/// <summary>The swap feature: lane wishes from the lobby, the delay with a fresh read, one send at a time, the panel note, Stop.</summary>
public class SwapFeatureTests
{
    private const string SessionPath = "/lol-champ-select/v1/session";
    private const string AskTop = "/lol-champ-select/v1/session/position-swaps/10/request";
    private readonly FakeLcu _lcu = new();
    private readonly GlamourSettings _settings = new() { LaneSwapAsk = true };
    private readonly FakeTimeProvider _time = new(DateTimeOffset.FromUnixTimeMilliseconds(1_000_000));
    private readonly AutomationGate _gate;
    private readonly SwapFeature _swaps;

    public SwapFeatureTests()
    {
        _gate = new AutomationGate(_settings, new EventHub(), _time);
        _swaps = new SwapFeature(_lcu, new ActionGateway(_lcu, _settings, NullLogger<ActionGateway>.Instance), _settings, _time, NullLogger<SwapFeature>.Instance, _gate);
    }

    /// <summary>Me Support (cell 2), Top in cell 0 with an AVAILABLE lane swap (id 10).</summary>
    private static JsonNode Session(string topSwapState = "AVAILABLE", bool topLocked = false) => JsonNode.Parse($$"""
        {"localPlayerCellId":2,"timer":{"phase":"BAN_PICK","adjustedTimeLeftInPhase":30000,"internalNowInEpochMs":1},"queueId":420,
         "myTeam":[{"cellId":0,"assignedPosition":"top","championId":{{(topLocked ? 86 : 0)}}},{"cellId":2,"assignedPosition":"utility"}],
         "theirTeam":[],
         "actions":[[{"id":1,"actorCellId":0,"type":"pick","championId":{{(topLocked ? 86 : 0)}},"completed":{{(topLocked ? "true" : "false")}},"isInProgress":false}],
                    [{"id":2,"actorCellId":2,"type":"pick","championId":0,"completed":false,"isInProgress":false}]],
         "positionSwaps":[{"id":10,"cellId":0,"state":"{{topSwapState}}"}],"pickOrderSwaps":[],"trades":[]}
        """)!;

    private async Task LobbyAsync(string first, string second) =>
        await _swaps.OnEventAsync(new LcuEvent("/lol-lobby/v2/lobby", "Update", new JsonObject { ["localMember"] = new JsonObject { ["firstPositionPreference"] = first, ["secondPositionPreference"] = second } }), default);

    private async Task SeeAsync(JsonNode session)
    {
        _lcu.Gets[SessionPath] = session;
        await _swaps.OnChampSelectAsync(ChampSelectSession.FromJson(session)!, default);
    }

    private int Sent(string path) => _lcu.Writes.Count(w => w.Path == path);

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition());
    }

    private static Task Settle() => Task.Delay(60);

    [Fact]
    public async Task Asks_for_my_first_lane_after_the_delay_and_notes_it()
    {
        await LobbyAsync("TOP", "MIDDLE");
        await SeeAsync(Session());
        await Settle();
        Assert.Equal(0, Sent(AskTop));
        _time.Advance(TimeSpan.FromSeconds(2.1));
        await WaitFor(() => Sent(AskTop) == 1);
        await WaitFor(() => JsonSerializer.SerializeToElement(_gate.Snapshot()).GetProperty("swapNotes").EnumerateArray().Select(n => n.GetString()).Contains("Asked Top for their lane"));

        await SeeAsync(Session("DECLINED"));
        _time.Advance(TimeSpan.FromSeconds(3));
        await Settle();
        await SeeAsync(Session());
        _time.Advance(TimeSpan.FromSeconds(3));
        await Settle();
        Assert.Equal(1, Sent(AskTop)); // asked once per champ select
    }

    [Fact]
    public async Task A_change_while_waiting_cancels_the_send()
    {
        await LobbyAsync("TOP", "MIDDLE");
        await SeeAsync(Session());
        _lcu.Gets[SessionPath] = Session(topLocked: true); // Top locked meanwhile
        _time.Advance(TimeSpan.FromSeconds(2.1));
        await Settle();
        Assert.Equal(0, Sent(AskTop));
    }

    [Fact]
    public async Task Stop_for_this_champ_select_stops_swaps()
    {
        await LobbyAsync("TOP", "MIDDLE");
        await _gate.OnPhaseAsync("ChampSelect", default);
        Assert.True(_gate.SetDraftStopped(true));
        await SeeAsync(Session());
        _time.Advance(TimeSpan.FromSeconds(2.1));
        await Settle();
        Assert.Empty(_lcu.Writes);
    }

    [Fact]
    public async Task Without_a_lane_wish_nothing_is_asked()
    {
        await LobbyAsync("FILL", "UNSELECTED");
        await SeeAsync(Session());
        _time.Advance(TimeSpan.FromSeconds(2.1));
        await Settle();
        Assert.Empty(_lcu.Writes);
    }

    [Fact]
    public async Task A_new_champ_select_may_ask_again()
    {
        await LobbyAsync("TOP", "MIDDLE");
        await SeeAsync(Session());
        _time.Advance(TimeSpan.FromSeconds(2.1));
        await WaitFor(() => Sent(AskTop) == 1);
        await _swaps.OnPhaseAsync("Lobby", default);
        await SeeAsync(Session());
        _time.Advance(TimeSpan.FromSeconds(2.1));
        await WaitFor(() => Sent(AskTop) == 2);
    }
}
