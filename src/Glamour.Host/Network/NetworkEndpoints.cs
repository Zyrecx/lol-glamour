using Glamour.Host.Auth;

namespace Glamour.Host.Network;

public sealed record WebUiChoice(bool Enabled);

public sealed record PhoneAccessChoice(bool Enabled);

public static class NetworkEndpoints
{
    private static FirewallStatus Both(FirewallStatus a, FirewallStatus? b) =>
        b is null ? a : new(a.Supported && b.Supported, a.Allowed && b.Allowed, a.RulePresent && b.RulePresent, a.NetworkIsPublic || b.NetworkIsPublic);

    public static void MapNetwork(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/network");

        // What a phone needs to reach this PC: is the host listening on the LAN, is the firewall open, which addresses to use.
        g.MapGet("", (HttpContext ctx, IFirewall firewall, PhoneAccess phone, HostSettings hostSettings, IConfiguration config) =>
        {
            var holder = ctx.RequestServices.GetRequiredService<LocalCaHolder>();
            var ca = holder.Current;
            var port = holder.HttpPort; // the phone port; the PC window's own port is not what phones use
            var fw = Both(firewall.Check(port), firewall.Check(holder.HttpsPort));
            return new
            {
                port,
                webUiEnabled = ctx.RequestServices.GetRequiredService<HostSettings>().WebUiEnabled,
                requireHttps = ctx.RequestServices.GetRequiredService<HostSettings>().RequireHttps,
                https = ca is null ? null : new
                {
                    port = ca.HttpsPort,
                    fingerprint = ca.Fingerprint,
                    installUrl = LanAddress.Hosts().FirstOrDefault() is { } h ? $"http://{h}:{ca.HttpPort}/install" : null,
                },
                listeningOnLan = phone.Active,
                // The saved choice, which can differ from what is running until the host restarts.
                // Same rule as the host's startup: the command-line key wins over the saved choice, and then the switch in the UI cannot change anything.
                phoneAccessWanted = config.GetValue<bool?>("Glamour:AllowLanFromArgs") ?? hostSettings.AllowLan,
                phoneAccessForced = config.GetValue<bool?>("Glamour:AllowLanFromArgs") is not null,
                addresses = LanAddress.Hosts(),
                firewall = fw,
                // One line the UI can show as is.
                problem = phone.Blocked ? "port-in-use"
                    : !phone.Requested ? "lan-off"
                    : fw is { Supported: true, Allowed: false, NetworkIsPublic: true } ? "network-public"
                    : fw is { Supported: true, Allowed: false } ? "firewall-blocked"
                    : null,
            };
        });

        // "Allow phone access": saved in host.json and applied when the listeners restart. The desktop app restarts the host by itself; the console host asks for a manual restart.
        g.MapPut("/phone-access", (HttpContext ctx, HostSettings host, IServiceProvider sp, IConfiguration config, [Microsoft.AspNetCore.Mvc.FromBody] PhoneAccessChoice body) =>
        {
            if (!AuthEndpoints.IsLoopback(ctx)) return Results.StatusCode(403);
            if (config.GetValue<bool?>("Glamour:AllowLanFromArgs") is not null) return Results.Conflict(new { error = "Phone access is set by a start-up argument (Glamour:AllowLan), which overrides this switch." });
            host.AllowLan = body.Enabled;
            var shell = sp.GetService<IDesktopShell>();
            if (shell is not null) _ = Task.Run(async () => { await Task.Delay(400); await shell.RestartHostAsync(); }); // after this response is out
            return Results.Ok(new { restarting = shell is not null });
        });

        // Windows shows its admin prompt on the PC, so only the PC itself may ask for it, never a phone.
        g.MapPost("/firewall", async (HttpContext ctx, IFirewall firewall, CancellationToken ct) =>
        {
            if (!AuthEndpoints.IsLoopback(ctx)) return Results.StatusCode(403);
            var holder = ctx.RequestServices.GetRequiredService<LocalCaHolder>();
            int[] ports = [holder.HttpPort, holder.HttpsPort]; // both phone ports, whether or not the certificate exists yet
            var ok = await firewall.AddRuleAsync(ports, ct);
            return Results.Json(new { added = ok, firewall = Both(firewall.Check(holder.HttpPort), firewall.Check(holder.HttpsPort)) }, statusCode: ok ? 200 : 409);
        });

        // Block plain HTTP on the network (only the certificate install page stays). Only once the CA exists, or no phone could still get in.
        g.MapPut("/require-https", (HttpContext ctx, HostSettings host, LocalCaHolder holder, [Microsoft.AspNetCore.Mvc.FromBody] WebUiChoice body) =>
        {
            if (!AuthEndpoints.IsLoopback(ctx)) return Results.StatusCode(403);
            if (body.Enabled && holder.Current is null) return Results.Conflict(new { error = "Create the certificate first: show a pairing code with \"Install as app\"." });
            host.RequireHttps = body.Enabled;
            return Results.NoContent();
        });

        // Whether phones are served the web pages at all (the API stays reachable for other clients). PC only.
        g.MapPut("/web-ui", (HttpContext ctx, HostSettings host, [Microsoft.AspNetCore.Mvc.FromBody] WebUiChoice body) =>
        {
            if (!AuthEndpoints.IsLoopback(ctx)) return Results.StatusCode(403);
            host.WebUiEnabled = body.Enabled;
            return Results.NoContent();
        });
    }
}
