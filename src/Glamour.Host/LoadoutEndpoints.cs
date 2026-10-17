using System.Text.Json.Nodes;
using Glamour.Core;
using Glamour.Lcu;

namespace Glamour.Host;

/// <summary>
/// The id of the last rune page Glamour created, so the next apply replaces that one and never touches any other page, and the page as the client
/// answered the create (<see cref="Page"/>), what <see cref="RunePageGuard"/> keeps.
/// </summary>
public sealed class RunePageTracker
{
    public int? LastCreated { get; set; }
    public JsonNode? Page { get; private set; }

    /// <summary>Records a create's answer; false when it carries no page id.</summary>
    public bool Created(JsonNode? page)
    {
        if (page?["id"]?.GetValue<int>() is not { } id) return false;
        (LastCreated, Page) = (id, page.DeepClone());
        return true;
    }
}

public sealed record SkinChoice(int SkinId);
public sealed record RuneChoice(int ChampionId, int Index, string? Position = null);

public static class LoadoutEndpoints
{
    private const string AssetPrefix = "/lol-game-data/assets/";
    private static readonly string[] ImageTypes = [".png", ".jpg", ".jpeg", ".webp"];

    public static void MapLoadout(this RouteGroupBuilder api)
    {
        // Only game-data images, never an open relay to the client: fixed prefix, image extensions, no traversal or encoded tricks.
        api.MapGet("/lcu-asset", async (string path, ILcuAssets assets, CancellationToken ct) =>
        {
            if (!IsAssetPath(path)) return Results.BadRequest(new { error = "Not an asset path." });
            var a = await assets.GetAssetAsync(path, ct);
            if (a.Bytes is null) return Results.StatusCode(a.Status is 404 ? 404 : 502);
            var response = Results.File(a.Bytes, a.ContentType ?? "image/png");
            return response;
        });

        api.MapGet("/champselect/skins", async (int? championId, ILcuClient c, Engine e, CancellationToken ct) =>
            e.Phase != "ChampSelect" ? Results.BadRequest(new { error = "Not in champion select." })
            : Results.Ok(await OwnedSkinsAsync(c, ct, championId)));

        api.MapPut("/champselect/skin", async (SkinChoice b, ILcuClient c, IActionGateway g, Engine e, CancellationToken ct) =>
            e.Phase != "ChampSelect" ? Results.BadRequest(new { error = "Not in champion select." })
            : !Skins.Contains(await OwnedSkinsAsync(c, ct), b.SkinId) ? Results.BadRequest(new { error = "You do not own that skin." }) // the client would accept it and silently ignore it
            : Relay(await g.SetSelectionAsync(null, null, b.SkinId, ct)));

        api.MapGet("/champselect/runes", async (int championId, string? position, ILcuClient c, Engine e, CancellationToken ct) =>
        {
            if (e.Phase != "ChampSelect") return Results.BadRequest(new { error = "Not in champion select." });
            var (pages, pos, status) = await RecommendedAsync(c, championId, position, ct);
            return pages is null ? Results.Json(new { error = "Recommended runes are not available for this champion." }, statusCode: status) : Results.Ok(new { position = pos, pages });
        });

        api.MapPost("/champselect/runes/apply", async (RuneChoice b, ILcuClient c, IActionGateway g, RunePageTracker tracker, Engine e, CancellationToken ct) =>
        {
            if (e.Phase != "ChampSelect") return Results.BadRequest(new { error = "Not in champion select." });
            var (pages, _, status) = await RecommendedAsync(c, b.ChampionId, b.Position, ct); // ids come from a fresh fetch, never from the caller
            if (pages is null || pages.FirstOrDefault(p => p.Index == b.Index) is not { } page)
                return Results.Json(new { error = "That recommendation is not available any more." }, statusCode: pages is null ? status : 404);

            await RemoveOwnPreviousPageAsync(c, g, tracker, ct);
            var created = await g.CreateTempRunePageAsync(Runes.TempPageBody(page, b.ChampionId), ct);
            if (created.IsSuccess) tracker.Created(created.Body);
            return created.IsSuccess ? Results.Ok(new { page.Keystone, spellIds = page.SpellIds }) : Relay(created);
        });
    }

    /// <summary>
    /// The matchup loadout (SPEC "Matchup loadout"): `GET /api/loadout?enemy=` previews it against that enemy (else the guessed opponent), with the keystone's
    /// name and icon and the Data Dragon version for item icons; `POST /api/loadout/apply {enemy}` applies every part; `POST /api/loadout/cancel` cancels the automatic one for this champ select.
    /// </summary>
    public static void MapMatchupLoadout(this RouteGroupBuilder api)
    {
        api.MapGet("/loadout", async (int? enemy, Engine e, MatchupLoadoutService loadout, GlamourSettings s, ILcuClient c, ChampionCatalog champions, CancellationToken ct) =>
        {
            if (e.Phase != "ChampSelect") return Results.Ok(new { state = "none" });
            if (await loadout.PlanAsync(e.Tracker.Board, enemy, ct) is not { } plan) return Results.Ok(new { state = "waiting" });
            var keystone = plan.Runes is { } r ? await PerkAsync(c, r.PerkIds[0], ct) : null;
            return Results.Ok(new
            {
                state = "ready", plan, keystone, version = (await champions.GetAsync(ct))?.Version,
                applied = loadout.Applied == (plan.ChampionId, plan.EnemyId), auto = s.MatchupLoadout,
            });
        });

        api.MapPost("/loadout/apply", async (LoadoutApply b, Engine e, MatchupLoadoutService loadout, CancellationToken ct) =>
            e.Phase != "ChampSelect" ? Results.BadRequest(new { error = "Not in champion select." })
            : await loadout.PlanAsync(e.Tracker.Board, b.Enemy, ct) is not { } plan ? Results.Conflict(new { error = "No loadout for this matchup yet." })
            : Results.Ok(await loadout.ApplyByHandAsync(plan, ct)));

        api.MapPost("/loadout/cancel", (MatchupLoadoutService loadout) =>
            loadout.CancelAuto() ? Results.NoContent() : Results.Conflict(new { error = "No automatic loadout to cancel." }));
    }

    private static JsonArray? _perks;

    /// <summary>One rune's name and icon from `GET /lol-perks/v1/perks` (read once per run).</summary>
    private static async Task<object?> PerkAsync(ILcuClient c, int id, CancellationToken ct)
    {
        _perks ??= await c.GetAsync("/lol-perks/v1/perks", ct) is { Status: 200, Body: JsonArray all } ? all : null;
        return _perks?.OfType<JsonObject>().FirstOrDefault(p => p["id"]?.GetValue<int>() == id) is { } perk
            ? new { id, name = perk["name"]?.GetValue<string>() ?? "", icon = perk["iconPath"]?.GetValue<string>() ?? "" } : null;
    }

    public static bool IsAssetPath(string? path) =>
        path is { Length: > 0 and < 400 } && path.StartsWith(AssetPrefix, StringComparison.Ordinal)
        && !path.Contains("..") && !path.Contains('%') && !path.Contains('?') && !path.Contains('#') && !path.Contains('\\')
        && ImageTypes.Contains(Path.GetExtension(path).ToLowerInvariant());

    private static IResult Relay(LcuResponse r) => Results.Json(r.Body, statusCode: r.Status);

    private static async Task<List<SkinInfo>> OwnedSkinsAsync(ILcuClient c, CancellationToken ct, int? championId = null) =>
        Skins.OwnedFrom(await c.GetAsync("/lol-champ-select/v1/skin-carousel-skins", ct) is { Status: 200 } r ? r.Body : null, championId);

    /// <summary>
    /// My lane, else the champion's first recommended lane (modes without lanes). The map comes from the gameflow session
    /// (`gameData.queue.mapId`, from the client spec), 11 (Summoner's Rift) when it is not there.
    /// </summary>
    private static async Task<(List<RunePage>? Pages, string Position, int Status)> RecommendedAsync(ILcuClient c, int championId, string? position, CancellationToken ct)
    {
        var pos = position is "TOP" or "JUNGLE" or "MIDDLE" or "BOTTOM" or "UTILITY" ? position : null;
        if (pos is null && await c.GetAsync("/lol-perks/v1/recommended-champion-positions", ct) is { Status: 200 } lanes
            && lanes.Body?[championId.ToString()]?["recommendedPositions"] is JsonArray a && a.Count > 0)
            pos = a[0]!.GetValue<string>();
        if (pos is null) return (null, "", 404);
        var mapId = await c.GetAsync("/lol-gameflow/v1/session", ct) is { Status: 200 } s && s.Body?["gameData"]?["queue"]?["mapId"] is { } m ? m.GetValue<int>() : 11;
        var r = await c.GetAsync($"/lol-perks/v1/recommended-pages/champion/{championId}/position/{pos}/map/{mapId}", ct);
        var pages = r.Status == 200 ? Runes.RecommendedFrom(r.Body) : null;
        return (pages is { Count: > 0 } ? pages : null, pos, r.Status == 200 ? 404 : r.Status);
    }

    /// <summary>
    /// Deletes the page Glamour made last time, and only that one, only if it is still a temporary page (by id: League renames it in place, LIVE, so the
    /// name says nothing).
    /// </summary>
    public static async Task RemoveOwnPreviousPageAsync(ILcuClient c, IActionGateway g, RunePageTracker tracker, CancellationToken ct)
    {
        if (tracker.LastCreated is not { } id) return;
        tracker.LastCreated = null;
        var all = await c.GetAsync("/lol-perks/v1/pages", ct);
        var ours = (all.Body as JsonArray)?.OfType<JsonObject>().Any(p => p["id"]?.GetValue<int>() == id && p["isTemporary"]?.GetValue<bool>() == true);
        if (ours == true) await g.DeleteRunePageAsync(id, ct);
    }
}
