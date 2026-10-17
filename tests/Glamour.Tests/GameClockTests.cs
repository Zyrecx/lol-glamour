using System.Text.Json;
using System.Text.Json.Nodes;
using Glamour.Core;
using Microsoft.Extensions.Time.Testing;

namespace Glamour.Tests;

/// <summary>gamestats shape from a real game (tools/logs/livetest-*.jsonl): {"gameMode":"CLASSIC","gameTime":393.87,"mapName":"Map11",...}.</summary>
public class GameClockTests
{
    private readonly FakeTimeProvider _time = new(DateTimeOffset.FromUnixTimeMilliseconds(5_000_000));
    private readonly EventHub _hub = new();
    private readonly GameClock _clock;
    private readonly EventHub.Subscription _sub;

    public GameClockTests() { _clock = new GameClock(_hub, _time); _sub = _hub.Subscribe(); }

    private static JsonNode Stats(double seconds) => JsonNode.Parse($$"""{"gameMode":"CLASSIC","gameTime":{{seconds}},"mapName":"Map11","mapNumber":11,"mapTerrain":"Default"}""")!;

    private JsonElement Snap() => JsonSerializer.SerializeToElement(_clock.Snapshot());

    private async Task<int> GameEventsAsync()
    {
        var n = 0;
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        try { await foreach (var e in _sub.ReadAllAsync(cts.Token)) if (e.Type == "game") n++; } catch (OperationCanceledException) { }
        return n;
    }

    [Fact]
    public async Task Clock_is_sent_once_and_counts_in_every_snapshot()
    {
        _clock.Update(Stats(0)); // loading screen
        Assert.False(_clock.InGame);
        _clock.Update(Stats(393.8));
        _time.Advance(TimeSpan.FromSeconds(10));
        _clock.Update(Stats(403.9)); // on time: no message
        Assert.Equal(403_800, Snap().GetProperty("gameTimeMs").GetInt64());
        Assert.Equal("CLASSIC", Snap().GetProperty("gameMode").GetString());
        Assert.Equal(1, await GameEventsAsync());
    }

    [Fact]
    public async Task A_pause_in_the_game_republishes_the_clock()
    {
        _clock.Update(Stats(100));
        _time.Advance(TimeSpan.FromSeconds(30));
        _clock.Update(Stats(110)); // paused for 20 s
        Assert.Equal(110_000, Snap().GetProperty("gameTimeMs").GetInt64());
        Assert.Equal(2, await GameEventsAsync());
    }

    [Fact]
    public async Task Game_ends_after_repeated_misses_or_when_the_client_leaves_the_game_phases()
    {
        _clock.Update(Stats(100));
        _clock.Update(null);
        Assert.True(_clock.InGame); // one miss is not the end
        _clock.Update(null);
        Assert.False(_clock.InGame);

        _clock.Update(Stats(100));
        _clock.OnPhase("InProgress", connected: true);
        Assert.True(_clock.InGame);
        _clock.OnPhase("WaitingForStats", connected: true);
        Assert.False(_clock.InGame);
        Assert.Equal(4, await GameEventsAsync());
    }

    [Theory]
    [InlineData("InProgress", true, false, true)]
    [InlineData("Reconnect", true, false, true)]
    [InlineData("None", false, true, true)]    // the client dropped but the game runs
    [InlineData("None", true, true, false)]    // connected and not in a game phase
    [InlineData("Lobby", false, false, false)]
    [InlineData("InProgress", false, false, false)] // a stale phase after the client dropped, no game process
    public void Polls_only_when_a_game_can_be_running(string phase, bool connected, bool gameProcess, bool poll) =>
        Assert.Equal(poll, GameClock.ShouldPoll(phase, connected, gameProcess));
}
