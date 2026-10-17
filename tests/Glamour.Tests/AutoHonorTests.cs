using System.Text.Json.Nodes;
using Glamour.Core;
using Microsoft.Extensions.Logging.Abstractions;

namespace Glamour.Tests;

public class AutoHonorTests
{
    private const string Juniperra = "6c1f0d2e-7a3b-5c49-9e81-2f4b6d8a0c11", Stranger = "3d9e2b71-4c6a-5f08-a1b3-7e5c9d2f4a66";
    private readonly FakeLcu _lcu = new();
    private readonly GlamourSettings _settings = new() { AutoHonorEnabled = true, AutoHonorFriends = ["Juniperra#EUNE"] };
    private const string Pellucid = "pellucid-puuid";
    private int _delays;

    private AutoHonorFeature Feature(Action? onDelay = null)
    {
        var gateway = new ActionGateway(_lcu, _settings, NullLogger<ActionGateway>.Instance);
        return new AutoHonorFeature(_lcu, gateway, _settings, NullLogger<AutoHonorFeature>.Instance, (_, _) => { _delays++; onDelay?.Invoke(); return Task.CompletedTask; });
    }

    private static JsonNode Ballot(long gameId, string honored = "[]", params string[] allies) => Ballot(gameId, 1, honored, allies);

    private static JsonNode Ballot(long gameId, int votes, string honored, params string[] allies)
    {
        var list = string.Join(",", allies.Select(p => "{\"puuid\":\"" + p + "\",\"summonerName\":\"x\"}"));
        return JsonNode.Parse("{\"gameId\":" + gameId + ",\"eligibleAllies\":[" + list + "],\"honoredPlayers\":" + honored + ",\"votePool\":{\"votes\":" + votes + "}}")!;
    }

    private void Friends() => _lcu.Gets["/lol-chat/v1/friends"] = JsonNode.Parse($$"""[{"gameName":"Juniperra","gameTag":"EUNE","puuid":"{{Juniperra}}"},{"gameName":"Other","gameTag":"1","puuid":"o"}]""");

    private async Task RunAsync(AutoHonorFeature f, string phase = "PreEndOfGame")
    {
        await f.OnPhaseAsync(phase, default);
        await f.Idle;
    }

    private IEnumerable<(string Path, string? Body)> Writes() => _lcu.Writes.Select(w => (w.Path, w.Body));

    [Fact]
    public async Task VotesOnceCoolThenSubmits()
    {
        Friends();
        _lcu.Gets["/lol-honor-v2/v1/ballot"] = Ballot(7000000102, "[]", Stranger, Juniperra);
        await RunAsync(Feature());
        Assert.Equal(
            [("/lol-honor/v1/honor", $$"""{"honorType":"COOL","recipientPuuid":"{{Juniperra}}"}"""), ("/lol-honor/v1/ballot", null)],
            Writes());
    }

    [Fact]
    public async Task VotesEveryListedFriendOnTheBallotInListOrderUpToTheVotesIHave()
    {
        _settings.AutoHonorFriends = ["Juniperra#EUNE", "Pellucid#3434"];
        _lcu.Gets["/lol-chat/v1/friends"] = JsonNode.Parse($$"""[{"gameName":"Juniperra","gameTag":"EUNE","puuid":"{{Juniperra}}"},{"gameName":"Pellucid","gameTag":"3434","puuid":"{{Pellucid}}"}]""");
        _lcu.Gets["/lol-honor-v2/v1/ballot"] = Ballot(7, 3, "[]", Pellucid, Juniperra);
        await RunAsync(Feature());
        Assert.Equal(
            [("/lol-honor/v1/honor", $$"""{"honorType":"COOL","recipientPuuid":"{{Juniperra}}"}"""), ("/lol-honor/v1/honor", $$"""{"honorType":"COOL","recipientPuuid":"{{Pellucid}}"}"""), ("/lol-honor/v1/ballot", null)],
            Writes());
    }

    [Fact]
    public async Task OneVoteLeftMeansOnlyTheFirstListedFriendGetsIt()
    {
        _settings.AutoHonorFriends = ["Juniperra#EUNE", "Pellucid#3434"];
        _lcu.Gets["/lol-chat/v1/friends"] = JsonNode.Parse($$"""[{"gameName":"Juniperra","gameTag":"EUNE","puuid":"{{Juniperra}}"},{"gameName":"Pellucid","gameTag":"3434","puuid":"{{Pellucid}}"}]""");
        _lcu.Gets["/lol-honor-v2/v1/ballot"] = Ballot(7, 1, "[]", Pellucid, Juniperra);
        await RunAsync(Feature());
        Assert.Equal(2, _lcu.Writes.Count); // one vote, one submit
        Assert.Contains(Juniperra, _lcu.Writes[0].Body);
    }

    [Fact]
    public async Task Stay_stages_the_friends_and_leaves_the_screen_open_while_votes_are_left()
    {
        _settings.HonorStay = true;
        _settings.SkipHonorScreen = true; // a vote was cast: no skip either
        Friends();
        _lcu.Gets["/lol-honor-v2/v1/ballot"] = Ballot(7, 4, "[]", Stranger, Juniperra);
        await RunAsync(Feature());
        Assert.Equal([("/lol-honor/v1/honor", $$"""{"honorType":"COOL","recipientPuuid":"{{Juniperra}}"}""")], Writes()); // no submit, no skip
    }

    [Fact]
    public async Task Stay_still_submits_when_the_friends_use_up_every_vote()
    {
        _settings.HonorStay = true;
        Friends();
        _lcu.Gets["/lol-honor-v2/v1/ballot"] = Ballot(7, 1, "[]", Stranger, Juniperra);
        await RunAsync(Feature());
        Assert.Equal("/lol-honor/v1/ballot", _lcu.Writes[^1].Path);
    }

    [Fact]
    public async Task WaitsForTheBallotToFill()
    {
        Friends();
        _lcu.Gets["/lol-honor-v2/v1/ballot"] = Ballot(0);
        await RunAsync(Feature(() => _lcu.Gets["/lol-honor-v2/v1/ballot"] = Ballot(7, "[]", Juniperra)));
        Assert.Equal(2, _lcu.Writes.Count);
        Assert.Equal(1, _delays);
    }

    [Fact]
    public async Task GivesUpWhenTheBallotStaysEmpty()
    {
        Friends();
        _lcu.Gets["/lol-honor-v2/v1/ballot"] = Ballot(0);
        await RunAsync(Feature());
        Assert.Empty(_lcu.Writes);
    }

    [Fact]
    public async Task NoListedFriendOnBallot_VotesForNobody()
    {
        Friends();
        _lcu.Gets["/lol-honor-v2/v1/ballot"] = Ballot(7, "[]", Stranger);
        await RunAsync(Feature());
        Assert.Empty(_lcu.Writes);
    }

    [Fact]
    public async Task MatchesByPuuidNotByName()
    {
        // A stranger named like the friend is on the ballot, the real friend is not.
        Friends();
        _lcu.Gets["/lol-honor-v2/v1/ballot"] = JsonNode.Parse("""{"gameId":7,"eligibleAllies":[{"puuid":"zzz","summonerName":"Juniperra"}],"honoredPlayers":[]}""");
        await RunAsync(Feature());
        Assert.Empty(_lcu.Writes);
    }

    [Fact]
    public async Task UserAlreadyVoted_LeavesTheBallotAlone()
    {
        Friends();
        _lcu.Gets["/lol-honor-v2/v1/ballot"] = Ballot(7, $$"""[{"honorType":"HEART","recipientPuuid":"{{Stranger}}"}]""", Stranger, Juniperra);
        await RunAsync(Feature());
        Assert.Empty(_lcu.Writes);
    }

    [Fact]
    public async Task SameGameTwice_VotesOnce()
    {
        Friends();
        _lcu.Gets["/lol-honor-v2/v1/ballot"] = Ballot(7, "[]", Juniperra);
        var f = Feature();
        await RunAsync(f);
        await RunAsync(f); // a reconnect resync re-fires the phase
        Assert.Equal(2, _lcu.Writes.Count);
    }

    [Theory]
    [InlineData(false, "PreEndOfGame")]
    [InlineData(true, "EndOfGame")]
    public async Task OffOrWrongPhase_DoesNothing(bool enabled, string phase)
    {
        Friends();
        _settings.AutoHonorEnabled = enabled;
        _lcu.Gets["/lol-honor-v2/v1/ballot"] = Ballot(7, "[]", Juniperra);
        await RunAsync(Feature(), phase);
        Assert.Empty(_lcu.Writes);
    }

    private const string SkipPath = "/lol-pre-end-of-game/v1/skip-pre-end-of-game";

    [Fact]
    public async Task SkipOnly_SkipsAtOnceWithoutTouchingTheBallot()
    {
        _settings.AutoHonorEnabled = false;
        _settings.SkipHonorScreen = true;
        await RunAsync(Feature());
        Assert.Equal([SkipPath], Writes().Select(w => w.Path));
    }

    [Fact]
    public async Task SkipOff_NeverSkips()
    {
        Friends();
        _lcu.Gets["/lol-honor-v2/v1/ballot"] = Ballot(7, "[]", Stranger);
        await RunAsync(Feature());
        Assert.Empty(_lcu.Writes);
    }

    [Fact]
    public async Task SkipOn_VoteCast_DoesNotSkip()
    {
        Friends();
        _settings.SkipHonorScreen = true;
        _lcu.Gets["/lol-honor-v2/v1/ballot"] = Ballot(7, "[]", Juniperra);
        await RunAsync(Feature());
        Assert.DoesNotContain(SkipPath, Writes().Select(w => w.Path));
        Assert.Equal(2, _lcu.Writes.Count);
    }

    [Fact]
    public async Task SkipOn_NoListedFriendOnBallot_Skips()
    {
        Friends();
        _settings.SkipHonorScreen = true;
        _lcu.Gets["/lol-honor-v2/v1/ballot"] = Ballot(7, "[]", Stranger);
        await RunAsync(Feature());
        Assert.Equal([SkipPath], Writes().Select(w => w.Path));
    }

    [Fact]
    public async Task SkipOn_UserAlreadyVoted_DoesNotSkip()
    {
        Friends();
        _settings.SkipHonorScreen = true;
        _lcu.Gets["/lol-honor-v2/v1/ballot"] = Ballot(7, $$"""[{"honorType":"HEART","recipientPuuid":"{{Stranger}}"}]""", Stranger, Juniperra);
        await RunAsync(Feature());
        Assert.Empty(_lcu.Writes);
    }

    [Fact]
    public async Task SkipOn_BallotNeverFills_Skips()
    {
        Friends();
        _settings.SkipHonorScreen = true;
        _lcu.Gets["/lol-honor-v2/v1/ballot"] = Ballot(0);
        await RunAsync(Feature());
        Assert.Equal([SkipPath], Writes().Select(w => w.Path));
    }

    [Fact]
    public async Task SkipOnly_WrongPhase_DoesNothing()
    {
        _settings.AutoHonorEnabled = false;
        _settings.SkipHonorScreen = true;
        await RunAsync(Feature(), "EndOfGame");
        Assert.Empty(_lcu.Writes);
    }

    [Fact]
    public async Task DryRun_SendsNothing()
    {
        Friends();
        _settings.DryRun = true;
        _lcu.Gets["/lol-honor-v2/v1/ballot"] = Ballot(7, "[]", Juniperra);
        await RunAsync(Feature());
        Assert.Empty(_lcu.Writes);
    }
}

public class SwapReplyTests
{
    [Theory]
    [InlineData("position", "accept", "/lol-champ-select/v1/session/position-swaps/3/accept")]
    [InlineData("pick-order", "decline", "/lol-champ-select/v1/session/pick-order-swaps/3/decline")]
    [InlineData("position", "request", "/lol-champ-select/v1/session/position-swaps/3/request")]
    [InlineData("trade", "cancel", "/lol-champ-select/v1/session/champion-swaps/3/cancel")]
    public async Task Swap_UsesTheKindsPath(string kind, string verb, string path)
    {
        var lcu = new FakeLcu();
        var gateway = new ActionGateway(lcu, new GlamourSettings(), NullLogger<ActionGateway>.Instance);
        await gateway.SwapAsync(kind, 3, verb);
        Assert.Equal([(HttpMethod.Post, path)], lcu.Writes.Select(w => (w.Method, w.Path)));
    }
}
