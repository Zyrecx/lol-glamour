using System.Text.Json.Nodes;
using Glamour.Core;
using Microsoft.Extensions.Logging.Abstractions;

namespace Glamour.Tests;

public class PartiesTests
{
    // Shape copied from a live friend presence (an open Flex party of one).
    private const string OpenPty = """{\"agsActivityId\":\"hKOhGHnmVn7K\",\"isPartyOpen\":true,\"maxPlayers\":5,\"partyId\":\"5a7b3c91-2e4d-4f60-8a1b-c3d5e7f9a2b4\",\"queueId\":440,\"summonerPuuids\":[\"c836f0bf\"],\"summoners\":[1]}""";

    private static JsonNode Friend(string name, string ptyType, string pty) =>
        JsonNode.Parse("{\"gameName\":\"" + name + "\",\"gameTag\":\"3434\",\"puuid\":\"p-" + name + "\",\"lol\":{\"ptyType\":\"" + ptyType + "\",\"pty\":\"" + pty + "\"}}")!;

    [Fact]
    public void OpenParty_IsReadFromPresence()
    {
        var friends = new JsonArray(Friend("Pellucid", "open", OpenPty), Friend("Closed", "closed", ""), JsonNode.Parse("""{"gameName":"Off","lol":{}}""")!);
        var party = Assert.Single(Parties.OpenFrom(friends));
        Assert.Equal(new OpenParty("Pellucid#3434", "p-Pellucid", "5a7b3c91-2e4d-4f60-8a1b-c3d5e7f9a2b4", 440, 1, 5), party);
    }

    [Fact]
    public void FullOrMalformedParties_AreSkipped()
    {
        var full = OpenPty.Replace("\\\"c836f0bf\\\"", "\\\"a\\\",\\\"b\\\",\\\"c\\\",\\\"d\\\",\\\"e\\\"");
        var friends = new JsonArray(Friend("Full", "open", full), Friend("Junk", "open", "not json"), Friend("Notopen", "open", OpenPty.Replace("true", "false")));
        Assert.Empty(Parties.OpenFrom(friends));
        Assert.Empty(Parties.OpenFrom(null));
    }

    [Fact]
    public void OnlyPendingInvitations_AreListed()
    {
        var json = JsonNode.Parse("""
            [{"invitationId":"a","state":"Pending","fromSummonerName":"Pellucid","canAcceptInvitation":true,"gameConfig":{"queueId":440}},
             {"invitationId":"b","state":"Declined","fromSummonerName":"X"},
             {"invitationId":"c","state":"Pending"}]
            """);
        var list = Parties.PendingFrom(json);
        Assert.Equal([new PendingInvitation("a", "Pellucid", 440, true), new PendingInvitation("c", "A friend", 0, true)], list);
    }

    [Fact]
    public async Task Tracker_PublishesInvitationsOnlyOnChange()
    {
        var hub = new EventHub();
        using var sub = hub.Subscribe();
        var t = new EventTracker(hub);
        var one = JsonNode.Parse("""[{"invitationId":"a","state":"Pending","fromSummonerName":"Pellucid"}]""");
        t.OnInvitations(one);
        t.OnInvitations(one);
        t.OnInvitations(JsonNode.Parse("[]"));
        using var cts = new CancellationTokenSource(200);
        var seen = new List<string>();
        try { await foreach (var e in sub.ReadAllAsync(cts.Token)) seen.Add(e.Type); } catch (OperationCanceledException) { }
        Assert.Equal(["invitations", "invitations"], seen);
    }

    [Theory]
    [InlineData(true, "/lol-lobby/v2/received-invitations/inv%201/accept")]
    [InlineData(false, "/lol-lobby/v2/received-invitations/inv%201/decline")]
    public async Task Gateway_RepliesToAnInvitation(bool accept, string path)
    {
        var lcu = new FakeLcu();
        await new ActionGateway(lcu, new GlamourSettings(), NullLogger<ActionGateway>.Instance).ReplyInvitationAsync("inv 1", accept);
        Assert.Equal([(HttpMethod.Post, path)], lcu.Writes.Select(w => (w.Method, w.Path)));
    }

    [Fact]
    public async Task Gateway_SetsPositionPreferences()
    {
        var lcu = new FakeLcu();
        await new ActionGateway(lcu, new GlamourSettings(), NullLogger<ActionGateway>.Instance).SetPositionsAsync("TOP", "MIDDLE");
        var w = Assert.Single(lcu.Writes);
        Assert.Equal((HttpMethod.Put, "/lol-lobby/v2/lobby/members/localMember/position-preferences", "{\"firstPreference\":\"TOP\",\"secondPreference\":\"MIDDLE\"}"), (w.Method, w.Path, w.Body));
    }

    [Theory]
    [InlineData("TOP", false, true)]
    [InlineData("FILL", false, true)]
    [InlineData("UNSELECTED", false, false)]
    [InlineData("UNSELECTED", true, true)]
    [InlineData("SUPPORT", true, false)] // the client says UTILITY
    [InlineData(null, true, false)]
    public void PositionValues_AreWhatTheClientAccepts(string? value, bool allowUnselected, bool ok) => Assert.Equal(ok, LaneNames.IsPosition(value, allowUnselected));

    [Fact]
    public async Task Tracker_LobbyCarriesQueueAndLanes()
    {
        var hub = new EventHub();
        using var sub = hub.Subscribe();
        var t = new EventTracker(hub);
        t.OnLobby(JsonNode.Parse("""{"gameConfig":{"queueId":440,"showPositionSelector":true},"localMember":{"allowedStartActivity":true,"firstPositionPreference":"TOP","secondPositionPreference":"MIDDLE"},"members":[{},{}]}"""));
        t.OnLobby(JsonNode.Parse("""{"gameConfig":{"queueId":440,"showPositionSelector":true},"localMember":{"allowedStartActivity":true,"firstPositionPreference":"JUNGLE","secondPositionPreference":"MIDDLE"},"members":[{},{}]}"""));
        using var cts = new CancellationTokenSource(200);
        var seen = new List<string>();
        try { await foreach (var e in sub.ReadAllAsync(cts.Token)) seen.Add(System.Text.Json.JsonSerializer.Serialize(e.Data)); } catch (OperationCanceledException) { }
        Assert.Equal(2, seen.Count);
        Assert.Contains("\"QueueId\":440", seen[0]);
        Assert.Contains("\"First\":\"JUNGLE\"", seen[1]);
    }

    [Fact]
    public async Task Gateway_JoinsAPartyById()
    {
        var lcu = new FakeLcu();
        await new ActionGateway(lcu, new GlamourSettings(), NullLogger<ActionGateway>.Instance).JoinPartyAsync("5a7b3c91-2e4d");
        Assert.Equal([(HttpMethod.Post, "/lol-lobby/v2/party/5a7b3c91-2e4d/join")], lcu.Writes.Select(w => (w.Method, w.Path)));
    }
}

public class ChampSelectRouteTests : IDisposable
{
    private readonly TestHost _host = new();
    public void Dispose() => _host.Dispose();

    [Theory]
    [InlineData("/api/champselect/actions/7/hover", """{"championId":238}""")]
    [InlineData("/api/champselect/actions/7/complete", """{"championId":238,"type":"pick"}""")]
    public async Task HoverAndLock_AreRefusedOutsideChampSelect(string path, string body)
    {
        var r = await _host.Client(TestHost.Loopback).PostAsync(path, new StringContent(body, System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, r.StatusCode);
    }
}

/// <summary>Queue objects shaped like the client's (fields from real gameflow sessions: tools/logs/events-*.jsonl `gameData.queue`).</summary>
public class PlayableQueueTests
{
    [Fact]
    public void Only_open_non_custom_queues_with_the_clients_display_name_ranked_first()
    {
        var queues = System.Text.Json.Nodes.JsonNode.Parse("""
            [{"id":400,"category":"PvP","gameMode":"CLASSIC","name":"Normal","description":"Draft Pick","mapId":11,"isRanked":false,"isCustom":false,"queueAvailability":"Available","showPositionSelector":true},
             {"id":3110,"category":"Custom","gameMode":"CLASSIC","name":"SR Draft Pick Custom","description":"SR Draft Pick Custom","mapId":11,"isCustom":true,"queueAvailability":"Available"},
             {"id":2400,"category":"PvP","gameMode":"KIWI","name":"ARAM: Mayhem","description":"ARAM: Mayhem","mapId":12,"isRanked":false,"isCustom":false,"queueAvailability":"Available"},
             {"id":420,"category":"PvP","gameMode":"CLASSIC","name":"Ranked Solo/Duo","description":"","mapId":11,"isRanked":true,"isCustom":false,"queueAvailability":"Available","showPositionSelector":true},
             {"id":1700,"category":"PvP","gameMode":"CHERRY","name":"Arena","mapId":30,"isCustom":false,"queueAvailability":"PlatformDisabled"},
             {"id":870,"category":"VersusAi","gameMode":"CLASSIC","name":"Co-op vs. AI","mapId":11,"isCustom":false,"queueAvailability":"Available"}]
            """);
        var maps = System.Text.Json.Nodes.JsonNode.Parse("""[{"id":11,"name":"Summoner's Rift"},{"id":12,"name":"Howling Abyss"}]""");
        var list = Queues.Playable(queues, maps);
        Assert.Equal([420, 400, 2400, 870], list.Select(q => q.Id));
        Assert.Equal("Draft Pick", list[1].Name);
        Assert.Equal("Ranked Solo/Duo", list[0].Name); // empty description falls back to the name
        Assert.Equal("Howling Abyss", list[2].MapName);
        Assert.True(list[0].ShowPositions);
        Assert.Equal("", Queues.Playable(queues, null)[0].MapName);
    }

    [Fact]
    public void Queues_the_client_marks_not_visible_are_hidden_unless_all_is_asked_for()
    {
        // Real client data (2026-10): Clash, the tutorials and a blank entry are Available but isVisible false.
        var queues = System.Text.Json.Nodes.JsonNode.Parse("""
            [{"id":400,"category":"PvP","name":"Normal","mapId":11,"isCustom":false,"isVisible":true,"queueAvailability":"Available"},
             {"id":700,"category":"PvP","name":"Clash","mapId":11,"isCustom":false,"isVisible":false,"queueAvailability":"Available"},
             {"id":843,"category":"VersusAi","name":"","mapId":0,"isCustom":false,"isVisible":false,"queueAvailability":"Available"},
             {"id":1700,"category":"PvP","name":"Arena","mapId":30,"isCustom":false,"isVisible":true,"queueAvailability":"PlatformDisabled"}]
            """);
        Assert.Equal([400], Queues.Playable(queues, null).Select(q => q.Id));
        Assert.Equal([400, 700, 843, 1700], Queues.Playable(queues, null, all: true).Select(q => q.Id).Order());
    }
}
