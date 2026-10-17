using Glamour.Host.Auth;
using Microsoft.AspNetCore.Mvc;

namespace Glamour.Host.Push;

public sealed record PushSubscribeRequest(string? Endpoint, string? P256dh, string? Auth);

public static class PushEndpoints
{
    public static void MapPush(this RouteGroupBuilder api)
    {
        // The public VAPID key the browser needs for PushManager.subscribe.
        api.MapGet("/push/key", (PushStore store) => new { key = store.Vapid(WebPushSender.NewKeys).Public });

        api.MapGet("/push/status", (HttpContext ctx, PushStore store) =>
            new { subscribed = AuthEndpoints.CurrentDevice(ctx) is { } d && store.Has(d.Id), devices = store.All().Count });

        api.MapPut("/push/subscription", (HttpContext ctx, [FromBody] PushSubscribeRequest r, PushStore store) =>
        {
            if (AuthEndpoints.CurrentDevice(ctx) is not { } d) return Results.BadRequest(new { error = "only a paired device can subscribe" });
            if (string.IsNullOrWhiteSpace(r.P256dh) || string.IsNullOrWhiteSpace(r.Auth) || r.P256dh.Length > 256 || r.Auth.Length > 64) return Results.BadRequest(new { error = "bad keys" });
            // The PC calls this endpoint on the device's behalf, so only https push services on public names are acceptable (never this PC or the LAN).
            if (!IsPushService(r.Endpoint)) return Results.BadRequest(new { error = "bad endpoint" });
            store.Set(new PushSub(d.Id, r.Endpoint!, r.P256dh, r.Auth));
            return Results.NoContent();
        });

        api.MapDelete("/push/subscription", (HttpContext ctx, PushStore store) =>
        {
            if (AuthEndpoints.CurrentDevice(ctx) is { } d) store.RemoveDevice(d.Id);
            return Results.NoContent();
        });
    }

    private static readonly string[] PrivateSuffixes = [".local", ".lan", ".home", ".internal", ".intranet", ".corp", ".localhost", ".home.arpa", ".test", ".invalid"];

    /// <summary>An https URL on a public DNS name (the browser vendors' push services all are): no IP literal, no localhost, no single-label or private-use name.</summary>
    public static bool IsPushService(string? endpoint) =>
        endpoint is { Length: <= 1024 }
        && Uri.TryCreate(endpoint, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(u.UserInfo)
        && u.HostNameType == UriHostNameType.Dns && u.IdnHost.TrimEnd('.') is var host && host.Contains('.')
        && !host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        && !PrivateSuffixes.Any(s => host.EndsWith(s, StringComparison.OrdinalIgnoreCase));
}
