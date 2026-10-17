using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Glamour.Host;
using Glamour.Lcu;
using Microsoft.Extensions.DependencyInjection;

namespace Glamour.Tests;

public class ChallengeTokenTests : IDisposable
{
    private readonly TestHost _host = new();
    private readonly ScriptedLcu _lcu = new();
    private readonly HttpClient _pc;

    public ChallengeTokenTests()
    {
        _pc = _host.Client(TestHost.Loopback);
        _host.Services.GetRequiredService<SwappableLcuClient>().Current = _lcu;
        _lcu.Gets["/lol-challenges/v1/summary-player-data/local-player"] = JsonNode.Parse("""
            {"bannerId":"4","title":{"itemId":20100304},"topChallenges":[{"id":402409,"name":"Get Paid, and Get Out"}]}
            """);
        _lcu.Gets["/lol-challenges/v1/challenges/local-player"] = JsonNode.Parse("""
            {"402409":{"id":402409,"name":"Get Paid, and Get Out","currentLevel":"GRANDMASTER","descriptionShort":"d","category":"TEAMWORK","levelToIconPath":{"GRANDMASTER":"/lol-game-data/assets/x/GRANDMASTER.png"}},
             "401102":{"id":401102,"name":"Wise Master","currentLevel":"DIAMOND","levelToIconPath":{"DIAMOND":"/lol-game-data/assets/y/DIAMOND.png"}},
             "5":{"id":5,"name":"Unearned","currentLevel":"NONE","levelToIconPath":{}}}
            """);
    }

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task Lists_equipped_and_earned_challenges_only()
    {
        var j = await _pc.GetFromJsonAsync<JsonNode>("/api/challenges/tokens");
        Assert.Equal(402409, j!["equipped"]![0]!.GetValue<int>());
        var names = j["owned"]!.AsArray().Select(o => o!["name"]!.ToString()).ToList();
        Assert.Equal(["Get Paid, and Get Out", "Wise Master"], names);
        Assert.Equal("/lol-game-data/assets/y/DIAMOND.png", j["owned"]![1]!["icon"]!.ToString());
    }

    [Fact]
    public async Task Saving_three_of_the_same_keeps_banner_accent_and_title()
    {
        var r = await _pc.PutAsJsonAsync("/api/challenges/tokens", new { challengeIds = new[] { 401102, 401102, 401102 } });
        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        var w = Assert.Single(_lcu.Writes);
        Assert.Equal("/lol-challenges/v1/update-player-preferences", w.Path);
        var b = JsonNode.Parse(w.Body!)!;
        Assert.Equal("4", b["bannerAccent"]!.ToString());
        Assert.Equal("20100304", b["title"]!.ToString());
        Assert.Equal([401102, 401102, 401102], b["challengeIds"]!.AsArray().Select(x => x!.GetValue<int>()).ToArray());
    }

    [Fact]
    public async Task Clearing_sends_an_empty_list()
    {
        Assert.Equal(HttpStatusCode.NoContent, (await _pc.PutAsJsonAsync("/api/challenges/tokens", new { challengeIds = Array.Empty<int>() })).StatusCode);
        Assert.Empty(JsonNode.Parse(Assert.Single(_lcu.Writes).Body!)!["challengeIds"]!.AsArray());
    }

    [Theory]
    [InlineData("[5]")]
    [InlineData("[999]")]
    [InlineData("[401102,401102,401102,401102]")]
    public async Task Unearned_unknown_or_too_many_are_refused(string ids)
    {
        var r = await _pc.PutAsync("/api/challenges/tokens", new StringContent("{\"challengeIds\":" + ids + "}", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Empty(_lcu.Writes);
    }
}
