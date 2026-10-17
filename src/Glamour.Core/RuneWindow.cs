namespace Glamour.Core;

/// <summary>
/// The client's rune window (recommender or editor) over champ select, as the desktop app sees it on screen (its gold frame, four times a second).
/// The console host has no screen watch: <see cref="Watching"/> stays false and <see cref="OpenWithin"/> says null (unknown).
/// </summary>
public sealed class RuneWindow
{
    private readonly Lock _lock = new();
    private bool _watching, _open;
    private DateTimeOffset _lastOpen = DateTimeOffset.MinValue;

    public bool Watching { get { lock (_lock) return _watching; } }

    /// <summary>One look at the screen: <paramref name="watching"/> false when the window could not be looked for (no marks shown, client minimized).</summary>
    public void Report(bool watching, bool open, DateTimeOffset now)
    {
        lock (_lock)
        {
            _watching = watching;
            if (open || _open) _lastOpen = now; // closing counts as open until now
            _open = watching && open;
        }
    }

    /// <summary>Whether the window was open at any time in the last <paramref name="span"/> before <paramref name="now"/>; null when nothing watches it.</summary>
    public bool? OpenWithin(TimeSpan span, DateTimeOffset now)
    {
        lock (_lock) return _watching ? _open || now - _lastOpen <= span : null;
    }
}
