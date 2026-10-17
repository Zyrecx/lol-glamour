using System.Text.Json;
using System.Text.Json.Nodes;
using Glamour.Core;
using Microsoft.Extensions.Time.Testing;

namespace Glamour.Tests;

/// <summary>Queue and ready-check times leave the host as elapsed milliseconds (the phone's clock may differ), fresh in every snapshot, never republished per tick.</summary>
public class ElapsedTimeTests
{
    private readonly FakeTimeProvider _time = new(DateTimeOffset.FromUnixTimeMilliseconds(1_000_000));
    private readonly EventHub _hub = new();
    private readonly EventTracker _t;

    public ElapsedTimeTests() => _t = new EventTracker(_hub, _time);

    private JsonElement Snap(string key) => JsonSerializer.SerializeToElement(_t.Snapshot(), JsonSerializerOptions.Web).GetProperty(key);

    private static JsonNode Search(int timeInQueue, string state = "Searching") =>
        JsonNode.Parse($$"""{"searchState":"{{state}}","queueId":440,"estimatedQueueTime":116.19,"timeInQueue":{{timeInQueue}}}""")!;

    private static JsonNode Rc(string state, string resp, int timer) =>
        JsonNode.Parse($$"""{"declinerIds":[],"dodgeWarning":"None","playerResponse":"{{resp}}","state":"{{state}}","suppressUx":false,"timer":{{timer}}}""")!;

    [Fact]
    public void Queue_time_counts_from_the_first_search_seen_even_when_opened_mid_queue()
    {
        _t.OnSearch(Search(40)); // the host connected 40 s into the search
        Assert.Equal(40_000, Snap("queue").GetProperty("elapsedMs").GetInt64());
        _time.Advance(TimeSpan.FromSeconds(5));
        _t.OnSearch(Search(45));
        Assert.Equal(45_000, Snap("queue").GetProperty("elapsedMs").GetInt64());
        Assert.Equal(116.19, Snap("queue").GetProperty("estimatedQueueTime").GetDouble());
        _t.OnSearch(null);
        Assert.Equal(0, Snap("queue").GetProperty("elapsedMs").GetInt64());
        _t.OnSearch(Search(1)); // a new search starts from its own time
        Assert.Equal(1_000, Snap("queue").GetProperty("elapsedMs").GetInt64());
    }

    [Fact]
    public async Task Ready_check_sends_elapsed_and_window_and_ignores_timer_ticks()
    {
        using var sub = _hub.Subscribe();
        _t.OnReadyCheck(Rc("InProgress", "None", 2));
        _time.Advance(TimeSpan.FromSeconds(1));
        _t.OnReadyCheck(Rc("InProgress", "None", 3)); // tick only
        var rc = Snap("readyCheck");
        Assert.Equal(3_000, rc.GetProperty("elapsedMs").GetInt64());
        Assert.Equal((long)EventTracker.ReadyCheckWindow.TotalMilliseconds, rc.GetProperty("windowMs").GetInt64());
        _t.OnReadyCheck(null);
        Assert.Equal(0, Snap("readyCheck").GetProperty("elapsedMs").GetInt64());

        var types = new List<string>();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        try { await foreach (var e in sub.ReadAllAsync(cts.Token)) types.Add(e.Type); } catch (OperationCanceledException) { }
        Assert.Equal(["ready-check", "ready-check"], types);
    }
}
