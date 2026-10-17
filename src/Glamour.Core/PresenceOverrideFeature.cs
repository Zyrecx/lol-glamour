using System.Text.Json.Nodes;
using Glamour.Lcu;
using Microsoft.Extensions.Logging;

namespace Glamour.Core;

/// <summary>Values the presence rank fields accept (INSIGHTS "Rank, status, badges"). All presence values are strings.</summary>
public static class RankValues
{
    public static readonly string[] Tiers = ["IRON", "BRONZE", "SILVER", "GOLD", "PLATINUM", "EMERALD", "DIAMOND", "MASTER", "GRANDMASTER", "CHALLENGER"];
    public static readonly string[] Divisions = ["I", "II", "III", "IV", "NA"];
    public static readonly string[] Queues = ["RANKED_SOLO_5x5", "RANKED_FLEX_SR", "RANKED_TFT"];
    public const int MaxStatusLength = 200;

    public static bool IsValid(GlamourSettings s) =>
        (!s.RankOverrideEnabled || (Tiers.Contains(s.RankTier) && Divisions.Contains(s.RankDivision) && Queues.Contains(s.RankQueue)))
        && (s.StatusMessageOverride is null || s.StatusMessageOverride.Length <= MaxStatusLength);
}

/// <summary>
/// Keeps my chat presence at the rank and status message the user chose. The client rewrites presence from real data (after a game, INSIGHTS),
/// so every presence update is compared with the target and only a difference is written back (never on our own echo, so no loop).
/// Writes are rate limited in case the client keeps reverting. Turning the override off puts the real tier back from my ranked stats.
/// Live: the merge-style PUT of the whole `lol` object worked on this client; the status message PUT and the restore are [UNVERIFIED].
/// </summary>
public sealed class PresenceOverrideFeature(ILcuClient client, IActionGateway gateway, GlamourSettings settings, TimeProvider time, ILogger<PresenceOverrideFeature> log) : IFeature
{
    private const string MePath = "/lol-chat/v1/me";
    private const int MaxWritesPerWindow = 6;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Queue<DateTimeOffset> _writes = new();
    private bool _rankApplied, _statusApplied;
    private string? _realStatus;

    public Task OnPhaseAsync(string phase, CancellationToken ct) => ReconcileAsync(null, ct);
    public Task OnChampSelectAsync(ChampSelectSession session, CancellationToken ct) => Task.CompletedTask;
    public Task OnResyncAsync(CancellationToken ct) => ReconcileAsync(null, ct);
    public Task OnSettingsChangedAsync(CancellationToken ct) => ReconcileAsync(null, ct);

    /// <summary>Another Riot account is signed in now. What this remembers (applied flags, the real status) belonged to the old one and must not be written onto the new one.</summary>
    public void ForgetAccount()
    {
        _rankApplied = _statusApplied = false;
        _realStatus = null;
    }

    public Task OnEventAsync(LcuEvent e, CancellationToken ct) =>
        e.Uri == MePath && e.EventType != "Delete" ? ReconcileAsync(e.Data, ct) : Task.CompletedTask;

    private async Task ReconcileAsync(JsonNode? me, CancellationToken ct)
    {
        if (!settings.RankOverrideEnabled && !_rankApplied && settings.StatusMessageOverride is null && !_statusApplied) return;
        await _gate.WaitAsync(ct);
        try
        {
            if (me?["lol"] is null)
            {
                if (await client.GetAsync(MePath, ct) is not { Status: 200, Body: { } fresh }) return;
                me = fresh;
            }
            await ApplyAsync(me, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "Presence override failed");
        }
        finally { _gate.Release(); }
    }

    private async Task ApplyAsync(JsonNode me, CancellationToken ct)
    {
        if (me["lol"] is not JsonObject lol) return;

        if (settings.RankOverrideEnabled)
        {
            if (!RankValues.IsValid(settings)) return;
            if (lol["rankedLeagueTier"]?.GetValue<string>() != settings.RankTier || lol["rankedLeagueDivision"]?.GetValue<string>() != settings.RankDivision
                || lol["rankedLeagueQueue"]?.GetValue<string>() != settings.RankQueue)
            {
                if (!AllowWrite()) return;
                var merged = (JsonObject)lol.DeepClone(); // the whole lol object goes back, so other presence fields survive (the tested style)
                merged["rankedLeagueTier"] = settings.RankTier;
                merged["rankedLeagueDivision"] = settings.RankDivision;
                merged["rankedLeagueQueue"] = settings.RankQueue;
                log.LogInformation("Presence rank {Real} -> {Tier} {Div}", lol["rankedLeagueTier"], settings.RankTier, settings.RankDivision);
                await gateway.SetPresenceAsync(new { lol = merged }, ct);
                _rankApplied = true;
            }
        }
        else if (_rankApplied)
        {
            _rankApplied = false;
            await RestoreRankAsync(lol, ct);
        }

        var current = me["statusMessage"]?.GetValue<string>() ?? "";
        if (settings.StatusMessageOverride is { } status)
        {
            if (current != status && AllowWrite())
            {
                if (!_statusApplied) _realStatus = current;
                await gateway.SetPresenceAsync(new { statusMessage = status }, ct);
                _statusApplied = true;
            }
        }
        else if (_statusApplied)
        {
            _statusApplied = false;
            if (_realStatus is not null && current != _realStatus) await gateway.SetPresenceAsync(new { statusMessage = _realStatus }, ct);
        }
    }

    /// <summary>Real tier, division and wins for the chosen queue from my own ranked stats. If they cannot be read the client puts them back itself.</summary>
    private async Task RestoreRankAsync(JsonObject lol, CancellationToken ct)
    {
        var puuid = lol["puuid"]?.GetValue<string>();
        var queue = lol["rankedLeagueQueue"]?.GetValue<string>() ?? "";
        if (string.IsNullOrEmpty(puuid) || await client.GetAsync($"/lol-ranked/v1/ranked-stats/{Uri.EscapeDataString(puuid)}", ct) is not { Status: 200, Body: { } stats }) return;
        if (stats["queueMap"]?[queue] is not JsonObject real) return;
        var merged = (JsonObject)lol.DeepClone();
        merged["rankedLeagueTier"] = real["tier"]?.GetValue<string>() ?? "";
        merged["rankedLeagueDivision"] = real["division"]?.GetValue<string>() ?? "NA";
        if (real["wins"] is { } wins) merged["rankedWins"] = wins.ToString();
        log.LogInformation("Restoring real presence rank {Tier} {Div}", merged["rankedLeagueTier"], merged["rankedLeagueDivision"]);
        await gateway.SetPresenceAsync(new { lol = merged }, ct);
    }

    /// <summary>At most <see cref="MaxWritesPerWindow"/> writes a minute: a client that keeps reverting must not start a fight.</summary>
    private bool AllowWrite()
    {
        var now = time.GetUtcNow();
        while (_writes.Count > 0 && now - _writes.Peek() > Window) _writes.Dequeue();
        if (_writes.Count >= MaxWritesPerWindow)
        {
            log.LogWarning("Presence keeps changing back; pausing the override for a moment");
            return false;
        }
        _writes.Enqueue(now);
        return true;
    }
}
