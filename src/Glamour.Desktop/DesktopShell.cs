using Glamour.Host;
using Glamour.Host.Panel;
using Microsoft.Win32;

namespace Glamour.Desktop;

/// <summary>Start with Windows: a per-user Run entry (HKCU, no admin). The entry starts the app in the tray. The panel side is the tray app's own choice, passed through.</summary>
internal sealed class DesktopShell(Func<Task> restartHost, Func<PanelSide?> getSide, Action<PanelSide?> setSide) : IDesktopShell
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run", ValueName = "Glamour";

    public Task RestartHostAsync() => restartHost();

    public PanelSide? PanelSide { get => getSide(); set => setSide(value); }

    public bool PanelAutoOpen { get => Dock.DockPrefs.AutoOpen; set => Dock.DockPrefs.AutoOpen = value; } // the dock reads it every tick

    private static string Command => $"\"{Install.LaunchPath}\" {Program.TrayFlag}"; // the installed stub survives updates

    /// <summary>Uninstall: the entry would start an exe that is gone.</summary>
    public static void RemoveAutostart()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    public bool Autostart
    {
        get
        {
            // On only if the entry starts THIS exe: an entry left by a copy that moved or was deleted starts that copy or nothing.
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string v && v.StartsWith($"\"{Install.LaunchPath}\"", StringComparison.OrdinalIgnoreCase);
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value) key.SetValue(ValueName, Command);
            else key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
