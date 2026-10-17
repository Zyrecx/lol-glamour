using System.Text.Json.Nodes;
using Glamour.Core;
using Glamour.Host;
using Glamour.Lcu;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Glamour.Tests;

/// <summary>League's rewrite of Glamour's rune page ~6 s before champ select ends is put back; the user's own changes never are (INSIGHTS "Runes").</summary>
public class RunePageGuardTests
{
    private sealed class Client : ILcuClient
    {
        public List<(string Path, string Body)> Puts { get; } = [];

        public Task<LcuResponse> SendAsync(HttpMethod method, string path, object? body = null, CancellationToken ct = default)
        {
            if (method == HttpMethod.Put) Puts.Add((path, System.Text.Json.JsonSerializer.Serialize(body)));
            return Task.FromResult(new LcuResponse(201, null));
        }
    }

    private const int Ours = 1795163757;
    private static readonly int[] GlamourPerks = [8230, 8275, 8234, 8236, 9104, 9111, 5005, 5008, 5001];
    private static readonly int[] LeaguePerks = [8010, 9111, 9104, 8299, 8224, 8234, 5005, 5008, 5001];
    private static readonly int[] HandPerks = [8010, 9111, 9104, 8299, 8224, 8236, 5005, 5008, 5001];

    private readonly Client _lcu = new();
    private readonly FakeTimeProvider _time = new(DateTimeOffset.FromUnixTimeMilliseconds(1_000_000));
    private readonly RunePageTracker _pages = new();
    private readonly RuneWindow _window = new();
    private readonly GlamourSettings _settings = new();
    private readonly AutomationGate _gate;
    private readonly RunePageGuard _guard;

    public RunePageGuardTests()
    {
        _gate = new AutomationGate(_settings, new EventHub(), _time);
        _guard = new RunePageGuard(_pages, new ActionGateway(_lcu, _settings, NullLogger<ActionGateway>.Instance), _window, _gate, _time, NullLogger<RunePageGuard>.Instance);
        _pages.Created(Page(Ours, "Glamour vs Rumble", 8200, 8000, GlamourPerks));
        _window.Report(true, false, _time.GetUtcNow());
    }

    private static JsonObject Page(int id, string name, int primary, int sub, int[] perks, bool temporary = true) => new()
    {
        ["id"] = id, ["name"] = name, ["primaryStyleId"] = primary, ["subStyleId"] = sub,
        ["selectedPerkIds"] = new JsonArray([.. perks.Select(p => (JsonNode)p)]), ["isTemporary"] = temporary, ["current"] = true,
    };

    private Task Current(JsonObject page) => _guard.OnEventAsync(new LcuEvent(RunePageGuard.CurrentPage, "Update", page), default);

    private async Task Left(double seconds, string phase = "FINALIZATION")
    {
        await _guard.OnPhaseAsync("ChampSelect", default);
        await _guard.OnChampSelectAsync(new ChampSelectSession(0, new ChampSelectTimer(phase, (long)(seconds * 1000), _time.GetUtcNow().ToUnixTimeMilliseconds()), []), default);
    }

    private JsonObject League() => Page(Ours, "Darius - Conqueror", 8000, 8200, LeaguePerks);

    [Fact]
    public async Task League_rewrite_six_seconds_before_the_end_is_put_back_once()
    {
        await Left(25);
        await Current(Page(Ours, "Glamour vs Rumble", 8200, 8000, GlamourPerks));
        _time.Advance(TimeSpan.FromSeconds(19));
        await Current(League());
        var put = Assert.Single(_lcu.Puts);
        Assert.Equal($"/lol-perks/v1/pages/{Ours}", put.Path);
        Assert.Contains("\"name\":\"Glamour vs Rumble\"", put.Body);
        Assert.Contains("[8230,8275,8234,8236,9104,9111,5005,5008,5001]", put.Body);

        await Current(League()); // the client sends the rewrite twice (LIVE)
        await Current(Page(Ours, "Glamour vs Rumble", 8200, 8000, GlamourPerks)); // the restore coming back
        Assert.Single(_lcu.Puts);
    }

    [Fact]
    public async Task The_page_as_created_is_kept_even_when_the_first_event_is_already_the_rewrite()
    {
        await Left(6);
        await Current(League());
        Assert.Contains("[8230,8275,8234,8236,9104,9111,5005,5008,5001]", Assert.Single(_lcu.Puts).Body);
    }

    [Fact]
    public async Task A_recommended_page_picked_with_the_rune_window_open_is_the_users()
    {
        await Left(6.5);
        await Current(Page(Ours, "Glamour vs Rumble", 8200, 8000, GlamourPerks));
        _window.Report(true, true, _time.GetUtcNow());
        _time.Advance(TimeSpan.FromSeconds(0.5));
        await Current(League());
        Assert.Empty(_lcu.Puts);

        _window.Report(true, false, _time.GetUtcNow()); // closed just now: still counts for a moment
        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.True(_window.OpenWithin(RunePageGuard.Quiet, _time.GetUtcNow()));
    }

    [Fact]
    public async Task A_hand_edit_earlier_is_what_comes_back_after_the_rewrite()
    {
        await Left(25);
        await Current(Page(Ours, "Glamour vs Rumble", 8200, 8000, GlamourPerks));
        _time.Advance(TimeSpan.FromSeconds(5));
        await Current(Page(Ours, "Glamour vs Rumble", 8000, 8200, HandPerks)); // 20 s left: the user's
        Assert.Empty(_lcu.Puts);
        _time.Advance(TimeSpan.FromSeconds(14));
        await Current(League());
        Assert.Contains("[8010,9111,9104,8299,8224,8236,5005,5008,5001]", Assert.Single(_lcu.Puts).Body);
    }

    [Fact]
    public async Task Without_a_screen_watch_timing_alone_decides()
    {
        _window.Report(false, false, _time.GetUtcNow());
        await Left(6);
        await Current(League());
        Assert.Single(_lcu.Puts);
    }

    [Fact]
    public async Task Other_pages_game_starting_and_planning_are_left_alone()
    {
        await Left(6);
        await Current(Page(1096560098, "New Runes Page 1", 8000, 8400, HandPerks, temporary: false));
        await Current(Page(42, "Kayle - Fleet Footwork", 8000, 8200, LeaguePerks)); // another temporary page
        await Left(0, "GAME_STARTING");
        await Current(League());
        await Left(6, "BAN_PICK");
        await Current(Page(Ours, "Glamour vs Rumble", 8200, 8000, LeaguePerks));
        Assert.Empty(_lcu.Puts);
    }

    [Fact]
    public async Task Nothing_is_put_back_after_stop()
    {
        await _gate.OnPhaseAsync("ChampSelect", default);
        _gate.SetDraftStopped(true);
        await Left(6);
        await Current(League());
        Assert.Empty(_lcu.Puts);
    }

    [Fact]
    public async Task At_most_two_restores_then_the_change_stays()
    {
        await Left(6);
        await Current(League());
        await Current(Page(Ours, "Darius - Lethal Tempo", 8000, 8200, HandPerks));
        await Current(League());
        Assert.Equal(2, _lcu.Puts.Count);
    }
}
