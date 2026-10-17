using System.Text.Json;
using System.Text.Json.Nodes;
using Glamour.Core;
using Glamour.Lcu;
using Microsoft.Extensions.Logging.Abstractions;

namespace Glamour.Tests;

/// <summary>Parties from `teamParticipantId`, shaped like the live ranked dump (my duo and I shared 6, every stranger had their own).</summary>
public class PartyTests
{
    private readonly FakeLcu _lcu = new();
    private readonly EventHub _hub = new();
    private readonly Engine _engine;

    public PartyTests()
    {
        _engine = new Engine(_lcu, [], _hub, NullLogger<Engine>.Instance);
        _lcu.Gets["/lol-summoner/v1/current-summoner"] = JsonNode.Parse("""{"puuid":"me"}""");
        _lcu.Gets["/lol-champ-select/v1/session"] = JsonNode.Parse("""{"gameId":42}""");
        foreach (var (puuid, name) in new[] { ("me", "Tidewren"), ("duo", "Pellucid"), ("e1", "Red"), ("e2", "Blue") })
            _lcu.Gets[$"/lol-summoner/v2/summoners/puuid/{puuid}"] = JsonNode.Parse($$"""{"gameName":"{{name}}","tagLine":"EUW"}""");
    }

    private static JsonNode Session(string phase, long gameId = 42) => JsonNode.Parse($$$"""
        {"phase":"{{{phase}}}","gameData":{"gameId":{{{gameId}}},
          "teamOne":[{"puuid":"e1","championId":1,"teamParticipantId":3},{"puuid":"e2","championId":2,"teamParticipantId":3},{"puuid":"e3","championId":3,"teamParticipantId":4}],
          "teamTwo":[{"puuid":"me","championId":875,"teamParticipantId":6},{"puuid":"duo","championId":141,"teamParticipantId":6},{"puuid":"s1","championId":235,"teamParticipantId":8}]}}
        """)!;

    private async Task<List<JsonElement>> PartyEventsAsync(params JsonNode?[] sessions)
    {
        using var sub = _hub.Subscribe();
        foreach (var s in sessions) await _engine.HandleAsync(new LcuEvent("/lol-gameflow/v1/session", "Update", s), default);
        var found = new List<JsonElement>();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        try { await foreach (var e in sub.ReadAllAsync(cts.Token)) if (e.Type == "parties") found.Add(JsonSerializer.SerializeToElement(e.Data, JsonSerializerOptions.Web)); } catch (OperationCanceledException) { }
        return found;
    }

    [Fact]
    public void Champ_select_carries_each_party_members_queue_position_but_no_champion()
    {
        var s = JsonNode.Parse("""
            {"phase":"ChampSelect","gameData":{"gameId":7,
              "teamOne":[{"puuid":"e1","championId":1,"teamParticipantId":3,"selectedPosition":"JUNGLE"},{"puuid":"e2","championId":2,"teamParticipantId":3,"selectedPosition":"UTILITY"},{"puuid":"e3","teamParticipantId":4}],
              "teamTwo":[{"puuid":"me","championId":875,"teamParticipantId":6,"selectedPosition":"MIDDLE"},{"puuid":"duo","teamParticipantId":6,"selectedPosition":"NONE"}]}}
            """);
        var p = GameParties.From(s, "me")!.Parties;
        Assert.Equal(["MIDDLE", ""], p[0].Players.Select(x => x.Lane)); // mine first; an unknown position is blank
        Assert.Equal(["JUNGLE", "SUPPORT"], p[1].Players.Select(x => x.Lane));
        Assert.All(p.SelectMany(x => x.Players), x => Assert.Equal(0, x.ChampionId)); // the gameflow's champion is not trusted in champ select
    }

    [Fact]
    public async Task Players_sharing_a_team_participant_id_are_one_party_with_names()
    {
        var e = Assert.Single(await PartyEventsAsync(Session("GameStart")));
        var parties = e.GetProperty("parties");
        Assert.Equal(2, parties.GetArrayLength());
        var mine = parties[0];
        Assert.True(mine.GetProperty("ally").GetBoolean());
        Assert.Equal(["Tidewren#EUW", "Pellucid#EUW"], mine.GetProperty("players").EnumerateArray().Select(p => p.GetProperty("name").GetString()));
        Assert.True(mine.GetProperty("players")[0].GetProperty("isMe").GetBoolean());
        Assert.False(parties[1].GetProperty("ally").GetBoolean()); // teamOne is the enemy here
        Assert.Equal(2, parties[1].GetProperty("players").GetArrayLength());
    }

    [Fact]
    public async Task Champ_select_shows_the_groups_without_names_or_champions_then_the_game_names_them()
    {
        var events = await PartyEventsAsync(Session("ChampSelect"), Session("ChampSelect"), Session("GameStart"));
        Assert.Equal(2, events.Count); // once in champ select, once more at game start
        var picking = events[0].GetProperty("parties");
        Assert.Equal(2, picking.GetArrayLength());
        Assert.All(picking.EnumerateArray().SelectMany(p => p.GetProperty("players").EnumerateArray()), p =>
        {
            Assert.Equal("", p.GetProperty("name").GetString());
            Assert.Equal(0, p.GetProperty("championId").GetInt32());
        });
        Assert.Equal(["Tidewren#EUW", "Pellucid#EUW"], events[1].GetProperty("parties")[0].GetProperty("players").EnumerateArray().Select(p => p.GetProperty("name").GetString()));
    }

    [Fact]
    public async Task No_name_is_looked_up_in_champ_select()
    {
        await PartyEventsAsync(Session("ChampSelect"));
        Assert.DoesNotContain(_lcu.Reads, c => c.Contains("/lol-summoner/v2/summoners/puuid/"));
    }

    [Fact]
    public async Task One_game_is_read_once_and_a_new_phase_outside_a_game_clears_it()
    {
        Assert.Single(await PartyEventsAsync(Session("GameStart"), Session("InProgress")));
        using var sub = _hub.Subscribe();
        await _engine.HandleAsync(new LcuEvent("/lol-gameflow/v1/gameflow-phase", "Update", JsonValue.Create("Lobby")), default);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        JsonElement? cleared = null;
        try { await foreach (var e in sub.ReadAllAsync(cts.Token)) if (e.Type == "parties") cleared = JsonSerializer.SerializeToElement(e.Data, JsonSerializerOptions.Web); } catch (OperationCanceledException) { }
        Assert.Equal(0, cleared?.GetProperty("gameId").GetInt64());
    }

    [Fact]
    public async Task Champ_select_updates_look_again_when_the_gameflow_session_had_no_teams_yet()
    {
        await _engine.HandleAsync(new LcuEvent("/lol-gameflow/v1/gameflow-phase", "Update", JsonValue.Create("ChampSelect")), default);
        using var sub = _hub.Subscribe();
        var early = JsonNode.Parse("""{"phase":"ChampSelect","gameData":{"gameId":42,"teamOne":[],"teamTwo":[]}}""");
        await _engine.HandleAsync(new LcuEvent("/lol-gameflow/v1/session", "Update", early), default);
        _lcu.Gets["/lol-gameflow/v1/session"] = Session("ChampSelect");
        await _engine.HandleAsync(new LcuEvent("/lol-champ-select/v1/session", "Update", JsonNode.Parse("{}")), default);
        var found = new List<JsonElement>();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
        try { await foreach (var e in sub.ReadAllAsync(cts.Token)) if (e.Type == "parties") found.Add(JsonSerializer.SerializeToElement(e.Data, JsonSerializerOptions.Web)); } catch (OperationCanceledException) { }
        var last = found[^1];
        Assert.Equal(2, last.GetProperty("parties").GetArrayLength());
    }

    [Fact]
    public async Task Champ_select_ignores_the_previous_game_the_gameflow_session_still_holds_then_reads_its_own()
    {
        await _engine.HandleAsync(new LcuEvent("/lol-gameflow/v1/gameflow-phase", "Update", JsonValue.Create("ChampSelect")), default);
        _lcu.Gets["/lol-gameflow/v1/session"] = Session("ChampSelect", gameId: 41); // last game's teams (LIVE: every champ select start)
        Assert.Empty(await PartyEventsAsync(Session("ChampSelect", gameId: 41)));
        _lcu.Gets["/lol-gameflow/v1/session"] = Session("ChampSelect"); // its own game, at GAME_STARTING
        Assert.Single(await PartyEventsAsync(Session("ChampSelect")));
    }

    [Fact]
    public async Task Back_in_the_lobby_the_last_games_leftover_session_is_not_read()
    {
        var lobby = JsonNode.Parse("""{"phase":"Lobby","gameData":{"gameId":42,"teamOne":[],"teamTwo":[]}}""");
        Assert.Empty(await PartyEventsAsync(lobby, lobby));
        Assert.DoesNotContain(_lcu.Reads, c => c.Contains("/lol-summoner/v1/current-summoner"));
    }

    [Fact]
    public void A_missing_or_zero_id_is_solo()
    {
        var s = JsonNode.Parse("""{"phase":"InProgress","gameData":{"gameId":1,"teamOne":[{"puuid":"a","teamParticipantId":0},{"puuid":"b","teamParticipantId":0},{"puuid":"c"}],"teamTwo":[]}}""");
        Assert.Empty(GameParties.From(s, "a")!.Parties);
    }

    [Fact]
    public void Off_summoners_rift_no_parties_in_champ_select_and_no_positions_in_game()
    {
        var s = JsonNode.Parse("""
            {"phase":"InProgress","gameData":{"gameId":7,"queue":{"id":2400,"gameMode":"KIWI","mapId":12},
              "teamOne":[{"puuid":"me","teamParticipantId":3,"selectedPosition":"JUNGLE"},{"puuid":"duo","teamParticipantId":3,"selectedPosition":"UTILITY"}],
              "teamTwo":[{"puuid":"x","teamParticipantId":6,"selectedPosition":"MIDDLE"},{"puuid":"y","teamParticipantId":6,"selectedPosition":"TOP"}]}}
            """);
        var p = GameParties.From(s, "me")!.Parties;
        Assert.Equal(2, p.Count);
        Assert.All(p.SelectMany(x => x.Players), x => Assert.Equal("", x.Lane));
        s["phase"] = "ChampSelect";
        Assert.Null(GameParties.From(s, "me")); // the groups there are not the parties (LIVE, ARAM)
    }

    [Theory]
    [InlineData("\"isCustomGame\":true")]
    [InlineData("\"queue\":{\"id\":3100}")]
    public void Custom_games_have_no_parties(string marker)
    {
        var s = JsonNode.Parse("{\"phase\":\"ChampSelect\",\"gameData\":{\"gameId\":1," + marker + ",\"teamOne\":[{\"puuid\":\"a\",\"teamParticipantId\":2},{\"puuid\":\"b\",\"teamParticipantId\":2}],\"teamTwo\":[{\"puuid\":\"x\",\"teamParticipantId\":3},{\"puuid\":\"y\",\"teamParticipantId\":3}]}}");
        Assert.Empty(GameParties.From(s, "a")!.Parties);
    }
}
