using System.Text.Json.Nodes;

namespace Glamour.Core;

/// <summary>
/// The in-game clock from the game's Live Client Data (`GET https://127.0.0.1:2999/liveclientdata/gamestats` → `gameTime` in seconds, `gameMode`; LIVE,
/// INSIGHTS "Live Client Data"). Sent as `gameTimeMs` as of the message so the phone counts on its own; republished only when the game starts or ends,
/// or the clock drifts (a pause in the game), never per tick. Published as the `game` event.
/// </summary>
public sealed class GameClock(EventHub events, TimeProvider time)
{
    /// <summary>A correction smaller than this is not worth a message.</summary>
    public static readonly TimeSpan Drift = TimeSpan.FromSeconds(2);

    /// <summary>Failed reads in a row before the game counts as over (one can fail while the game is busy).</summary>
    public const int MissesToEnd = 2;

    private static readonly HashSet<string> GamePhases = ["GameStart", "InProgress", "Reconnect"];
    private readonly object _gate = new();
    private DateTimeOffset? _start; // host time when gameTime was 0
    private string _mode = "";
    private int _misses;

    public bool InGame { get { lock (_gate) return _start is not null; } }

    /// <summary>The running game's `gameMode` ("KIWI" in Mayhem); empty when no game runs.</summary>
    public string Mode { get { lock (_gate) return _start is null ? "" : _mode; } }

    /// <summary>Whether the game API is worth asking: the client says a game runs, or the client is gone while the game process runs (the LCU may drop at game start).</summary>
    public static bool ShouldPoll(string phase, bool connected, bool gameProcessRunning) => connected ? GamePhases.Contains(phase) : gameProcessRunning; // the phase is stale once the client is gone

    /// <summary>A gamestats body, or null when the read failed.</summary>
    public void Update(JsonNode? stats)
    {
        lock (_gate)
        {
            var now = time.GetUtcNow();
            if (stats?["gameTime"] is not JsonValue v || !v.TryGetValue<double>(out var seconds) || seconds <= 0) // loading screen: no clock yet
            {
                if (stats is null && _start is not null && ++_misses >= MissesToEnd) End();
                return;
            }
            _misses = 0;
            var start = now - TimeSpan.FromSeconds(seconds);
            var mode = (stats["gameMode"] is JsonValue m && m.TryGetValue<string>(out var gm) ? gm : "");
            if (_start is { } known && (start - known).Duration() < Drift && mode == _mode) return;
            _start = start;
            _mode = mode;
            events.Publish("game", SnapshotLocked());
        }
    }

    /// <summary>The client left the game phases while connected: the game is over even if the game API still answers for a moment.</summary>
    public void OnPhase(string phase, bool connected)
    {
        lock (_gate)
            if (connected && !GamePhases.Contains(phase) && _start is not null) End();
    }

    private void End()
    {
        _start = null;
        _mode = "";
        _misses = 0;
        events.Publish("game", SnapshotLocked());
    }

    public object Snapshot()
    {
        lock (_gate) return SnapshotLocked();
    }

    private object SnapshotLocked() => new
    {
        inGame = _start is not null,
        gameTimeMs = _start is { } s ? (long)Math.Max(0, (time.GetUtcNow() - s).TotalMilliseconds) : 0,
        gameMode = _mode,
    };
}
