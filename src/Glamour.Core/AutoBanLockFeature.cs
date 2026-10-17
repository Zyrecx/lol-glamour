using System.Text.Json.Nodes;
using Glamour.Lcu;
using Microsoft.Extensions.Logging;

namespace Glamour.Core;

/// <summary>
/// Bans and locks the configured champions (per assigned lane when set up) on my turn, after a short delay or in the last seconds of the turn.
/// Never touches an action the user already hovered, and only acts while timer.phase is BAN_PICK. A delayed ban or lock re-reads the session
/// first and gives up if the user changed the hover, the turn ended or automation was switched off meanwhile. A lock set to
/// <see cref="LaneSetup.BestCounter"/> takes the panel's best counter (<paramref name="counters"/>) on the turn and asks again right before it locks.
/// </summary>
public sealed class AutoBanLockFeature(ILcuClient client, IActionGateway gateway, GlamourSettings settings, TimeProvider time, ILogger<AutoBanLockFeature> log,
    AutomationGate? gate = null, ICounterChooser? counters = null) : IFeature
{
    /// <summary>The second look at the best counter, right before the lock, gets this long; past it the champion already hovered is locked.
    /// It starts this much before the last-second margin, so the lock still lands at the margin.</summary>
    public static readonly TimeSpan RechooseTimeout = TimeSpan.FromSeconds(1.5);

    /// <summary>The first look at the best counter on my turn gets this long (a cold cache fetches several tables; the network may be slow); past it nothing is locked.</summary>
    public static readonly TimeSpan ChooseTimeout = TimeSpan.FromSeconds(5);

    private bool Active => gate?.DraftActive ?? true;

    private readonly Dictionary<int, CancellationTokenSource> _handled = [];
    private readonly Dictionary<int, int> _preHovered = []; // pick action id -> the champion Glamour hovered early
    private readonly HashSet<int> _hoveredInPlanning = []; // of those, sent in PLANNING: one retry in BAN_PICK if it left nothing
    private readonly object _waitLock = new();
    private (ChampSelectAction Action, int Champion, CancellationTokenSource Cts)? _waiting; // the delayed ban or lock and the champion Glamour hovered, for Skip

    public Task OnPhaseAsync(string phase, CancellationToken ct)
    {
        if (phase != "ChampSelect") Reset();
        return Task.CompletedTask;
    }

    public async Task OnChampSelectAsync(ChampSelectSession session, CancellationToken ct)
    {
        // ARAM and Mayhem deal champions: their pick actions are all live at once, but there is nothing to ban, pick or lock (SPEC "Game modes").
        if (session.Board is { Mode: not ChampSelectMode.Draft } || !Active) return;
        await PreHoverAsync(session, ct);
        if (session.Timer.Phase != "BAN_PICK") return;
        var action = session.MyActiveAction;
        if (action is not null) LeaveToUserHover(action);
        if (action is null || _handled.ContainsKey(action.Id)) return;

        var isBan = action.Type == "ban";
        var setup = settings.ForLane(session.Lane);
        var champion = isBan ? setup.BanChampionId : setup.LockChampionId;
        if (champion is null) return;
        var bestCounter = !isBan && champion == LaneSetup.BestCounter;
        // A hover Glamour made itself is not the user's: it never blocks the lock. Anything else is the user's and is left alone.
        var ownHover = _preHovered.TryGetValue(action.Id, out var early) && early == action.ChampionId;
        if (action.ChampionId != 0 && !ownHover)
        {
            log.LogInformation("Action {Id} already has champion {Champ}, leaving it to the user", action.Id, action.ChampionId);
            _handled[action.Id] = new CancellationTokenSource();
            return;
        }

        if (isBan && settings.SkipBanIfAllyHovered && session.IsHoveredByAlly(champion.Value))
        {
            log.LogInformation("A teammate has {Champ} hovered, not banning it", champion);
            _handled[action.Id] = new CancellationTokenSource();
            return;
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (bestCounter)
        {
            _handled[action.Id] = cts; // before the wait: session updates arriving meanwhile must not start a second one
            var best = await BestCounterAsync(session, ChooseTimeout, cts.Token);
            if (best is null)
            {
                log.LogInformation("Best counter: nothing to lock on action {Id} (no data, too slow, or nothing pickable)", action.Id);
                return;
            }
            champion = best;
        }
        // bannable-champion-ids is unreliable (live: only [-1] even on my turn), so only picks are pre-checked; bans are verified by effect.
        else if (!isBan)
        {
            var allowed = await client.GetAsync("/lol-champ-select/v1/pickable-champion-ids", ct);
            if (allowed.Body is not JsonArray ids || !ids.Any(n => n?.GetValue<int>() == champion))
            {
                log.LogWarning("Champion {Champ} is not pickable, skipping", champion);
                _handled[action.Id] = new CancellationTokenSource();
                return;
            }
        }

        _handled[action.Id] = cts;
        var timing = isBan ? setup.BanTiming : setup.LockTiming;
        await gateway.HoverAsync(action.Id, champion.Value, ct);

        TimeSpan delay;
        if (timing == ActionTiming.Instant)
        {
            delay = TimeSpan.FromSeconds(Math.Max(0, settings.ActionDelaySeconds));
            if (delay == TimeSpan.Zero)
            {
                await gateway.CompleteAsync(action.Id, champion.Value, action.Type, ct);
                return;
            }
        }
        else delay = session.TimeLeft(time.GetUtcNow()) - TimeSpan.FromSeconds(settings.LastSecondMarginSeconds);
        if (delay < TimeSpan.Zero) delay = TimeSpan.Zero; // clamped so it never overruns
        var completeAt = time.GetUtcNow() + delay;
        // The second look at the best counter starts its own limit before the lock, so the lock still lands at the margin.
        var lookAt = bestCounter && timing == ActionTiming.LastSecond ? completeAt - RechooseTimeout : completeAt;
        if (lookAt < time.GetUtcNow()) lookAt = time.GetUtcNow();
        log.LogInformation("Will {Type} {Champ} in {Delay:0.0}s", action.Type, champion, delay.TotalSeconds);
        lock (_waitLock) _waiting = (action, champion.Value, cts);
        gate?.SetDraftPending(new DraftPending(action.Type, champion.Value, completeAt));
        _ = CompleteLaterAsync(action, champion.Value, lookAt - time.GetUtcNow(), completeAt, bestCounter, cts.Token);
    }

    /// <summary>
    /// The first champion of the best counter list I can pick and no teammate has hovered, for the session's board.
    /// Null when there is none or the list took longer than <paramref name="within"/>.
    /// </summary>
    private async Task<int?> BestCounterAsync(ChampSelectSession session, TimeSpan within, CancellationToken ct)
    {
        if (counters is null || session.Board is not { } board) return null;
        IReadOnlyList<int> order;
        try { order = await counters.ChooseAsync(board, ct).WaitAsync(within, time, ct); }
        catch (TimeoutException) { log.LogWarning("Best counter: no answer within {Seconds}s", within.TotalSeconds); return null; }
        if (order.Count == 0) return null;
        var allowed = await client.GetAsync("/lol-champ-select/v1/pickable-champion-ids", ct);
        if (allowed.Body is not JsonArray ids) return null;
        var pickable = ids.Select(n => n?.GetValue<int>() ?? 0).ToHashSet();
        var pick = order.FirstOrDefault(id => pickable.Contains(id) && !session.IsHoveredByAlly(id));
        log.LogInformation("Best counter: {Pick} of [{Order}]", pick, string.Join(",", order.Take(6)));
        return pick > 0 ? pick : null;
    }

    /// <summary>
    /// Leaves the waiting ban or lock to the user for this champ select (the panel's and phone's Skip); the hover Glamour made stays. False when nothing waits.
    /// </summary>
    public bool SkipPending()
    {
        (ChampSelectAction Action, int Champion, CancellationTokenSource Cts)? waiting;
        lock (_waitLock) { waiting = _waiting; _waiting = null; }
        if (waiting is not { } w) return false;
        try { w.Cts.Cancel(); } catch (ObjectDisposedException) { } // champ select ended at this very moment
        log.LogInformation("Skipped the automatic {Type} on action {Id}", w.Action.Type, w.Action.Id);
        gate?.SetDraftSkipped(w.Action.Type);
        return true;
    }

    /// <summary>
    /// A hover on the waiting action that is not Glamour's (nor its earlier pre-hover, which an update may still carry) is the user's:
    /// the wait ends now, so the countdown goes at once instead of at the deadline's re-check.
    /// </summary>
    private void LeaveToUserHover(ChampSelectAction action)
    {
        (ChampSelectAction Action, int Champion, CancellationTokenSource Cts) w;
        lock (_waitLock)
        {
            if (_waiting is not { } waiting || waiting.Action.Id != action.Id || action.ChampionId == 0 || action.ChampionId == waiting.Champion) return;
            if (_preHovered.TryGetValue(action.Id, out var early) && early == action.ChampionId) return;
            w = waiting;
            _waiting = null;
        }
        try { w.Cts.Cancel(); } catch (ObjectDisposedException) { }
        log.LogInformation("The user hovered {Champ} on action {Id}, leaving it", action.ChampionId, action.Id);
        gate?.SetDraftPending(null);
    }

    /// <param name="delay">Until the session is looked at again (for a last-second best counter, <see cref="RechooseTimeout"/> before <paramref name="completeAt"/>).</param>
    private async Task CompleteLaterAsync(ChampSelectAction action, int champion, TimeSpan delay, DateTimeOffset completeAt, bool bestCounter, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, time, ct);
            var hovered = champion;
            var fresh = await StillOursAsync(action, hovered, ct);
            if (fresh is null) return;
            if (action.Type == "ban" && settings.SkipBanIfAllyHovered && fresh.IsHoveredByAlly(champion))
            {
                log.LogInformation("A teammate hovered {Champ} while waiting, not banning it", champion);
                return;
            }
            if (bestCounter)
            {
                // The board moved on while waiting (more enemies picked, my counter banned or taken, a teammate hovered it): ask again, keep the hover if that fails.
                if (await BestCounterAsync(fresh, RechooseTimeout, ct) is { } again) champion = again;
                else if (fresh.IsHoveredByAlly(champion) || (fresh.Board is { } board && CounterPick.Taken(board).Contains(champion)))
                {
                    log.LogWarning("Best counter: {Champ} is gone and nothing replaces it, leaving action {Id}", champion, action.Id);
                    return;
                }
                // The answer came early: wait out the rest to the margin (live: it took 0.1 s and the lock went in 1.4 s early), then look once more for a user hover.
                var rest = completeAt - time.GetUtcNow();
                if (rest > TimeSpan.Zero)
                {
                    await Task.Delay(rest, time, ct);
                    if (await StillOursAsync(action, hovered, ct) is null) return;
                }
            }
            await gateway.CompleteAsync(action.Id, champion, action.Type, ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { log.LogError(e, "Delayed {Type} failed", action.Type); }
        finally { EndWait(action.Id); }
    }

    /// <summary>The fresh session when the action still waits for me with Glamour's hover on it and automation is on; else null (logged).</summary>
    private async Task<ChampSelectSession?> StillOursAsync(ChampSelectAction action, int hovered, CancellationToken ct)
    {
        if (!Active) { log.LogInformation("Automation was switched off while waiting, not acting on {Id}", action.Id); return null; }
        var fresh = ChampSelectSession.FromJson((await client.GetAsync("/lol-champ-select/v1/session", ct)).Body);
        var now = fresh?.Actions.FirstOrDefault(a => a.Id == action.Id);
        // The hover Glamour made must still be there: anything else is the user's choice (dry run sends no hover, so the action stays empty).
        var stillOurs = now is not null && (now.ChampionId == hovered || (settings.DryRun && now.ChampionId == 0));
        if (fresh?.Timer.Phase == "BAN_PICK" && now is { Completed: false, IsInProgress: true } && stillOurs) return fresh;
        log.LogWarning("Action {Id} changed while waiting (user hover, turn over or stage changed), leaving it: phase {Phase}, champion {Champ}, completed {Done}", action.Id, fresh?.Timer.Phase, now?.ChampionId, now?.Completed);
        return null;
    }

    private void EndWait(int actionId)
    {
        lock (_waitLock)
        {
            if (_waiting?.Action.Id != actionId) return; // skipped, or replaced by a later action
            _waiting = null;
        }
        gate?.SetDraftPending(null);
    }

    /// <summary>
    /// Hovers the pre-select champion on my pending pick, once per action, only when nobody (me included) has a hover on it.
    /// Never locks and never repeats, so a champion the user picks afterwards is not overwritten. Hovering in PLANNING is untested live.
    /// </summary>
    private async Task PreHoverAsync(ChampSelectSession session, CancellationToken ct)
    {
        if (session.Timer.Phase is not ("PLANNING" or "BAN_PICK") || session.MyPendingPick is not { } pick) return;
        // One more try in BAN_PICK when the PLANNING one left nothing: the client may drop writes made in PLANNING (as it does bans and locks).
        if (_preHovered.ContainsKey(pick.Id) && !(session.Timer.Phase == "BAN_PICK" && _hoveredInPlanning.Remove(pick.Id))) return;
        if (settings.ForLane(session.Lane).HoverChampionId is not { } champion) return;
        var mine = session.Board?.MyTeam.FirstOrDefault(p => p.IsMe);
        if (pick.ChampionId != 0 || (mine?.ChampionId ?? 0) != 0) return; // the user already hovered something, leave it
        if (session.IsHoveredByAlly(champion)) return;
        var allowed = await client.GetAsync("/lol-champ-select/v1/pickable-champion-ids", ct);
        if (allowed.Body is not JsonArray ids || !ids.Any(n => n?.GetValue<int>() == champion)) return;
        _preHovered[pick.Id] = champion; // recorded before the call so a failure is not retried on every update
        if (session.Timer.Phase == "PLANNING") _hoveredInPlanning.Add(pick.Id);
        log.LogInformation("Pre-hovering {Champ} on pick action {Id}", champion, pick.Id);
        await gateway.HoverAsync(pick.Id, champion, ct);
    }

    private void Reset()
    {
        lock (_waitLock) _waiting = null;
        _preHovered.Clear();
        _hoveredInPlanning.Clear();
        // Disposed too: a linked source stays registered on the host's lifetime token until then, one per action of every game.
        foreach (var cts in _handled.Values) { cts.Cancel(); cts.Dispose(); }
        _handled.Clear();
    }
}
