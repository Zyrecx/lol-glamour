using Glamour.Host.Push;
using Glamour.Core;
using Glamour.Host;
using Glamour.Host.Auth;
using Glamour.Host.Logging;
using Glamour.Host.Network;
using Glamour.Host.Profiles;
using Glamour.Lcu;
using Microsoft.Extensions.DependencyInjection.Extensions;

/// <summary>Builds the whole Glamour host without running it, so Program.cs (the console host) and Glamour.Desktop (tray + window) share one composition root.</summary>
public static class GlamourApp
{
    /// <param name="configure">Runs right after the builder exists, before anything reads configuration (the desktop app sets its URLs here).</param>
    public static WebApplication Build(string[] args, WebApplicationOptions? options = null, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(options ?? new WebApplicationOptions { Args = args });
        configure?.Invoke(builder);
        // No Windows Event Log: with its service off (sandboxes, stripped-down Windows) every warning written there throws and takes the request down with it (GLAMOUR-5, GLAMOUR-6).
        foreach (var d in builder.Services.Where(d => d.ImplementationType == typeof(Microsoft.Extensions.Logging.EventLog.EventLogLoggerProvider)).ToList()) builder.Services.Remove(d);
        // The log file (Diagnostics says what goes in it). The desktop app brings its own, shared across host restarts; tests and the demo host (NoClient) write none.
        if (!builder.Configuration.GetValue<bool>("Glamour:NoClient") && !builder.Services.Any(d => !d.IsKeyedService && d.ImplementationInstance is FileLog))
            builder.Logging.AddProvider(new FileLog(builder.Configuration["Glamour:LogDir"] ?? FileLog.DefaultDir));
        builder.Services.AddHostedService<Diagnostics.EventLogService>();
        // "Help improve Glamour" (SPEC "Publishing"): the desktop app brings its own, shared across host restarts like the log; tests and the demo host (NoClient) have none.
        var telemetry = builder.Services.FirstOrDefault(d => d.ServiceType == typeof(Glamour.Host.Telemetry.Telemetry))?.ImplementationInstance as Glamour.Host.Telemetry.Telemetry;
        if (telemetry is null && !builder.Configuration.GetValue<bool>("Glamour:NoClient"))
        {
            telemetry = new Glamour.Host.Telemetry.Telemetry("console");
            builder.Services.AddSingleton(telemetry);
        }
        if (telemetry is not null)
        {
            builder.Logging.AddProvider(new Glamour.Host.Telemetry.TelemetryLog(telemetry));
            builder.Services.AddSingleton<Glamour.Host.Telemetry.UsageTally>();
            builder.Services.AddSingleton<IUsage>(sp => sp.GetRequiredService<Glamour.Host.Telemetry.UsageTally>());
            builder.Services.AddSingleton<IFeature>(sp => sp.GetRequiredService<Glamour.Host.Telemetry.UsageTally>());
            builder.Services.AddHostedService(sp => sp.GetRequiredService<Glamour.Host.Telemetry.UsageTally>());
        }
        builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
        builder.Services.TryAddSingleton<ShotNames>(); // README shots; the desktop app brings its own, so names stay the same across host restarts
        builder.Services.AddSingleton<GlamourSettings>(); // the EFFECTIVE settings (shared defaults + the active account's overrides); features read this
        builder.Services.AddSingleton(sp => new SettingsStore(new GlamourSettings(), builder.Configuration["Glamour:SettingsPath"] ?? SettingsStore.DefaultPath, sp.GetRequiredService<ILogger<SettingsStore>>()));
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<SwappableLcuClient>();
        builder.Services.AddSingleton<ILcuClient>(sp => sp.GetRequiredService<SwappableLcuClient>());
        builder.Services.AddSingleton<ILcuAssets>(sp => sp.GetRequiredService<SwappableLcuClient>());
        builder.Services.AddSingleton<RunePageTracker>();
        builder.Services.AddSingleton<IActionGateway, ActionGateway>();
        builder.Services.AddSingleton<AutomationGate>();
        builder.Services.AddSingleton<IFeature>(sp => sp.GetRequiredService<AutomationGate>()); // first, so a pause ends before the others see the phase
        builder.Services.AddSingleton<AutoAcceptFeature>();
        builder.Services.AddSingleton<IFeature>(sp => sp.GetRequiredService<AutoAcceptFeature>());
        builder.Services.AddSingleton<AutoBanLockFeature>();
        builder.Services.AddSingleton<IFeature>(sp => sp.GetRequiredService<AutoBanLockFeature>());
        builder.Services.AddSingleton<IFeature, SwapFeature>();
        builder.Services.AddSingleton<IFeature, AutoHonorFeature>();
        builder.Services.AddSingleton<IFeature, ScreenSkipFeature>();
        builder.Services.AddSingleton<IFeature, ProfileIconWatch>();
        builder.Services.AddSingleton<PresenceOverrideFeature>();
        builder.Services.AddSingleton<IFeature>(sp => sp.GetRequiredService<PresenceOverrideFeature>());
        builder.Services.AddSingleton(sp => new AuthService(sp.GetRequiredService<TimeProvider>(), builder.Configuration["Glamour:AuthPath"] ?? AuthService.DefaultPath, sp.GetRequiredService<ILogger<AuthService>>()));
        builder.Services.AddSingleton<LoginThrottle>();
        builder.Services.AddSingleton<IFirewall, WindowsFirewall>();
        builder.Services.AddSingleton<EventHub>();
        builder.Services.AddSingleton<IRiotClient, WindowsRiotClient>();
        builder.Services.AddSingleton(sp => new ProfileStore(builder.Configuration["Glamour:ProfilesPath"] ?? ProfileStore.DefaultDir, sp.GetRequiredService<ILogger<ProfileStore>>()));
        builder.Services.AddSingleton<SettingsService>();
        builder.Services.AddSingleton(sp => new ProfileService(sp.GetRequiredService<ProfileStore>(), sp.GetRequiredService<IRiotClient>(), () => sp.GetRequiredService<Engine>().Phase,
            builder.Configuration["Glamour:RiotSessionPath"] ?? ProfileStore.DefaultLiveSessionFile, sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<EventHub>(),
            () => sp.GetRequiredService<SettingsService>().Recompute(), sp.GetRequiredService<IProfileCardSource>()));
        builder.Services.AddSingleton<IProfileCardSource>(sp => new LcuProfileCardSource(sp.GetRequiredService<SwappableLcuClient>(), sp.GetRequiredService<SwappableLcuClient>()));
        builder.Services.AddHttpClient("ddragon", c => c.Timeout = TimeSpan.FromSeconds(10));
        builder.Services.AddSingleton<IChampionSource, DataDragonChampionSource>();
        builder.Services.AddSingleton(sp => new ChampionCatalog(sp.GetRequiredService<IChampionSource>(), sp.GetRequiredService<TimeProvider>(), builder.Configuration["Glamour:ChampionsPath"] ?? ChampionCatalog.DefaultPath, sp.GetRequiredService<ILogger<ChampionCatalog>>()));
        builder.Services.AddSingleton<ISpellSource, DataDragonSpellSource>();
        builder.Services.AddSingleton<SpellCatalog>();
        builder.Services.AddHttpClient("lolalytics", c => { c.Timeout = TimeSpan.FromSeconds(10); c.DefaultRequestHeaders.UserAgent.ParseAdd("Glamour"); });
        builder.Services.AddSingleton(sp => new LolalyticsMatchupSource(sp.GetRequiredService<IHttpClientFactory>(), sp.GetRequiredService<ChampionCatalog>(), builder.Configuration["Glamour:MatchupsPath"] ?? LolalyticsMatchupSource.DefaultDir, sp.GetRequiredService<ILogger<LolalyticsMatchupSource>>()));
        builder.Services.AddSingleton<IMatchupSource>(sp => sp.GetRequiredService<LolalyticsMatchupSource>());
        builder.Services.AddSingleton<IChampionProfileSource>(sp => sp.GetRequiredService<LolalyticsMatchupSource>());
        builder.Services.AddSingleton<IAllySource>(sp => sp.GetRequiredService<LolalyticsMatchupSource>());
        builder.Services.AddHttpClient("mayhem", c => { c.Timeout = TimeSpan.FromSeconds(10); c.DefaultRequestHeaders.UserAgent.ParseAdd("Glamour"); });
        builder.Services.AddSingleton(sp => new MayhemSource(sp.GetRequiredService<IHttpClientFactory>(), sp.GetRequiredService<ChampionCatalog>(), sp.GetRequiredService<GlamourSettings>(),
            builder.Configuration["Glamour:MatchupsPath"] ?? LolalyticsMatchupSource.DefaultDir, sp.GetRequiredService<ILogger<MayhemSource>>()));
        builder.Services.AddSingleton<IModeWinRateSource, ModeWinRateSources>();
        builder.Services.AddHttpClient("augments", c => { c.Timeout = TimeSpan.FromSeconds(15); c.DefaultRequestHeaders.UserAgent.ParseAdd("Glamour"); });
        builder.Services.AddSingleton(sp => new AugmentDataSource(sp.GetRequiredService<IHttpClientFactory>(), sp.GetRequiredService<ChampionCatalog>(),
            Path.Combine(builder.Configuration["Glamour:MatchupsPath"] ?? LolalyticsMatchupSource.DefaultDir, "augments"), sp.GetRequiredService<ILogger<AugmentDataSource>>()));
        builder.Services.AddSingleton<CounterChoice>();
        builder.Services.AddSingleton<IFeature>(sp => sp.GetRequiredService<CounterChoice>());
        builder.Services.AddSingleton<CounterChooser>();
        builder.Services.AddSingleton<ICounterChooser>(sp => sp.GetRequiredService<CounterChooser>());
        builder.Services.AddSingleton<IFeature>(sp => sp.GetRequiredService<CounterChooser>());
        builder.Services.AddHttpClient("opgg", c => { c.Timeout = TimeSpan.FromSeconds(20); c.DefaultRequestHeaders.UserAgent.ParseAdd("Glamour"); });
        builder.Services.AddSingleton<IMatchupBuildSource>(sp => new OpggBuildSource(sp.GetRequiredService<IHttpClientFactory>(), sp.GetRequiredService<ChampionCatalog>(), builder.Configuration["Glamour:MatchupsPath"] ?? LolalyticsMatchupSource.DefaultDir, sp.GetRequiredService<ILogger<OpggBuildSource>>()));
        builder.Services.AddSingleton<MatchupLoadoutService>();
        builder.Services.AddSingleton<IFeature>(sp => sp.GetRequiredService<MatchupLoadoutService>());
        if (!builder.Services.Any(d => d.ServiceType == typeof(RuneWindow))) builder.Services.AddSingleton(new RuneWindow()); // the desktop app brings the one it watches
        builder.Services.AddSingleton<RunePageGuard>();
        builder.Services.AddSingleton<IFeature>(sp => sp.GetRequiredService<RunePageGuard>());
        builder.Services.AddHttpClient("ntfy", c => c.Timeout = TimeSpan.FromSeconds(10));
        builder.Services.AddSingleton<INotifier, NtfyNotifier>();
        builder.Services.AddSingleton(sp => new Glamour.Host.Push.PushStore(builder.Configuration["Glamour:PushPath"] ?? Glamour.Host.Push.PushStore.DefaultPath, sp.GetRequiredService<ILogger<Glamour.Host.Push.PushStore>>()));
        builder.Services.AddSingleton<Glamour.Host.Push.IWebPushSender, Glamour.Host.Push.WebPushSender>();
        builder.Services.AddHostedService<NotificationService>();
        builder.Services.AddHostedService<AppEndpoints.UpdateRelay>();
        builder.Services.AddSingleton<Engine>();
        builder.Services.AddSingleton<ConnectionService>();
        builder.Services.AddSingleton<GameClock>();
        // Tests set Glamour:NoClient so an in-memory host can never find and write to a real League client.
        if (!builder.Configuration.GetValue<bool>("Glamour:NoClient"))
        {
            builder.Services.AddHostedService(sp => sp.GetRequiredService<ConnectionService>());            builder.Services.AddHostedService<ProfileCardService>();
            builder.Services.AddSingleton<ILiveGameClient, LiveGameHttpClient>();
            builder.Services.AddHostedService<GameClockService>();
            builder.Services.AddSingleton<IShellOpener, WindowsShellOpener>();
        }

        var hostSettings = new HostSettings(builder.Configuration["Glamour:HostSettingsPath"] ?? HostSettings.DefaultPath, Microsoft.Extensions.Logging.Abstractions.NullLogger<HostSettings>.Instance);
        builder.Services.AddSingleton(hostSettings);

        // Off by default: only this PC can connect. "Allow phone access" (host.json) or Glamour:AllowLan=true lets phones on the LAN reach the (guarded) API;
        // the command-line key wins over the saved choice. With it on, the HTTPS port is bound too, but has no certificate until someone picks
        // "Install as app" in the Pairing card (SPEC "Local HTTPS"). Glamour:Https=true creates the certificate at startup instead.
        var fromArgs = builder.Configuration.GetValue<bool?>("Glamour:AllowLan");
        if (fromArgs is { } forced) builder.Configuration["Glamour:AllowLanFromArgs"] = forced ? "true" : "false"; // so the UI can say the switch is overridden
        var allowLan = fromArgs ?? hostSettings.AllowLan;
        builder.Configuration["Glamour:AllowLan"] = allowLan ? "true" : "false"; // the rest of the host reads the one key
        var httpPort = builder.Configuration.GetValue("Glamour:Port", 5170);
        var caHolder = new LocalCaHolder(builder.Configuration["Glamour:CaDir"] ?? LocalCa.DefaultDir, TimeProvider.System, httpPort, builder.Configuration.GetValue("Glamour:HttpsPort", httpPort + 1));
        builder.Services.AddSingleton(caHolder);
        builder.Services.AddHostedService<LocalCaService>();
        var phone = new PhoneAccess(allowLan);
        builder.Services.AddSingleton(phone);
        if (allowLan && builder.Configuration.GetValue<bool>("Glamour:Https")) caHolder.Ensure();
        // The tray app has its own loopback-only port for its window (Glamour:DesktopPort), so it never competes with the phone port.
        var desktopPort = builder.Configuration.GetValue<int>("Glamour:DesktopPort");
        if (allowLan || desktopPort > 0)
            builder.WebHost.ConfigureKestrel(k =>
            {
                if (allowLan)
                {
                    // The port is open to the network: bound what a stranger on it can cost (see also Exposure.UseExposureGuard).
                    k.Limits.MaxConcurrentConnections = 100;
                    k.Limits.MaxRequestBodySize = 1_000_000;
                    k.Limits.MaxRequestHeadersTotalSize = 16 * 1024;
                    k.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
                    // Another program on the phone port must not stop Glamour from starting: phone access is then reported as blocked, the rest works.
                    if (PhoneAccess.PortFree(httpPort) && PhoneAccess.PortFree(caHolder.HttpsPort))
                    {
                        k.ListenAnyIP(httpPort);
                        k.ListenAnyIP(caHolder.HttpsPort, o => o.UseHttps(h => h.ServerCertificateSelector = (_, _) => caHolder.Current?.ServerCertificate));
                    }
                    else phone.Blocked = true;
                }
                if (desktopPort > 0) k.ListenLocalhost(desktopPort);
            });

        var app = builder.Build();
        app.Services.GetRequiredService<AuthService>().DeviceRemoved += app.Services.GetRequiredService<Glamour.Host.Push.PushStore>().RemoveDevice; // a revoked phone stops getting pushes
        app.Services.GetRequiredService<SettingsStore>().Load();
        app.Services.GetRequiredService<ProfileStore>().Load();
        app.Services.GetRequiredService<SettingsService>().Recompute(publish: false);
        if (telemetry is not null)
        {
            telemetry.IsOn = () => hostSettings.Telemetry;
            var profiles = app.Services.GetRequiredService<ProfileStore>();
            telemetry.KnownNames = () => profiles.Data.Profiles.Select(p => p.RiotId).OfType<string>().ToList();
            app.Lifetime.ApplicationStopped.Register(() => telemetry.Flush(TimeSpan.FromSeconds(2)));
        }

        app.LogStart();
        app.UseRequestLog(); // first, so it also sees what the guards refuse
        // Who a client write serves, for the usage counts: a device's request, else a feature on its own.
        app.Use((ctx, next) =>
        {
            if (ctx.Request.Path.StartsWithSegments("/api")) UsageOrigin.Set(AuthEndpoints.IsLoopback(ctx) ? "pc" : "phone");
            return next(ctx);
        });
        // Kestrel binds loopback by default; the guard lets in the PC itself and paired devices only.
        app.UseExposureGuard();
        app.UseDefaultFiles();
        app.UseStaticFiles(new StaticFileOptions
        {
            // The service worker and the pages must never be served from a cache, or an update would not reach phones or the panel:
            // without a header WebView2 kept an old index.html, which still found its old chunks in wwwroot and ran a stale UI.
            // Built chunks carry a content hash in their name, so a new build is a new URL: those never need asking again.
            OnPrepareResponse = c =>
            {
                if (c.File.Name == "sw.js" || c.File.Name.EndsWith(".html", StringComparison.Ordinal)) c.Context.Response.Headers.CacheControl = "no-cache";
                else if (c.Context.Request.Path.StartsWithSegments("/assets")) c.Context.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
            },
        });
        app.MapCaPages();
        app.UseApiGuard();
        // A call to the League client that dies with it (closed, restarting, the connection dropped mid-request) answers 503 instead of an unhandled 500.
        app.Use(async (ctx, next) =>
        {
            try { await next(); }
            catch (Exception e) when (!ctx.Response.HasStarted && !ctx.RequestAborted.IsCancellationRequested
                                      && e is HttpRequestException or TaskCanceledException or ObjectDisposedException)
            {
                ctx.Response.StatusCode = 503;
                await ctx.Response.WriteAsJsonAsync(new { error = "The League client is not running." });
            }
        });
        // README shots (SPEC "Publishing"): with Glamour:ShotNames every player name a page gets is a made-up one; events are swapped in StreamEvents.
        var shotNames = app.Configuration.GetValue<bool>("Glamour:ShotNames") ? app.Services.GetRequiredService<ShotNames>() : null;
        if (shotNames is not null) app.Use((ctx, next) => ShotNamesAnswer(ctx, next, shotNames));        var api = app.MapGroup("/api");
        api.MapAuth();
        api.MapNetwork();
        api.MapDesktop();
        api.MapApp();
        api.MapProfiles();
        api.MapLoadout();
        api.MapMatchupLoadout();
        api.MapHonor();
        api.MapLobbyMembers();
        api.MapChallenges();
        api.MapProfileIcon();
        api.MapPush();
        api.MapClientLog();
        api.MapLogReport(builder.Configuration["Glamour:LogDir"] ?? FileLog.DefaultDir);
        api.MapGet("/state", (Engine e, ConnectionService c) => new { c.Connected, c.Stage, e.Phase });
        api.MapGet("/settings", (GlamourSettings s) => s);
        // PUT and PATCH edit the SHARED defaults and answer with the effective settings (what this account runs on). One account's overrides: PATCH /api/profiles/{id}/settings.
        api.MapPut("/settings", ([Microsoft.AspNetCore.Mvc.FromBody] GlamourSettings incoming, SettingsService settings) => // FromBody: the type is also a DI singleton, so without it `incoming` is the live object
            settings.ReplaceDefaults(incoming));
        api.MapPatch("/settings", ([Microsoft.AspNetCore.Mvc.FromBody] System.Text.Json.Nodes.JsonObject patch, SettingsService settings) => settings.PatchDefaults(patch));

        api.MapGet("/counter-pick", async (Engine e, CounterChooser counters, CounterChoice choice, ILogger<CounterPickView> log, int? enemy, CancellationToken ct) =>
        {
            choice.Opponent = enemy; // the auto-lock's best counter follows who the user chose to face
            var view = await counters.ViewAsync(e.Tracker.Board, e.Phase == "ChampSelect", ct);
            // What the panel was given, for reading a draft back: the panel itself logs nothing.
            log.LogInformation("Counter pick: {State} vs {Enemy} (asked {Asked}), {Picks} picks, {Team} team picks, auto-lock {Auto}, my lines {Lines}, lanes {Lanes}, my lane {Chances}, their bans {Bans}", view.State, view.EnemyId, enemy,
                view.Picks?.Count ?? 0, view.TeamPicks?.Count ?? 0, view.AutoLock, view.Mine?.Lines.Count ?? 0, view.EnemyLanes is null ? "" : string.Join(",", view.EnemyLanes.Select(l => $"{l.Key}:{l.Value}")),
                view.Chances is null ? "" : string.Join(",", view.Chances.Select(c => $"{c.Key}:{c.Value}%")),
                // Who banned what (cell:ban:pick), for testing later whether a player's ban hints at their lane.
                string.Join(",", (e.Tracker.Board?.BanList ?? []).Where(b => !b.Mine).Select(b => $"{b.CellId}:{b.ChampionId}:{e.Tracker.Board!.TheirTeam.FirstOrDefault(p => p.CellId == b.CellId)?.ChampionId ?? 0}")));
            return view;
        });

        // Bench advice (ARAM, Mayhem): every champion's win rate (or tier) in the mode; the UI ranks the bench against my champion, so a swap asks nothing new.
        api.MapGet("/winrates/{mode}", async (string mode, IModeWinRateSource source, CancellationToken ct) =>
            await source.GetModeAsync(mode, ct) is { } rates
                ? Results.Ok(new
                {
                    rates.Mode, rates.Patch, rates.Source,
                    Champions = rates.Rows.Select(r => new { ChampionId = r.OpponentId, WinRate = (double?)r.WinRate, r.Games, r.OlderGames, r.OlderPatch, Tier = rates.Tiers?.GetValueOrDefault(r.OpponentId) })
                        .Concat((rates.Tiers ?? new Dictionary<int, string>()).Where(t => rates.Rows.All(r => r.OpponentId != t.Key))
                            .Select(t => new { ChampionId = t.Key, WinRate = (double?)null, Games = 0, OlderGames = 0, OlderPatch = (string?)null, Tier = (string?)t.Value })),
                })
                : Results.Json(new { error = $"No {mode} win rates right now." }, statusCode: 503));

        api.MapGet("/champions", async (ChampionCatalog champions, ILcuClient lcu, ConnectionService connection, CancellationToken ct) =>
            await champions.GetAsync(ct) is { } list
                ? Results.Ok(connection.Connected && await lcu.GetAsync("/lol-perks/v1/recommended-champion-positions", ct) is { Status: 200 } r ? ClientLanes.Overlay(list, ClientLanes.Parse(r.Body)) : list)
                : Results.Json(new { error = "Champion list unavailable: no internet and nothing cached yet." }, statusCode: 503));

        api.MapGet("/spells", async (SpellCatalog spells, CancellationToken ct) =>
            await spells.GetAsync(ct) is { } list ? Results.Ok(list) : Results.Json(new { error = "Spell list unavailable: no internet." }, statusCode: 503));

        api.MapPost("/notify/test", async (GlamourSettings s, INotifier n, Glamour.Host.Push.IWebPushSender webPush, CancellationToken ct) =>
        {
            if (s.WebPushEnabled)
            {
                try
                {
                    return await webPush.SendAsync(new("Glamour test", "Notifications work.", 3), ct) > 0 ? Results.NoContent() : Results.BadRequest(new { error = "no phone is subscribed to web push yet" });
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { return Results.Json(new { error = ex.Message }, statusCode: 502); }
            }
            if (!s.NtfyEnabled || string.IsNullOrEmpty(s.NtfyTopic)) return Results.BadRequest(new { error = "notifications are off" });
            try { await n.SendAsync(s.NtfyServer, s.NtfyTopic, new("Glamour test", "Notifications work.", 3), ct); return Results.NoContent(); }
            catch (Exception ex) when (ex is not OperationCanceledException) { return Results.Json(new { error = ex.Message }, statusCode: 502); }
        });

        api.MapGet("/events", (HttpContext ctx, EventHub events, Engine e, ConnectionService c, GlamourSettings s, SettingsService defaults, ProfileService profiles, AutomationGate automation, GameClock game) =>
            TypedResults.ServerSentEvents(StreamEvents(events, e, c, s, defaults, profiles, automation, game, shotNames, AuthEndpoints.RevocationToken(ctx), ctx.RequestAborted)));

        static async IAsyncEnumerable<System.Net.ServerSentEvents.SseItem<GlamourEvent>> StreamEvents(
            EventHub events, Engine engine, ConnectionService connection, GlamourSettings settings, SettingsService settingsService, ProfileService profiles, AutomationGate automation, GameClock game, ShotNames? shot, CancellationToken revoked,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken aborted)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(aborted, revoked);
            var ct = linked.Token;
            // Subscribe before taking the snapshot so nothing published in between is lost.
            using var sub = events.Subscribe();
            yield return Item(shot, new GlamourEvent("snapshot", new { connection.Connected, connection.Stage, engine.Phase, settings, defaults = settingsService.Defaults, profiles = profiles.Snapshot(), settingsMeta = SettingsRules.Meta(), automation = automation.Snapshot(), game = game.Snapshot(), state = engine.Tracker.Snapshot() }));
            await using var live = sub.ReadAllAsync(ct).GetAsyncEnumerator(ct);
            while (await MoveNextOrEnd(live)) yield return Item(shot, live.Current); // revoked or disconnected: end the stream quietly
        }

        static async Task<bool> MoveNextOrEnd(IAsyncEnumerator<GlamourEvent> e)
        {
            try { return await e.MoveNextAsync(); }
            catch (OperationCanceledException) { return false; }
        }

        static System.Net.ServerSentEvents.SseItem<GlamourEvent> Item(ShotNames? shot, GlamourEvent e) =>
            new(shot is null ? e : e with { Data = shot.Rewrite(System.Text.Json.JsonSerializer.SerializeToNode(e.Data, SettingsOverlay.Json)) }, e.Type);

        static IResult Relay(LcuResponse r) => Results.Json(r.Body, statusCode: r.Status);
        api.MapPost("/ready-check/accept", async (IActionGateway g) => Relay(await g.AcceptReadyCheckAsync()));
        api.MapPost("/ready-check/decline", async (IActionGateway g) => Relay(await g.DeclineReadyCheckAsync()));
        api.MapPost("/ready-check/auto-accept/cancel", (AutoAcceptFeature a) => a.CancelPending() ? Results.NoContent() : Results.Conflict(new { error = "No automatic accept is waiting." }));
        api.MapPost("/champselect/auto/skip", (AutoBanLockFeature f) => f.SkipPending() ? Results.NoContent() : Results.Conflict(new { error = "No automatic ban or lock is waiting." }));
        api.MapPut("/automation/pause", (PauseChoice p, AutomationGate gate) => { gate.SetPaused(p.Paused); return Results.Ok(gate.Snapshot()); });
        // Stop auto ban, lock and pre-select for this champ select only (the panel's Stop).
        api.MapPut("/automation/champ-select", (PauseChoice p, AutomationGate gate) =>
            gate.SetDraftStopped(p.Paused) ? Results.Ok(gate.Snapshot()) : Results.Conflict(new { error = "Not in champion select." }));
        // Leaving champ select: a dodge in a matchmade one. The UI asks twice; refused where a custom game would start anyway.
        api.MapPost("/champselect/dodge", async (ILcuClient c, IActionGateway g, Engine e, CancellationToken ct) =>
            await ChampSelectExit.LeaveAsync(c, g, e.Phase, ct) is { Error: { } error } o ? Results.Json(new { error }, statusCode: o.Status) : Results.NoContent());
        // A swap request goes to another player: only on a tap, only inside champ select. Cancel, accept and decline only answer one that exists.
        api.MapPost("/swap/{kind}/{id:int}/{verb}", async (string kind, int id, string verb, IActionGateway g, Engine e) =>
            kind is not ("position" or "pick-order" or "trade") || !ActionGateway.SwapVerbs.Contains(verb) ? Results.BadRequest(new { error = "kind is position, pick-order or trade; verb is request, cancel, accept or decline" })
            : e.Phase != "ChampSelect" ? Results.BadRequest(new { error = "Not in champion select." })
            : Relay(await g.SwapAsync(kind, id, verb)));
        // Bench swap and reroll only change my own champion (ARAM, Mayhem).
        api.MapPost("/champselect/bench/{championId:int}", async (int championId, IActionGateway g, Engine e) =>
            e.Phase != "ChampSelect" ? Results.BadRequest(new { error = "Not in champion select." }) : Relay(await g.BenchSwapAsync(championId)));
        api.MapPost("/champselect/reroll", async (IActionGateway g, Engine e) =>
            e.Phase != "ChampSelect" ? Results.BadRequest(new { error = "Not in champion select." }) : Relay(await g.RerollAsync()));
        api.MapPost("/lobby/{queueId:int}", async (int queueId, IActionGateway g) => Relay(await g.CreateLobbyAsync(queueId)));
        api.MapDelete("/lobby", async (IActionGateway g) => Relay(await g.LeaveLobbyAsync()));
        api.MapPut("/lobby/positions", async (PositionChoice p, IActionGateway g) =>
            LaneNames.IsPosition(p.First, allowUnselected: false) && LaneNames.IsPosition(p.Second, allowUnselected: true)
                ? Relay(await g.SetPositionsAsync(p.First, p.Second))
                : Results.BadRequest(new { error = "first is TOP, JUNGLE, MIDDLE, BOTTOM, UTILITY or FILL; second may also be UNSELECTED" }));
        api.MapGet("/champselect/pickable", async (ILcuClient c) => Relay(await c.GetAsync("/lol-champ-select/v1/pickable-champion-ids")));
        api.MapGet("/champselect/bannable", async (ILcuClient c) => Relay(await c.GetAsync("/lol-champ-select/v1/bannable-champion-ids")));
        api.MapPost("/champselect/actions/{actionId:int}/hover", async (int actionId, ChampionChoice body, IActionGateway g, Engine e) =>
            e.Phase != "ChampSelect" ? Results.BadRequest(new { error = "Not in champion select." }) : Relay(await g.HoverAsync(actionId, body.ChampionId)));
        // Locking in or banning ends the turn for good, and a finished champ select starts the game: only inside champ select, only pick or ban.
        api.MapPost("/champselect/actions/{actionId:int}/complete", async (int actionId, ChampionChoice body, IActionGateway g, Engine e) =>
            e.Phase != "ChampSelect" ? Results.BadRequest(new { error = "Not in champion select." })
            : body.Type is not ("pick" or "ban") ? Results.BadRequest(new { error = "type is pick or ban" })
            : Relay(await g.CompleteAsync(actionId, body.ChampionId, body.Type)));
        // Spells and skin only change my own selection. They stick in PLANNING too (INSIGHTS "Spells and skins").
        api.MapPut("/champselect/spells", async (SpellChoice b, IActionGateway g, Engine e) =>
            e.Phase != "ChampSelect" ? Results.BadRequest(new { error = "Not in champion select." })
            : b.Spell1Id <= 0 || b.Spell2Id <= 0 || b.Spell1Id == b.Spell2Id ? Results.BadRequest(new { error = "Choose two different spells." })
            : Relay(await g.SetSelectionAsync(b.Spell1Id, b.Spell2Id, null)));
        // Client repair: Reconnect only in the Reconnect phase; a UI restart never mid-game (it would bring the client window back over the game).
        api.MapPost("/game/reconnect", async (IActionGateway g, Engine e) =>
            e.Phase != "Reconnect" ? Results.Conflict(new { error = "No game to reconnect to." }) : Relay(await g.ReconnectAsync()));
        api.MapPost("/client/restart-ux", async (IActionGateway g, Engine e) =>
            e.Phase is "GameStart" or "InProgress" ? Results.Conflict(new { error = "Not during a game." }) : Relay(await g.RestartClientUxAsync()));
        api.MapPost("/queue", async (IActionGateway g) => Relay(await g.StartQueueAsync()));
        api.MapDelete("/queue", async (IActionGateway g) => Relay(await g.CancelQueueAsync()));
        // A lobby member's readable name: the lobby payload has none, so the UI asks per summonerId. Names do not change mid-session; cached.
        var names = new System.Collections.Concurrent.ConcurrentDictionary<long, string>();
        api.MapGet("/summoner/{summonerId:long}", async (long summonerId, ILcuClient c) =>
        {
            if (names.TryGetValue(summonerId, out var known)) return Results.Ok(new { name = known });
            var r = await c.GetAsync($"/lol-summoner/v1/summoners/{summonerId}");
            if (r.Status != 200 || r.Body is not { } b) return Results.NotFound(new { error = "Unknown summoner." });
            var game = b["gameName"]?.GetValue<string>() ?? "";
            var name = game.Length > 0 ? (b["tagLine"]?.GetValue<string>() is { Length: > 0 } t ? $"{game}#{t}" : game) : b["displayName"]?.GetValue<string>() ?? "";
            if (name.Length > 0) names[summonerId] = name;
            return Results.Ok(new { name });
        });
        api.MapGet("/party/open", async (ILcuClient c) => Parties.OpenFrom((await c.GetAsync("/lol-chat/v1/friends")) is { Status: 200 } r ? r.Body : null));
        api.MapPost("/party/{partyId}/join", async (string partyId, IActionGateway g) => Relay(await g.JoinPartyAsync(partyId)));
        api.MapPost("/invitations/{id}/{reply}", async (string id, string reply, IActionGateway g) =>
            reply is "accept" or "decline" ? Relay(await g.ReplyInvitationAsync(id, reply == "accept")) : Results.BadRequest(new { error = "reply is accept or decline" }));
        api.MapGet("/queues", async (ILcuClient c) => Relay(await c.GetAsync("/lol-game-queues/v1/queues")));
        api.MapGet("/queues/playable", async (bool? all, ILcuClient c, CancellationToken ct) =>
            await c.GetAsync("/lol-game-queues/v1/queues", ct) is { Status: 200 } q
                ? Results.Ok(Queues.Playable(q.Body, (await c.GetAsync("/lol-maps/v2/maps", ct)) is { Status: 200 } m ? m.Body : null, all == true))
                : Results.Json(new { error = "The League client is not running." }, statusCode: 503));
        // The skin the user set as their profile background in the client (id = champion * 1000 + skin number); the Play screen's backdrop.
        api.MapGet("/me/backdrop", async (ILcuClient c, CancellationToken ct) =>
            await c.GetAsync("/lol-summoner/v1/current-summoner/summoner-profile", ct) is { Status: 200, Body: { } b } && b["backgroundSkinId"] is System.Text.Json.Nodes.JsonValue v && v.TryGetValue<int>(out var id) && id >= 1000
                ? Results.Ok(new { skinId = id, championId = id / 1000, skinNum = id % 1000 })
                : Results.Ok(new { skinId = 0, championId = 0, skinNum = 0 }));

        return app;
    }

    /// <summary>An `/api` JSON answer with its player names made up (<see cref="ShotNames"/>); the event stream is swapped where it is written, anything else passes.</summary>
    private static async Task ShotNamesAnswer(HttpContext ctx, RequestDelegate next, ShotNames names)
    {
        if (!ctx.Request.Path.StartsWithSegments("/api") || ctx.Request.Path.StartsWithSegments("/api/events")) { await next(ctx); return; }
        var body = ctx.Response.Body;
        using var buffer = new MemoryStream();
        ctx.Response.Body = buffer;
        try { await next(ctx); }
        finally { ctx.Response.Body = body; }
        buffer.Position = 0;
        if (ctx.Response.ContentType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true && buffer.Length > 0)
        {
            System.Text.Json.Nodes.JsonNode? node = null;
            try { node = System.Text.Json.Nodes.JsonNode.Parse(buffer); } catch (System.Text.Json.JsonException) { /* not JSON after all */ }
            if (node is not null)
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(names.Rewrite(node)?.ToJsonString() ?? "null");
                ctx.Response.ContentLength = bytes.Length;
                await body.WriteAsync(bytes, ctx.RequestAborted);
                return;
            }
            buffer.Position = 0;
        }
        await buffer.CopyToAsync(body, ctx.RequestAborted);
    }
}

public sealed record ChampionChoice(int ChampionId, string? Type = null);
public sealed record SpellChoice(int Spell1Id, int Spell2Id);
public sealed record PositionChoice(string First, string Second);
public sealed record PauseChoice(bool Paused);

public partial class Program;
