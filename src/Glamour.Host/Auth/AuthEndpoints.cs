using System.Net;
using QRCoder;

namespace Glamour.Host.Auth;

public sealed record PairRequest(string? Secret, string? Name);
public sealed record LoginRequest(string? Password, string? Name);
public sealed record PasswordRequest(string? Password);
public sealed record PairingChoice(bool Https);

/// <summary>
/// Who may call /api: the PC itself (loopback, with browser-attack checks) or a paired device. Pair and login are the only public calls.
/// </summary>
public static class AuthEndpoints
{
    internal const string DeviceKey = "glamour.device";
    private static readonly string[] PublicPaths = ["/api/auth/status", "/api/auth/pair", "/api/auth/login"];

    public static void UseApiGuard(this WebApplication app)
    {
        app.Use(async (ctx, next) =>
        {
            if (!ctx.Request.Path.StartsWithSegments("/api")) { await next(); return; }
            var auth = ctx.RequestServices.GetRequiredService<AuthService>();

            // A web page on another site must not be able to drive this API through the user's browser.
            if (!IsSafeMethod(ctx.Request.Method) && !SameOrigin(ctx.Request)) { ctx.Response.StatusCode = 403; return; }
            // DNS rebinding: a hostile site can point its own name at this PC (loopback or LAN address) and then talk to it as "same origin".
            // Real use always names the PC by address, localhost or its .local name, so any other name is refused, signed in or not.
            var loopback = IsLoopback(ctx);
            if (!loopback && !IsKnownHost(ctx)) { ctx.Response.StatusCode = 403; return; }

            var token = ctx.Request.Cookies[AuthService.CookieName];
            if (token is not null && auth.Authenticate(token) is { } who)
            {
                ctx.Items[DeviceKey] = who.Device;
                if (who.RefreshCookie) SetCookie(ctx, token);
                await next();
                return;
            }

            if (loopback) { await next(); return; }

            if (PublicPaths.Contains(ctx.Request.Path.Value, StringComparer.OrdinalIgnoreCase)) { await next(); return; }
            ctx.Response.StatusCode = 401;
        });
    }

    public static void MapAuth(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/auth");

        g.MapGet("/status", (HttpContext ctx, AuthService auth) => new
        {
            authenticated = ctx.Items.ContainsKey(DeviceKey) || IsLoopback(ctx),
            loopback = IsLoopback(ctx),
            passwordSet = auth.HasPassword,
            device = ctx.Items[DeviceKey] as DeviceInfo,
        });

        // Shows a QR for a phone. The QR carries a one-time secret; the page it opens spends it.
        // PC only: a paired phone must not be able to add more devices, so a stolen phone cannot widen its own access.
        g.MapPost("/pairing", (HttpContext ctx, AuthService auth, IConfiguration config, Network.LocalCaHolder holder, [Microsoft.AspNetCore.Mvc.FromBody(EmptyBodyBehavior = Microsoft.AspNetCore.Mvc.ModelBinding.EmptyBodyBehavior.Allow)] PairingChoice? choice) =>
        {
            if (!IsLoopback(ctx)) return Results.StatusCode(403);
            // "Install as app" needs HTTPS: the certificate is made the first time it is asked for.
            Network.LocalCa? ca = null;
            if (choice?.Https == true)
            {
                if (!config.GetValue<bool>("Glamour:AllowLan")) return Results.Json(new { error = "Phone access is off. Turn on \"Allow phone access\" on the Phone page first." }, statusCode: 409);
                try { ca = holder.Ensure(); }
                catch (System.Security.Cryptography.CryptographicException ex) { return Results.Json(new { error = "Could not create the certificate: " + ex.Message }, statusCode: 500); }
            }
            var code = auth.CreatePairing();
            var port = holder.HttpPort; // the phone port, never the port this request came in on (the PC window has its own)
            var bases = config["Glamour:PublicUrl"] is { Length: > 0 } fixedUrl
                ? new List<string> { fixedUrl.TrimEnd('/') }
                : LanAddress.Hosts()
                    .Select(h => $"http://{h}:{port}").ToList();
            if (bases.Count == 0) bases.Add($"{ctx.Request.Scheme}://{ctx.Request.Host}");
            // With HTTPS on the QR opens the setup page, which installs the certificate if needed and then moves on to https://.../pair.html with the same secret.
            var urls = bases.Select(b => $"{b}/{(ca is null ? "pair.html" : "install")}#{code.Secret}").ToList();

            using var generator = new QRCodeGenerator();
            using var data = generator.CreateQrCode(urls[0], QRCodeGenerator.ECCLevel.M);
            var svg = new SvgQRCode(data).GetGraphic(6);
            return Results.Ok(new { url = urls[0], otherUrls = urls.Skip(1), svg, fingerprint = ca?.Fingerprint, expiresAt = code.ExpiresAt });
        });

        g.MapPost("/pair", (HttpContext ctx, AuthService auth, LoginThrottle throttle, [Microsoft.AspNetCore.Mvc.FromBody] PairRequest body) =>
            Guarded(ctx, throttle, shared: false, () => body.Secret is { Length: > 0 and <= 200 } s
                ? auth.Redeem(s, ctx.Request.Cookies[AuthService.CookieName], body.Name, ctx.Request.Headers.UserAgent)
                : null));

        g.MapPost("/login", (HttpContext ctx, AuthService auth, LoginThrottle throttle, [Microsoft.AspNetCore.Mvc.FromBody] LoginRequest body) =>
            Guarded(ctx, throttle, shared: true, () => body.Password is { Length: > 0 and <= AuthService.MaxPasswordLength } p
                ? auth.Login(p, ctx.Request.Cookies[AuthService.CookieName], body.Name, ctx.Request.Headers.UserAgent)
                : null));

        // PC only, like pairing: a password is another way to add devices.
        g.MapPut("/password", (HttpContext ctx, AuthService auth, [Microsoft.AspNetCore.Mvc.FromBody] PasswordRequest body) =>
            !IsLoopback(ctx) ? Results.StatusCode(403)
            : body.Password is { } p && auth.SetPassword(p)
                ? Results.NoContent()
                : Results.BadRequest(new { error = $"PIN must be {AuthService.MinPasswordLength} to {AuthService.MaxPasswordLength} digits." }));

        g.MapDelete("/password", (HttpContext ctx, AuthService auth) =>
        {
            if (!IsLoopback(ctx)) return Results.StatusCode(403);
            auth.ClearPassword();
            return Results.NoContent();
        });

        g.MapGet("/devices", (HttpContext ctx, AuthService auth) =>
        {
            var current = (ctx.Items[DeviceKey] as DeviceInfo)?.Id;
            return auth.Devices().Select(d => new { d.Id, d.Name, d.CreatedAt, d.LastSeen, current = d.Id == current });
        });

        g.MapDelete("/devices/{id}", (string id, HttpContext ctx, AuthService auth) =>
        {
            var removed = auth.Revoke(id);
            if (removed && (ctx.Items[DeviceKey] as DeviceInfo)?.Id == id) ctx.Response.Cookies.Delete(AuthService.CookieName);
            return removed ? Results.NoContent() : Results.NotFound();
        });

        // Ends this browser's session only. The PC itself (loopback, no device) has nothing to end.
        g.MapPost("/logout", (HttpContext ctx, AuthService auth) =>
        {
            if (ctx.Items[DeviceKey] is DeviceInfo d) auth.Revoke(d.Id);
            ctx.Response.Cookies.Delete(AuthService.CookieName);
            return Results.NoContent();
        });

        g.MapPost("/logout-all", (HttpContext ctx, AuthService auth) =>
        {
            auth.RevokeAll();
            ctx.Response.Cookies.Delete(AuthService.CookieName);
            return Results.NoContent();
        });
    }

    /// <summary>The paired device making this call; null for the PC itself.</summary>
    public static DeviceInfo? CurrentDevice(HttpContext ctx) => ctx.Items[DeviceKey] as DeviceInfo;

    /// <summary>Cancelled when the calling device is revoked; None for the PC itself.</summary>
    public static CancellationToken RevocationToken(HttpContext ctx) =>
        ctx.Items[DeviceKey] is DeviceInfo d ? ctx.RequestServices.GetRequiredService<AuthService>().RevocationToken(d.Id) : CancellationToken.None;

    /// <param name="shared">Also spend the budget shared by all addresses (PIN guesses). QR secrets cannot be guessed, so pairing keeps working while someone tries PINs.</param>
    private static IResult Guarded(HttpContext ctx, LoginThrottle throttle, bool shared, Func<Issued?> attempt)
    {
        var key = ClientKey(ctx);
        if (throttle.RetryAfter(key, shared) is { } wait)
        {
            ctx.Response.Headers.RetryAfter = ((int)Math.Ceiling(wait.TotalSeconds)).ToString();
            return Results.StatusCode(429);
        }
        if (attempt() is not { } issued)
        {
            throttle.Failure(key, shared);
            return Results.Json(new { error = "Invalid or expired." }, statusCode: 401);
        }
        throttle.Success(key);
        SetCookie(ctx, issued.Token);
        return Results.Ok(issued.Device);
    }

    private static void SetCookie(HttpContext ctx, string token) =>
        ctx.Response.Cookies.Append(AuthService.CookieName, token, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Strict,
            Secure = ctx.Request.IsHttps,
            MaxAge = AuthService.SessionLifetime,
            Path = "/",
        });

    /// <summary>The throttle key: the IPv4 address, or the /64 of an IPv6 one (a device picks any address in its /64 at will).</summary>
    public static string ClientKey(HttpContext ctx)
    {
        if (ctx.Connection.RemoteIpAddress is not { } ip) return "unknown";
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6) return ip.ToString();
        var b = ip.GetAddressBytes();
        Array.Clear(b, 8, 8);
        return new IPAddress(b) + "/64";
    }

    private static bool IsSafeMethod(string m) => HttpMethods.IsGet(m) || HttpMethods.IsHead(m) || HttpMethods.IsOptions(m);

    /// <summary>
    /// Loopback peer, addressed as localhost or a loopback IP, and no sign of a proxy. A local reverse proxy (Tailscale Serve, a tunnel) forwards
    /// remote users from 127.0.0.1, so those must not count as the PC; nor may a hostile site whose name was rebound to 127.0.0.1, even with a cookie.
    /// </summary>
    public static bool IsLoopback(HttpContext ctx) =>
        ctx.Connection.RemoteIpAddress is { } ip && IPAddress.IsLoopback(ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip)
        && IsLoopbackHost(ctx.Request.Host.Host)
        && !ctx.Request.Headers.ContainsKey("X-Forwarded-For") && !ctx.Request.Headers.ContainsKey("Forwarded") && !ctx.Request.Headers.ContainsKey("X-Real-IP");

    /// <summary>A name only this PC's own network can answer for: an IP literal, localhost, a .local (mDNS) or Tailscale name, the bare machine name, or Glamour:PublicUrl's host.</summary>
    private static bool IsKnownHost(HttpContext ctx)
    {
        var host = ctx.Request.Host.Host.TrimEnd('.');
        if (host.Length == 0) return false;
        if (IsLoopbackHost(host) || IPAddress.TryParse(host.Trim('[', ']'), out _)) return true;
        if (host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".ts.net", StringComparison.OrdinalIgnoreCase)) return true;
        if (host.Equals(Environment.MachineName, StringComparison.OrdinalIgnoreCase)) return true;
        return ctx.RequestServices.GetRequiredService<IConfiguration>()["Glamour:PublicUrl"] is { Length: > 0 } url
            && Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Host.Equals(host, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLoopbackHost(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host is "127.0.0.1" or "[::1]" or "::1";

    /// <summary>An Origin header, when the browser sends one, must be this server. "null" origins (sandboxed frames, file pages) are refused.</summary>
    private static bool SameOrigin(HttpRequest r)
    {
        if (r.Headers.TryGetValue("Sec-Fetch-Site", out var site) && site.ToString() is "cross-site" or "same-site") return false;
        if (!r.Headers.TryGetValue("Origin", out var origin)) return true;
        return Uri.TryCreate(origin.ToString(), UriKind.Absolute, out var u)
            && u.Authority.Equals(r.Host.ToUriComponent(), StringComparison.OrdinalIgnoreCase);
    }
}
