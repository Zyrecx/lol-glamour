using Glamour.Core;
using Glamour.Lcu;

namespace Glamour.Host;

/// <summary>
/// Kick or promote another lobby member from the UI. Both touch another player, so each is a deliberate tap (the UI asks twice) and the host
/// checks against the live lobby: I must be the leader, and the target must be a different human in it.
/// </summary>
public static class LobbyMemberEndpoints
{
    public static void MapLobbyMembers(this RouteGroupBuilder api)
    {
        api.MapPost("/lobby/members/{puuid}/{verb}", async (string puuid, string verb, ILcuClient c, IActionGateway g, CancellationToken ct) =>
        {
            if (verb is not ("kick" or "promote")) return Results.BadRequest(new { error = "verb is kick or promote" });
            var roster = LobbyMember.FromLobby((await c.GetAsync("/lol-lobby/v2/lobby", ct)) is { Status: 200 } r ? r.Body : null);
            if (roster.Count == 0) return Results.BadRequest(new { error = "You are not in a lobby." });
            if (!roster.Any(m => m.IsMe && m.IsLeader)) return Results.BadRequest(new { error = "Only the party leader can do that." });
            if (roster.FirstOrDefault(m => m.Puuid == puuid) is not { } target) return Results.BadRequest(new { error = "That player is not in your lobby." });
            if (target.IsMe) return Results.BadRequest(new { error = "That is you." });
            if (target.IsBot) return Results.BadRequest(new { error = "That is a bot." });
            var res = verb == "kick" ? await g.KickMemberAsync(puuid, ct) : await g.PromoteMemberAsync(puuid, ct);
            return Results.Json(res.Body, statusCode: res.Status);
        });
    }
}
