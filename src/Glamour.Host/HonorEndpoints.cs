using Glamour.Core;
using Glamour.Lcu;

namespace Glamour.Host;

public sealed record HonorChoice(string Puuid, string HonorType);

/// <summary>One friend: Riot ID (Name#Tag, as auto-honor lists them) and whether they are online (any availability but offline and mobile).</summary>
public sealed record Friend(string RiotId, string Puuid, bool Online);

public static class FriendList
{
    public static List<Friend> From(System.Text.Json.Nodes.JsonArray list) =>
        list.Select(f => (name: f?["gameName"]?.GetValue<string>() ?? "", tag: f?["gameTag"]?.GetValue<string>() ?? "", puuid: f?["puuid"]?.GetValue<string>() ?? "",
                availability: f?["availability"]?.GetValue<string>() ?? "offline"))
            .Where(f => f.name.Length > 0 && f.tag.Length > 0)
            .Select(f => new Friend($"{f.name}#{f.tag}", f.puuid, f.availability is not ("offline" or "mobile")))
            .OrderByDescending(f => f.Online).ThenBy(f => f.RiotId, StringComparer.OrdinalIgnoreCase)
            .ToList();
}

/// <summary>
/// Manual honoring from the UI: read the ballot, stage a vote for one player, submit. Every call is a tap by the user, and a vote can only go to
/// someone on the current ballot, never more than the votes the ballot says I have (INSIGHTS "Post-game").
/// </summary>
public static class HonorEndpoints
{
    public static void MapHonor(this RouteGroupBuilder api)
    {
        api.MapGet("/honor/ballot", async (ILcuClient c, CancellationToken ct) => Results.Ok(await ReadAsync(c, ct)));

        // My League friends by Riot ID, for picking who auto-honor votes for (read only). Online first, then by name.
        api.MapGet("/friends", async (ILcuClient c, CancellationToken ct) =>
            await c.GetAsync("/lol-chat/v1/friends", ct) is { Status: 200, Body: System.Text.Json.Nodes.JsonArray list }
                ? Results.Ok(FriendList.From(list))
                : Results.Json(new { error = "The League client is not running." }, statusCode: 503));

        api.MapPost("/honor/vote", async (HonorChoice b, ILcuClient c, IActionGateway g, CancellationToken ct) =>
        {
            var type = b.HonorType?.ToUpperInvariant() ?? "";
            if (!HonorBallotView.Types.Contains(type)) return Results.BadRequest(new { error = "honorType is HEART, COOL or SHOTCALLER" });
            var ballot = await ReadAsync(c, ct);
            if (ballot.GameId == 0) return Results.BadRequest(new { error = "There is no honor ballot right now." });
            if (ballot.Candidates.FirstOrDefault(p => p.Puuid == b.Puuid) is not { } who) return Results.BadRequest(new { error = "That player is not on the ballot." });
            // The client toggles a vote it already holds, so a staged player may always be tapped; a new one needs a vote left.
            if (who.Honor is null && ballot.Staged >= ballot.Votes) return Results.BadRequest(new { error = "No votes left." });
            return Relay(await g.HonorAsync(b.Puuid, type, ct));
        });

        api.MapPost("/honor/ballot", async (ILcuClient c, IActionGateway g, CancellationToken ct) =>
        {
            var ballot = await ReadAsync(c, ct);
            if (ballot.GameId == 0) return Results.BadRequest(new { error = "There is no honor ballot right now." });
            return ballot.Staged == 0 ? Results.BadRequest(new { error = "Choose someone to honor first." }) : Relay(await g.SubmitBallotAsync(ct));
        });

        // Closes the post-game honor screen in the client (LIVE: skip-pre-end-of-game while honor-vote is showing, INSIGHTS "Post-game"). A tap by the user.
        api.MapPost("/honor/skip", async (Engine e, IActionGateway g, CancellationToken ct) =>
            e.Phase == "PreEndOfGame" ? Relay(await g.SkipHonorAsync(ct)) : Results.Conflict(new { error = "The honor screen is not open." }));
    }

    private static async Task<HonorBallotView> ReadAsync(ILcuClient c, CancellationToken ct)
    {
        if (await c.GetAsync("/lol-honor-v2/v1/ballot", ct) is not { Status: 200, Body: { } ballot }) return HonorBallotView.Empty;
        var friends = await c.GetAsync("/lol-chat/v1/friends", ct);
        return HonorBallotView.From(ballot, friends.Status == 200 ? friends.Body : null);
    }

    private static IResult Relay(LcuResponse r) => Results.Json(r.Body, statusCode: r.Status);
}
