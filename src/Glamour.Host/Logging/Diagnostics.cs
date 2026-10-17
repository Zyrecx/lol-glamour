using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using Glamour.Core;
using Glamour.Host.Auth;

namespace Glamour.Host.Logging;

/// <summary>
/// What the log file needs so a session can be read back afterwards: a line per start, every action a device asked for, every refused or failed request,
/// errors from the web UI, and one line per event the UI was sent (so a wrong screen can be told apart from wrong data).
/// </summary>
public static class Diagnostics
{
    private const int MaxEventChars = 4000, MaxClientChars = 2000, ClientLinesPerMinute = 30;

    /// <summary>The first middleware: sees the final status of every request, including the ones the guards refuse.</summary>
    public static void UseRequestLog(this WebApplication app)
    {
        var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Glamour.Requests");
        var refused = new RepeatLimiter(TimeSpan.FromSeconds(30), app.Services.GetRequiredService<TimeProvider>());
        app.Use(async (ctx, next) =>
        {
            var started = Environment.TickCount64;
            await next();
            var path = ctx.Request.Path.Value ?? "";
            var method = ctx.Request.Method;
            var status = ctx.Response.StatusCode;
            var isApi = ctx.Request.Path.StartsWithSegments("/api");
            var action = isApi && !HttpMethods.IsGet(method) && !HttpMethods.IsHead(method);
            if (status < 400 && !action || path == "/api/client-log") return;
            var who = Who(ctx);
            var ms = Environment.TickCount64 - started;
            if (status is 401 or 403 or 404)
            {
                // Refusals can come in floods (a phone retrying, a scanner on the LAN): one line per kind per 30 s, with the count of the hidden ones.
                if (!refused.Allow($"{status} {method} {path} {ctx.Connection.RemoteIpAddress}", out var hidden)) return;
                log.LogWarning("{Method} {Path} -> {Status} for {Who} (host {Host}, origin {Origin}){Hidden}", method, path, status, who, ctx.Request.Host.Value,
                    ctx.Request.Headers.Origin.ToString() is { Length: > 0 } o ? o : "none", hidden > 0 ? $", {hidden} more like it hidden" : "");
                return;
            }
            if (status >= 400) log.LogWarning("{Method} {Path} -> {Status} for {Who} in {Ms} ms", method, path, status, who, ms);
            else log.LogInformation("{Method} {Path} -> {Status} for {Who} in {Ms} ms", method, path, status, who, ms);
        });
    }

    /// <summary>A paired device by name, the PC itself, or the bare address.</summary>
    private static string Who(HttpContext ctx) =>
        ctx.Items[AuthEndpoints.DeviceKey] is DeviceInfo d ? $"device '{d.Name}'"
        : AuthEndpoints.IsLoopback(ctx) ? "this PC"
        : ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    /// <summary>POST /api/client-log: a script error from the phone or PC app, written to the log file. Capped so a page in an error loop cannot fill it.</summary>
    public static void MapClientLog(this RouteGroupBuilder api)
    {
        var budget = new RepeatLimiter(TimeSpan.FromMinutes(1), TimeProvider.System, ClientLinesPerMinute);
        api.MapPost("/client-log", (ClientLogEntry e, HttpContext ctx, ILoggerFactory f) =>
        {
            if (budget.Allow("all", out _))
                f.CreateLogger("Glamour.Web").LogWarning("{Who} on {Page}: {Kind}: {Message}{Stack}", Who(ctx), Cut(e.Page, 200), Cut(e.Kind, 40), Cut(e.Message, MaxClientChars),
                    e.Stack is { Length: > 0 } s ? Environment.NewLine + Cut(s, MaxClientChars) : "");
            return Results.NoContent();
        });
    }

    /// <summary>The start line: which build, which way it runs, what it listens on. Makes one session easy to tell from the next in the file.</summary>
    public static void LogStart(this WebApplication app)
    {
        var log = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Glamour.Start");
        var version = typeof(Diagnostics).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?";
        var kind = app.Services.GetService<IDesktopShell>() is null ? "console host" : "desktop app";
        var phone = app.Services.GetRequiredService<Network.PhoneAccess>();
        app.Lifetime.ApplicationStarted.Register(() => log.LogInformation("Glamour {Version} started as the {Kind}; phone access {Phone}; listening on {Urls}",
            version, kind, phone.Blocked ? "blocked (port in use)" : app.Configuration.GetValue<bool>("Glamour:AllowLan") ? "on" : "off", string.Join(" ", app.Urls)));
        app.Lifetime.ApplicationStopping.Register(() => log.LogInformation("Glamour stopping"));
    }

    private static string Cut(string? s, int max) => s is null ? "" : s.Length <= max ? s : s[..max] + "…";

    /// <summary>
    /// Writes every event sent to the UI as one compact JSON line, so the log shows what each screen was fed. An event identical to the last of its type is skipped.
    /// Settings events are named only: they can hold the ntfy topic, which works like a password.
    /// </summary>
    public sealed class EventLogService(EventHub events, ILoggerFactory logs) : BackgroundService
    {
        private readonly ILogger log = logs.CreateLogger("Glamour.Events");
        private static readonly HashSet<string> NameOnly = ["settings", "defaults"];
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var last = new Dictionary<string, string>();
            using var sub = events.Subscribe();
            try
            {
                await foreach (var e in sub.ReadAllAsync(stoppingToken))
                {
                    string body;
                    try { body = NameOnly.Contains(e.Type) ? "(changed)" : JsonSerializer.Serialize(e.Data, Json); }
                    catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or JsonException) { body = "(not serializable: " + ex.Message + ")"; }
                    if (last.TryGetValue(e.Type, out var prev) && prev == body) continue;
                    last[e.Type] = body;
                    log.LogInformation("{Type} {Data}", e.Type, Cut(body, MaxEventChars));
                }
            }
            catch (OperationCanceledException) { /* host stopping */ }
        }
    }

    /// <summary>Lets a key through at most `limit` times per window and counts what it held back.</summary>
    private sealed class RepeatLimiter(TimeSpan window, TimeProvider time, int limit = 1)
    {
        private readonly ConcurrentDictionary<string, (DateTimeOffset Start, int Used, int Hidden)> _keys = new();

        public bool Allow(string key, out int hidden)
        {
            if (_keys.Count > 1000) _keys.Clear(); // a flood of distinct keys: forget, rather than grow
            var now = time.GetUtcNow();
            var allowed = false;
            var held = 0;
            _keys.AddOrUpdate(key, _ => { allowed = true; held = 0; return (now, 1, 0); }, (_, s) =>
            {
                held = 0;
                if (now - s.Start >= window) { allowed = true; held = s.Hidden; return (now, 1, 0); }
                if (s.Used < limit) { allowed = true; return (s.Start, s.Used + 1, s.Hidden); }
                allowed = false;
                return (s.Start, s.Used, s.Hidden + 1);
            });
            hidden = held;
            return allowed;
        }
    }
}

public sealed record ClientLogEntry(string? Kind, string? Message, string? Stack, string? Page);
