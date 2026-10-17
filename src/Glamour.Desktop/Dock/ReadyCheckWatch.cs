using Microsoft.Extensions.Logging;

namespace Glamour.Desktop.Dock;

/// <summary>
/// Diagnostics for the League client dropping behind every app, or minimizing, when a match is found while Glamour runs (user, 2026-10-12; cause not
/// known). From the ready check until 5 s after it, logs each change of the client window's state (minimized, always on top, the window in front, the
/// window right above it) and every z-order, owner and foreground call Glamour makes (<see cref="Win32.Trace"/>).
/// </summary>
internal sealed class ReadyCheckWatch : IDisposable
{
    private static readonly TimeSpan After = TimeSpan.FromSeconds(5);

    private readonly Func<string?> _phase;
    private readonly ILogger _log;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _stop = new(false);

    public ReadyCheckWatch(Func<string?> phase, ILogger log)
    {
        _phase = phase;
        _log = log;
        _thread = new Thread(Run) { IsBackground = true, Name = "Glamour ready check watch", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    private void Run()
    {
        DateTime? until = null;
        var last = "";
        while (!_stop.IsSet)
        {
            try
            {
                if (_phase() == "ReadyCheck")
                {
                    if (until is null)
                    {
                        Win32.Trace = line => _log.LogInformation("Ready check window: Glamour calls {Call}", line);
                        last = "";
                    }
                    until = DateTime.UtcNow + After;
                }
                if (until is { } end && DateTime.UtcNow > end)
                {
                    Win32.Trace = null;
                    until = null;
                    _log.LogInformation("Ready check window: watch ended");
                }
                if (until is not null)
                {
                    var now = State();
                    if (now != last) _log.LogInformation("Ready check window: {State}", now);
                    last = now;
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { _log.LogWarning(ex, "Ready check window: a look failed"); }
            _stop.Wait(until is null ? 500 : 50);
        }
        Win32.Trace = null;
    }

    private static string State()
    {
        var client = Win32.FindLeagueClient();
        if (client == IntPtr.Zero) return "no client window";
        var (next, count) = Win32.ShownAbove(client);
        return $"client {Win32.Describe(client)} minimized {Win32.IsMinimized(client)}, shown {Win32.IsShown(client)}, topmost {Win32.IsTopmost(client)}; " +
               $"in front {Win32.Describe(Win32.Foreground())}; right above it {Win32.Describe(next)}, {count} shown window(s) above it";
    }

    public void Dispose()
    {
        _stop.Set();
        _thread.Join(TimeSpan.FromSeconds(1));
    }
}
