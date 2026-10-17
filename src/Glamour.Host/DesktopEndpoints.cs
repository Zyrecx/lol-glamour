using Glamour.Host.Auth;

namespace Glamour.Host;

/// <summary>What the Windows tray app (Glamour.Desktop) offers the UI. Not registered in the console host or tests, so the endpoints then say "not available".</summary>
public interface IDesktopShell
{
    bool Autostart { get; set; }

    /// <summary>Side of the League client the docked panel takes; null is Auto (whichever side has room).</summary>
    Panel.PanelSide? PanelSide { get; set; }

    /// <summary>Whether the docked panel opens by itself in champ select.</summary>
    bool PanelAutoOpen { get; set; }

    /// <summary>Stops the host and starts a fresh one on the same desktop port (the window reconnects by itself). Used when a setting that is read only at startup changes.</summary>
    Task RestartHostAsync();
}

public static class DesktopEndpoints
{
    public static void MapDesktop(this RouteGroupBuilder api)
    {
        api.MapGet("/desktop", (IServiceProvider sp) => sp.GetService<IDesktopShell>() is { } d ? Info(d) : (object)new { available = false, autostart = false, panelSide = "auto", panelAutoOpen = true });
        api.MapPut("/desktop/panel-side", (HttpContext ctx, IServiceProvider sp, [Microsoft.AspNetCore.Mvc.FromBody] PanelSideChoice body) =>
        {
            if (!AuthEndpoints.IsLoopback(ctx)) return Results.StatusCode(403);
            if (sp.GetService<IDesktopShell>() is not { } d) return Results.NotFound(new { error = "Not running as the desktop app." });
            switch (body.Side)
            {
                case "auto": d.PanelSide = null; break;
                case "right": d.PanelSide = Panel.PanelSide.Right; break;
                case "left": d.PanelSide = Panel.PanelSide.Left; break;
                default: return Results.BadRequest(new { error = "Side is auto, right or left." });
            }
            return Results.Ok(Info(d));
        });
        api.MapPut("/desktop/panel-auto-open", (HttpContext ctx, IServiceProvider sp, [Microsoft.AspNetCore.Mvc.FromBody] EnabledChoice body) =>
        {
            if (!AuthEndpoints.IsLoopback(ctx)) return Results.StatusCode(403);
            if (sp.GetService<IDesktopShell>() is not { } d) return Results.NotFound(new { error = "Not running as the desktop app." });
            d.PanelAutoOpen = body.Enabled;
            return Results.Ok(Info(d));
        });
        // A registry write on this PC: only the PC itself may ask, never a phone.
        api.MapPut("/desktop/autostart", (HttpContext ctx, IServiceProvider sp, [Microsoft.AspNetCore.Mvc.FromBody] EnabledChoice body) =>
        {
            if (!AuthEndpoints.IsLoopback(ctx)) return Results.StatusCode(403);
            if (sp.GetService<IDesktopShell>() is not { } d) return Results.NotFound(new { error = "Not running as the desktop app." });
            try { d.Autostart = body.Enabled; }
            catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { return Results.Json(new { error = ex.Message }, statusCode: 500); }
            return Results.Ok(Info(d));
        });
    }

    private static object Info(IDesktopShell d) => new { available = true, autostart = d.Autostart, panelSide = d.PanelSide?.ToString().ToLowerInvariant() ?? "auto", panelAutoOpen = d.PanelAutoOpen };
}

public sealed record PanelSideChoice(string? Side);
