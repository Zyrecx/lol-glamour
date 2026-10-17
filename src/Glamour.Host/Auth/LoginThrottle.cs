namespace Glamour.Host.Auth;

/// <summary>
/// Slows guessing per client address: a few free failures, then a lock that doubles up to 15 minutes. A success clears it.
/// PIN guesses also spend one budget shared by every address, so a guesser who keeps changing address still gets only a few tries an hour.
/// </summary>
public sealed class LoginThrottle(TimeProvider time)
{
    private const int FreeFailures = 5, SharedFreeFailures = 20, MaxEntries = 10_000;
    private const string Everyone = "*"; // never an address
    private static readonly TimeSpan BaseLock = TimeSpan.FromSeconds(30), MaxLock = TimeSpan.FromMinutes(15), Forget = TimeSpan.FromHours(1);

    private sealed record Entry(int Failures, DateTimeOffset LockedUntil, DateTimeOffset Last);

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = [];

    /// <summary>Time left on the lock for this client (and, with <paramref name="shared"/>, on the shared one), or null when it may try.</summary>
    public TimeSpan? RetryAfter(string key, bool shared = false)
    {
        lock (_gate)
        {
            var now = time.GetUtcNow();
            var left = Left(key, now);
            if (shared && Left(Everyone, now) is { } all && (left is null || all > left)) left = all;
            return left;
        }
    }

    public void Failure(string key, bool shared = false)
    {
        lock (_gate)
        {
            var now = time.GetUtcNow();
            if (_entries.Count > 1000) Prune(now);
            Count(key, FreeFailures, now);
            if (shared) Count(Everyone, SharedFreeFailures, now);
        }
    }

    /// <summary>Clears this client only; the shared budget runs out by itself, so a guesser cannot reset it by also signing in somewhere.</summary>
    public void Success(string key)
    {
        lock (_gate) _entries.Remove(key);
    }

    /// <summary>Clients with an entry (for tests).</summary>
    public int Tracked { get { lock (_gate) return _entries.Count; } }

    private TimeSpan? Left(string key, DateTimeOffset now) =>
        _entries.TryGetValue(key, out var e) && e.LockedUntil - now is var left && left > TimeSpan.Zero ? left : null;

    private void Count(string key, int free, DateTimeOffset now)
    {
        var failures = _entries.TryGetValue(key, out var e) && now - e.Last < Forget ? e.Failures + 1 : 1;
        var locked = now;
        if (failures > free)
            locked = now + TimeSpan.FromTicks(Math.Min(MaxLock.Ticks, BaseLock.Ticks << Math.Min(failures - free - 1, 10)));
        _entries[key] = new Entry(failures, locked, now);
    }

    private void Prune(DateTimeOffset now)
    {
        foreach (var (k, e) in _entries.ToArray())
            if (e.LockedUntil <= now && now - e.Last >= Forget) _entries.Remove(k);
        // Still full (many addresses within the hour): forget the quietest clients, never the shared budget.
        if (_entries.Count >= MaxEntries)
            foreach (var k in _entries.Where(p => p.Key != Everyone).OrderBy(p => p.Value.Last).Take(_entries.Count - MaxEntries / 2).Select(p => p.Key).ToArray())
                _entries.Remove(k);
    }
}
