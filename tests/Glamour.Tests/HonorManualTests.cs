using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Glamour.Core;
using Glamour.Host;
using Glamour.Lcu;
using Microsoft.Extensions.DependencyInjection;

namespace Glamour.Tests;

public class HonorManualTests : IDisposable
{
    private const string Ally = "ally-1", Friend = "friend-1", Foe = "foe-1";
    private readonly TestHost _host = new();
    private readonly ScriptedLcu _lcu = new();
    private readonly HttpClient _pc;

    public HonorManualTests()
    {
        _pc = _host.Client(TestHost.Loopback);
        _host.Services.GetRequiredService<SwappableLcuClient>().Current = _lcu;
    }

    public void Dispose() => _host.Dispose();

    private void Ballot(int votes = 2, string honored = "[]", long gameId = 99) =>
        _lcu.Gets["/lol-honor-v2/v1/ballot"] = JsonNode.Parse($$"""
            {"gameId":{{gameId}},"votePool":{"votes":{{votes}}},"honoredPlayers":{{honored}},
             "eligibleAllies":[{"puuid":"{{Ally}}","summonerId":1,"summonerName":"Ally","championId":48},{"puuid":"{{Friend}}","summonerId":2,"summonerName":"fr","championId":32}],
             "eligibleOpponents":[{"puuid":"{{Foe}}","summonerId":3,"summonerName":"Foe","championId":266}]}
            """);

    private void Friends() => _lcu.Gets["/lol-chat/v1/friends"] = JsonNode.Parse($$"""[{"gameName":"Juniperra","gameTag":"EUNE","puuid":"{{Friend}}"}]""");

    [Fact]
    public async Task Ballot_lists_all_candidates_with_votes_friends_and_staged_honors()
    {
        Ballot(honored: $$"""[{"honorType":"cool","recipientPuuid":"{{Foe}}"}]""");
        Friends();
        var b = await _pc.GetFromJsonAsync<HonorBallotView>("/api/honor/ballot");
        Assert.Equal(99, b!.GameId);
        Assert.Equal(2, b.Votes);
        Assert.Equal(1, b.Staged);
        Assert.Equal(3, b.Candidates.Count);
        Assert.Contains(b.Candidates, c => c.Puuid == Friend && c.IsFriend && c.Name == "Juniperra#EUNE" && c.Ally);
        Assert.Contains(b.Candidates, c => c.Puuid == Foe && !c.Ally && c.Honor == "COOL");
    }

    [Fact]
    public async Task No_ballot_is_an_empty_view()
    {
        var b = await _pc.GetFromJsonAsync<HonorBallotView>("/api/honor/ballot");
        Assert.Equal(0, b!.GameId);
        Assert.Empty(b.Candidates);
    }

    [Theory]
    [InlineData("heart", "HEART")]
    [InlineData("COOL", "COOL")]
    [InlineData("shotcaller", "SHOTCALLER")]
    public async Task Vote_stages_the_chosen_type_for_a_player_on_the_ballot(string sent, string expected)
    {
        Ballot();
        var r = await _pc.PostAsJsonAsync("/api/honor/vote", new { puuid = Foe, honorType = sent });
        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        var w = Assert.Single(_lcu.Writes);
        Assert.Equal("/lol-honor/v1/honor", w.Path);
        Assert.Equal($$"""{"honorType":"{{expected}}","recipientPuuid":"{{Foe}}"}""", w.Body);
    }

    [Theory]
    [InlineData("stranger", "COOL")]
    [InlineData(Ally, "OPT_OUT")]
    [InlineData(Ally, "")]
    public async Task Vote_refuses_someone_off_the_ballot_or_an_unknown_type(string puuid, string type)
    {
        Ballot();
        var r = await _pc.PostAsJsonAsync("/api/honor/vote", new { puuid, honorType = type });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Empty(_lcu.Writes);
    }

    [Fact]
    public async Task Vote_refuses_a_new_player_when_no_votes_are_left_but_lets_a_staged_one_be_tapped_again()
    {
        Ballot(votes: 1, honored: $$"""[{"honorType":"COOL","recipientPuuid":"{{Ally}}"}]""");
        Assert.Equal(HttpStatusCode.BadRequest, (await _pc.PostAsJsonAsync("/api/honor/vote", new { puuid = Foe, honorType = "COOL" })).StatusCode);
        Assert.Empty(_lcu.Writes);
        Assert.Equal(HttpStatusCode.NoContent, (await _pc.PostAsJsonAsync("/api/honor/vote", new { puuid = Ally, honorType = "COOL" })).StatusCode);
        Assert.Single(_lcu.Writes);
    }

    [Fact]
    public async Task Vote_with_no_ballot_is_refused()
    {
        Ballot(gameId: 0);
        Assert.Equal(HttpStatusCode.BadRequest, (await _pc.PostAsJsonAsync("/api/honor/vote", new { puuid = Ally, honorType = "COOL" })).StatusCode);
        Assert.Empty(_lcu.Writes);
    }

    [Fact]
    public async Task Submit_needs_a_staged_vote_and_then_posts_the_ballot()
    {
        Ballot();
        Assert.Equal(HttpStatusCode.BadRequest, (await _pc.PostAsync("/api/honor/ballot", null)).StatusCode);
        Assert.Empty(_lcu.Writes);

        Ballot(honored: $$"""[{"honorType":"COOL","recipientPuuid":"{{Ally}}"}]""");
        Assert.Equal(HttpStatusCode.NoContent, (await _pc.PostAsync("/api/honor/ballot", null)).StatusCode);
        Assert.Equal("/lol-honor/v1/ballot", Assert.Single(_lcu.Writes).Path);
    }
}
