using System.Windows.Forms;
using Glamour.Host.Logging;
using Microsoft.Extensions.Logging;

namespace Glamour.Desktop;

internal static class Program
{
    /// <summary>Passed by the start-at-login entry. Every start is in the tray now (the window opening by itself annoyed the user); the flag stays for old entries.</summary>
    public const string TrayFlag = "--tray";

    [STAThread]
    private static int Main(string[] args)
    {
        Install.RunVelopack(); // first: Velopack's install, update and uninstall hooks run here and exit
        ApplicationConfiguration.Initialize();
        if (args is ["--augment-shot", var shot])
        {
            Game.AugmentWatcher.Shot(shot);
            return 0;
        }
        if (args is ["--loading-shot", var loading, .. var cards])
        {
            Game.LoadingWatcher.Shot(loading, cards);
            return 0;
        }
        if (args is ["--mask-shot", var masked, .. var real])
        {
            Shots.ShotMask.Shot(masked, real);
            return 0;
        }
        if (args is ["--mask-frames", var clip, .. var clipArgs])
        {
            Shots.ShotMask.Frames(clip, clipArgs);
            return 0;
        }
        if (args is ["--write-icons", var iconDir])
        {
            BlossomIcon.Write(iconDir);
            return 0;
        }
        // One copy per user. A second launch asks the first to show its window and leaves.
        using var mutex = new Mutex(true, @"Local\Glamour.Desktop", out var first);
        using var show = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\Glamour.Desktop.Show");
        if (!first)
        {
            show.Set();
            return 0;
        }

        var hostArgs = args.Where(a => a != TrayFlag).ToArray();
        using var log = new FileLog(FileLog.DefaultDir);
        // "Help improve Glamour": one for the whole run, shared by every host the app builds (none for a stand-in run with Glamour:NoClient).
        using var telemetry = hostArgs.Any(a => a.Contains("NoClient=true", StringComparison.OrdinalIgnoreCase)) ? null : new Glamour.Host.Telemetry.Telemetry("desktop");
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            log.Write(LogLevel.Critical, "Glamour.Desktop", "Unhandled exception", e.ExceptionObject as Exception);
            if (e.ExceptionObject is Exception ex) telemetry?.Crash("Glamour.Desktop", ex);
        };
        // A failure on the UI thread (a tray or window handler) is logged and reported, and Glamour keeps running instead of showing .NET's error box.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
        {
            log.Write(LogLevel.Critical, "Glamour.Desktop", "Unhandled exception on the UI thread", e.Exception);
            telemetry?.Error("Glamour.Desktop", "Unhandled exception on the UI thread", "Unhandled exception on the UI thread", e.Exception, fatal: true);
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            log.Write(LogLevel.Error, "Glamour.Desktop", "A background task failed unobserved", e.Exception);
            telemetry?.Error("Glamour.Desktop", "A background task failed unobserved", "A background task failed unobserved", e.Exception);
            e.SetObserved();
        };
        try
        {
            using var app = new TrayApp(hostArgs, show, log, telemetry);
            app.StartAsync().GetAwaiter().GetResult(); // the window opens from the tray icon or a second launch, and by itself only for the first-run tour
            Application.Run(app);
            return 0;
        }
        catch (Exception ex)
        {
            log.Write(LogLevel.Critical, "Glamour.Desktop", "Could not start", ex);
            telemetry?.Crash("Glamour.Desktop", ex);
            // No console exists, so a failed start has to be said in a box. A busy port is not one of the causes: the window picks a free port of its own.
            MessageBox.Show($"Glamour could not start.\n\n{ex.Message}", "Glamour", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }
}
