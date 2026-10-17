using System.Text.Json;
using System.Text.Json.Nodes;
using Glamour.Core;
using Glamour.Lcu;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Glamour.Tests;

/// <summary>Ban/lock delay with its re-check, the auto-accept delay with cancel, the global automation switch and "pause next game".</summary>
public class DelayAndGateTests
{
    private const string SessionPath = "/lol-champ-select/v1/session";
    private readonly FakeLcu _lcu = new();
    private readonly GlamourSettings _settings = new();
    private readonly FakeTimeProvider _time = new(DateTimeOffset.FromUnixTimeMilliseconds(1_000_000));
    private readonly EventHub _hub = new();
    private readonly AutomationGate _gate;
    private readonly AutoBanLockFeature _banLock;
    private readonly AutoAcceptFeature _accept;

    public DelayAndGateTests()
    {
        _gate = new AutomationGate(_settings, _hub, _time);
        var gateway = new ActionGateway(_lcu, _settings, NullLogger<ActionGateway>.Instance);
        _banLock = new AutoBanLockFeature(_lcu, gateway, _settings, _time, NullLogger<AutoBanLockFeature>.Instance, _gate);
        _accept = new AutoAcceptFeature(gateway, _settings, _gate, _time);
        _lcu.Gets["/lol-champ-select/v1/pickable-champion-ids"] = JsonNode.Parse("[1,2,518]");
        _settings.AutoBanChampionId = 238;
    }

    private ChampSelectSession Turn() =>
        new(0, new ChampSelectTimer("BAN_PICK", 30_000, _time.GetUtcNow().ToUnixTimeMilliseconds()), [new ChampSelectAction(7, 0, "ban", 0, false, true)]);

    /// <summary>The session as the client reports it while Glamour waits.</summary>
    private void ClientSays(int championId, string phase = "BAN_PICK", bool completed = false) =>
        _lcu.Gets[SessionPath] = JsonNode.Parse($$"""
            {"localPlayerCellId":0,"timer":{"phase":"{{phase}}","adjustedTimeLeftInPhase":20000,"internalNowInEpochMs":1},
             "actions":[[{"id":7,"actorCellId":0,"type":"ban","championId":{{championId}},"completed":{{completed.ToString().ToLowerInvariant()}},"isInProgress":true}]]}
            """);

    private int Completes => _lcu.Writes.Count(w => w.Body?.Contains("\"completed\":true") == true);
    private int Accepts => _lcu.Writes.Count(w => w.Path.EndsWith("ready-check/accept"));

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition());
    }

    private static async Task Settle() => await Task.Delay(60);

    [Fact]
    public async Task Default_delay_hovers_at_once_and_bans_two_seconds_later()
    {
        Assert.Equal(2, _settings.ActionDelaySeconds);
        ClientSays(238);
        await _banLock.OnChampSelectAsync(Turn(), default);
        Assert.Single(_lcu.Writes); // hover
        _time.Advance(TimeSpan.FromSeconds(1.9));
        await Settle();
        Assert.Equal(0, Completes);
        _time.Advance(TimeSpan.FromSeconds(0.2));
        await WaitFor(() => Completes == 1);
    }

    [Fact]
    public async Task Waiting_ban_is_published_with_its_countdown_and_cleared_once_sent()
    {
        ClientSays(238);
        await _banLock.OnChampSelectAsync(Turn(), default);
        var pending = JsonSerializer.SerializeToElement(_gate.Snapshot()).GetProperty("draftPending");
        Assert.Equal("ban", pending.GetProperty("type").GetString());
        Assert.Equal(238, pending.GetProperty("championId").GetInt32());
        Assert.Equal(2000, pending.GetProperty("timeLeftMs").GetInt64());
        _time.Advance(TimeSpan.FromSeconds(2.1));
        await WaitFor(() => Completes == 1);
        await WaitFor(() => JsonSerializer.SerializeToElement(_gate.Snapshot()).GetProperty("draftPending").ValueKind == JsonValueKind.Null);
    }

    [Fact]
    public async Task Skip_leaves_the_waiting_ban_to_the_user_for_this_champ_select()
    {
        ClientSays(238);
        Assert.False(_banLock.SkipPending());
        await _banLock.OnChampSelectAsync(Turn(), default);
        Assert.True(_banLock.SkipPending());
        var snap = JsonSerializer.SerializeToElement(_gate.Snapshot());
        Assert.Equal(JsonValueKind.Null, snap.GetProperty("draftPending").ValueKind);
        Assert.Equal(["ban"], snap.GetProperty("draftSkipped").EnumerateArray().Select(e => e.GetString()));
        await _banLock.OnChampSelectAsync(Turn(), default); // the same turn again: not scheduled anew
        _time.Advance(TimeSpan.FromSeconds(3));
        await Settle();
        Assert.Equal(0, Completes);
        Assert.Single(_lcu.Writes); // only the hover, which stays
        await _gate.OnPhaseAsync("Lobby", default);
        Assert.Empty(JsonSerializer.SerializeToElement(_gate.Snapshot()).GetProperty("draftSkipped").EnumerateArray());
    }

    [Fact]
    public async Task A_user_hover_while_waiting_ends_the_countdown_at_once()
    {
        await _banLock.OnChampSelectAsync(Turn(), default);
        await _banLock.OnChampSelectAsync(Turn() with { Actions = [new ChampSelectAction(7, 0, "ban", 238, false, true)] }, default); // Glamour's own hover
        Assert.Equal(JsonValueKind.Object, JsonSerializer.SerializeToElement(_gate.Snapshot()).GetProperty("draftPending").ValueKind);
        await _banLock.OnChampSelectAsync(Turn() with { Actions = [new ChampSelectAction(7, 0, "ban", 99, false, true)] }, default);
        Assert.Equal(JsonValueKind.Null, JsonSerializer.SerializeToElement(_gate.Snapshot()).GetProperty("draftPending").ValueKind);
        ClientSays(238);
        _time.Advance(TimeSpan.FromSeconds(3));
        await Settle();
        Assert.Equal(0, Completes);
    }

    [Theory]
    [InlineData(99, "BAN_PICK", false)]      // the user hovered something else
    [InlineData(238, "FINALIZATION", false)] // the stage moved on
    [InlineData(238, "BAN_PICK", true)]      // already done (by the user)
    public async Task Delayed_ban_gives_up_when_anything_changed_meanwhile(int champion, string phase, bool completed)
    {
        await _banLock.OnChampSelectAsync(Turn(), default);
        ClientSays(champion, phase, completed);
        _time.Advance(TimeSpan.FromSeconds(3));
        await Settle();
        Assert.Equal(0, Completes);
    }

    [Fact]
    public async Task Switching_automation_off_stops_a_waiting_ban_and_blocks_new_ones()
    {
        ClientSays(238);
        await _banLock.OnChampSelectAsync(Turn(), default);
        _settings.AutomationEnabled = false;
        _time.Advance(TimeSpan.FromSeconds(3));
        await Settle();
        Assert.Equal(0, Completes);

        await _banLock.OnPhaseAsync("None", default);
        _lcu.Writes.Clear();
        await _banLock.OnChampSelectAsync(Turn(), default);
        Assert.Empty(_lcu.Writes); // not even a hover
    }

    [Fact]
    public async Task Pause_covers_one_game_and_its_post_game_but_survives_a_dodge()
    {
        _gate.SetPaused(true);
        foreach (var p in new[] { "Lobby", "Matchmaking", "ReadyCheck", "ChampSelect", "Lobby" }) await _gate.OnPhaseAsync(p, default); // dodged
        Assert.True(_gate.Paused);
        foreach (var p in new[] { "Matchmaking", "ReadyCheck", "ChampSelect", "GameStart", "InProgress", "WaitingForStats", "PreEndOfGame", "EndOfGame" })
        {
            await _gate.OnPhaseAsync(p, default);
            Assert.True(_gate.Paused);
            Assert.False(_gate.Active);
        }
        await _gate.OnPhaseAsync("Lobby", default);
        Assert.False(_gate.Paused);
        Assert.True(_gate.Active);
    }

    [Fact]
    public async Task Stop_for_this_draft_blocks_ban_and_lock_only_and_ends_with_champ_select()
    {
        Assert.False(_gate.SetDraftStopped(true)); // only inside champ select
        await _gate.OnPhaseAsync("ChampSelect", default);
        ClientSays(238);
        await _banLock.OnChampSelectAsync(Turn(), default); // hovered, ban waiting on the delay
        Assert.True(_gate.SetDraftStopped(true));
        _time.Advance(TimeSpan.FromSeconds(3));
        await Settle();
        Assert.Equal(0, Completes);
        Assert.True(_gate.Active); // accept, honor and skips are not stopped
        Assert.False(_gate.DraftActive);

        await _gate.OnPhaseAsync("Lobby", default); // dodged: the next draft runs again
        Assert.False(_gate.DraftStopped);
        Assert.True(_gate.DraftActive);
    }

    [Theory]
    [InlineData("InProgress")]
    [InlineData("EndOfGame")]
    public async Task Pause_pressed_in_a_game_covers_that_game_only(string now)
    {
        foreach (var p in new[] { "ChampSelect", "GameStart", "InProgress", "WaitingForStats", "PreEndOfGame", "EndOfGame" })
        {
            await _gate.OnPhaseAsync(p, default);
            if (p == now) _gate.SetPaused(true);
        }
        Assert.True(_gate.Paused);
        await _gate.OnPhaseAsync("Lobby", default);
        Assert.False(_gate.Paused); // the next game is automated again
    }

    [Fact]
    public async Task Pause_changes_are_published_with_the_snapshot_shape()
    {
        using var sub = _hub.Subscribe();
        _gate.SetPaused(true);
        _gate.SetPaused(true); // no change, no event
        _gate.SetPaused(false);
        var data = new List<string>();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        try { await foreach (var e in sub.ReadAllAsync(cts.Token)) data.Add($"{e.Type} {JsonSerializer.Serialize(e.Data)}"); } catch (OperationCanceledException) { }
        Assert.Equal(["automation {\"paused\":true,\"draftStopped\":false,\"acceptPending\":null,\"draftPending\":null,\"draftSkipped\":[],\"swapNotes\":[],\"loadout\":null}", "automation {\"paused\":false,\"draftStopped\":false,\"acceptPending\":null,\"draftPending\":null,\"draftSkipped\":[],\"swapNotes\":[],\"loadout\":null}"], data);
    }

    [Fact]
    public async Task Accept_delay_waits_shows_pending_and_then_accepts()
    {
        _settings.AutoAccept = AutoAcceptMode.Always;
        _settings.AutoAcceptDelaySeconds = 4;
        await _accept.OnPhaseAsync("ReadyCheck", default);
        Assert.Equal(0, Accepts);
        var pending = JsonSerializer.SerializeToElement(_gate.Snapshot()).GetProperty("acceptPending");
        Assert.Equal(4000, pending.GetProperty("timeLeftMs").GetInt64());
        _time.Advance(TimeSpan.FromSeconds(4));
        await WaitFor(() => Accepts == 1);
        await _accept.Idle;
        Assert.Equal(JsonValueKind.Null, JsonSerializer.SerializeToElement(_gate.Snapshot()).GetProperty("acceptPending").ValueKind);
    }

    [Fact]
    public async Task Last_second_accept_waits_for_the_latest_safe_moment_whatever_the_delay()
    {
        _settings.AutoAccept = AutoAcceptMode.Always;
        _settings.AcceptTiming = ActionTiming.LastSecond;
        _settings.AutoAcceptDelaySeconds = 0;
        Assert.Equal(TimeSpan.FromSeconds(11), AutoAcceptFeature.LastSecondDelay);
        await _accept.OnPhaseAsync("ReadyCheck", default);
        _time.Advance(TimeSpan.FromSeconds(10.9));
        await Settle();
        Assert.Equal(0, Accepts);
        _time.Advance(TimeSpan.FromSeconds(0.1));
        await WaitFor(() => Accepts == 1);
        var match = new GlamourEvent("ready-check", new { state = "InProgress", playerResponse = "None" });
        Assert.Contains("accepts in 11 s", NotificationRules.For(match, _settings)!.Body);
    }

    [Fact]
    public async Task Accept_delay_can_be_cancelled_for_this_match_only()
    {
        _settings.AutoAccept = AutoAcceptMode.Always;
        _settings.AutoAcceptDelaySeconds = 4;
        await _accept.OnPhaseAsync("ReadyCheck", default);
        Assert.True(_accept.CancelPending());
        Assert.False(_accept.CancelPending()); // nothing left
        _time.Advance(TimeSpan.FromSeconds(5));
        await Settle();
        Assert.Equal(0, Accepts);
        Assert.Equal(AutoAcceptMode.Always, _settings.AutoAccept);

        await _accept.OnPhaseAsync("Matchmaking", default); // the next match accepts again
        await _accept.OnPhaseAsync("ReadyCheck", default);
        _time.Advance(TimeSpan.FromSeconds(4));
        await WaitFor(() => Accepts == 1);
    }

    [Theory]
    [InlineData("Accepted")]
    [InlineData("Declined")]
    public async Task Accept_delay_stops_when_answered_elsewhere(string response)
    {
        _settings.AutoAccept = AutoAcceptMode.Always;
        _settings.AutoAcceptDelaySeconds = 4;
        await _accept.OnPhaseAsync("ReadyCheck", default);
        await _accept.OnEventAsync(new LcuEvent("/lol-matchmaking/v1/ready-check", "Update", JsonNode.Parse($$"""{"state":"InProgress","playerResponse":"{{response}}"}""")), default);
        _time.Advance(TimeSpan.FromSeconds(5));
        await Settle();
        Assert.Equal(0, Accepts);
    }

    /// <summary>The desktop's Accept ring shows only while <see cref="AutomationGate.AcceptWait"/> has a value.</summary>
    [Fact]
    public async Task Accept_wait_is_there_only_while_a_delayed_accept_can_still_fire()
    {
        _settings.AutoAccept = AutoAcceptMode.Always;
        _settings.AutoAcceptDelaySeconds = 4;
        var start = _time.GetUtcNow();
        await _accept.OnPhaseAsync("ReadyCheck", default);
        Assert.Equal((start, start.AddSeconds(4)), _gate.AcceptWait);
        _gate.SetPaused(true);
        Assert.Null(_gate.AcceptWait);
        _gate.SetPaused(false);
        _settings.AutomationEnabled = false;
        Assert.Null(_gate.AcceptWait);
        _settings.AutomationEnabled = true;

        await _accept.OnEventAsync(new LcuEvent("/lol-matchmaking/v1/ready-check", "Update", JsonNode.Parse("""{"state":"InProgress","playerResponse":"Accepted"}""")), default);
        Assert.Null(_gate.AcceptWait); // answered in the client

        await _accept.OnPhaseAsync("Matchmaking", default);
        await _accept.OnPhaseAsync("ReadyCheck", default);
        Assert.True(_accept.CancelPending());
        Assert.Null(_gate.AcceptWait); // cancelled from the phone

        await _accept.OnPhaseAsync("Matchmaking", default);
        await _accept.OnPhaseAsync("ReadyCheck", default);
        _time.Advance(TimeSpan.FromSeconds(4));
        await WaitFor(() => Accepts == 1);
        await WaitFor(() => _gate.AcceptWait is null); // sent

        _settings.AutoAcceptDelaySeconds = 0;
        await _accept.OnPhaseAsync("Matchmaking", default);
        await _accept.OnPhaseAsync("ReadyCheck", default);
        Assert.Equal(2, Accepts);
        Assert.Null(_gate.AcceptWait); // instant: nothing waits
    }

    [Fact]
    public async Task Accept_delay_stops_when_the_ready_check_ends_or_automation_goes_off()
    {
        _settings.AutoAccept = AutoAcceptMode.Always;
        _settings.AutoAcceptDelaySeconds = 4;
        await _accept.OnPhaseAsync("ReadyCheck", default);
        await _accept.OnPhaseAsync("Matchmaking", default); // someone declined
        _time.Advance(TimeSpan.FromSeconds(5));
        await Settle();
        Assert.Equal(0, Accepts);

        await _accept.OnPhaseAsync("ReadyCheck", default);
        _settings.AutomationEnabled = false;
        _time.Advance(TimeSpan.FromSeconds(5));
        await Settle();
        Assert.Equal(0, Accepts);

        await _accept.OnPhaseAsync("Matchmaking", default);
        await _accept.OnPhaseAsync("ReadyCheck", default); // off from the start: nothing waits either
        Assert.Equal(JsonValueKind.Null, JsonSerializer.SerializeToElement(_gate.Snapshot()).GetProperty("acceptPending").ValueKind);
    }

    [Fact]
    public async Task Paused_accept_does_nothing()
    {
        _settings.AutoAccept = AutoAcceptMode.Always;
        _gate.SetPaused(true);
        await _accept.OnPhaseAsync("ReadyCheck", default);
        Assert.Equal(0, Accepts);
    }

    [Fact]
    public void Notifications_cover_what_automation_would_have_done_while_it_is_off()
    {
        _settings.AutoAccept = AutoAcceptMode.Always;
        var match = new GlamourEvent("ready-check", new { state = "InProgress", playerResponse = "None" });
        var turn = new GlamourEvent("myturn", new { actionId = 7, type = "ban", stage = "BAN_PICK", lane = "" });
        Assert.Null(NotificationRules.For(match, _settings));
        Assert.Null(NotificationRules.For(turn, _settings));
        Assert.NotNull(NotificationRules.For(match, _settings, automationActive: false));
        Assert.NotNull(NotificationRules.For(turn, _settings, automationActive: false));
        _settings.AutoAcceptDelaySeconds = 3;
        Assert.Contains("cancel", NotificationRules.For(match, _settings)!.Body);
    }
}

public class AutomationEndpointTests : IDisposable
{
    private readonly TestHost _host = new();

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task Pause_and_cancel_endpoints_and_the_snapshot()
    {
        var client = _host.Client(TestHost.Loopback);
        var paused = await client.PutAsync("/api/automation/pause", JsonContent("""{"paused":true}"""));
        paused.EnsureSuccessStatusCode();
        Assert.Contains("\"paused\":true", await paused.Content.ReadAsStringAsync());

        var cancel = await client.PostAsync("/api/ready-check/auto-accept/cancel", null);
        Assert.Equal(System.Net.HttpStatusCode.Conflict, cancel.StatusCode); // nothing waiting

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var response = await client.GetAsync("/api/events", HttpCompletionOption.ResponseHeadersRead, cts.Token);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cts.Token));
        var snapshot = await HostEventsTests.ReadEventAsync(reader);
        Assert.Contains("\"automation\":{\"paused\":true,\"draftStopped\":false,\"acceptPending\":null,\"draftPending\":null,\"draftSkipped\":[],\"swapNotes\":[],\"loadout\":null}", snapshot, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_host_wires_the_gate_into_the_features()
    {
        var settings = _host.Services.GetRequiredService<GlamourSettings>();
        settings.AutoAccept = AutoAcceptMode.Always;
        settings.AutoAcceptDelaySeconds = 5;
        var gate = _host.Services.GetRequiredService<AutomationGate>();
        var accept = _host.Services.GetServices<IFeature>().OfType<AutoAcceptFeature>().Single();
        Assert.Same(_host.Services.GetRequiredService<AutoAcceptFeature>(), accept);
        Assert.Same(gate, _host.Services.GetServices<IFeature>().First()); // first, so a pause ends before the other features see the phase

        await accept.OnPhaseAsync("ReadyCheck", default);
        Assert.Contains("timeLeftMs", JsonSerializer.Serialize(gate.Snapshot())); // the DI-built feature reports to the same gate
        Assert.True(accept.CancelPending());

        (await _host.Client(TestHost.Loopback).PutAsync("/api/automation/pause", JsonContent("""{"paused":true}"""))).EnsureSuccessStatusCode();
        await accept.OnPhaseAsync("Matchmaking", default);
        await accept.OnPhaseAsync("ReadyCheck", default);
        Assert.False(accept.CancelPending()); // paused: nothing was even scheduled
    }

    [Theory]
    [InlineData("actionDelaySeconds", 21)]
    [InlineData("autoAcceptDelaySeconds", 12)]
    [InlineData("autoAcceptDelaySeconds", -1)]
    public async Task Out_of_range_delays_are_refused(string key, double value)
    {
        var r = await _host.Client(TestHost.Loopback).PatchAsync("/api/settings", JsonContent($$"""{"{{key}}":{{value}}}"""));
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, r.StatusCode);
    }

    private static StringContent JsonContent(string json) => new(json, System.Text.Encoding.UTF8, "application/json");
}
