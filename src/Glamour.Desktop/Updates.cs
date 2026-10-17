using Glamour.Host;
using Glamour.Host.Logging;
using Glamour.Host.Network;
using Microsoft.Extensions.Logging;
using Velopack;
using Velopack.Sources;

namespace Glamour.Desktop;

/// <summary>
/// Updates through Velopack from the GitHub releases (SPEC "Publishing"). Automatic: looks at start and every 6 hours, downloads, and the update goes in
/// when Glamour exits (Exit in the tray; after a Windows shutdown Velopack puts it in at the next start). Notify me: the same download, then
/// "Restart to update" in the panel, the window and the tray. Off: never looks. Glamour never restarts itself for an update (user).
/// Only an installed copy (Velopack's Setup) with a known repo updates; a dev copy reports updates as not available.
/// </summary>
internal sealed class Updates : IUpdater, IDisposable
{
    private static readonly TimeSpan Every = TimeSpan.FromHours(6);

    private readonly UpdateManager? _manager;
    private readonly Func<UpdateMode> _mode;
    private readonly Func<string?> _phase;
    private readonly ILogger _log;
    private readonly Action _exitForRestart;
    private readonly System.Threading.Timer _timer;
    private readonly SemaphoreSlim _busy = new(1, 1);
    private UpdateStatus _status;
    private volatile bool _restart;

    /// <param name="exitForRestart">Stops the host and closes the app; <see cref="ApplyOnExit"/> then runs with restart.</param>
    /// <param name="phase">The client's phase: no download starts in champ select or a game (the next look is an hour later).</param>
    public Updates(Func<UpdateMode> mode, Func<string?> phase, ILogger log, Action exitForRestart)
    {
        _mode = mode;
        _phase = phase;
        _log = log;
        _exitForRestart = exitForRestart;
        if (LogReport.GitHubRepo is { } repo && Install.Root is not null)
        {
            try
            {
                var manager = new UpdateManager(new GithubSource($"https://github.com/{repo}", null, false));
                if (manager.IsInstalled) _manager = manager;
            }
            catch (Exception ex) { _log.LogWarning(ex, "Updates: Velopack did not start"); }
        }
        _status = _manager is null ? UpdateStatus.NotInstalled
            : _manager.UpdatePendingRestart is { } pending ? new UpdateStatus(true, "ready", pending.Version.ToString(), pending.NotesMarkdown)
            : new UpdateStatus(true, mode() == UpdateMode.Off ? "off" : "none");
        _timer = new System.Threading.Timer(_ => _ = LookAsync(), null, _manager is null ? Timeout.InfiniteTimeSpan : TimeSpan.FromMinutes(1), Every);
    }

    public string? InstallFolder => Install.Root;
    public UpdateStatus Status => _status;
    public event Action? Changed;

    /// <summary>For the tray menu: a downloaded update waiting for "Restart to update" (Notify me).</summary>
    public bool ReadyToRestart => _status.State == "ready" && _mode() == UpdateMode.Notify;

    public Task CheckAsync() => LookAsync(byHand: true);

    public void ModeChanged()
    {
        if (_manager is null) return;
        if (_mode() == UpdateMode.Off && _status.State != "ready") Set(_status with { State = "off", Error = null });
        else if (_status.State == "off") { Set(_status with { State = "none" }); _ = LookAsync(); }
        else Changed?.Invoke(); // the mode is part of what the pages show
    }

    private async Task LookAsync(bool byHand = false)
    {
        if (_manager is null || (!byHand && _mode() == UpdateMode.Off) || _status.State == "ready") return;
        if (!byHand && _phase() is "ChampSelect" or "GameStart" or "InProgress" or "Reconnect")
        {
            _timer.Change(TimeSpan.FromHours(1), Every);
            return;
        }
        if (!await _busy.WaitAsync(0)) return;
        try
        {
            Set(_status with { State = "checking", Error = null });
            var info = await _manager.CheckForUpdatesAsync();
            if (info is null) { Set(new UpdateStatus(true, _mode() == UpdateMode.Off ? "off" : "none")); return; }
            var target = info.TargetFullRelease;
            Set(new UpdateStatus(true, "downloading", target.Version.ToString(), target.NotesMarkdown));
            await _manager.DownloadUpdatesAsync(info);
            _log.LogInformation("Updates: {Version} downloaded, goes in {When}", target.Version, _mode() == UpdateMode.Automatic ? "when Glamour exits" : "on Restart");
            Set(new UpdateStatus(true, "ready", target.Version.ToString(), target.NotesMarkdown));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Updates: could not look or download");
            Set(new UpdateStatus(true, "error", _status.Version, _status.Notes, "Could not reach the update server."));
        }
        finally { _busy.Release(); }
    }

    private void Set(UpdateStatus s)
    {
        _status = s;
        Changed?.Invoke();
    }

    public void RestartToUpdate()
    {
        _restart = true;
        _exitForRestart();
    }

    /// <summary>Called as Glamour exits: hands a downloaded update to Velopack's updater, which waits for this process to end. Automatic, or Restart pressed.</summary>
    public void ApplyOnExit()
    {
        if (_manager?.UpdatePendingRestart is not { } pending || !(_restart || _mode() == UpdateMode.Automatic)) return;
        try
        {
            _log.LogInformation("Updates: putting in {Version}{Restart}", pending.Version, _restart ? ", then starting again" : "");
            _manager.WaitExitThenApplyUpdates(pending, silent: true, restart: _restart);
        }
        catch (Exception ex) { _log.LogWarning(ex, "Updates: could not hand the update over"); }
    }

    public void Dispose()
    {
        _timer.Dispose();
        _busy.Dispose();
    }
}

/// <summary>Where Velopack installed Glamour: `<root>\current\Glamour.exe` runs, `<root>\Glamour.exe` is the stub that stays put across updates.</summary>
internal static class Install
{
    /// <summary>Velopack's pack id, so the install folder is %LOCALAPPDATA%\GlamourApp: not %LOCALAPPDATA%\Glamour, which holds the settings and saved sessions an uninstall must not delete.</summary>
    public const string PackId = "GlamourApp";

    public static readonly string? Root = FindRoot();

    /// <summary>What a shortcut or the Start with Windows entry should start: the stub when installed, else this exe.</summary>
    public static string LaunchPath => Root is { } r && File.Exists(Path.Combine(r, "Glamour.exe")) ? Path.Combine(r, "Glamour.exe") : Environment.ProcessPath!;

    private static string? FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        return dir.Name.Equals("current", StringComparison.OrdinalIgnoreCase) && dir.Parent is { } root && File.Exists(Path.Combine(root.FullName, "Update.exe")) ? root.FullName : null;
    }

    /// <summary>The first lines of Main: Velopack's install, update and uninstall hooks run here and exit. A downloaded update goes in at start only with Automatic.</summary>
    public static void RunVelopack()
    {
        VelopackApp.Build()
            .SetAutoApplyOnStartup(HostSettings.ReadUpdates(HostSettings.DefaultPath) == UpdateMode.Automatic)
            .OnBeforeUninstallFastCallback(_ => DesktopShell.RemoveAutostart()) // the Run entry would start a deleted exe
            .Run();
    }
}
