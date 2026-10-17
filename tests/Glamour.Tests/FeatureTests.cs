using System.Text.Json.Nodes;
using Glamour.Core;
using Glamour.Lcu;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Glamour.Tests;

/// <summary>Fake client: canned GET answers, records every write.</summary>
public sealed class FakeLcu : ILcuClient
{
    public Dictionary<string, JsonNode?> Gets { get; } = [];
    public List<(HttpMethod Method, string Path, string? Body)> Writes { get; } = [];
    public List<string> Reads { get; } = [];
    /// <summary>Status a write to this path answers with (204 when not listed).</summary>
    public Dictionary<string, int> WriteStatus { get; } = [];

    public Task<LcuResponse> SendAsync(HttpMethod method, string path, object? body = null, CancellationToken ct = default)
    {
        if (method == HttpMethod.Get) Reads.Add(path);
        if (method == HttpMethod.Get) return Task.FromResult(new LcuResponse(Gets.TryGetValue(path, out var v) ? 200 : 404, v));
        Writes.Add((method, path, body is null ? null : System.Text.Json.JsonSerializer.Serialize(body)));
        return Task.FromResult(new LcuResponse(WriteStatus.GetValueOrDefault(path, 204), null));
    }
}

public class FeatureTests
{
    private readonly FakeLcu _lcu = new();
    private readonly GlamourSettings _settings = new();
    private readonly FakeTimeProvider _time = new(DateTimeOffset.FromUnixTimeMilliseconds(1_000_000));
    private readonly AutoBanLockFeature _banLock;
    private readonly AutoAcceptFeature _accept;

    public FeatureTests()
    {
        var gateway = new ActionGateway(_lcu, _settings, NullLogger<ActionGateway>.Instance);
        _banLock = new AutoBanLockFeature(_lcu, gateway, _settings, _time, NullLogger<AutoBanLockFeature>.Instance);
        _accept = new AutoAcceptFeature(gateway, _settings);
        _lcu.Gets["/lol-champ-select/v1/bannable-champion-ids"] = JsonNode.Parse("[1,2,238]");
        _lcu.Gets["/lol-champ-select/v1/pickable-champion-ids"] = JsonNode.Parse("[1,2,518]");
        _settings.ActionDelaySeconds = 0; // these tests pin the act-at-once path; the delay has its own tests (DelayAndGateTests)
    }

    /// <summary>What the client answers after Glamour hovered: action 7 holds <paramref name="championId"/>.</summary>
    private void LiveSession(string type, int championId) =>
        _lcu.Gets["/lol-champ-select/v1/session"] = JsonNode.Parse($$"""
            {"localPlayerCellId":0,"timer":{"phase":"BAN_PICK","adjustedTimeLeftInPhase":1000,"internalNowInEpochMs":1},
             "actions":[[{"id":7,"actorCellId":0,"type":"{{type}}","championId":{{championId}},"completed":false,"isInProgress":true}]]}
            """);

    private ChampSelectSession Session(string type, string phase = "BAN_PICK", int championId = 0, long timeLeftMs = 30_000) =>
        new(0, new ChampSelectTimer(phase, timeLeftMs, _time.GetUtcNow().ToUnixTimeMilliseconds()),
            [new ChampSelectAction(7, 0, type, championId, false, true)]);

    [Theory]
    [InlineData(AutoAcceptMode.Always, 1)]
    [InlineData(AutoAcceptMode.Phone, 0)]
    [InlineData(AutoAcceptMode.Off, 0)]
    public async Task AutoAccept_OnlyAcceptsInAlwaysMode(AutoAcceptMode mode, int expected)
    {
        _settings.AutoAccept = mode;
        await _accept.OnPhaseAsync("ReadyCheck", default);
        Assert.Equal(expected, _lcu.Writes.Count(w => w.Path.EndsWith("ready-check/accept")));
    }

    [Fact]
    public async Task AutoAccept_SkipsTheMomentaryReadyCheckOfACustomGame()
    {
        _settings.AutoAccept = AutoAcceptMode.Always;
        await _accept.OnEventAsync(new LcuEvent("/lol-lobby/v2/lobby", "Update", JsonNode.Parse("""{"gameConfig":{"isCustom":true}}""")), default);
        await _accept.OnPhaseAsync("ReadyCheck", default);
        Assert.DoesNotContain(_lcu.Writes, w => w.Path.EndsWith("ready-check/accept"));

        await _accept.OnEventAsync(new LcuEvent("/lol-lobby/v2/lobby", "Update", JsonNode.Parse("""{"gameConfig":{"isCustom":false}}""")), default);
        await _accept.OnPhaseAsync("Lobby", default);
        await _accept.OnPhaseAsync("ReadyCheck", default);
        Assert.Single(_lcu.Writes, w => w.Path.EndsWith("ready-check/accept"));
    }

    [Fact]
    public async Task Ban_Instant_HoversThenCompletes()
    {
        _settings.AutoBanChampionId = 238;
        await _banLock.OnChampSelectAsync(Session("ban"), default);
        Assert.Equal(2, _lcu.Writes.Count);
        Assert.Contains("\"championId\":238", _lcu.Writes[0].Body);
        Assert.Contains("\"completed\":true", _lcu.Writes[1].Body);
        Assert.Contains("\"type\":\"ban\"", _lcu.Writes[1].Body);
    }

    private ChampSelectSession WithAllyHover(int championId, long timeLeftMs = 30_000) =>
        new(0, new ChampSelectTimer("BAN_PICK", timeLeftMs, _time.GetUtcNow().ToUnixTimeMilliseconds()),
            [new ChampSelectAction(7, 0, "ban", 0, false, true), new ChampSelectAction(8, 1, "pick", championId, false, false)],
            new HashSet<int> { 0, 1 });

    private ChampSelectSession InLane(string type, string? assigned) =>
        Session(type) with { MyPosition = assigned };

    [Theory]
    [InlineData("utility", 267)]   // a lane with its own setup
    [InlineData("top", 238)]       // a lane without one uses the default
    [InlineData(null, 238)]        // a mode without lanes uses the default
    public async Task Ban_UsesTheAssignedLanesSetup(string? assigned, int expected)
    {
        _settings.AutoBanChampionId = 238;
        _settings.Lanes["SUPPORT"] = new LaneSetup { BanChampionId = 267 };
        await _banLock.OnChampSelectAsync(InLane("ban", assigned), default);
        Assert.Contains($"\"championId\":{expected}", _lcu.Writes[0].Body);
    }

    [Fact]
    public async Task Lock_LaneSetupHasItsOwnTiming_AndNoChampionMeansNoAction()
    {
        _settings.AutoLockChampionId = 518;                                  // default: lock Neeko instantly
        _settings.Lanes["JUNGLE"] = new LaneSetup { LockChampionId = 1, LockTiming = ActionTiming.LastSecond };
        _settings.Lanes["TOP"] = new LaneSetup();                            // own setup with nothing chosen
        await _banLock.OnChampSelectAsync(InLane("pick", "top"), default);
        Assert.Empty(_lcu.Writes);
        await _banLock.OnChampSelectAsync(InLane("pick", "jungle"), default);
        Assert.Single(_lcu.Writes);                                          // hover now, lock later
        Assert.DoesNotContain("completed", _lcu.Writes[0].Body);
    }

    [Theory]
    [InlineData("top", "TOP")]
    [InlineData("utility", "SUPPORT")]
    [InlineData("MIDDLE", "MIDDLE")]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void AssignedPosition_MapsToALane(string? assigned, string? lane) => Assert.Equal(lane, LaneNames.FromAssigned(assigned));

    [Fact]
    public void Session_ReadsMyAssignedPosition()
    {
        var json = JsonNode.Parse("""
            {"localPlayerCellId":2,"timer":{"phase":"BAN_PICK","adjustedTimeLeftInPhase":1000,"internalNowInEpochMs":1},"actions":[[]],
             "myTeam":[{"cellId":1,"assignedPosition":"top"},{"cellId":2,"assignedPosition":"utility","summonerId":5}]}
            """);
        Assert.Equal("SUPPORT", ChampSelectSession.FromJson(json)!.Lane);
    }

    [Theory]
    [InlineData(true, 0)]
    [InlineData(false, 2)]
    public async Task Ban_SkippedWhenAllyHoveredAndOptionOn(bool skip, int writes)
    {
        _settings.AutoBanChampionId = 238;
        _settings.SkipBanIfAllyHovered = skip;
        await _banLock.OnChampSelectAsync(WithAllyHover(238), default);
        Assert.Equal(writes, _lcu.Writes.Count);
    }

    [Fact]
    public async Task Ban_LastSecond_SkippedWhenAllyHoversWhileWaiting()
    {
        _settings.AutoBanChampionId = 238;
        _settings.BanTiming = ActionTiming.LastSecond;
        await _banLock.OnChampSelectAsync(Session("ban"), default);
        Assert.Single(_lcu.Writes); // hover only
        _lcu.Gets["/lol-champ-select/v1/session"] = JsonNode.Parse("""
            {"localPlayerCellId":0,"timer":{"phase":"BAN_PICK","adjustedTimeLeftInPhase":3000,"internalNowInEpochMs":1},
             "myTeam":[{"cellId":0},{"cellId":1}],
             "actions":[[{"id":7,"actorCellId":0,"type":"ban","championId":238,"completed":false,"isInProgress":true},
                         {"id":8,"actorCellId":1,"type":"pick","championId":238,"completed":false,"isInProgress":false}]]}
            """); // my ban still holds Glamour's hover, so only the ally's hover stops it
        _time.Advance(TimeSpan.FromSeconds(27.5));
        await Task.Delay(100);
        Assert.Single(_lcu.Writes);
    }

    [Fact]
    public async Task Ban_IgnoredDuringPlanning()
    {
        _settings.AutoBanChampionId = 238;
        await _banLock.OnChampSelectAsync(Session("ban", phase: "PLANNING"), default);
        Assert.Empty(_lcu.Writes);
    }

    [Fact]
    public async Task Ban_NotRepeatedOnLaterSessionUpdates()
    {
        _settings.AutoBanChampionId = 238;
        await _banLock.OnChampSelectAsync(Session("ban"), default);
        await _banLock.OnChampSelectAsync(Session("ban"), default);
        Assert.Equal(2, _lcu.Writes.Count);
    }

    [Fact]
    public async Task Lock_RespectsUsersOwnHover()
    {
        _settings.AutoLockChampionId = 518;
        await _banLock.OnChampSelectAsync(Session("pick", championId: 2), default);
        Assert.Empty(_lcu.Writes);
    }

    private ChampSelectSession Pending(string phase, int championId = 0, bool live = false, string type = "pick") =>
        new(0, new ChampSelectTimer(phase, 30_000, _time.GetUtcNow().ToUnixTimeMilliseconds()), [new ChampSelectAction(7, 0, type, championId, false, live)]);

    [Fact]
    public async Task PreHover_HoversOncePerPickAction_AndNeverLocks()
    {
        _settings.PreHoverChampionId = 518;
        await _banLock.OnChampSelectAsync(Pending("PLANNING"), default);
        await _banLock.OnChampSelectAsync(Pending("PLANNING"), default);
        Assert.Equal([(HttpMethod.Patch, "/lol-champ-select/v1/session/actions/7")], _lcu.Writes.Select(w => (w.Method, w.Path)));
        Assert.DoesNotContain("completed", System.Text.Json.JsonSerializer.Serialize(_lcu.Writes[0].Body));
    }

    [Fact]
    public async Task PreHover_DroppedInPlanning_IsTriedOnceMoreInBanPick_OnlyWhileNothingIsHovered()
    {
        _settings.PreHoverChampionId = 518;
        await _banLock.OnChampSelectAsync(Pending("PLANNING"), default);
        await _banLock.OnChampSelectAsync(Pending("BAN_PICK"), default); // the client dropped it: still empty
        await _banLock.OnChampSelectAsync(Pending("BAN_PICK"), default); // once only
        Assert.Equal(2, _lcu.Writes.Count);

        await _banLock.OnPhaseAsync("None", default);
        _lcu.Writes.Clear();
        await _banLock.OnChampSelectAsync(Pending("PLANNING"), default);
        await _banLock.OnChampSelectAsync(Pending("BAN_PICK", championId: 2), default); // the user hovered meanwhile
        Assert.Single(_lcu.Writes);
    }

    [Fact]
    public async Task PreHover_LeavesTheUsersOwnHoverAndAnUnpickableChampion()
    {
        _settings.PreHoverChampionId = 518;
        await _banLock.OnChampSelectAsync(Pending("PLANNING", championId: 2), default);
        _settings.PreHoverChampionId = 99; // not in the pickable list
        await _banLock.OnChampSelectAsync(Pending("PLANNING"), default);
        await _banLock.OnChampSelectAsync(Pending("PLANNING", type: "ban"), default);
        Assert.Empty(_lcu.Writes);
    }

    [Fact]
    public async Task PreHover_UsesTheLanesOwnChampion()
    {
        _settings.PreHoverChampionId = 1;
        _settings.Lanes["SUPPORT"] = new LaneSetup { HoverChampionId = 518 };
        var s = Pending("PLANNING");
        await _banLock.OnChampSelectAsync(s with { MyPosition = "utility" }, default);
        Assert.Contains("518", System.Text.Json.JsonSerializer.Serialize(_lcu.Writes.Single().Body));
    }

    [Fact]
    public async Task PreHover_OwnHoverDoesNotBlockTheAutoLock_ButAUsersChangeDoes()
    {
        _settings.PreHoverChampionId = 518;
        _settings.AutoLockChampionId = 518;
        await _banLock.OnChampSelectAsync(Pending("PLANNING"), default);
        _lcu.Writes.Clear();
        await _banLock.OnChampSelectAsync(Pending("BAN_PICK", championId: 518, live: true), default); // our own hover, then my turn
        Assert.Contains(_lcu.Writes, w => w.Body is not null && System.Text.Json.JsonSerializer.Serialize(w.Body).Contains("completed"));

        _lcu.Writes.Clear();
        await _banLock.OnPhaseAsync("None", default);
        await _banLock.OnChampSelectAsync(Pending("PLANNING"), default);
        _lcu.Writes.Clear();
        await _banLock.OnChampSelectAsync(Pending("BAN_PICK", championId: 2, live: true), default); // the user switched to 2
        Assert.Empty(_lcu.Writes);
    }

    [Fact]
    public async Task Lock_DisabledWhenNoChampionConfigured()
    {
        await _banLock.OnChampSelectAsync(Session("pick"), default);
        Assert.Empty(_lcu.Writes);
    }

    [Fact]
    public async Task Ban_IgnoresBannableList_WhichIsUnreliableLive()
    {
        _lcu.Gets["/lol-champ-select/v1/bannable-champion-ids"] = JsonNode.Parse("[-1]"); // what the live client returns on my turn
        _settings.AutoBanChampionId = 238;
        await _banLock.OnChampSelectAsync(Session("ban"), default);
        Assert.Equal(2, _lcu.Writes.Count); // hover + lock
    }

    [Fact]
    public async Task Lock_LastSecond_HoversNowCompletesWithMarginLeft()
    {
        _settings.AutoLockChampionId = 518;
        _settings.LockTiming = ActionTiming.LastSecond;
        _settings.LastSecondMarginSeconds = 3;
        LiveSession("pick", 518);
        await _banLock.OnChampSelectAsync(Session("pick", timeLeftMs: 30_000), default);
        Assert.Single(_lcu.Writes); // hover only

        _time.Advance(TimeSpan.FromSeconds(26.9));
        await Task.Yield();
        Assert.Single(_lcu.Writes); // 3.1 s left, not yet

        _time.Advance(TimeSpan.FromSeconds(0.2));
        await WaitFor(() => _lcu.Writes.Count == 2);
        Assert.Contains("\"completed\":true", _lcu.Writes[1].Body);
    }

    [Fact]
    public async Task Lock_LastSecond_CancelledWhenChampSelectEnds()
    {
        _settings.AutoLockChampionId = 518;
        _settings.LockTiming = ActionTiming.LastSecond;
        await _banLock.OnChampSelectAsync(Session("pick"), default);
        await _banLock.OnPhaseAsync("None", default);
        _time.Advance(TimeSpan.FromMinutes(1));
        await Task.Delay(50);
        Assert.Single(_lcu.Writes);
    }

    [Fact]
    public async Task Lock_LastSecond_ClampedWhenLittleTimeLeft()
    {
        _settings.AutoLockChampionId = 518;
        _settings.LockTiming = ActionTiming.LastSecond;
        LiveSession("pick", 518);
        await _banLock.OnChampSelectAsync(Session("pick", timeLeftMs: 1_000), default);
        await WaitFor(() => _lcu.Writes.Count == 2);
    }

    [Fact]
    public async Task DryRun_SendsNothing()
    {
        _settings.DryRun = true;
        _settings.AutoBanChampionId = 238;
        await _banLock.OnChampSelectAsync(Session("ban"), default);
        Assert.Empty(_lcu.Writes);
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition());
    }
}

public class ParsingTests
{
    [Fact]
    public void Discovery_ParsesPortAndToken() =>
        Assert.Equal(new LcuCredentials(51234, "abc-DEF_1"), LcuDiscovery.Parse("\"LeagueClientUx.exe\" --app-port=51234 --remoting-auth-token=abc-DEF_1 --foo"));

    [Fact]
    public void Event_ParsesWampMessage()
    {
        var e = LcuEventStream.Parse("[8,\"OnJsonApiEvent\",{\"uri\":\"/lol-gameflow/v1/gameflow-phase\",\"eventType\":\"Update\",\"data\":\"ReadyCheck\"}]");
        Assert.Equal("/lol-gameflow/v1/gameflow-phase", e?.Uri);
        Assert.Equal("ReadyCheck", e?.Data?.GetValue<string>());
        Assert.Null(LcuEventStream.Parse(""));
    }

    [Fact]
    public void Session_ParsesActionsAndTimeLeft()
    {
        var json = JsonNode.Parse("""
            {"localPlayerCellId":2,"timer":{"phase":"BAN_PICK","adjustedTimeLeftInPhase":10000,"internalNowInEpochMs":5000},
             "actions":[[{"id":1,"actorCellId":0,"type":"ban","championId":0,"completed":true,"isInProgress":false}],
                        [{"id":5,"actorCellId":2,"type":"pick","championId":0,"completed":false,"isInProgress":true}]]}
            """);
        var s = ChampSelectSession.FromJson(json)!;
        Assert.Equal(5, s.MyActiveAction?.Id);
        Assert.Equal(TimeSpan.FromSeconds(7), s.TimeLeft(DateTimeOffset.FromUnixTimeMilliseconds(8000)));
    }
}

public class EventTrackerTests
{
    private readonly EventHub _hub = new();
    private readonly EventTracker _t;
    private readonly EventHub.Subscription _sub;

    public EventTrackerTests() { _t = new EventTracker(_hub); _sub = _hub.Subscribe(); }

    private async Task<List<string>> DrainAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        var types = new List<string>();
        try { await foreach (var e in _sub.ReadAllAsync(cts.Token)) if (e.Type != "board") types.Add(e.Type); } catch (OperationCanceledException) { } // board has its own tests (BoardTests)
        return types;
    }

    private static ChampSelectSession Cs(string phase = "BAN_PICK", string swapState = "AVAILABLE", bool live = false, int actionId = 7, string type = "ban") =>
        ChampSelectSession.FromJson(JsonNode.Parse($$"""
            {"localPlayerCellId":0,"timer":{"phase":"{{phase}}","adjustedTimeLeftInPhase":30000,"internalNowInEpochMs":1},
             "myTeam":[{"cellId":0,"summonerId":9100000000000003}],
             "positionSwaps":[{"cellId":1,"id":0,"state":"{{swapState}}"}],
             "trades":[{"cellId":1,"id":12,"state":"INVALID"}],
             "actions":[[{"id":{{actionId}},"actorCellId":0,"type":"{{type}}","championId":0,"completed":false,"isInProgress":{{live.ToString().ToLowerInvariant()}}}]]}
            """))!;

    [Fact]
    public async Task MyTurn_OnceWhenLive_EndsWhenNot_AndNewActionReplacesOld()
    {
        _t.OnChampSelect(Cs(live: false));
        _t.OnChampSelect(Cs(live: true));
        _t.OnChampSelect(Cs(live: true)); // same action: no repeat
        _t.OnChampSelect(Cs(live: true, actionId: 9, type: "pick")); // ban done, pick live in one update
        _t.OnChampSelect(Cs(live: false));
        Assert.Equal(["cs-stage", "myturn", "myturn-end", "myturn", "myturn-end"], await DrainAsync());
    }

    [Fact]
    public async Task ReadyCheck_PublishesOnFieldChangeNotTimer_AndResultOnPhaseLeave()
    {
        JsonNode Rc(string state, string resp, int timer) => JsonNode.Parse($$"""{"declinerIds":[],"dodgeWarning":"None","playerResponse":"{{resp}}","state":"{{state}}","suppressUx":false,"timer":{{timer}}}""")!;
        _t.OnPhase("ReadyCheck");
        _t.OnReadyCheck(Rc("InProgress", "None", 0));
        _t.OnReadyCheck(Rc("InProgress", "None", 1)); // timer tick only
        _t.OnReadyCheck(Rc("InProgress", "Accepted", 2));
        _t.OnReadyCheck(Rc("EveryoneReady", "Accepted", 3));
        _t.OnReadyCheck(null);
        _t.OnPhase("ChampSelect");
        Assert.Equal(["ready-check", "ready-check", "ready-check", "ready-check", "ready-check-result", "champselect-start"], await DrainAsync());
    }

    [Fact]
    public async Task ReadyCheck_FailedWhenPhaseGoesBackToMatchmaking()
    {
        _t.OnPhase("ReadyCheck");
        _t.OnPhase("Matchmaking");
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        GlamourEvent? result = null;
        try { await foreach (var e in _sub.ReadAllAsync(cts.Token)) if (e.Type == "ready-check-result") result = e; } catch (OperationCanceledException) { }
        Assert.Contains("failed", System.Text.Json.JsonSerializer.Serialize(result!.Data));
    }

    [Fact]
    public async Task Swap_PublishedOnlyForActionableStates()
    {
        _t.OnChampSelect(Cs(swapState: "AVAILABLE"));
        _t.OnChampSelect(Cs(swapState: "RECEIVED"));
        _t.OnChampSelect(Cs(swapState: "RECEIVED")); // unchanged
        _t.OnChampSelect(Cs(swapState: "CANCELLED"));
        _t.OnChampSelect(Cs(swapState: "AVAILABLE"));
        Assert.Equal(["cs-stage", "swap", "swap"], await DrainAsync());
        _t.OnChampSelect(Cs(swapState: "RECEIVED"));
        Assert.Contains("RECEIVED", System.Text.Json.JsonSerializer.Serialize(_t.Snapshot()));
    }

    [Fact]
    public async Task Swap_EndIsPublished_WhenARequestTimesOutOrVanishes()
    {
        _t.OnChampSelect(Cs(swapState: "RECEIVED"));
        _t.OnChampSelect(Cs(swapState: "AVAILABLE")); // timed out: the phone must drop its buttons
        _t.OnChampSelect(Cs(swapState: "RECEIVED"));
        _t.OnChampSelect(ChampSelectSession.FromJson(JsonNode.Parse("""{"localPlayerCellId":0,"timer":{"phase":"BAN_PICK","adjustedTimeLeftInPhase":1,"internalNowInEpochMs":1},"actions":[[]]}"""))!); // gone
        var states = new List<string>();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        try { await foreach (var e in _sub.ReadAllAsync(cts.Token)) if (e.Type == "swap") states.Add(System.Text.Json.JsonSerializer.SerializeToElement(e.Data).GetProperty("state").GetString()!); } catch (OperationCanceledException) { }
        Assert.Equal(["RECEIVED", "AVAILABLE", "RECEIVED", "GONE"], states);
        Assert.Empty(System.Text.Json.JsonSerializer.SerializeToElement(_t.Snapshot()).GetProperty("champSelect").GetProperty("swaps").EnumerateArray());
    }

    [Fact]
    public async Task Dodge_ByMeDetectedFromSummonerId_AndChampSelectEndOutcome()
    {
        _t.OnPhase("ChampSelect");
        _t.OnChampSelect(Cs());
        _t.OnGameflowSession(JsonNode.Parse("""{"gameDodge":{"dodgeIds":[9100000000000003],"phase":"ChampSelect","state":"PartyDodged"}}"""));
        _t.OnGameflowSession(JsonNode.Parse("""{"gameDodge":{"dodgeIds":[9100000000000003],"phase":"ChampSelect","state":"PartyDodged"}}""")); // repeat
        _t.OnPhase("Lobby");
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        var all = new List<GlamourEvent>();
        try { await foreach (var e in _sub.ReadAllAsync(cts.Token)) if (e.Type != "board") all.Add(e); } catch (OperationCanceledException) { }
        Assert.Equal(["champselect-start", "cs-stage", "dodge", "champselect-end"], all.Select(e => e.Type));
        Assert.Contains("\"byMe\":true", System.Text.Json.JsonSerializer.Serialize(all[2].Data));
        Assert.Contains("ended", System.Text.Json.JsonSerializer.Serialize(all[3].Data));
    }

    [Fact]
    public async Task Queue_PublishesOnSearchStateChange()
    {
        _t.OnSearch(JsonNode.Parse("""{"searchState":"Searching","queueId":400,"estimatedQueueTime":60,"timeInQueue":1}"""));
        _t.OnSearch(JsonNode.Parse("""{"searchState":"Searching","queueId":400,"estimatedQueueTime":60,"timeInQueue":2}"""));
        _t.OnSearch(null);
        Assert.Equal(["queue", "queue"], await DrainAsync());
    }

    [Fact]
    public async Task Lobby_PublishesOnlyWhenStartPermissionChanges()
    {
        _t.OnLobby(JsonNode.Parse("""{"localMember":{"isLeader":false,"allowedStartActivity":false}}"""));
        _t.OnLobby(JsonNode.Parse("""{"localMember":{"isLeader":false,"allowedStartActivity":false}}"""));
        _t.OnLobby(JsonNode.Parse("""{"localMember":{"isLeader":true,"allowedStartActivity":true}}"""));
        _t.OnLobby(null); // left the lobby: stays startable, no event
        Assert.Equal(["lobby", "lobby"], await DrainAsync());
    }

    private const string TwoMembers = """
        {"localMember":{"puuid":"me","isLeader":true,"allowedStartActivity":true},"gameConfig":{"queueId":400},
         "members":[{"puuid":"me","summonerId":1,"isLeader":true,"ready":true,"summonerIconId":6100,"summonerLevel":348,"firstPositionPreference":"MIDDLE","secondPositionPreference":"FILL","isBot":false},
                    {"puuid":"you","summonerId":2,"isLeader":false,"ready":READY,"summonerIconId":1,"summonerLevel":30,"firstPositionPreference":"","secondPositionPreference":"","isBot":false}]}
        """;

    [Fact]
    public async Task Lobby_RosterIsPublishedAndAMemberChangeRepublishes()
    {
        _t.OnLobby(JsonNode.Parse(TwoMembers.Replace("READY", "false")));
        _t.OnLobby(JsonNode.Parse(TwoMembers.Replace("READY", "false"))); // same roster, no event
        _t.OnLobby(JsonNode.Parse(TwoMembers.Replace("READY", "true"))); // the other member readied up
        Assert.Equal(["lobby", "lobby"], await DrainAsync());
        var roster = System.Text.Json.JsonSerializer.SerializeToElement(_t.Snapshot()).GetProperty("lobby").GetProperty("Roster");
        Assert.Equal(2, roster.GetArrayLength());
        Assert.True(roster[0].GetProperty("IsMe").GetBoolean() && roster[0].GetProperty("IsLeader").GetBoolean());
        Assert.True(roster[1].GetProperty("Ready").GetBoolean() && !roster[1].GetProperty("IsMe").GetBoolean());
    }
}
