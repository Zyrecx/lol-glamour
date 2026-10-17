namespace Glamour.Core;

/// <summary>
/// Whether automation may act right now: the global switch (<see cref="GlamourSettings.AutomationEnabled"/>) and a pause for one game.
/// Every automatic write (accept, ban, lock, pre-hover, honor, screen skip) asks <see cref="Active"/> right before it is sent, so turning
/// automation off also stops work already waiting on a delay. Also holds a pending delayed accept so the phone can show and cancel it.
/// Published as the `automation` event (SPEC "Event stream").
/// </summary>
public sealed class AutomationGate(GlamourSettings settings, EventHub events, TimeProvider time) : IFeature
{
    private static readonly HashSet<string> GamePhases = ["GameStart", "InProgress", "Reconnect", "WaitingForStats", "PreEndOfGame", "EndOfGame"];
    private readonly object _gate = new();
    private bool _paused;
    private bool _gameRan; // the paused game started; the pause ends once it is over
    private string _phase = "None";
    private DateTimeOffset? _acceptAt, _acceptFrom;
    private DraftPending? _draftPending;
    private LoadoutStatus? _loadout;
    private readonly HashSet<string> _draftSkipped = [];
    private bool _draftStopped;
    private readonly List<string> _swapNotes = []; // swaps Glamour sent or accepted in this champ select

    public bool Active => settings.AutomationEnabled && !Paused;

    public bool Paused { get { lock (_gate) return _paused; } }

    /// <summary>Ban, lock and pre-select may act: <see cref="Active"/> and not stopped for this champ select.</summary>
    public bool DraftActive => Active && !DraftStopped;

    public bool DraftStopped { get { lock (_gate) return _draftStopped; } }

    /// <summary>
    /// Stops auto ban, lock and pre-select for the champ select running now only (the panel's Stop). Accept, honor and screen skips are untouched,
    /// and it ends with this champ select whether a game follows or not.
    /// </summary>
    public bool SetDraftStopped(bool stopped)
    {
        lock (_gate)
        {
            if (stopped && _phase != "ChampSelect") return false;
            if (_draftStopped == stopped) return true;
            _draftStopped = stopped;
        }
        Publish();
        return true;
    }

    /// <summary>
    /// Pauses automation for one game and its post-game screens: the game running now (pressed in game or post-game), else the next game that starts.
    /// A dodge or a failed ready check does not use it up.
    /// </summary>
    public void SetPaused(bool paused)
    {
        lock (_gate)
        {
            if (_paused == paused) return;
            _paused = paused;
            _gameRan = paused && GamePhases.Contains(_phase);
        }
        Publish();
    }

    /// <summary>The moment a delayed auto-accept will fire, or null when none is waiting.</summary>
    public void SetAcceptPending(DateTimeOffset? at)
    {
        lock (_gate)
        {
            if (_acceptAt == at) return;
            _acceptAt = at;
            _acceptFrom = at is null ? null : time.GetUtcNow();
        }
        Publish();
    }

    /// <summary>The waiting accept's start and the moment it fires, while it may still fire (the switch on, not paused); for the desktop's Accept ring.</summary>
    public (DateTimeOffset From, DateTimeOffset At)? AcceptWait
    {
        get
        {
            lock (_gate)
                return _acceptAt is { } at && _acceptFrom is { } from && settings.AutomationEnabled && !_paused ? (from, at) : null;
        }
    }

    /// <summary>The ban or lock auto ban/lock is waiting to send, or null; shown as a countdown with Skip.</summary>
    public void SetDraftPending(DraftPending? pending)
    {
        lock (_gate)
        {
            if (_draftPending == pending) return;
            _draftPending = pending;
        }
        Publish();
    }

    /// <summary>The automatic matchup loadout in this champ select: waiting on its delay (counts down with Cancel), done or cancelled; null before any.</summary>
    public void SetLoadout(LoadoutStatus? status)
    {
        lock (_gate)
        {
            if (_loadout == status) return;
            _loadout = status;
        }
        Publish();
    }

    /// <summary>Marks the action type ("ban" or "pick") as skipped by the user for this champ select, so the UI stops promising it.</summary>
    public void SetDraftSkipped(string type)
    {
        lock (_gate)
        {
            if (_draftPending?.Type == type) _draftPending = null;
            if (!_draftSkipped.Add(type)) return;
        }
        Publish();
    }

    /// <summary>A swap Glamour sent or accepted in this champ select ("Asked Mid for their lane"), shown among the panel's automation rows.</summary>
    public void AddSwapNote(string note)
    {
        lock (_gate) _swapNotes.Add(note);
        Publish();
    }

    public object Snapshot()
    {
        lock (_gate)
            return new
            {
                paused = _paused,
                draftStopped = _draftStopped,
                acceptPending = _acceptAt is { } at && settings.AutomationEnabled && !_paused ? new { timeLeftMs = LeftMs(at) } : null,
                draftPending = _draftPending is { } d && settings.AutomationEnabled && !_paused && !_draftStopped ? new { type = d.Type, championId = d.ChampionId, timeLeftMs = LeftMs(d.At) } : null,
                draftSkipped = _draftSkipped.Order().ToArray(),
                swapNotes = _swapNotes.ToArray(),
                loadout = _loadout is { } l ? new { state = l.State, enemyId = l.EnemyId, timeLeftMs = l.At is { } until ? LeftMs(until) : (long?)null } : null,
            };
    }

    private long LeftMs(DateTimeOffset at) => (long)Math.Max(0, (at - time.GetUtcNow()).TotalMilliseconds);

    private void Publish() => events.Publish("automation", Snapshot());

    public Task OnPhaseAsync(string phase, CancellationToken ct)
    {
        bool ended = false, draftOver;
        lock (_gate)
        {
            _phase = phase;
            draftOver = phase != "ChampSelect" && (_draftStopped || _draftPending is not null || _draftSkipped.Count > 0 || _swapNotes.Count > 0 || _loadout is not null);
            if (draftOver) { _draftStopped = false; _draftPending = null; _draftSkipped.Clear(); _swapNotes.Clear(); _loadout = null; }
            if (_paused)
            {
                if (phase is "GameStart" or "InProgress" or "Reconnect") _gameRan = true;
                ended = _gameRan && !GamePhases.Contains(phase);
                if (ended) { _paused = false; _gameRan = false; }
            }
        }
        if (ended || draftOver) Publish();
        return Task.CompletedTask;
    }

    public Task OnChampSelectAsync(ChampSelectSession session, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>A ban or lock waiting on its delay: the action type ("ban" or "pick"), the champion and when it is sent.</summary>
public sealed record DraftPending(string Type, int ChampionId, DateTimeOffset At);

/// <summary>The automatic matchup loadout: <see cref="State"/> is "waiting" (sent at <see cref="At"/>), "done" or "cancelled"; against <see cref="EnemyId"/>.</summary>
public sealed record LoadoutStatus(string State, int EnemyId, DateTimeOffset? At = null);
