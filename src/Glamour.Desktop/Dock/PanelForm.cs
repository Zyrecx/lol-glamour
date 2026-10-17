using Glamour.Host.Panel;
using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Glamour.Desktop.Dock;

/// <summary>The borderless window next to the League client: never takes focus, no taskbar button, no Alt+Tab entry. Shows Glamour's own `?view=panel` page.</summary>
/// <remarks>The page's messages: "open", "collapse", "toggle", "width:N" (css px at a 1280x720 client), "open-window", "keyboard", "menu:TOP,W,H" / "menu:off" (the account list over the collapsed rail),
/// "facing:N:B:S" (the enemy I face is the Nth card in the client, -1 none; B 1: the page draws its half of the band; S 1: reduce motion),
/// "tags:A,B,..." (my champion's win rate on each enemy card by cell order, empty for none, "~" after it: few games), "damage:P,M,T|P,M,T" (the picked enemies'
/// damage, then my team's, in whole percents, each empty for none), "bench:A,B,..." (each bench slot's win rate or tier in bench order, "^" first: better
/// than mine; <see cref="BenchTag.ParseAll"/>). To the page:
/// "side:left|right", "face:N" (the user clicked the Nth enemy card in the client's own roster), "own" (the user clicked their own card),
/// "marks:Y,STEP" (the client's enemy portraits are marked:
/// the first one's centre and the step between rows, in the page's css px from its top) or "marks:off".</remarks>
internal sealed class PanelForm : Form
{
    private readonly string _url;
    private readonly ILogger _log;
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.FromArgb(1, 10, 19) };

    /// <summary>The page asked to open (true), collapse (false) or flip (null) the panel.</summary>
    public event Action<bool?>? OpenRequested;

    /// <summary>The open section's width in design units (css px at a 1280x720 client).</summary>
    public event Action<int>? WidthRequested;

    /// <summary>The account list is open over the collapsed rail (its top, width and height in css px beside the rail), or closed (null).</summary>
    public event Action<(int Top, int Width, int Height)?>? MenuRequested;

    /// <summary>The page asked for the full Glamour window (things the panel does not carry, like phone pairing).</summary>
    public event Action? OpenWindowRequested;

    /// <summary>The page says who I face (the card's index in the client, -1 none), whether it draws its half of the band, and whether motion is reduced.</summary>
    public event Action<int, bool, bool>? FacingChanged;

    /// <summary>The page's win rates for the enemy cards (index: card, null: no tag).</summary>
    public event Action<MarkTag?[]>? TagsChanged;

    /// <summary>The page's numbers for the client's bench slots (index: slot, null: no tag).</summary>
    public event Action<BenchTag?[]>? BenchChanged;

    /// <summary>The picked enemies' damage split and my team's from the page (null: none yet).</summary>
    public event Action<DamageShare?, DamageShare?>? DamageChanged;

    private bool _keyboardLogged;
    private long _keyboardAsked = -1000;

    private void OnMessage(string message)
    {
        switch (message)
        {
            case "open": OpenRequested?.Invoke(true); break;
            case "collapse": OpenRequested?.Invoke(false); break;
            case "toggle": OpenRequested?.Invoke(null); break;
            // The page sends its width as soon as it is mounted (its app is a chunk loaded after navigation): the side again then, so it is never missed.
            case not null when message.StartsWith("width:", StringComparison.Ordinal) && int.TryParse(message.AsSpan(6), out var w): PostSide(); WidthRequested?.Invoke(w); break;
            case "open-window": OpenWindowRequested?.Invoke(); break;
            case "menu:off": MenuRequested?.Invoke(null); break;
            case not null when message.StartsWith("menu:", StringComparison.Ordinal):
                var m = message[5..].Split(',');
                if (m.Length == 3 && int.TryParse(m[0], out var top) && int.TryParse(m[1], out var mw) && int.TryParse(m[2], out var mh)) MenuRequested?.Invoke((top, mw, mh));
                break;
            case not null when message.StartsWith("facing:", StringComparison.Ordinal):
                var f = message[7..].Split(':');
                if (f.Length == 3 && int.TryParse(f[0], out var slot)) FacingChanged?.Invoke(slot, f[1] == "1", f[2] == "1");
                break;
            case not null when message.StartsWith("tags:", StringComparison.Ordinal):
                TagsChanged?.Invoke(MarkTag.ParseAll(message[5..]));
                break;
            case not null when message.StartsWith("bench:", StringComparison.Ordinal):
                BenchChanged?.Invoke(BenchTag.ParseAll(message[6..]));
                break;
            case not null when message.StartsWith("damage:", StringComparison.Ordinal):
                var d = message[7..].Split('|');
                DamageChanged?.Invoke(DamageShare.Parse(d[0]), d.Length > 1 ? DamageShare.Parse(d[1]) : null);
                break;
            // A text field got focus: the panel never activates on a click (so League keeps chat and hotkeys), which also means no typing.
            // The user just clicked here, so taking the foreground is allowed; one click back on League hands it back.
            case "keyboard":
                // Focusing the web view fires the page's focusin again, which asks again: without these two checks that loops and the caret
                // flickers (seen live in the champion picker, whose search box takes focus as it opens).
                if (Win32.Foreground() == Handle || Environment.TickCount64 - _keyboardAsked < 1000) break;
                _keyboardAsked = Environment.TickCount64;
                // The click went to WebView2's own input window (another process), so Windows may refuse this: the log says which, for the first live try.
                var ok = Win32.SetForeground(Handle);
                _web.Focus();
                if (!_keyboardLogged) { _keyboardLogged = true; _log.LogInformation("Panel asked for the keyboard: SetForegroundWindow {Ok}, foreground is the panel {Front}", ok, Win32.Foreground() == Handle); }
                break;
        }
    }

    public PanelForm(string url, ILogger log)
    {
        _url = url;
        _log = log;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(1, 10, 19);
        Controls.Add(_web);
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle = (cp.ExStyle | Win32.WsExToolWindow | Win32.WsExNoActivate) & ~Win32.WsExAppWindow; // a tool window never gets a taskbar button
            return cp;
        }
    }

    private double _zoom = 1;

    /// <summary>Page zoom on top of the monitor scale; kept and applied again once the page exists.</summary>
    public void SetZoom(double zoom)
    {
        if (Math.Abs(zoom - _zoom) < 0.001) return;
        _zoom = zoom;
        if (_web.CoreWebView2 is not null) _web.ZoomFactor = _zoom;
    }

    private PanelSide? _side;

    /// <summary>Tells the page which side of the client it is on (its rail goes against the client); sent again once the page has loaded.</summary>
    public void SetSide(PanelSide side)
    {
        if (_side == side) return;
        _side = side;
        PostSide();
    }

    /// <summary>A message to the page; dropped while the page is not up yet.</summary>
    public void Post(string message) => _web.CoreWebView2?.PostWebMessageAsString(message);

    private void PostSide()
    {
        if (_side is { } side && _web.CoreWebView2 is { } core) core.PostWebMessageAsString(side == PanelSide.Left ? "side:left" : "side:right");
    }

    public async Task StartPageAsync()
    {
        try
        {
            // Same profile folder as the main window: one WebView2 environment configuration, so both can run side by side.
            var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Glamour", "WebView2");
            await _web.EnsureCoreWebView2Async(await CoreWebView2Environment.CreateAsync(userDataFolder: data));
            if (IsDisposed) return;
            var core = _web.CoreWebView2;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false; // the browser's Back / Reload / Save as on a right click: nothing there for the panel
            _web.ZoomFactor = _zoom;
            core.NewWindowRequested += (_, e) => e.Handled = true;
            core.NavigationStarting += (_, e) => { if (!e.Uri.StartsWith(new Uri(_url).GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase)) e.Cancel = true; };
            core.WebMessageReceived += (_, e) => OnMessage(e.TryGetWebMessageAsString());
            core.NavigationCompleted += (_, _) => PostSide();
            core.ProcessFailed += (_, e) => { _log.LogWarning("Panel WebView2 process failed: {Kind}", e.ProcessFailedKind); if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.RenderProcessExited) core.Reload(); };
            _web.Source = new Uri(_url);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Panel WebView2 did not start");
        }
    }
}
