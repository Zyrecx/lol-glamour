using System.Security.Cryptography;
using System.Text.Json;

namespace Glamour.Core;

/// <summary>Click is the web page to open when the notification is tapped (ntfy's Click header); null opens ntfy itself.</summary>
public sealed record PhoneNotification(string Title, string Body, int Priority, string? Click = null, string? Action = null);

/// <summary>Buttons a Web Push notification can carry. The service worker maps each to an API call (ntfy has no buttons and ignores them).</summary>
public static class NotificationActions { public const string Accept = "accept", CancelAutoAccept = "cancel-auto-accept"; }

/// <summary>Sends one notification to the phone. Implemented over ntfy in the host; faked in tests.</summary>
public interface INotifier
{
    Task SendAsync(string server, string topic, PhoneNotification n, CancellationToken ct);
}

/// <summary>The groups the user can switch on or off. Each notification belongs to exactly one.</summary>
public static class NotificationKinds
{
    public const string Match = "match", ChampSelect = "champ-select", Turn = "turn", Swap = "swap", Dodge = "dodge";
    public static readonly string[] All = [Match, ChampSelect, Turn, Swap, Dodge];

    public static string? Of(string eventType) => eventType switch
    {
        "ready-check" or "ready-check-result" => Match,
        "champselect-start" or "cs-stage" => ChampSelect,
        "myturn" => Turn,
        "swap" => Swap,
        "dodge" => Dodge,
        _ => null,
    };
}

/// <summary>Which events become a phone notification. Pure, so every rule is unit-tested.</summary>
public static class NotificationRules
{
    /// <summary><paramref name="automationActive"/> false (switched off or paused): the phone is told about everything automation would have handled.</summary>
    public static PhoneNotification? For(GlamourEvent e, GlamourSettings settings, bool automationActive = true)
    {
        if (NotificationKinds.Of(e.Type) is { } kind && settings.MutedNotifications.Contains(kind)) return null;
        var autoAccepts = automationActive && settings.AutoAccept == AutoAcceptMode.Always;
        var d = e.Data is null ? default : JsonSerializer.SerializeToElement(e.Data, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        string Str(string key) => d.ValueKind == JsonValueKind.Object && d.TryGetProperty(key, out var v) ? v.ToString() : "";
        return e.Type switch
        {
            // Always-accept handles the popup itself; only ping when the phone is meant to accept, or may still cancel a delayed accept.
            "ready-check" when Str("state") == "InProgress" && Str("playerResponse") == "None" && !autoAccepts
                => new("Match found", "Accept it before the timer runs out.", 5, Action: NotificationActions.Accept),
            "ready-check" when Str("state") == "InProgress" && Str("playerResponse") == "None" && AutoAcceptFeature.DelayFor(settings) > TimeSpan.Zero
                => new("Match found", $"Glamour accepts in {AutoAcceptFeature.DelayFor(settings).TotalSeconds:0.#} s. Open to cancel.", 4, Action: NotificationActions.CancelAutoAccept),
            "ready-check-result" when Str("result") == "failed" => new("Match did not start", "Someone declined or the ready check expired.", 3),
            "champselect-start" => new("Champion select started", "All players accepted.", 3),
            "cs-stage" when Str("stage") == "FINALIZATION" => new("Lock-in phase", "Picks are done; runes and spells now.", 2),
            // The auto feature acts within a moment, so a ping would only be noise.
            "myturn" when !automationActive || !(Str("type") == "ban" ? settings.ForLane(Str("lane") is { Length: > 0 } l ? l : null).BanChampionId : settings.ForLane(Str("lane") is { Length: > 0 } l2 ? l2 : null).LockChampionId).HasValue
                => new($"Your turn to {Str("type")}", "Choose before the timer ends.", 5),
            "swap" when Str("state") == "RECEIVED" => new($"Swap request ({Str("kind")})", "A teammate wants to swap with you.", 4),
            "dodge" when Str("byMe") != "True" => new("Someone dodged", "Champion select was cancelled.", 4),
            _ => null,
        };
    }

    /// <summary>A hard-to-guess topic name: whoever knows it can read the notifications.</summary>
    public static string NewTopic() => "glamour-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(10)).ToLowerInvariant();

    /// <summary>The server is called by the PC on behalf of any paired device, so only https URLs are accepted.</summary>
    public static bool IsValidServer(string? server) =>
        Uri.TryCreate(server, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(u.UserInfo);
}
