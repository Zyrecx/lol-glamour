using Glamour.Lcu;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Glamour.Core;

/// <summary>
/// Mode Always accepts, at once, after <see cref="GlamourSettings.AutoAcceptDelaySeconds"/> or at the last second (<see cref="LastSecondDelay"/>); a waiting accept can be cancelled for this match
/// (<see cref="CancelPending"/>) and stops by itself when the ready check ends or is answered elsewhere (INSIGHTS "Ready check").
/// Phone leaves the decision to the phone, Off does nothing.
/// </summary>
public sealed class AutoAcceptFeature(IActionGateway gateway, GlamourSettings settings, AutomationGate? gate = null, TimeProvider? time = null,
    ILogger<AutoAcceptFeature>? log = null) : IFeature
{
    private const string ReadyCheckPath = "/lol-matchmaking/v1/ready-check";
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly ILogger _log = log ?? NullLogger<AutoAcceptFeature>.Instance;
    private readonly object _lock = new();
    private CancellationTokenSource? _pending;

    /// <summary>The waiting accept (tests await it).</summary>
    public Task Idle { get; private set; } = Task.CompletedTask;

    private bool Active => gate?.Active ?? true;

    /// <summary>
    /// "Last second": the latest the accept is sent, counted from the ready check opening. Keeps 2 s of the 13 s window in hand
    /// (INSIGHTS "Ready check"); a missed ready check costs a queue penalty. Also the longest delay allowed.
    /// </summary>
    public static readonly TimeSpan LastSecondDelay = EventTracker.ReadyCheckWindow - TimeSpan.FromSeconds(2);

    /// <summary>How long after the ready check opens Glamour accepts.</summary>
    public static TimeSpan DelayFor(GlamourSettings s) =>
        s.AcceptTiming == ActionTiming.LastSecond ? LastSecondDelay : TimeSpan.FromSeconds(Math.Clamp(s.AutoAcceptDelaySeconds, 0, LastSecondDelay.TotalSeconds));

    public async Task OnPhaseAsync(string phase, CancellationToken ct)
    {
        if (phase != "ReadyCheck") { Stop(); return; }
        if (settings.AutoAccept != AutoAcceptMode.Always || !Active || _custom) return;
        var delay = DelayFor(settings);
        if (delay == TimeSpan.Zero) { await gateway.AcceptReadyCheckAsync(ct); return; }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        lock (_lock) { _pending?.Cancel(); _pending = cts; }
        gate?.SetAcceptPending(_time.GetUtcNow() + delay);
        _log.LogInformation("Accepting in {Delay:0.0}s unless cancelled", delay.TotalSeconds);
        Idle = AcceptLaterAsync(delay, cts);
    }

    private async Task AcceptLaterAsync(TimeSpan delay, CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(delay, _time, cts.Token);
            // Settings or the switch may have changed while waiting.
            if (settings.AutoAccept == AutoAcceptMode.Always && Active) await gateway.AcceptReadyCheckAsync(cts.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { _log.LogError(e, "Delayed accept failed"); }
        finally { Finish(cts); cts.Dispose(); } // disposed here, its only owner: a linked source stays registered on the host's lifetime token until then
    }

    /// <summary>Stops the waiting accept for this match only; the setting stays. False when nothing was waiting.</summary>
    public bool CancelPending()
    {
        lock (_lock)
            if (_pending is null) return false;
        _log.LogInformation("Delayed accept cancelled for this match");
        Stop();
        return true;
    }

    private void Stop()
    {
        CancellationTokenSource? cts;
        lock (_lock) { cts = _pending; _pending = null; }
        if (cts is null) return;
        try { cts.Cancel(); } catch (ObjectDisposedException) { } // the wait finished at this very moment
        gate?.SetAcceptPending(null);
    }

    private void Finish(CancellationTokenSource cts)
    {
        lock (_lock)
        {
            if (_pending != cts) return; // replaced or stopped already
            _pending = null;
        }
        gate?.SetAcceptPending(null);
    }

    // A custom game passes through ReadyCheck for a moment with nothing to accept (accept -> 500, LIVE glamour.log 2026-10-08 21:33:54).
    private bool _custom;

    /// <summary>Accepted or declined in the client (or from the phone): nothing left to wait for. Also notes whether the lobby is a custom game.</summary>
    public Task OnEventAsync(LcuEvent e, CancellationToken ct)
    {
        if (e.Uri == "/lol-lobby/v2/lobby") _custom = e.EventType != "Delete" && e.Data?["gameConfig"]?["isCustom"]?.GetValue<bool>() == true;
        if (e.Uri == ReadyCheckPath && (e.EventType == "Delete" || e.Data?["playerResponse"]?.GetValue<string>() is "Accepted" or "Declined")) Stop();
        return Task.CompletedTask;
    }

    public Task OnChampSelectAsync(ChampSelectSession session, CancellationToken ct) => Task.CompletedTask;
}
