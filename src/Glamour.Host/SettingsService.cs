using System.Text.Json;
using System.Text.Json.Nodes;
using Glamour.Core;
using Glamour.Host.Profiles;

namespace Glamour.Host;

/// <summary>What a settings change is checked against, for the shared defaults and for one account's effective settings alike.</summary>
public static class SettingsRules
{
    /// <summary>A delayed accept must land well inside the ready check, whose length is itself an assumption (INSIGHTS "Ready check").</summary>
    public static readonly double MaxAutoAcceptDelaySeconds = AutoAcceptFeature.LastSecondDelay.TotalSeconds;
    public const double MaxActionDelaySeconds = 20, MaxLastSecondMarginSeconds = 30;

    /// <summary>What the UI needs to build its settings controls without copying host rules: the account-independent keys and the number ranges.</summary>
    public static object Meta() => new
    {
        sharedKeys = SettingsOverlay.SharedKeys,
        maxAutoAcceptDelaySeconds = MaxAutoAcceptDelaySeconds,
        maxActionDelaySeconds = MaxActionDelaySeconds,
        maxLastSecondMarginSeconds = MaxLastSecondMarginSeconds,
    };

    public static string? Validate(GlamourSettings s)
    {
        if (!NotificationRules.IsValidServer(s.NtfyServer)) return "ntfy server must be an https URL";
        if (s.MutedNotifications.Any(k => !NotificationKinds.All.Contains(k))) return "unknown notification kind";
        if (s.NtfyEnabled && s.WebPushEnabled) return "choose one notification channel: ntfy or web push";
        if (!Enum.IsDefined(s.AutoAccept) || !Enum.IsDefined(s.AcceptTiming) || !Enum.IsDefined(s.BanTiming) || !Enum.IsDefined(s.LockTiming)) return "unknown mode or timing value";
        if (s.Lanes.Any(l => !LaneNames.All.Contains(l.Key) || !Enum.IsDefined(l.Value.BanTiming) || !Enum.IsDefined(l.Value.LockTiming))) return "unknown lane or timing value";
        if (s.ChampionPools.Any(p => !LaneNames.All.Contains(p.Key) || p.Value.Count > GlamourSettings.MaxPoolSize || p.Value.Any(id => id <= 0)))
            return $"champion pool: a known lane and at most {GlamourSettings.MaxPoolSize} champions";
        if (!RankValues.IsValid(s)) return "unknown rank tier, division or queue, or a status message over 200 characters";
        if (s.SkipScreens.Any(x => !SkippableScreens.All.Contains(x))) return "unknown post-game screen";
        if (!InRange(s.LastSecondMarginSeconds, MaxLastSecondMarginSeconds)) return $"last-second margin must be between 0 and {MaxLastSecondMarginSeconds} seconds";
        if (!InRange(s.ActionDelaySeconds, MaxActionDelaySeconds)) return $"ban/lock delay must be between 0 and {MaxActionDelaySeconds} seconds";
        if (s.PinnedQueueIds.Count > 20 || s.PinnedQueueIds.Any(id => id <= 0)) return "pinned queues: at most 20 queue ids";
        if (!InRange(s.AutoAcceptDelaySeconds, MaxAutoAcceptDelaySeconds)) return $"accept delay must be between 0 and {MaxAutoAcceptDelaySeconds} seconds";
        return null;
    }

    private static bool InRange(double v, double max) => double.IsFinite(v) && v >= 0 && v <= max;
}

/// <summary>
/// Owns the two layers of settings. The shared defaults are in <see cref="SettingsStore"/> (and settings.json); each profile keeps its own sparse overrides.
/// The <paramref name="live"/> singleton that every feature reads is the EFFECTIVE view: defaults plus the active account's overrides, rebuilt on every change
/// and whenever the active account changes. Nothing but the defaults is ever written to settings.json.
/// </summary>
public sealed class SettingsService(GlamourSettings live, SettingsStore store, ProfileStore profiles, EventHub events, PresenceOverrideFeature presence, ILogger<SettingsService> log)
{
    private readonly object _sync = new();
    private string? _account;

    public GlamourSettings Defaults => store.Defaults;

    /// <summary>The account whose overrides apply: none while an add is open (the client is signed out) or before any profile is active.</summary>
    private string? ActiveId() => profiles.Data.Adding ? null : profiles.Data.ActiveId;

    /// <summary>Rebuilds the effective settings. At startup call with <paramref name="publish"/> false (nobody is listening, no client yet).</summary>
    public void Recompute(bool publish = true)
    {
        lock (_sync)
        {
            var id = ActiveId();
            var overrides = id is null ? null : profiles.Data.Profiles.FirstOrDefault(p => p.Id == id)?.Overrides;
            GlamourSettings effective;
            try { effective = SettingsOverlay.Apply(Defaults, overrides); }
            catch (JsonException ex)
            {
                log.LogWarning(ex, "Overrides of profile {Id} are unreadable; using the shared defaults", id);
                effective = SettingsOverlay.Apply(Defaults, null);
            }
            if (id != _account) { _account = id; presence.ForgetAccount(); }
            live.CopyFrom(effective);
            if (publish) Publish();
        }
    }

    private void Publish()
    {
        events.Publish("settings", live);
        events.Publish("defaults", Defaults);
        _ = presence.OnSettingsChangedAsync(CancellationToken.None); // apply or restore the presence override now, not at the next client event
    }

    /// <summary>Replaces the shared defaults (PUT). Returns the effective settings.</summary>
    public IResult ReplaceDefaults(GlamourSettings incoming)
    {
        lock (_sync)
        {
            if (SettingsRules.Validate(incoming) is { } error) return Results.BadRequest(new { error });
            incoming.SkipScreens = incoming.SkipScreens.Distinct().ToList();
            incoming.MutedNotifications = incoming.MutedNotifications.Distinct().ToList();
            if (incoming.NtfyEnabled && string.IsNullOrWhiteSpace(incoming.NtfyTopic)) incoming.NtfyTopic = Defaults.NtfyTopic ?? NotificationRules.NewTopic();
            Defaults.CopyFrom(incoming);
            store.Save();
            Recompute();
            return Results.Ok(live);
        }
    }

    /// <summary>Changes only the fields sent, merged into the shared defaults. Two open UIs (PC and phone) each send what they changed,
    /// so one cannot overwrite the other's change with a stale copy of the rest.</summary>
    public IResult PatchDefaults(JsonObject patch)
    {
        lock (_sync)
        {
            var merged = JsonSerializer.SerializeToNode(Defaults, SettingsOverlay.Json)!.AsObject();
            foreach (var (key, value) in patch) merged[key] = value?.DeepClone();
            // Turning one notification channel on turns the other off, so a single PATCH from the UI is enough.
            if (IsTrue(patch["webPushEnabled"]) && patch["ntfyEnabled"] is null) merged["ntfyEnabled"] = false;
            if (IsTrue(patch["ntfyEnabled"]) && patch["webPushEnabled"] is null) merged["webPushEnabled"] = false;
            GlamourSettings? incoming;
            try { incoming = JsonSerializer.Deserialize<GlamourSettings>(merged, SettingsOverlay.Json); }
            catch (JsonException ex) { return Results.BadRequest(new { error = $"Invalid settings: {ex.Message}" }); }
            return incoming is null ? Results.BadRequest(new { error = "Invalid settings." }) : ReplaceDefaults(incoming);
        }
    }

    private static bool IsTrue(JsonNode? n) => n is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    /// <summary>Sets and clears overrides for one account. A set value (null included, which for a champion means "off") replaces the default for that
    /// account; a cleared key follows the shared default again. The effective settings are validated, not just the fragment.</summary>
    public Task<object> PatchOverridesAsync(ProfileService service, string id, JsonObject? set, IEnumerable<string>? clear) =>
        service.ChangeOverridesAsync(id, current =>
        {
            var next = current is null ? new JsonObject() : (JsonObject)current.DeepClone();
            foreach (var (key, value) in set ?? [])
            {
                var k = SettingsOverlay.Canonical(key) ?? throw new ProfileException(400, "bad-setting", $"Unknown setting \"{key}\".");
                if (SettingsOverlay.IsShared(k)) throw new ProfileException(400, "shared-setting", $"\"{k}\" is the same for every account.");
                next[k] = value?.DeepClone();
            }
            foreach (var key in clear ?? []) next.Remove(SettingsOverlay.Canonical(key) ?? key);
            GlamourSettings effective;
            try { effective = SettingsOverlay.Apply(Defaults, next); }
            catch (JsonException ex) { throw new ProfileException(400, "bad-setting", $"Invalid settings: {ex.Message}"); }
            if (SettingsRules.Validate(effective) is { } error) throw new ProfileException(400, "bad-setting", error);
            return next;
        });
}
