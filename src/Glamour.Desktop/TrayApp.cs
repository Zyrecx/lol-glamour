using System.Net;
using System.Net.Sockets;
using Glamour.Host;
using Glamour.Host.Profiles;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Glamour.Core;
using Glamour.Desktop.Dock;
using Glamour.Host.Logging;
using Glamour.Host.Panel;
using Microsoft.Extensions.Logging;

namespace Glamour.Desktop;

/// <summary>The tray icon plus the in-process Glamour host. The window is created on demand and destroyed on close; Exit stops the host.</summary>
internal sealed class TrayApp : ApplicationContext
{
    /// <summary>The window's own loopback-only port, kept apart from the phone port (5170/5171) so the two can never collide. A different free one is used if it is taken.</summary>
    private const int PreferredDesktopPort = 5180;

    private readonly string[] _hostArgs;
    private readonly int _desktopPort;
    private readonly string _url;

    /// <summary>
    /// Names the UI build in the URLs the app opens (`ui=` plus the build time of index.html), so WebView2 can never show a page cached from an
    /// earlier build: a new build is a new URL. The pages themselves are also sent no-cache.
    /// </summary>
    internal static readonly string UiStamp = "ui=" + File.GetLastWriteTimeUtc(Path.Combine(AppContext.BaseDirectory, "wwwroot", "index.html")).Ticks.ToString("x", System.Globalization.CultureInfo.InvariantCulture);
    private readonly NotifyIcon _tray;
    private readonly Icon _icon;
    private readonly Icon _windowIcon; // larger, so the taskbar and Alt+Tab do not stretch the 16 px tray icon
    private readonly FileLog _log;
    private readonly Glamour.Host.Telemetry.Telemetry? _telemetry;
    private readonly Updates _updates;
    private readonly ILogger _logger;
    private readonly Control _ui = new(); // gives the second-instance signal a way onto the UI thread
    private readonly RegisteredWaitHandle _wait;
    private WebApplication _host;
    private MainWindow? _window;
    private readonly SemaphoreSlim _hostGate = new(1, 1); // one restart at a time, and Exit waits for a running one
    private volatile bool _exiting;
    private volatile bool _dockOn = DockPrefs.Enabled;
    private PanelSide? _panelSide = DockPrefs.Side; // also read on the dock thread; a stale read lasts one tick
    private readonly ClientDock _dock;
    private readonly ReadyCheckWatch _readyWatch;
    private readonly Game.AugmentWatcher _augments;
    private readonly Game.LoadingShots _loadingShots;
    private readonly Game.LoadingWatcher _loadingParties;
    private readonly bool _devClient; // dev: a stand-in client in champ select (DockPrefs.FakeClient)
    private readonly bool _shotMode = DockPrefs.ShotMode; // dev: README shots
    private readonly ShotNames _shotNames = new(); // one per run, so a player keeps one made-up name across host restarts
    private readonly RuneWindow _runeWindow = new(); // the dock watches it, the host's rune page guard reads it
    private readonly Shots.ShotKeys? _shotKeys;

    public TrayApp(string[] hostArgs, EventWaitHandle showSignal, FileLog log, Glamour.Host.Telemetry.Telemetry? telemetry)
    {
        _hostArgs = hostArgs;
        _telemetry = telemetry;
        _log = log;
        _logger = log.CreateLogger("Glamour.Desktop");
        _desktopPort = FreeLoopbackPort(new ConfigurationBuilder().AddCommandLine(hostArgs).Build().GetValue("Glamour:DesktopPort", PreferredDesktopPort));
        _url = $"http://localhost:{_desktopPort}/";
        _updates = new Updates(UpdateMode, CurrentPhase, _logger, () => _ui.BeginInvoke(async () => await ExitAsync()));
        _host = BuildHost();

        _icon = BlossomIcon.Create(SystemInformation.SmallIconSize.Width);
        _windowIcon = BlossomIcon.Create(SystemInformation.IconSize.Width);
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open Glamour", null, (_, _) => ShowWindow());
        var accounts = new ToolStripMenuItem("Accounts");
        menu.Items.Add(accounts);
        var dock = new ToolStripMenuItem("Dock panel to League") { Checked = _dockOn, CheckOnClick = true };
        dock.CheckedChanged += (_, _) => { _dockOn = dock.Checked; DockPrefs.Enabled = _dockOn; _dock?.Refresh(); };
        menu.Items.Add(dock);
        var sideMenu = new ToolStripMenuItem("Panel side");
        foreach (var (label, value) in new (string, PanelSide?)[] { ("Auto", null), ("Right", PanelSide.Right), ("Left", PanelSide.Left) })
            sideMenu.DropDownItems.Add(new ToolStripMenuItem(label, null, (_, _) => SetPanelSide(value)) { Tag = value });
        sideMenu.DropDownOpening += (_, _) => { foreach (ToolStripMenuItem i in sideMenu.DropDownItems) i.Checked = (PanelSide?)i.Tag == _panelSide; };
        menu.Items.Add(sideMenu);
        menu.Opening += (_, _) => FillAccounts(accounts);
        var restart = new ToolStripMenuItem("Restart to update", null, (_, _) => _updates.RestartToUpdate()) { Visible = false };
        menu.Items.Add(restart);
        menu.Opening += (_, _) => restart.Visible = _updates.ReadyToRestart;
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, async (_, _) => await ExitAsync());
        _tray = new NotifyIcon { Icon = _icon, Text = "Glamour", ContextMenuStrip = menu, Visible = true };
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowWindow(); };

        _ = _ui.Handle;
        var panelQuery = "?view=panel&" + UiStamp;
        if (DockPrefs.FakeClient is { } picture)
        {
            var dev = new DevClient(picture);
            dev.Show();
            Win32.DevClient = dev.Handle;
            _devClient = true;
            panelQuery += "&demo=champ-final";
            _logger.LogWarning("Dev client on: the dock uses {Picture} as the League client", picture);
        }
        // Its own origin (127.0.0.1, not localhost): WebView2 keeps a zoom level per origin, and the panel is zoomed to the client while the window must stay at 100%.
        _dock = new ClientDock(_url.Replace("localhost", "127.0.0.1") + panelQuery, CurrentPhase, CurrentBoard, () => _dockOn, () => _panelSide, () => DockPrefs.ClickLog, CurrentAcceptWait, () => { if (!_exiting) _ui.BeginInvoke(ShowWindow); }, _runeWindow, _logger);
        _dock.Start();
        _readyWatch = new ReadyCheckWatch(CurrentPhase, _logger);
        _augments = new Game.AugmentWatcher(CurrentServices, _logger);
        _loadingShots = new Game.LoadingShots(CurrentServices, _logger);
        _loadingParties = new Game.LoadingWatcher(CurrentServices, _logger);
        if (_shotMode) _shotKeys = new Shots.ShotKeys(() => _dock.Windows, CurrentServices, CurrentPhase, _logger);
        _wait = ThreadPool.RegisterWaitForSingleObject(showSignal, (_, _) => { if (!_exiting) _ui.BeginInvoke(ShowWindow); }, null, Timeout.Infinite, executeOnlyOnce: false);
    }

    /// <summary>From the tray menu or the UI (any thread).</summary>
    private void SetPanelSide(PanelSide? side)
    {
        _panelSide = side;
        DockPrefs.Side = side;
        _dock?.Refresh();
    }

    private string? CurrentPhase()
    {
        if (_devClient) return "ChampSelect";
        try { return _host.Services.GetService<Engine>()?.Phase; }
        catch (ObjectDisposedException) { return null; } // the host is being replaced
    }

    private IServiceProvider? CurrentServices()
    {
        try { return _host.Services; }
        catch (ObjectDisposedException) { return null; }
    }

    /// <summary>Settings > Updates from the current host, or from host.json while there is none yet (or it is being replaced).</summary>
    private Glamour.Host.Network.UpdateMode UpdateMode()
    {
        try { if (_host is not null) return _host.Services.GetRequiredService<Glamour.Host.Network.HostSettings>().Updates; }
        catch (ObjectDisposedException) { /* replaced now */ }
        return Glamour.Host.Network.HostSettings.ReadUpdates(Glamour.Host.Network.HostSettings.DefaultPath);
    }

    private ChampSelectBoard? CurrentBoard()
    {
        if (_devClient) return DevClient.Board();
        try { return _host.Services.GetService<Engine>()?.Tracker.Board; }
        catch (ObjectDisposedException) { return null; }
    }

    private (DateTimeOffset From, DateTimeOffset At)? CurrentAcceptWait()
    {
        try { return _host.Services.GetService<AutomationGate>()?.AcceptWait; }
        catch (ObjectDisposedException) { return null; }
    }

    private WebApplication BuildHost() =>
        GlamourApp.Build(_hostArgs, new WebApplicationOptions { Args = _hostArgs, ContentRootPath = AppContext.BaseDirectory }, b =>
        {
            b.Services.AddSingleton<IDesktopShell>(new DesktopShell(RestartHostAsync, () => _panelSide, SetPanelSide));
            b.Services.AddHostedService(sp => new PhaseRelay(sp.GetRequiredService<EventHub>(), () => _dock?.Refresh()));
            b.Logging.AddProvider(_log); // not disposed with the host: the next host after a restart keeps writing to it
            if (_telemetry is not null) b.Services.AddSingleton(_telemetry); // GlamourApp adds its log hook and usage tally
            b.Services.AddSingleton<IUpdater>(_updates);
            b.Services.AddSingleton(_shotNames);
            b.Services.AddSingleton(_runeWindow);
            if (_shotMode) b.Configuration["Glamour:ShotNames"] = "true";
            b.Configuration["Glamour:DesktopPort"] = _desktopPort.ToString();
            // The window's port is bound by the host itself (GlamourApp); with phone access off it is the only listener.
        });

    /// <summary>
    /// Stops the host and starts a new one on the same window port, for settings the host reads only at startup (phone access). The page reconnects by itself.
    /// A second call while one runs waits and restarts again, so the newest saved choice always wins.
    /// </summary>
    private async Task RestartHostAsync()
    {
        UsageOrigin.Set("auto"); // started by a request: the new host's loops must not count as that device's writes
        await _hostGate.WaitAsync();
        try
        {
            if (_exiting) return;
            try { await _host.StopAsync(TimeSpan.FromSeconds(3)); } catch (Exception) { /* replaced anyway */ }
            try { await _host.DisposeAsync(); } catch (Exception ex) { _logger.LogWarning(ex, "Old host did not dispose cleanly"); }
            _host = BuildHost();
            await _host.StartAsync();
        }
        catch (Exception ex)
        {
            // Nobody awaits this (it runs after the HTTP answer), so say it here: the window has nothing to talk to now.
            _logger.LogError(ex, "Host restart failed");
            if (!_exiting) _ui.BeginInvoke(() => _tray.ShowBalloonTip(10_000, "Glamour", "Glamour stopped. Exit it from the tray and open it again.", ToolTipIcon.Error));
        }
        finally { _hostGate.Release(); }
    }

    /// <summary>One entry per saved account, the signed-in one checked; all disabled while a switch would be refused. Built each time the menu opens, from the current host.</summary>
    private void FillAccounts(ToolStripMenuItem root)
    {
        root.DropDownItems.Clear();
        try
        {
            var service = _host.Services.GetRequiredService<ProfileService>();
            var store = _host.Services.GetRequiredService<ProfileStore>();
            var d = store.Data;
            if (d.Profiles.Count == 0) { root.DropDownItems.Add(new ToolStripMenuItem("No saved accounts") { Enabled = false }); return; }
            var canSwitch = service.CanSwitch;
            foreach (var p in d.Profiles.ToList())
            {
                var label = (p.RiotId ?? p.Name) + (string.IsNullOrEmpty(p.Region) ? "" : "  " + p.Region.ToUpperInvariant());
                var active = p.Id == d.ActiveId && !d.Adding;
                var item = new ToolStripMenuItem(label.Replace("&", "&&")) { Checked = active, Enabled = canSwitch && !active && store.HasSession(p.Id) };
                var (id, name) = (p.Id, p.RiotId ?? p.Name);
                item.Click += (_, _) => _ = SwitchAccountAsync(id, name);
                root.DropDownItems.Add(item);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not list accounts for the tray menu");
            root.DropDownItems.Add(new ToolStripMenuItem("Unavailable") { Enabled = false });
        }
    }

    /// <summary>The same switch as the Accounts page. If it needs the Riot Client closed, a message box asks first.</summary>
    private async Task SwitchAccountAsync(string id, string name)
    {
        try
        {
            var service = _host.Services.GetRequiredService<ProfileService>();
            Balloon($"Switching to {name}...", ToolTipIcon.Info);
            try { await service.SwitchAsync(id, confirm: false, CancellationToken.None); }
            catch (ProfileException ex) when (ex.Code == "confirm")
            {
                var willClose = (ex.Extra?.GetType().GetProperty("willClose")?.GetValue(ex.Extra) as IEnumerable<string>)?.ToList() ?? [];
                var list = string.Join(" and ", ["the Riot Client", .. willClose]);
                var answer = MessageBox.Show($"Switching to {name} closes {list}. Close and switch?", "Glamour", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
                if (answer != DialogResult.OK) return;
                await service.SwitchAsync(id, confirm: true, CancellationToken.None);
            }
            Balloon($"Switched to {name}.", ToolTipIcon.Info);
        }
        catch (ProfileException ex) { Balloon(ex.Message, ToolTipIcon.Warning); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Tray account switch failed");
            Balloon("Could not switch accounts.", ToolTipIcon.Error);
        }
    }

    private void Balloon(string text, ToolTipIcon icon)
    {
        if (!_exiting) _ui.BeginInvoke(() => _tray.ShowBalloonTip(5_000, "Glamour", text, icon));
    }

    /// <summary>`preferred` if nothing holds it on this PC, else the first free port after it, else any free one. Checked on IPv4 and IPv6 loopback, as Kestrel binds both for localhost and fails if either is taken.</summary>
    private static int FreeLoopbackPort(int preferred)
    {
        for (var p = preferred; p < preferred + 20; p++) if (IsFree(p)) return p;
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }

    private static bool IsFree(int port) => IsFree(IPAddress.Loopback, port) && (!Socket.OSSupportsIPv6 || IsFree(IPAddress.IPv6Loopback, port));

    private static bool IsFree(IPAddress address, int port)
    {
        try
        {
            using var l = new TcpListener(address, port);
            l.Start();
            return true;
        }
        catch (SocketException) { return false; }
    }

    public async Task StartAsync()
    {
        await _host.StartAsync().ConfigureAwait(false); // the caller blocks the UI thread on this: never come back to it
        // The one time the window opens by itself: the first-run tour, until it is finished or skipped (the user disliked it opening at every start).
        if (!_host.Services.GetRequiredService<Glamour.Host.Network.HostSettings>().TourDone) _ui.BeginInvoke(ShowWindow);
    }

    public void ShowWindow()
    {
        if (_exiting) return;
        if (_window is null || _window.IsDisposed)
        {
            _window = new MainWindow(_url, _windowIcon, _logger);
            _window.FormClosed += (_, _) => _window = null;
        }
        _window.Bring();
    }

    private async Task ExitAsync()
    {
        if (_exiting) return;
        _exiting = true;
        _tray.Visible = false;
        _window?.Close();
        await _hostGate.WaitAsync(); // a restart in progress finishes first, so the host stopped here is the last one
        try { await _host.StopAsync(TimeSpan.FromSeconds(3)); } catch (Exception) { /* leaving anyway */ }
        finally { _hostGate.Release(); }
        _updates.ApplyOnExit(); // Velopack's updater waits for this process to end
        _telemetry?.Flush(TimeSpan.FromSeconds(2));
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _wait.Unregister(null);
            _dock.Dispose();
            _readyWatch.Dispose();
            _augments.Dispose();
            _loadingShots.Dispose();
            _loadingParties.Dispose();
            _shotKeys?.Dispose();
            _tray.Dispose();
            _icon.Dispose();
            _windowIcon.Dispose();
            _ui.Dispose();
            _host.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _hostGate.Dispose();
            _updates.Dispose();
        }
        base.Dispose(disposing);
    }
}
