using Glamour.Lcu;
using Microsoft.Extensions.Logging;

namespace Glamour.Core;

/// <summary>Post-game screens Glamour can skip, each opt-in through <see cref="GlamourSettings.SkipScreens"/>. The honor screen has its own settings.</summary>
public static class SkippableScreens
{
    public const string LevelUp = "challenge-level-up-celebration", Missions = "missions-celebration", Ranked = "ranked-celebration", Stats = "end-of-game-stats";

    /// <summary>The first three are PreEndOfGame sequence events, finished by `complete/{name}`; <see cref="Stats"/> is the EndOfGame stats screen.</summary>
    public static readonly string[] All = [LevelUp, Missions, Ranked, Stats];
}

/// <summary>
/// Finishes the post-game screens the user opted into. A sequence screen is completed when its name shows up as the current sequence event
/// (once per name per game); the stats screen is dismissed shortly after the phase becomes EndOfGame.
/// Live: only `missions-celebration` is known to be completed this way by another tool (INSIGHTS "Post-game"), the rest are [SPEC].
/// </summary>
public sealed class ScreenSkipFeature(ILcuClient client, IActionGateway gateway, GlamourSettings settings, ILogger<ScreenSkipFeature> log,
    Func<TimeSpan, CancellationToken, Task>? delay = null, AutomationGate? gate = null) : IFeature
{
    private const string SequencePath = "/lol-pre-end-of-game/v1/currentSequenceEvent";
    private static readonly TimeSpan StatsDelay = TimeSpan.FromMilliseconds(1600); // LeagueAkari waits about this long in EndOfGame
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;
    private readonly HashSet<string> _done = []; // touched by the event loop and by the phase run on the thread pool: only under its lock
    private string _phase = "None";

    /// <summary>The run started by the last phase change (tests await it).</summary>
    public Task Idle { get; private set; } = Task.CompletedTask;

    public Task OnPhaseAsync(string phase, CancellationToken ct)
    {
        _phase = phase;
        if (phase is not ("WaitingForStats" or "PreEndOfGame" or "EndOfGame")) lock (_done) _done.Clear();
        if (settings.SkipScreens.Count > 0 && (phase is "PreEndOfGame" or "EndOfGame") && (gate?.Active ?? true))
            Idle = Task.Run(() => RunAsync(phase, ct), CancellationToken.None);
        return Task.CompletedTask;
    }

    public Task OnChampSelectAsync(ChampSelectSession session, CancellationToken ct) => Task.CompletedTask;

    public async Task OnEventAsync(LcuEvent e, CancellationToken ct)
    {
        if (e.Uri == SequencePath && e.EventType != "Delete" && e.Data?["name"] is { } name && name.GetValue<string>() is { Length: > 0 } n)
            await SkipSequenceAsync(n, ct);
    }

    private async Task RunAsync(string phase, CancellationToken ct)
    {
        try
        {
            if (phase == "PreEndOfGame") // the event may have fired before we were listening
            {
                if (await client.GetAsync(SequencePath, ct) is { Status: 200, Body: { } body } && body["name"]?.GetValue<string>() is { Length: > 0 } n)
                    await SkipSequenceAsync(n, ct);
                return;
            }
            if (!settings.SkipScreens.Contains(SkippableScreens.Stats)) return;
            await _delay(StatsDelay, ct);
            if (_phase == "EndOfGame" && (gate?.Active ?? true) && Once(SkippableScreens.Stats)) await gateway.DismissStatsAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "Skipping a post-game screen failed");
        }
    }

    /// <summary>True the first time a screen is seen this game.</summary>
    private bool Once(string name)
    {
        lock (_done) return _done.Add(name);
    }

    private async Task SkipSequenceAsync(string name, CancellationToken ct)
    {
        if (!(gate?.Active ?? true) || name == SkippableScreens.Stats || !SkippableScreens.All.Contains(name) || !settings.SkipScreens.Contains(name) || !Once(name)) return;
        log.LogInformation("Skipping post-game screen {Screen}", name);
        await gateway.CompleteSequenceAsync(name, ct);
    }
}
