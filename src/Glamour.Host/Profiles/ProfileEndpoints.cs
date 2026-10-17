namespace Glamour.Host.Profiles;

public static class ProfileEndpoints
{
    public sealed record NameBody(string? Name, string? Id = null);
    public sealed record OrderBody(List<string>? Ids);
    public sealed record SettingsBody(System.Text.Json.Nodes.JsonObject? Set, List<string>? Clear);

    public static void MapProfiles(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/profiles");
        g.MapGet("", (ProfileService p, CancellationToken ct) => Run(() => p.RefreshIdentityAsync(ct)));
        // Save the account that is signed in now (first run), or with an id refresh that profile after a re-sign-in. A blank name means the Riot ID.
        g.MapPost("", (NameBody b, ProfileService p, CancellationToken ct) => Run(() => p.SaveCurrentAsync(b.Name, b.Id, ct)));
        // These close the client, and the panel that asked is docked to it and drops the request as it goes: not the request's token, or the
        // switch stops halfway, after the kill and before the new session and the relaunch.
        g.MapPost("/add/begin", (bool? confirm, ProfileService p) => Run(() => p.BeginAddAsync(confirm == true, CancellationToken.None)));
        g.MapPost("/add/cancel", (ProfileService p) => Run(() => p.CancelAddAsync(CancellationToken.None)));
        g.MapPut("/order", (OrderBody b, ProfileService p) => Run(() => p.ReorderAsync(b.Ids ?? [])));
        g.MapPost("/{id}/switch", (string id, bool? confirm, ProfileService p) => Run(() => p.SwitchAsync(id, confirm == true, CancellationToken.None)));
        // Per-account overrides of the shared settings: `set` replaces a field for this account (null is a real value), `clear` makes it follow the defaults again.
        g.MapPatch("/{id}/settings", (string id, SettingsBody b, SettingsService s, ProfileService p) => Run(() => s.PatchOverridesAsync(p, id, b.Set, b.Clear)));
        // The account's profile icon as cached while it was signed in (the League client only has it for the running account).
        g.MapGet("/{id}/icon", (string id, ProfileStore s) => s.Data.Profiles.Any(p => p.Id == id) && File.Exists(s.IconPath(id))
            ? Results.File(s.IconPath(id), "image/jpeg", lastModified: File.GetLastWriteTimeUtc(s.IconPath(id)))
            : Results.NotFound());
        g.MapPatch("/{id}",(string id, NameBody b, ProfileService p) => Run(() => p.RenameAsync(id, b.Name ?? "")));
        g.MapDelete("/{id}", (string id, ProfileService p) => Run(() => p.RemoveAsync(id)));
    }

    private static async Task<IResult> Run(Func<Task<object>> action)
    {
        try { return Results.Ok(await action()); }
        catch (ProfileException ex) { return Results.Json(new { error = ex.Message, code = ex.Code, detail = ex.Extra }, statusCode: ex.Status); }
    }
}
