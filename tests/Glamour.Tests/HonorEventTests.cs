using System.Text.Json;
using System.Text.Json.Nodes;
using Glamour.Core;
using Glamour.Lcu;
using Microsoft.Extensions.Logging.Abstractions;

namespace Glamour.Tests;

/// <summary>The `honor` event, replayed from a real post-game: ballot Create, two Updates (votes staged), Delete (Fixtures/honor-ballot-events.jsonl, from tools/logs/events-2026-10-02T06-42-49-661Z.jsonl).</summary>
public class HonorEventTests
{
    private const string Ballot = "/lol-honor-v2/v1/ballot";
    private readonly FakeLcu _lcu = new();
    private readonly EventHub _hub = new();
    private readonly Engine _engine;

    public HonorEventTests() => _engine = new Engine(_lcu, [], _hub, NullLogger<Engine>.Instance);

    private static List<LcuEvent> RealBallotEvents()
    {
        var lines = File.ReadAllLines(FixtureFiles.PathOf("honor-ballot-events.jsonl"));
        return lines.Select(l => JsonNode.Parse(l)!).Where(o => (string?)o["uri"] == Ballot)
            .SkipWhile(o => (string?)o["type"] != "Create").Take(4)
            .Select(o => new LcuEvent(Ballot, (string)o["type"]!, o["data"]?.DeepClone())).ToList();
    }

    private async Task<List<JsonElement>> HonorEventsAsync()
    {
        using var sub = _hub.Subscribe();
        foreach (var e in RealBallotEvents()) await _engine.HandleAsync(e, default);
        var found = new List<JsonElement>();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        try { await foreach (var e in sub.ReadAllAsync(cts.Token)) if (e.Type == "honor") found.Add(JsonSerializer.SerializeToElement(e.Data, JsonSerializerOptions.Web)); } catch (OperationCanceledException) { }
        return found;
    }

    [Fact]
    public async Task Ballot_events_open_follow_and_close_the_honor_screen()
    {
        var events = await HonorEventsAsync();
        Assert.Equal(["Create", "Update", "Update", "Delete"], RealBallotEvents().Select(e => e.EventType));
        Assert.True(events.Count >= 2);
        Assert.NotEqual(0, events[0].GetProperty("gameId").GetInt64());
        Assert.Equal(9, events[0].GetProperty("candidates").GetArrayLength());
        Assert.Equal(0, events[^1].GetProperty("gameId").GetInt64()); // Delete closes it
        Assert.Contains(events, e => e.GetProperty("staged").GetInt32() > 0); // a staged vote shows up
    }

    [Fact]
    public async Task Friends_are_read_once_per_game_and_an_unchanged_ballot_is_not_republished()
    {
        var reads = new CountingLcu(_lcu);
        var engine = new Engine(reads, [], _hub, NullLogger<Engine>.Instance);
        using var sub = _hub.Subscribe();
        var create = RealBallotEvents()[0];
        await engine.HandleAsync(create, default);
        await engine.HandleAsync(create, default);
        Assert.Equal(1, reads.Count("/lol-chat/v1/friends"));
        var types = new List<string>();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        try { await foreach (var e in sub.ReadAllAsync(cts.Token)) types.Add(e.Type); } catch (OperationCanceledException) { }
        Assert.Equal(["honor"], types);
    }

    [Fact]
    public async Task Resync_reads_the_ballot_into_the_snapshot()
    {
        _lcu.Gets["/lol-gameflow/v1/gameflow-phase"] = JsonValue.Create("PreEndOfGame");
        var create = RealBallotEvents()[0];
        _lcu.Gets[Ballot] = create.Data;
        await _engine.ResyncAsync(default);
        var snap = JsonSerializer.SerializeToElement(_engine.Tracker.Snapshot(), JsonSerializerOptions.Web);
        Assert.NotEqual(0, snap.GetProperty("honor").GetProperty("gameId").GetInt64());
    }

    [Fact]
    public async Task A_ballot_with_game_id_0_is_no_ballot()
    {
        _lcu.Gets["/lol-gameflow/v1/gameflow-phase"] = JsonValue.Create("WaitingForStats");
        _lcu.Gets[Ballot] = JsonNode.Parse("""{"gameId":0,"eligibleAllies":[],"eligibleOpponents":[],"honoredPlayers":[],"votePool":{"votes":1}}""");
        await _engine.ResyncAsync(default);
        var snap = JsonSerializer.SerializeToElement(_engine.Tracker.Snapshot(), JsonSerializerOptions.Web);
        Assert.Equal(0, snap.GetProperty("honor").GetProperty("gameId").GetInt64());
        Assert.Equal(0, snap.GetProperty("honor").GetProperty("candidates").GetArrayLength());
    }

    private sealed class CountingLcu(ILcuClient inner) : ILcuClient
    {
        private readonly List<string> _paths = [];
        public int Count(string path) => _paths.Count(p => p == path);

        public Task<LcuResponse> SendAsync(HttpMethod method, string path, object? body = null, CancellationToken ct = default)
        {
            if (method == HttpMethod.Get) _paths.Add(path);
            return inner.SendAsync(method, path, body, ct);
        }
    }
}
