using Glamour.Core;
using Glamour.Host.Network;

namespace Glamour.Host.Telemetry;

/// <summary>
/// Usage counts (SPEC "Publishing"): one "start" event per run and one "game" event per game, so a user costs a couple of events a game.
/// The game event carries the queue, which automation was switched on, and how often each client write was made and by whom
/// ("auto_lock": 1, "phone_hover": 2); the counts of a champ select that ended without a game go with the next game.
/// </summary>
public sealed class UsageTally(Telemetry telemetry, GlamourSettings settings, IServiceProvider services) : IUsage, IFeature, IHostedService
{
    private readonly Dictionary<string, int> _counts = [];
    private readonly Lock _gate = new();
    private bool _inGame;
    private int _queue;

    public void Count(string what)
    {
        lock (_gate) _counts[what] = _counts.GetValueOrDefault(what) + 1;
    }

    public Task OnPhaseAsync(string phase, CancellationToken ct)
    {
        if (phase is "GameStart" or "InProgress")
        {
            if (!_inGame) _queue = services.GetService<Engine>()?.Tracker.QueueId ?? 0; // read now: after the game the lobby may be another queue
            _inGame = true;
        }
        // Back in the client after a game: it ended (honor and the post-game screens counted in).
        else if (_inGame && phase is "None" or "Lobby" or "Matchmaking" or "ReadyCheck" or "ChampSelect") Flush();
        return Task.CompletedTask;
    }

    public Task OnChampSelectAsync(ChampSelectSession session, CancellationToken ct) => Task.CompletedTask;

    private void Flush()
    {
        _inGame = false;
        Dictionary<string, object> props;
        lock (_gate)
        {
            props = _counts.ToDictionary(c => c.Key, c => (object)c.Value);
            _counts.Clear();
        }
        props["queue"] = _queue.ToString(System.Globalization.CultureInfo.InvariantCulture);
        foreach (var (key, on) in Switches()) props[key] = on ? "on" : "off";
        telemetry.Track("game", props);
    }

    /// <summary>Which automation and advice is on (the settings in effect for the signed-in account), as on/off only.</summary>
    private IEnumerable<(string, bool)> Switches()
    {
        var lanes = LaneNames.All.Select(settings.ForLane).Append(settings.ForLane(null)).ToList();
        yield return ("automation", settings.AutomationEnabled);
        yield return ("set_accept", settings.AutoAccept == AutoAcceptMode.Always);
        yield return ("set_ban", lanes.Any(l => l.BanChampionId is > 0));
        yield return ("set_lock", lanes.Any(l => l.LockChampionId is > 0));
        yield return ("set_best_counter", lanes.Any(l => l.LockChampionId == LaneSetup.BestCounter));
        yield return ("set_preselect", lanes.Any(l => l.HoverChampionId is > 0));
        yield return ("set_loadout", settings.MatchupLoadout);
        yield return ("set_swaps", settings.LaneSwapAccept || settings.LaneSwapAsk || settings.PickSlotAccept || settings.PickSlotAsk);
        yield return ("set_honor", settings.AutoHonorEnabled);
        yield return ("set_skip_screens", settings.SkipHonorScreen || settings.SkipScreens.Count > 0);
        yield return ("set_augments", settings.AugmentTips);
        yield return ("set_loading_parties", settings.LoadingParties);
        yield return ("set_rank_override", settings.RankOverrideEnabled);
        yield return ("set_push", settings.WebPushEnabled || settings.NtfyEnabled);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!telemetry.FirstStart()) return Task.CompletedTask;
        var phone = services.GetService<PhoneAccess>();
        var profiles = services.GetService<Profiles.ProfileStore>()?.Data.Profiles.Count ?? 0;
        telemetry.Track("start", new Dictionary<string, object>
        {
            ["app"] = services.GetService<IDesktopShell>() is null ? "console" : "desktop",
            ["phone_access"] = phone is { Blocked: false } && services.GetRequiredService<IConfiguration>().GetValue<bool>("Glamour:AllowLan") ? "on" : "off",
            ["accounts"] = profiles,
        });
        return Task.CompletedTask;
    }

    /// <summary>Glamour closed in a game (or before it went back to the client): that game still counts.</summary>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (_inGame) Flush();
        return Task.CompletedTask;
    }
}
