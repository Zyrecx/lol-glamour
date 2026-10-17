using Glamour.Host.Push;
using Glamour.Core;

namespace Glamour.Host;

/// <summary>Listens to the event stream and forwards the notifying events to the phone through the chosen channel: ntfy or Web Push.</summary>
public sealed class NotificationService(EventHub events, GlamourSettings settings, AutomationGate gate, INotifier notifier, IWebPushSender webPush, ILogger<NotificationService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var sub = events.Subscribe();
        try
        {
            await foreach (var e in sub.ReadAllAsync(ct))
            {
                var viaNtfy = settings.NtfyEnabled && !string.IsNullOrEmpty(settings.NtfyTopic);
                if (!viaNtfy && !settings.WebPushEnabled || NotificationRules.For(e, settings, gate.Active) is not { } n) continue;
                try
                {
                    if (viaNtfy) await notifier.SendAsync(settings.NtfyServer, settings.NtfyTopic!, n, ct);
                    else await webPush.SendAsync(n, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning("Notification failed: {Message}", ex.Message); }
            }
        }
        catch (OperationCanceledException) { }
    }
}

public sealed class NtfyNotifier(IHttpClientFactory http, IConfiguration config) : INotifier
{
    /// <summary>
    /// Where a tap on the notification should go: the Glamour page on this PC. `Glamour:PublicUrl` if set, else the best LAN address
    /// and the listening port. Null when the host only listens on this PC (a phone could not open it anyway).
    /// </summary>
    public static string? UiUrl(IConfiguration config)
    {
        if (config["Glamour:PublicUrl"] is { Length: > 0 } fixedUrl) return fixedUrl.TrimEnd('/') + "/";
        if (!config.GetValue<bool>("Glamour:AllowLan") || Auth.LanAddress.Hosts() is not { Count: > 0 } hosts) return null;
        return $"http://{hosts[0]}:{config.GetValue("Glamour:Port", 5170)}/";
    }

    public async Task SendAsync(string server, string topic, PhoneNotification n, CancellationToken ct)
    {
        if (!NotificationRules.IsValidServer(server)) throw new InvalidOperationException("ntfy server must be an https URL");
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{server.TrimEnd('/')}/{Uri.EscapeDataString(topic)}") { Content = new StringContent(n.Body) };
        req.Headers.Add("Title", n.Title);
        req.Headers.Add("Priority", n.Priority.ToString());
        req.Headers.Add("Tags", "video_game");
        if ((n.Click ?? UiUrl(config)) is { } click) req.Headers.Add("Click", click);
        using var res = await http.CreateClient("ntfy").SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
    }
}
