using System.Net;
using System.Text.Json.Nodes;
using Glamour.Core;
using Glamour.Host;
using Glamour.Lcu;
using Microsoft.Extensions.DependencyInjection;

namespace Glamour.Tests;

public class LobbyMemberTests : IDisposable
{
    private readonly TestHost _host = new();
    private readonly ScriptedLcu _lcu = new();
    private readonly HttpClient _pc;

    public LobbyMemberTests()
    {
        _pc = _host.Client(TestHost.Loopback);
        _host.Services.GetRequiredService<SwappableLcuClient>().Current = _lcu;
    }

    public void Dispose() => _host.Dispose();

    private void Lobby(bool iAmLeader) =>
        _lcu.Gets["/lol-lobby/v2/lobby"] = JsonNode.Parse($$"""
            {"localMember":{"puuid":"me"},
             "members":[{"puuid":"me","summonerId":1,"isLeader":{{(iAmLeader ? "true" : "false")}}},
                        {"puuid":"pal","summonerId":2,"isLeader":{{(iAmLeader ? "false" : "true")}}},
                        {"puuid":"bot","summonerId":3,"isBot":true}]}
            """);

    [Theory]
    [InlineData("kick")]
    [InlineData("promote")]
    public async Task Leader_can_kick_or_promote_a_human_member(string verb)
    {
        Lobby(iAmLeader: true);
        var r = await _pc.PostAsync($"/api/lobby/members/pal/{verb}", null);
        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        var w = Assert.Single(_lcu.Writes);
        Assert.Equal(HttpMethod.Post, w.Method);
        Assert.Equal($"/lol-lobby/v3/lobby/members/pal/{verb}", w.Path);
    }

    [Fact]
    public async Task Non_leader_is_refused()
    {
        Lobby(iAmLeader: false);
        Assert.Equal(HttpStatusCode.BadRequest, (await _pc.PostAsync("/api/lobby/members/pal/kick", null)).StatusCode);
        Assert.Empty(_lcu.Writes);
    }

    [Theory]
    [InlineData("me", "kick")]
    [InlineData("bot", "promote")]
    [InlineData("stranger", "kick")]
    [InlineData("pal", "ban")]
    public async Task Self_bot_stranger_and_unknown_verb_are_refused(string puuid, string verb)
    {
        Lobby(iAmLeader: true);
        Assert.Equal(HttpStatusCode.BadRequest, (await _pc.PostAsync($"/api/lobby/members/{puuid}/{verb}", null)).StatusCode);
        Assert.Empty(_lcu.Writes);
    }

    [Fact]
    public async Task No_lobby_is_refused()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await _pc.PostAsync("/api/lobby/members/pal/kick", null)).StatusCode);
        Assert.Empty(_lcu.Writes);
    }
}
