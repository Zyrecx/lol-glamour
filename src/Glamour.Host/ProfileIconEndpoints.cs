using Glamour.Core;
using Glamour.Lcu;

namespace Glamour.Host;

/// <summary>
/// The profile icon of the signed-in account. Clearing sets icon 29, the new-account default that every account owns but the client's
/// picker hides (INSIGHTS "Rank, status, badges").
/// </summary>
public static class ProfileIconEndpoints
{
    public const int Blank = 29;

    public static void MapProfileIcon(this RouteGroupBuilder api)
    {
        api.MapGet("/profile-icon", async (ILcuClient c, CancellationToken ct) =>
        {
            var me = await c.GetAsync("/lol-summoner/v1/current-summoner", ct);
            if (!me.IsSuccess || me.Body?["profileIconId"]?.GetValue<int>() is not { } id) return Results.Json(new { error = "The League client is not available." }, statusCode: 503);
            return Results.Ok(new { id, blank = id == Blank });
        });

        api.MapDelete("/profile-icon", async (ILcuClient c, CancellationToken ct) =>
        {
            var res = await c.SendAsync(HttpMethod.Put, "/lol-summoner/v1/current-summoner/icon", new { profileIconId = Blank }, ct);
            return res.IsSuccess ? Results.NoContent() : Results.Json(res.Body, statusCode: res.Status);
        });
    }
}

/// <summary>Tells the UI when the icon changes, in the client or here, so the Profile icon section follows without a reload.</summary>
public sealed class ProfileIconWatch(EventHub events) : IFeature
{
    private int? _last;

    public Task OnPhaseAsync(string phase, CancellationToken ct) => Task.CompletedTask;
    public Task OnChampSelectAsync(ChampSelectSession session, CancellationToken ct) => Task.CompletedTask;

    public Task OnEventAsync(LcuEvent e, CancellationToken ct)
    {
        if (e.Uri != "/lol-summoner/v1/current-summoner" || e.Data?["profileIconId"] is not System.Text.Json.Nodes.JsonValue v || !v.TryGetValue<int>(out var id) || id == _last)
            return Task.CompletedTask;
        _last = id;
        events.Publish("profile-icon", new { id, blank = id == ProfileIconEndpoints.Blank });
        return Task.CompletedTask;
    }
}
