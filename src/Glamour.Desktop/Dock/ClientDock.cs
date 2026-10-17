using Glamour.Core;
using Glamour.Host.Panel;
using Microsoft.Extensions.Logging;

namespace Glamour.Desktop.Dock;

/// <summary>
/// Keeps Glamour's side panel glued to the League client window (SPEC "PC app direction" 2). Everything runs on its own thread with its own
/// message loop, so a slow or hung League window can never stall the tray menu. A one-second timer finds the client, opens and closes the panel
/// with the game phase and catches whatever the window events miss; the window events (move, resize, minimize) make it follow instantly.
/// </summary>
internal sealed class ClientDock : IDisposable
{
    private const int GoneTicksBeforeClose = 3;

    private readonly string _url;
    private readonly Func<string?> _phase;
    private readonly Func<bool> _enabled;
    private readonly Func<PanelSide?> _side;
    private readonly Action _openWindow; // called on the dock thread: the receiver marshals to its own
    private readonly Func<ChampSelectBoard?> _board;
    private readonly Func<bool> _clickLogOn;
    private readonly Func<(DateTimeOffset From, DateTimeOffset At)?> _acceptWait;
    private readonly ILogger _log;
    private readonly ClickHook _clicks;
    private readonly ClickLog _clickLog;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private readonly Win32.WinEventProc _onEvent; // a field: a collected delegate under a live hook crashes the process
    private Control? _sync;
    private System.Windows.Forms.Timer? _timer;
    private System.Windows.Forms.Timer? _popupTimer;
    private bool _popup; // the client's rune window is open over champ select: the marks step aside
    private bool _popupWarned;
    private readonly RuneWindow _runeWindow; // what the popup watch sees, for the rune page guard: a hand pick of a recommended page looks like League's rewrite
    private PanelForm? _form;
    private volatile IntPtr _client; // also read by the click worker
    private volatile IntPtr _panel; // the panel window once made, for README shots (any thread)
    private IntPtr _hookLife, _hookMove, _hookMinimize;
    private int _gone;
    private bool _ownerLogged;
    private bool _clicksOn;
    private string _lastGroup = "";
    private bool _collapsedNow = true;
    private bool? _override; // the user's own open (true) or collapse (false) since the phase last changed
    private int _openWidth = 288; // what the open section asked for (design units)
    private (int Top, int Width, int Height)? _menu; // the account list over the collapsed rail (css px), from the page
    private string _clipped = ""; // the window region now set ("" none)
    private RosterOverlay? _overlay;
    private BenchOverlay? _bench;
    private BenchTag?[] _benchTags = []; // each bench slot's number, from the page
    private AcceptOverlay? _acceptRing;
    private bool _acceptLogged; // one line per ready check
    private volatile IntPtr _overlayHandle; // read by the click worker: a press on the overlay (which every click passes through) still counts as one on the client
    private PanelPlacement? _placement;
    private int _facing = -1; // the client card of the enemy I face, from the page
    private bool _pageBand, _still;
    private MarkTag?[] _tags = []; // my champion's win rate on each enemy card, from the page
    private DamageShare? _damage, _ourDamage; // the picked enemies' damage split and my team's, from the page
    private string _marksSent = "";

    public ClientDock(string url, Func<string?> phase, Func<ChampSelectBoard?> board, Func<bool> enabled, Func<PanelSide?> side, Func<bool> clickLog,
        Func<(DateTimeOffset From, DateTimeOffset At)?> acceptWait, Action openWindow, RuneWindow runeWindow, ILogger log)
    {
        _runeWindow = runeWindow;
        _acceptWait = acceptWait;
        _url = url;
        _phase = phase;
        _board = board;
        _enabled = enabled;
        _side = side;
        _clickLogOn = clickLog;
        _openWindow = openWindow;
        _log = log;
        _onEvent = OnWinEvent;
        _clicks = new ClickHook(log);
        _clicks.Clicked += OnClientClick;
        _clickLog = new ClickLog(log);
        _thread = new Thread(Run) { IsBackground = true, Name = "Glamour dock" };
        _thread.SetApartmentState(ApartmentState.STA);
    }

    public void Start()
    {
        _thread.Start();
        _ready.Wait(TimeSpan.FromSeconds(5));
    }

    /// <summary>Re-evaluates now (the setting changed).</summary>
    public void Refresh() => _sync?.BeginInvoke(Tick);

    /// <summary>The League client and, while shown, the panel (README shots, ShotKeys); zero when there is no client.</summary>
    public (IntPtr Client, IntPtr Panel) Windows => (_client, Win32.Exists(_panel) && Win32.IsShown(_panel) ? _panel : IntPtr.Zero);

    private void Run()
    {
        _sync = new Control();
        _ = _sync.Handle;
        _timer = new System.Windows.Forms.Timer { Interval = 1000 };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        _popupTimer = new System.Windows.Forms.Timer { Interval = 250 };
        _popupTimer.Tick += (_, _) => WatchPopup();
        _popupTimer.Start();
        _ready.Set();
        Application.Run();
    }

    private void Tick()
    {
        try
        {
            if (!_enabled()) { Close(); WatchClicks(); return; }
            if (!Win32.Exists(_client))
            {
                var found = Win32.FindLeagueClient();
                if (found == IntPtr.Zero)
                {
                    if (++_gone >= GoneTicksBeforeClose) Close();
                    WatchClicks();
                    return;
                }
                Attach(found);
            }
            _gone = 0;
            Reposition();
            WatchClicks();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Dock tick failed");
        }
    }

    private void Attach(IntPtr client)
    {
        Detach();
        _client = client;
        var pid = Win32.ProcessOf(client);
        // Narrow ranges: a wider one also queues every focus and state event of a Chromium process onto this thread.
        _hookLife = Win32.Hook(Win32.EventObjectDestroy, Win32.EventObjectHide, pid, _onEvent);
        _hookMove = Win32.Hook(Win32.EventObjectLocationChange, Win32.EventObjectLocationChange, pid, _onEvent);
        _hookMinimize = Win32.Hook(Win32.EventSystemMinimizeStart, Win32.EventSystemMinimizeEnd, pid, _onEvent);
        _log.LogInformation("Docking to the League client window {Handle:X}", client.ToInt64());
    }

    private void Detach()
    {
        Win32.Unhook(_hookLife);
        Win32.Unhook(_hookMove);
        Win32.Unhook(_hookMinimize);
        _hookLife = _hookMove = _hookMinimize = IntPtr.Zero;
        _client = IntPtr.Zero;
    }

    private void OnWinEvent(IntPtr hook, uint type, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        // Location changes also fire for carets and child objects: only the client window itself counts.
        if (hwnd != _client || idObject != Win32.ObjidWindow) return;
        try
        {
            if (type == Win32.EventObjectDestroy) { Close(); return; }
            Reposition();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Dock event failed");
        }
    }

    private void Reposition()
    {
        if (!Win32.Exists(_client)) return;
        if (Win32.IsMinimized(_client) || !Win32.IsShown(_client)) { _form?.Hide(); HideMarks(); _acceptRing?.Set(null, IntPtr.Zero); return; }

        var phase = _phase() ?? "";
        // The panel is the PC surface whenever the client shows (SPEC "PC app direction" 2): a rail by default, open by itself only in champ select (unless turned off).
        // During a game the client window is normally gone already; if it lingers, the panel still steps aside.
        // Reconnect is not a game here: the client is back after a crash and the panel carries the Reconnect button.
        if (phase is "GameStart" or "InProgress")
        {
            HideMarks(); _acceptRing?.Set(null, IntPtr.Zero); _lastGroup = ""; _override = null;
            ClosePanel(); // its WebView2 processes end with it (~170 MB); made again when the client shows after the game
            return;
        }
        var group = phase == "ChampSelect" ? "champ" : "other";
        if (group != _lastGroup) { _lastGroup = group; _override = null; } // a new phase starts from its own default again
        var collapsed = _override.HasValue ? !_override.Value : group != "champ" || !DockPrefs.AutoOpen;

        var (work, dpi) = Win32.MonitorOf(_client);
        var rect = Win32.ClientRect(_client);
        var side = _side();
        var placement = PanelPlacement.Compute(rect, work, collapsed, side, _openWidth);
        // With no room beside the client the open panel would cover part of it: that only happens when the user asks, never by default.
        if (placement.Overlaps && _override != true) placement = PanelPlacement.Compute(rect, work, true, side, _openWidth);
        _collapsedNow = placement.Collapsed;

        if (_form is null || _form.IsDisposed) CreateForm();
        _form!.SetZoom(placement.Scale / (Math.Max(dpi, 96) / 96.0)); // the page keeps its css size at any client size; WebView2 already applies the monitor scale
        _form.SetSide(placement.Side);
        var bounds = placement.Bounds;
        // The account list over the collapsed rail: the window grows by the list's width, cut to the rail and the list, so the panel stays collapsed.
        if (_menu is { } menu && placement.Collapsed)
        {
            int Px(int css) => (int)Math.Round(css * placement.Scale); // one css px is Scale physical px (the page's zoom times the monitor's)
            var (tab, top, w, h) = (bounds.Width, Px(menu.Top), Px(menu.Width), Px(menu.Height));
            bounds = placement.Side == PanelSide.Right ? bounds with { Right = bounds.Left + tab + w } : bounds with { Left = bounds.Right - tab - w };
            var right = placement.Side == PanelSide.Right;
            var rail = new Rectangle(right ? 0 : w, 0, tab, placement.Bounds.Height);
            var list = new Rectangle(right ? tab : 0, top, w, h);
            Clip($"{rail}{list}", rail, list); // before the window grows, so the rest never shows
        }
        // The one-second tick lands here too: no window call when nothing changed.
        if (!_form.Visible || Win32.ClientRect(_form.Handle) != bounds) Win32.Place(_form.Handle, bounds);
        if (_menu is null || !placement.Collapsed) Clip(""); // after the window shrank back
        if (!_form.Visible)
        {
            _form.Show();
            EnsureOwner(); // WinForms may reset the owner while it shows the window
            Win32.PlaceAbove(_form.Handle, _client); // Show put it over every app; League may be behind one
        }
        _panel = _form.Handle;
        _placement = placement;
        UpdateMarks();
        UpdateBench();
        UpdateAcceptRing(rect);
    }

    /// <summary>
    /// The Accept ring (SPEC "Click layer"): told where the button is whenever the client shows; it draws only while a ready check is on and auto-accept
    /// waits, which it asks for itself each frame.
    /// </summary>
    private void UpdateAcceptRing(PixelRect client)
    {
        var ready = _phase() == "ReadyCheck";
        if (ready && !_acceptLogged)
        {
            _acceptLogged = true;
            _log.LogInformation("Ready check: client {Client}, layout {Fits}, auto-accept waiting {Waiting}", client, AcceptLayout.Outline(client, 0) is not null, _acceptWait() is not null);
        }
        else if (!ready) _acceptLogged = false;
        if (AcceptLayout.Outline(client, AcceptOverlay.Offset) is not { } outline) { _acceptRing?.Set(null, IntPtr.Zero); return; }
        _acceptRing ??= new AcceptOverlay(() => _phase() == "ReadyCheck" ? _acceptWait() : null, _log);
        _acceptRing.Set(outline, _client);
    }

    /// <summary>
    /// The marks on the client's enemy cards (SPEC "Click layer on the client's roster"): wherever the click layer works (5v5 draft, a 16:9 client) and the
    /// panel does not cover the enemy column. The band to the panel only when the panel is open on the right and the page draws its half.
    /// </summary>
    private void UpdateMarks()
    {
        var board = _phase() == "ChampSelect" ? _board() : null;
        var client = Win32.ClientRect(_client);
        var portraits = board is null ? null : RosterLayout.EnemyPortraits(client, board.Mode, board.MyTeam.Count, board.TheirTeam.Count, board.Stage,
            [.. board.TheirTeam.OrderBy(p => p.CellId).Select(p => board.BanCells?.Contains(p.CellId) == true)]);
        // Only while a click on a card can choose (the hook is in; the strip hides on marks) and the panel is clear of the client: an open panel over it or the
        // short tab at a screen edge would fight the marks for the spot above the client.
        if (portraits is null || !_clicks.Live || _placement is not { } placement || Intersects(placement.Bounds, client) || _form is not { Visible: true }) { HideMarks(); return; }

        var choosable = board!.TheirTeam.OrderBy(p => p.CellId).Select(p => p.ChampionId > 0).ToArray();
        var chosen = _facing >= 0 && _facing < choosable.Length && choosable[_facing] ? _facing : -1;
        var band = chosen >= 0 && _pageBand && placement.Side == PanelSide.Right && !placement.Collapsed;
        if (_overlay is null)
        {
            _overlay = new RosterOverlay();
            _overlayHandle = _overlay.Handle;
        }
        var tags = choosable.Select((c, i) => c && i < _tags.Length ? _tags[i] : null).ToArray();
        if (_popup) _overlay.Hide(); // the page keeps its marks layout: the popup goes again in a moment
        else _overlay.Show(new RosterMarks(portraits, chosen, choosable, client, band, _still, tags, _damage, _ourDamage), _client);
        // The page's rows in its own css px (its zoom makes one css px = Scale physical px), from the panel's top, which the work area may clamp.
        var y0 = (portraits[0].Y - placement.Bounds.Top) / placement.Scale;
        var step = (portraits[1].Y - portraits[0].Y) / placement.Scale;
        PostMarks(FormattableString.Invariant($"marks:{y0:0.#},{step:0.##}"));
    }

    /// <summary>
    /// Bench advice on the client's bench strip (SPEC "PC app direction" 5): where the strip is measured (Mayhem, a 16:9 client), the page has numbers and
    /// the panel is clear of the client. The panel keeps its own Bench section either way.
    /// </summary>
    private void UpdateBench()
    {
        var board = _phase() == "ChampSelect" ? _board() : null;
        var client = Win32.ClientRect(_client);
        var slots = board is null ? null : BenchLayout.Slots(client, board.Mode);
        if (slots is null || _benchTags.Length == 0 || _popup || _placement is not { } placement || Intersects(placement.Bounds, client) || _form is not { Visible: true })
        {
            _bench?.Hide();
            return;
        }
        _bench ??= new BenchOverlay();
        _bench.Show(new BenchMarks(slots, _benchTags), _client);
    }

    /// <summary>
    /// The client's rune window (recommender or editor) covers the middle of champ select, and the damage strips drew over it (user). The client says
    /// nothing when it opens (<see cref="ClientPopup"/>), so its gold frame is looked for on screen four times a second while the marks show.
    /// </summary>
    private void WatchPopup()
    {
        try
        {
            var spots = (_overlay is not null || _bench is not null) && _phase() == "ChampSelect" && Win32.Exists(_client) && !Win32.IsMinimized(_client)
                ? ClientPopup.Spots(Win32.ClientRect(_client)) : null;
            var popup = spots is not null && spots.All(s => Win32.AnyPixel(s, ClientPopup.IsGold));
            _runeWindow.Report(spots is not null, popup, DateTimeOffset.UtcNow);
            _popupWarned = false;
            if (popup == _popup) return;
            _popup = popup;
            _log.LogInformation("Client popup {State}: marks {Marks}", popup ? "open" : "closed", popup ? "hidden" : "back");
            Reposition();
        }
        catch (Exception ex)
        {
            // The screen cannot be read while Windows shows its lock or UAC screen: once in the log, not four times a second.
            _runeWindow.Report(false, false, DateTimeOffset.UtcNow);
            if (!_popupWarned) _log.LogWarning(ex, "Client popup check failed");
            _popupWarned = true;
        }
    }

    private static bool Intersects(PixelRect a, PixelRect b) => a.Left < b.Right && b.Left < a.Right && a.Top < b.Bottom && b.Top < a.Bottom;

    private void HideMarks()
    {
        _overlay?.Hide();
        PostMarks("marks:off");
    }

    private void PostMarks(string message)
    {
        if (message == _marksSent || _form is null) return;
        _marksSent = message;
        _form.Post(message);
    }

    /// <summary>
    /// The click layer (SPEC "Click layer on the client's roster"): the mouse hook is on only during champ select, and only when there is something to
    /// do with a click: a measured roster layout for this kind of draft, or the dev click log.
    /// </summary>
    private void WatchClicks()
    {
        var champ = _enabled() && Win32.Exists(_client) && _phase() == "ChampSelect";
        var board = champ ? _board() : null;
        var logOn = champ && _clickLogOn();
        if (logOn) _clickLog.Stage(_client, "ChampSelect", board);
        else if (_enabled() && Win32.Exists(_client) && _phase() == "ReadyCheck" && _clickLogOn()) _clickLog.Stage(_client, "ReadyCheck", null);
        else _clickLog.Stage(IntPtr.Zero, null, null);
        var want = champ && (logOn || (board is not null && LayoutOf(board) is not null));
        if (want == _clicksOn) return;
        _clicksOn = want;
        _clicks.Set(want);
        _log.LogInformation("Mouse hook {State} (phase {Phase}, click log {Log})", want ? "on" : "off", _phase(), logOn);
    }

    private static IReadOnlyList<RosterSlot>? LayoutOf(ChampSelectBoard board) => RosterLayout.For(board.Mode, board.MyTeam.Count, board.TheirTeam.Count);

    /// <summary>
    /// On the click worker. A press on an enemy's card in the client's own roster chooses who I face, like a tap on that enemy in the panel; a press on my
    /// own card shows or hides who beats my champion. Other allies' cards do nothing.
    /// </summary>
    private void OnClientClick(ScreenClick click)
    {
        var client = _client;
        if (!Win32.Exists(client)) return;
        var board = _board();
        if (_clickLogOn()) _clickLog.Click(click, client, board);
        if (click.Button != MouseButtons.Left || board is null || LayoutOf(board) is not { } slots) return;
        if (RosterLayout.HitTest(Win32.ClientRect(client), click.X, click.Y, slots) is not { } slot) return;
        var message = slot.Team == RosterTeam.Theirs ? $"face:{slot.Index}"
            : board.MyTeam.OrderBy(p => p.CellId).ElementAtOrDefault(slot.Index) is { IsMe: true } ? "own" : null;
        if (message is null) return;
        // The press went to something over the client. Our own marks let every click through, but count them as the client in case Windows names them here.
        var root = Win32.WindowAt(click.X, click.Y).Root;
        if (root != client && (root == IntPtr.Zero || root != _overlayHandle)) return;
        _sync?.BeginInvoke(() => _form?.Post(message));
    }

    private void Clip(string key, params Rectangle[] parts)
    {
        if (key == _clipped || _form is null) return;
        _clipped = key;
        Win32.Clip(_form.Handle, parts);
    }

    private void CreateForm()
    {
        _clipped = "";
        _form = new PanelForm(_url, _log);
        _form.OpenRequested += open => { _override = open ?? _collapsedNow; _menu = null; Tick(); }; // a flip opens if it is collapsed now, else collapses
        _form.MenuRequested += menu => { _menu = menu; Tick(); };
        _form.WidthRequested += width =>
        {
            if (width == _openWidth) return;
            _openWidth = width;
            if (!_collapsedNow) Tick();
        };
        _form.OpenWindowRequested += () => _openWindow();
        _form.FacingChanged += (slot, band, still) =>
        {
            (_facing, _pageBand, _still) = (slot, band, still);
            _marksSent = ""; // the page (re)mounted or changed: tell it again
            UpdateMarks();
        };
        _form.TagsChanged += tags =>
        {
            _tags = tags;
            UpdateMarks();
        };
        _form.BenchChanged += tags =>
        {
            _benchTags = tags;
            UpdateBench();
        };
        _form.DamageChanged += (theirs, ours) =>
        {
            (_damage, _ourDamage) = (theirs, ours);
            UpdateMarks();
        };
        _ = _form.Handle;
        EnsureOwner();
        _ = _form.StartPageAsync();
    }

    private void EnsureOwner()
    {
        if (_form is null || !Win32.Exists(_client)) return;
        if (Win32.OwnerOf(_form.Handle) == _client) return;
        Win32.SetOwner(_form.Handle, _client);
        if (!_ownerLogged)
        {
            _ownerLogged = true;
            _log.LogInformation("Panel owner is the League client: {Ok}", Win32.OwnerOf(_form.Handle) == _client);
        }
    }

    private void ClosePanel()
    {
        if (_form is { IsDisposed: false } f) { f.Close(); f.Dispose(); } // takes the panel's WebView2 processes with it
        _form = null;
        _marksSent = "";
    }

    private void Close()
    {
        ClosePanel();
        _overlay?.Dispose();
        _overlay = null;
        _bench?.Dispose();
        _bench = null;
        _acceptRing?.Dispose();
        _acceptRing = null;
        _overlayHandle = IntPtr.Zero;
        _marksSent = "";
        if (!Win32.Exists(_client)) Detach();
    }

    public void Dispose()
    {
        if (_sync is null) return;
        try
        {
            // Never wait for ever: a stuck dock thread must not keep the tray from exiting. The thread is a background thread and ends with the process.
            var done = _sync.BeginInvoke(() =>
            {
                _timer?.Stop();
                _popupTimer?.Stop();
                Close();
                Detach();
                _clicks.Dispose();
                Application.ExitThread();
            });
            done.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(2));
        }
        catch (Exception) { /* the thread is already gone */ }
    }
}
