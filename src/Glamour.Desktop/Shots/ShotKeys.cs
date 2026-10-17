using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Forms;
using Glamour.Core;
using Glamour.Desktop.Dock;
using Glamour.Host;
using Glamour.Host.Profiles;
using Glamour.Lcu;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Glamour.Desktop.Shots;

/// <summary>
/// README shots (SPEC "Publishing"), dev only: desktop.json `"shotMode": true` turns on made-up names in every page (`Glamour:ShotNames`) and two keys.
/// Ctrl+Alt+S saves the League client with the panel and Glamour's marks (what is on screen in their rectangle, never the rest of the screen), names
/// painted over (<see cref="ShotMask"/>). Ctrl+Alt+R starts and stops a recording (10 frames a second, 30 s at most), masked frame by frame after it
/// stops. Everything goes to logs\shots\readme: `<name>.png` (masked), `<name>.raw.png` (real names, never to share) and `<name>.txt` (what was painted,
/// any leak; a shot with a leak is named `<name>.LEAK.png`). `tools/readme-gif.ps1` makes the GIF from a recording's masked frames.
/// </summary>
internal sealed class ShotKeys : NativeWindow, IDisposable
{
    private const int WmHotkey = 0x0312, Shot = 1, Record = 2;
    private const uint ModAlt = 0x1, ModControl = 0x2, ModNoRepeat = 0x4000;
    private static readonly TimeSpan Frame = TimeSpan.FromMilliseconds(100), MaxRecording = TimeSpan.FromSeconds(30);

    private readonly Func<(IntPtr Client, IntPtr Panel)> _windows;
    private readonly Func<IServiceProvider?> _services;
    private readonly Func<string?> _phase;
    private readonly ILogger _log;
    private readonly string _dir = Path.Combine(Glamour.Host.Logging.FileLog.DefaultDir, "shots", "readme");
    private readonly object _maskGate = new(); // one OCR job at a time
    private CancellationTokenSource? _recording;

    public ShotKeys(Func<(IntPtr Client, IntPtr Panel)> windows, Func<IServiceProvider?> services, Func<string?> phase, ILogger log)
    {
        _phase = phase;
        _windows = windows;
        _services = services;
        _log = log;
        CreateHandle(new CreateParams { Caption = "Glamour shot keys" }); // a title so tools can find it (hidden, never shown)
        var s = RegisterHotKey(Handle, Shot, ModControl | ModAlt | ModNoRepeat, (uint)Keys.S);
        var r = RegisterHotKey(Handle, Record, ModControl | ModAlt | ModNoRepeat, (uint)Keys.R);
        _log.LogInformation("Shot mode on: made-up names in pages; Ctrl+Alt+S shot {S}, Ctrl+Alt+R recording {R}", s ? "ready" : "taken by another app", r ? "ready" : "taken by another app");
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmHotkey)
        {
            // A NativeWindow drops what its WndProc throws without a word (OnThreadException does nothing), so log it here.
            try
            {
                if (m.WParam == Shot) TakeShot();
                else if (m.WParam == Record) ToggleRecording();
            }
            catch (Exception ex) { System.Media.SystemSounds.Hand.Play(); _log.LogWarning(ex, "Shot: failed"); }
            return;
        }
        base.WndProc(ref m);
    }

    /// <summary>The client and the panel together, in screen pixels; the client alone in that picture's pixels. Null with no client on screen.</summary>
    private (Rectangle Area, Rectangle Client)? Where()
    {
        var (client, panel) = _windows();
        if (!Win32.Exists(client) || Win32.IsMinimized(client) || !Win32.IsShown(client)) return null;
        var c = Win32.ClientRect(client);
        var cr = new Rectangle(c.Left, c.Top, c.Width, c.Height);
        var area = cr;
        if (panel != IntPtr.Zero && Win32.ClientRect(panel) is var p && p.Width > 0) area = Rectangle.Union(area, new Rectangle(p.Left, p.Top, p.Width, p.Height));
        return (area, cr with { X = cr.X - area.X, Y = cr.Y - area.Y });
    }

    /// <summary>What is on screen there, Glamour's layered marks included (CAPTUREBLT). A BitBlt of its own: CopyFromScreen refuses SourceCopy | CaptureBlt.</summary>
    private static Bitmap Grab(Rectangle area)
    {
        var bmp = new Bitmap(area.Width, area.Height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        var screen = GetDC(IntPtr.Zero);
        var dest = g.GetHdc();
        try
        {
            if (!BitBlt(dest, 0, 0, area.Width, area.Height, screen, area.Left, area.Top, SrcCopy | CaptureBlt)) throw new System.ComponentModel.Win32Exception();
        }
        finally
        {
            g.ReleaseHdc(dest);
            ReleaseDC(IntPtr.Zero, screen);
        }
        return bmp;
    }

    private void TakeShot()
    {
        if (Where() is not var (area, client)) { System.Media.SystemSounds.Hand.Play(); _log.LogWarning("Shot: no League client on screen"); return; }
        if (!InFront()) _log.LogWarning("Shot: League is not in front; another window may be in the picture");
        var bmp = Grab(area);
        System.Media.SystemSounds.Asterisk.Play();
        var name = "readme-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var champ = Phase() == "ChampSelect";
        _ = Task.Run(() =>
        {
            try { using (bmp) Save(bmp, Path.Combine(_dir, name), client, champ); }
            catch (Exception ex) { _log.LogWarning(ex, "Shot: failed"); }
        });
    }

    private void ToggleRecording()
    {
        if (_recording is { } running) { running.Cancel(); _recording = null; return; }
        if (Where() is null) { System.Media.SystemSounds.Hand.Play(); return; }
        var cts = new CancellationTokenSource(MaxRecording);
        _recording = cts;
        var folder = Path.Combine(_dir, "rec-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        System.Media.SystemSounds.Asterisk.Play();
        _ = Task.Run(async () =>
        {
            try { await RecordAsync(folder, cts.Token); }
            catch (Exception ex) { _log.LogWarning(ex, "Shot: recording failed"); }
        });
    }

    private async Task RecordAsync(string folder, CancellationToken stop)
    {
        var raw = Path.Combine(folder, "raw");
        Directory.CreateDirectory(raw);
        var frames = new List<(string File, Rectangle Client, bool Champ)>();
        _log.LogInformation("Shot: recording to {Folder}", folder);
        using var timer = new PeriodicTimer(Frame);
        try
        {
            do
            {
                if (Where() is not var (area, client)) continue;
                using var bmp = Grab(area);
                var file = Path.Combine(raw, $"{frames.Count + 1:0000}.png");
                bmp.Save(file, ImageFormat.Png);
                frames.Add((file, client, Phase() == "ChampSelect"));
            }
            while (await timer.WaitForNextTickAsync(stop));
        }
        catch (OperationCanceledException) { /* stopped, or 30 s */ }
        System.Media.SystemSounds.Asterisk.Play();
        _log.LogInformation("Shot: recording stopped, {Count} frames; masking them", frames.Count);
        var masked = Path.Combine(folder, "masked");
        Directory.CreateDirectory(masked);
        var leaks = 0;
        foreach (var (file, client, champ) in frames)
        {
            using var bmp = new Bitmap(file);
            using var copy = new Bitmap(bmp);
            if (!Save(copy, Path.Combine(masked, Path.GetFileNameWithoutExtension(file)), client, champ, keepRaw: false)) leaks++;
        }
        _log.LogInformation("Shot: recording masked, {Count} frames, {Leaks} with a leak ({Folder})", frames.Count, leaks, masked);
    }

    /// <summary>Learns who is on screen from the host, paints them over and saves; false when a leak was found (the shot is then named .LEAK).</summary>
    private bool Save(Bitmap bmp, string path, Rectangle client, bool champ, bool keepRaw = true)
    {
        lock (_maskGate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (keepRaw) bmp.Save(path + ".raw.png", ImageFormat.Png);
                var names = Learn();
                var ocr = ShotMask.Engine();
                if (names is null || ocr is null) { _log.LogWarning("Shot: no host or no OCR; only the raw shot was saved"); return false; }
                var result = ShotMask.Apply(bmp, names, ocr, client, champ);
                var leak = result.Leaks.Count > 0;
                bmp.Save(path + (leak ? ".LEAK.png" : ".png"), ImageFormat.Png);
                File.WriteAllLines(path + ".txt", [$"{result.Painted} painted, {result.Leaks.Count} leak(s)", .. result.Leaks.Select(l => "LEAK: " + l)]);
                _log.LogInformation("Shot: saved {Path} ({Painted} painted, {Leaks} leak(s))", path, result.Painted, result.Leaks.Count);
                return !leak;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ExternalException or COMException)
            {
                _log.LogWarning(ex, "Shot: could not save {Path}", path);
                return false;
            }
        }
    }

    /// <summary>The host's made-up names, taught everyone the client may show: the champ select and game state, saved accounts and League friends.</summary>
    private ShotNames? Learn()
    {
        var sp = _services();
        if (sp?.GetService<ShotNames>() is not { } names) return null;
        try
        {
            if (sp.GetService<Engine>() is { } engine) names.Rewrite(JsonSerializer.SerializeToNode(engine.Tracker.Snapshot(), SettingsOverlay.Json));
            if (sp.GetService<ProfileService>() is { } profiles) names.Rewrite(JsonSerializer.SerializeToNode(profiles.Snapshot(), SettingsOverlay.Json));
            if (sp.GetService<ILcuClient>() is { } lcu && lcu.GetAsync("/lol-chat/v1/friends").GetAwaiter().GetResult() is { Status: 200, Body: { } friends })
                names.Rewrite(friends.DeepClone());
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException or InvalidOperationException or ObjectDisposedException)
        {
            _log.LogWarning(ex, "Shot: could not read every name from the host");
        }
        return names;
    }

    private string? Phase() => _phase();

    private bool InFront()
    {
        var (client, panel) = _windows();
        var front = Win32.Foreground();
        return front == client || front == panel || Win32.ProcessOf(front) == (uint)Environment.ProcessId;
    }

    public void Dispose()
    {
        _recording?.Cancel();
        UnregisterHotKey(Handle, Shot);
        UnregisterHotKey(Handle, Record);
        DestroyHandle();
    }

    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hwnd, int id);

    private const uint SrcCopy = 0x00CC0020, CaptureBlt = 0x40000000;
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern bool BitBlt(IntPtr dest, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);
}
