using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Glamour.Core;
using Glamour.Desktop.Dock;
using Glamour.Host.Panel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Glamour.Desktop.Game;

/// <summary>
/// A few shots of the game's loading screen to logs\shots (`loading-<time>-<n>.png`), taken by itself each game: what the loading-screen party marks
/// are measured on (SPEC "Parties on the loading screen"). At about 1, 3 and 6 s after the game window has its real size, until the game clock runs
/// (which it does while the loading screen still shows, LIVE: so the shots, not the clock, end the loading). Read off the screen while the game is in
/// front, else through PrintWindow (the file name says "-pw"; it caught a borderless loading screen whole, LIVE). Only the newest <see cref="Keep"/> are kept.
/// </summary>
internal sealed class LoadingShots : IDisposable
{
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(250), Idle = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan[] At = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(6)];
    private const int Keep = 12;

    private readonly Func<IServiceProvider?> _services;
    private readonly ILogger _log;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _stop = new(false);

    // One game, on the shots thread: when the window was first seen, how many shots are taken, whether this game is done.
    private DateTime? _seen;
    private int _taken;
    private bool _done;
    private string _stamp = "";

    public LoadingShots(Func<IServiceProvider?> services, ILogger log)
    {
        _services = services;
        _log = log;
        _thread = new Thread(Run) { IsBackground = true, Name = "Glamour loading shots", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    private void Run()
    {
        while (!_stop.IsSet)
        {
            TimeSpan wait;
            try { wait = Step(); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _log.LogWarning(ex, "Loading shots: a tick failed");
                _done = true; // not again this game
                wait = Idle;
            }
            _stop.Wait(wait);
        }
    }

    private TimeSpan Step()
    {
        var sp = _services();
        var phase = sp?.GetService<Engine>()?.Phase;
        if (phase is not ("GameStart" or "InProgress" or "Reconnect")) { _seen = null; _taken = 0; _done = false; return Idle; }
        if (_done) return Idle;
        if (sp?.GetService<GameClock>()?.InGame == true)
        {
            _done = true;
            if (_seen is { } s) _log.LogInformation("Loading shots: game loaded {Seconds:0.0} s after its window showed, {Taken} shot(s)", (DateTime.UtcNow - s).TotalSeconds, _taken);
            return Idle;
        }
        var game = Win32.FindGame();
        if (game == IntPtr.Zero || !Win32.IsShown(game) || Win32.IsMinimized(game)) return Tick;
        var client = Win32.ClientArea(game);
        if (client.Width < 400 || client.Height < 400) return Tick; // the window shows at 1x1 first (LIVE): the shot then was empty
        if (_seen is null)
        {
            _seen = DateTime.UtcNow;
            _stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var (work, _) = Win32.MonitorOf(game);
            _log.LogInformation("Loading shots: game window at {Left},{Top} {Width}x{Height}, in front {Front}; {Sizes}; monitor work area {Work}",
                client.Left, client.Top, client.Width, client.Height, Win32.Foreground() == game, Win32.Sizes(game), work);
        }
        if (_taken < At.Length && DateTime.UtcNow - _seen >= At[_taken])
        {
            _taken++;
            Save(game, client);
            if (_taken == At.Length) Prune();
        }
        return Tick;
    }

    private void Save(IntPtr game, PixelRect client)
    {
        try
        {
            var front = Win32.Foreground() == game;
            using var bmp = front ? Grab(client) : Win32.Capture(game);
            if (bmp is null) return;
            var dir = Path.Combine(Glamour.Host.Logging.FileLog.DefaultDir, "shots");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, $"loading-{_stamp}-{_taken}{(front ? "" : "-pw")}.png");
            bmp.Save(file, ImageFormat.Png);
            _log.LogInformation("Loading shots: saved {File}", file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ExternalException) { _log.LogWarning(ex, "Loading shots: could not save the screen"); }
    }

    /// <summary>What is on screen in <paramref name="r"/> (without CAPTUREBLT: Glamour's own layered windows are left out).</summary>
    private static Bitmap Grab(PixelRect r)
    {
        var bmp = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.CopyFromScreen(r.Left, r.Top, 0, 0, bmp.Size);
        return bmp;
    }

    private void Prune()
    {
        try
        {
            var dir = Path.Combine(Glamour.Host.Logging.FileLog.DefaultDir, "shots");
            foreach (var old in new DirectoryInfo(dir).GetFiles("loading-*.png").OrderByDescending(f => f.Name, StringComparer.Ordinal).Skip(Keep)) old.Delete();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _log.LogWarning(ex, "Loading shots: could not remove old shots"); }
    }

    public void Dispose()
    {
        _stop.Set();
        _thread.Join(TimeSpan.FromSeconds(2));
    }
}
