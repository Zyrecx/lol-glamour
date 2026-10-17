using System.Text.Json.Nodes;
using Glamour.Lcu;

namespace Glamour.Host;

public sealed record TokenChoice(List<int> ChallengeIds);

/// <summary>
/// The three challenge tokens shown on the profile. The client keeps repeats (INSIGHTS "Rank, status, badges"), so the same challenge may fill all
/// three slots. Its save call replaces the whole selection: sending only the ids blanks the banner accent and the title, so both are read back
/// from the client and sent along.
/// </summary>
public static class ChallengeEndpoints
{
    public const int Slots = 3;

    public static void MapChallenges(this RouteGroupBuilder api)
    {
        api.MapGet("/challenges/tokens", async (ILcuClient c, CancellationToken ct) =>
        {
            var summary = await c.GetAsync("/lol-challenges/v1/summary-player-data/local-player", ct);
            var all = await c.GetAsync("/lol-challenges/v1/challenges/local-player", ct);
            if (!summary.IsSuccess || !all.IsSuccess) return Results.Json(new { error = "The League client is not available." }, statusCode: 503);
            return Results.Ok(new { equipped = Equipped(summary.Body), owned = Owned(all.Body) });
        });

        api.MapPut("/challenges/tokens", async (TokenChoice b, ILcuClient c, CancellationToken ct) =>
        {
            if (b.ChallengeIds is not { Count: <= Slots }) return Results.BadRequest(new { error = $"At most {Slots} tokens." });
            var summary = await c.GetAsync("/lol-challenges/v1/summary-player-data/local-player", ct);
            var all = await c.GetAsync("/lol-challenges/v1/challenges/local-player", ct);
            if (!summary.IsSuccess || !all.IsSuccess) return Results.Json(new { error = "The League client is not available." }, statusCode: 503);
            var owned = Owned(all.Body).Select(o => o.Id).ToHashSet();
            if (b.ChallengeIds.Any(id => !owned.Contains(id))) return Results.BadRequest(new { error = "You have not earned one of those challenges." });
            var res = await c.SendAsync(HttpMethod.Post, "/lol-challenges/v1/update-player-preferences", SavePayload(summary.Body, b.ChallengeIds), ct);
            return res.IsSuccess ? Results.NoContent() : Results.Json(res.Body, statusCode: res.Status);
        });
    }

    /// <summary>The body the client expects: the new ids plus the current banner accent and title, so nothing else on the profile changes.</summary>
    public static object SavePayload(JsonNode? summary, IReadOnlyList<int> ids) => new
    {
        bannerAccent = summary?["bannerId"]?.ToString() ?? "",
        challengeIds = ids,
        title = (summary?["title"]?["itemId"]?.ToString()) is { Length: > 0 } t ? t : "-1",
    };

    public static List<int> Equipped(JsonNode? summary) =>
        (summary?["topChallenges"] as JsonArray ?? []).Select(t => t?["id"]?.GetValue<int>() ?? 0).Where(id => id > 0).ToList();

    public sealed record OwnedChallenge(int Id, string Name, string Level, string Description, string Icon, string Category);

    /// <summary>Challenges with an earned level, the ones that have a token to show. Sorted by name.</summary>
    public static List<OwnedChallenge> Owned(JsonNode? all) =>
        (all as JsonObject ?? []).Select(kv => kv.Value as JsonObject).OfType<JsonObject>()
            .Where(o => o["id"] is not null && (o["currentLevel"]?.ToString() ?? "NONE") is not ("NONE" or ""))
            .Select(o =>
            {
                var level = o["currentLevel"]!.ToString();
                var icon = o["levelToIconPath"]?[level]?.ToString() ?? o["iconPath"]?.ToString() ?? "";
                return new OwnedChallenge(o["id"]!.GetValue<int>(), o["name"]?.ToString() ?? "", level, o["descriptionShort"]?.ToString() ?? "", icon, o["category"]?.ToString() ?? "");
            })
            .Where(o => o.Icon.Length > 0)
            .OrderBy(o => o.Name, StringComparer.OrdinalIgnoreCase).ToList();
}
