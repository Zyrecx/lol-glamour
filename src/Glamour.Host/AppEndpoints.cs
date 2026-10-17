using Glamour.Core;
using Glamour.Host.Auth;
using Glamour.Host.Logging;
using Glamour.Host.Network;

namespace Glamour.Host;

/// <summary>What an update stands at, for Settings > Updates and the "Update ready" line. <see cref="Available"/> is false outside an installed release (dev copies, the console host).</summary>
/// <param name="State">none (nothing new), checking, downloading, ready (downloaded, goes in at exit or on Restart), error, off.</param>
public sealed record UpdateStatus(bool Available, string State, string? Version = null, string? Notes = null, string? Error = null)
{
    public static readonly UpdateStatus NotInstalled = new(false, "none");
}

/// <summary>Updates (Velopack) and the install folder. Only the desktop app has one; the endpoints then say updates are not available.</summary>
public interface IUpdater
{
    /// <summary>Where Glamour is installed (shown on the tour's last screen and in Settings > About); null for a copy that was not installed.</summary>
    string? InstallFolder { get; }

    UpdateStatus Status { get; }

    /// <summary>Raised on any thread when <see cref="Status"/> changes.</summary>
    event Action? Changed;

    /// <summary>Looks now (Check now); with Automatic or Notify it also downloads.</summary>
    Task CheckAsync();

    /// <summary>Closes Glamour, puts the downloaded update in and starts it again (Notify me's Restart).</summary>
    void RestartToUpdate();

    /// <summary>The mode in host.json changed.</summary>
    void ModeChanged();
}

public static class AppEndpoints
{
    public static void MapApp(this RouteGroupBuilder api)
    {
        api.MapGet("/app", (IServiceProvider sp, HostSettings host) => Info(sp, host));

        api.MapPut("/app/telemetry", (HttpContext ctx, IServiceProvider sp, HostSettings host, [Microsoft.AspNetCore.Mvc.FromBody] EnabledChoice body) =>
        {
            if (!AuthEndpoints.IsLoopback(ctx)) return Results.StatusCode(403);
            host.Telemetry = body.Enabled;
            return Results.Ok(Info(sp, host));
        });

        api.MapPost("/app/tour-done", (HttpContext ctx, IServiceProvider sp, HostSettings host) =>
        {
            if (!AuthEndpoints.IsLoopback(ctx)) return Results.StatusCode(403);
            host.TourDone = true;
            return Results.Ok(Info(sp, host));
        });

        api.MapPut("/app/updates", (HttpContext ctx, IServiceProvider sp, HostSettings host, [Microsoft.AspNetCore.Mvc.FromBody] UpdateModeChoice body) =>
        {
            if (!AuthEndpoints.IsLoopback(ctx)) return Results.StatusCode(403);
            if (!Enum.TryParse<UpdateMode>(body.Mode, ignoreCase: true, out var mode)) return Results.BadRequest(new { error = "Mode is automatic, notify or off." });
            host.Updates = mode;
            sp.GetService<IUpdater>()?.ModeChanged();
            return Results.Ok(Info(sp, host));
        });

        api.MapPost("/app/updates/check", async (HttpContext ctx, IServiceProvider sp, HostSettings host) =>
        {
            if (!AuthEndpoints.IsLoopback(ctx)) return Results.StatusCode(403);
            if (sp.GetService<IUpdater>() is not { Status.Available: true } u) return Results.NotFound(new { error = "Updates come with the installed app." });
            await u.CheckAsync();
            return Results.Ok(Info(sp, host));
        });

        api.MapPost("/app/updates/restart", (HttpContext ctx, IServiceProvider sp) =>
        {
            if (!AuthEndpoints.IsLoopback(ctx)) return Results.StatusCode(403);
            if (sp.GetService<IUpdater>() is not { Status.State: "ready" } u) return Results.Conflict(new { error = "No update is ready." });
            u.RestartToUpdate();
            return Results.NoContent();
        });

        api.MapPost("/app/open-folder", (HttpContext ctx, IServiceProvider sp) =>
        {
            if (!AuthEndpoints.IsLoopback(ctx)) return Results.StatusCode(403);
            if (sp.GetService<IUpdater>()?.InstallFolder is not { } folder || sp.GetService<IShellOpener>() is not { } shell) return Results.NotFound(new { error = "Not installed." });
            shell.OpenLink(folder);
            return Results.NoContent();
        });
    }

    public static object Info(IServiceProvider sp, HostSettings host)
    {
        var updater = sp.GetService<IUpdater>();
        var status = updater?.Status ?? UpdateStatus.NotInstalled;
        return new
        {
            version = Telemetry.Telemetry.ShortVersion,
            channel = Telemetry.Telemetry.Channel,
            telemetry = host.Telemetry,
            tourDone = host.TourDone,
            installFolder = updater?.InstallFolder,
            updates = new { mode = host.Updates.ToString().ToLowerInvariant(), status.Available, status.State, status.Version, status.Notes, status.Error },
        };
    }

    /// <summary>Tells the open pages when an update's state changes (the updater outlives a host; this lives with one).</summary>
    public sealed class UpdateRelay(IServiceProvider sp, HostSettings host, EventHub events) : IHostedService
    {
        private IUpdater? _updater;

        private void OnChanged() => events.Publish("app", Info(sp, host));

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _updater = sp.GetService<IUpdater>();
            if (_updater is not null) _updater.Changed += OnChanged;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            if (_updater is not null) _updater.Changed -= OnChanged;
            return Task.CompletedTask;
        }
    }
}

public sealed record EnabledChoice(bool Enabled);
public sealed record UpdateModeChoice(string? Mode);
