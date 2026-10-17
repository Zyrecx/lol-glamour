using System.Net;
using Glamour.Host.Auth;

namespace Glamour.Host.Network;

/// <summary>
/// What a non-PC caller may reach. Runs before everything else: addresses outside the private ranges are refused even if the firewall rule is wrong,
/// plain HTTP from the LAN is limited to the certificate install page once HTTPS is on, and the pages (not the API) can be switched off for phones.
/// </summary>
public static class Exposure
{
    /// <summary>Loopback, home LAN ranges, Tailscale (100.64/10), link-local and IPv6 unique-local.</summary>
    public static bool IsPrivate(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip)) return true;
        var b = ip.GetAddressBytes();
        return b.Length == 4
            ? b[0] == 10 || b[0] == 192 && b[1] == 168 || b[0] == 172 && b[1] is >= 16 and <= 31 || b[0] == 100 && b[1] is >= 64 and <= 127 || b[0] == 169 && b[1] == 254
            : ip.IsIPv6LinkLocal || ip.IsIPv6UniqueLocal;
    }

    public static void UseExposureGuard(this WebApplication app)
    {
        app.Use(async (ctx, next) =>
        {
            if (ctx.Connection.RemoteIpAddress is { } remote && !IsPrivate(remote)) { ctx.Response.StatusCode = 403; return; }

            if (!AuthEndpoints.IsLoopback(ctx))
            {
                var path = ctx.Request.Path;
                var isApi = path.StartsWithSegments("/api");
                var certPage = path == "/ca.crt" || path == "/install";
                var host = ctx.RequestServices.GetRequiredService<HostSettings>();
                if (host.RequireHttps && ctx.RequestServices.GetRequiredService<LocalCaHolder>().Current is { } ca && !ctx.Request.IsHttps && !certPage)
                {
                    // The PIN, the session cookie and every API call stay off plain HTTP once HTTPS is on.
                    if (isApi || !(HttpMethods.IsGet(ctx.Request.Method) || HttpMethods.IsHead(ctx.Request.Method))) { ctx.Response.StatusCode = 403; return; }
                    ctx.Response.Redirect($"https://{ctx.Request.Host.Host}:{ca.HttpsPort}{ctx.Request.Path}{ctx.Request.QueryString}", permanent: false, preserveMethod: true);
                    return;
                }
                if (!isApi && path != "/ca.crt" && !host.WebUiEnabled) { ctx.Response.StatusCode = 404; return; }
            }
            await next();
        });
    }

    /// <summary>The CA certificate and its install page (HTTP is fine: the certificate is public, and the page shows the fingerprint to compare).</summary>
    public static void MapCaPages(this WebApplication app)
    {
        var holder = app.Services.GetRequiredService<LocalCaHolder>();
        app.MapGet("/ca.crt", () => holder.Current is { } ca ? Results.File(ca.CaDer, "application/x-x509-ca-cert", "glamour-ca.crt") : Results.NotFound());
        app.MapGet("/install", () => holder.Current is { } ca ? Results.Content(InstallPage(ca), "text/html; charset=utf-8") : Results.NotFound());
    }

    private static string InstallPage(LocalCa ca) => $$"""
        <!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
        <title>Set up Glamour</title>
        <style>body{font:16px/1.5 system-ui,sans-serif;background:#060A12;color:#e8edf5;margin:0;padding:1.2rem;max-width:34rem}
        a.btn{display:block;text-align:center;background:#3a7bd5;color:#fff;padding:.9rem;border-radius:.7rem;text-decoration:none;margin:1rem 0}
        #st{padding:.7rem 1rem;border-radius:.7rem;background:#101a2b}
        code{word-break:break-all;color:#9fc4ff}h1{font-size:1.4rem}h2{font-size:1.1rem}li{margin:.4rem 0}</style></head><body>
        <h1>Set up Glamour on this phone</h1>
        <p id="st">Checking whether this phone already trusts Glamour&hellip;</p>
        <div id="steps" hidden>
        <p>One time only: install Glamour's certificate so Chrome treats this connection as secure (this is what lets it install as an app). This page continues by itself once it works.</p>
        <a class="btn" href="/ca.crt">1. Download certificate</a>
        <p>Check that this fingerprint matches the one shown in Glamour on your PC:</p>
        <p><code>{{ca.Fingerprint}}</code></p>
        <h2>2. Install it</h2>
        <p><b>Android:</b> Settings &rarr; Security &amp; privacy &rarr; More security settings &rarr; Install from device storage &rarr; CA certificate, then pick <b>glamour-ca.crt</b>. Come back to this page afterwards.</p>
        <p><b>iPhone:</b> open the downloaded file, Settings &rarr; Profile Downloaded &rarr; Install, then Settings &rarr; General &rarr; About &rarr; Certificate Trust Settings and switch it on.</p>
        <p>The certificate only vouches for addresses on your own network.</p>
        </div>
        <script>
        const raw = location.hash.slice(1);
        const secret = /^[A-Za-z0-9_-]{8,200}$/.test(raw) ? raw : '';
        const secure = 'https://' + location.hostname + ':{{ca.HttpsPort}}';
        const st = document.getElementById('st'), steps = document.getElementById('steps');
        let done = false;
        async function probe() {
          if (done) return;
          try {
            await fetch(secure + '/ca.crt', { mode: 'no-cors', cache: 'no-store' }); // resolves only when this phone trusts the certificate
            done = true;
            steps.hidden = true;
            if (secret) { st.textContent = 'Certificate trusted. Signing in\u2026'; location.replace(secure + '/pair.html#' + secret); }
            else st.textContent = 'Certificate trusted. Now scan the pairing code in Glamour on your PC.';
          } catch {
            steps.hidden = false;
            st.textContent = 'Waiting for the certificate\u2026';
          }
        }
        setInterval(probe, 2000);
        document.addEventListener('visibilitychange', probe);
        probe();
        </script>
        </body></html>
        """;
}

/// <summary>Keeps the server certificate matching the PC's current addresses as Wi-Fi, hotspot or VPN change.</summary>
public sealed class LocalCaService(LocalCaHolder holder, ILogger<LocalCaService> log) : IHostedService, IDisposable
{
    private Timer? _debounce;

    public Task StartAsync(CancellationToken ct)
    {
        try { holder.LoadIfExists(); }
        catch (System.Security.Cryptography.CryptographicException ex) { log.LogWarning(ex, "Could not load the local CA"); }
        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += OnChanged;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged -= OnChanged;
        return Task.CompletedTask;
    }

    private void OnChanged(object? s, EventArgs e) => (_debounce ??= new Timer(_ => Refresh())).Change(TimeSpan.FromSeconds(2), Timeout.InfiniteTimeSpan);

    private void Refresh()
    {
        try
        {
            if (holder.Current is { } ca && ca.Refresh(LanAddress.Candidates(), LanAddress.MdnsName())) log.LogInformation("Server certificate reissued for the current addresses");
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or System.Net.Sockets.SocketException)
        {
            log.LogWarning(ex, "Could not reissue the server certificate");
        }
    }

    public void Dispose() => _debounce?.Dispose();
}
