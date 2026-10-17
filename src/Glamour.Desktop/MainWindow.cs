using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Glamour.Desktop;

/// <summary>The app window: WebView2 showing Glamour's own page. Closing it disposes the form and the WebView2, so no Chromium process stays running while only the tray is.</summary>
internal sealed class MainWindow : Form
{
    private static readonly Size Start = new(1180, 780), Smallest = new(900, 600); // in 96-dpi units; the PC UI is built for a desktop-sized window
    private readonly string _url;
    private readonly ILogger _log;
    private readonly WebView2 _web = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.FromArgb(6, 10, 18) };

    public MainWindow(string url, Icon icon, ILogger log)
    {
        _url = url;
        _log = log;
        Text = "Glamour";
        Icon = icon;
        BackColor = Color.FromArgb(6, 10, 18);
        StartPosition = FormStartPosition.CenterScreen;
        // The form does no DPI scaling of its own, so sizes are pixels: scale them, or at 150 % the page gets a 787 px wide window.
        ClientSize = LogicalToDeviceUnits(Start);
        MinimumSize = LogicalToDeviceUnits(Smallest);
        Controls.Add(_web);
        Load += async (_, _) => await InitAsync();
    }

    private async Task InitAsync()
    {
        try
        {
            var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Glamour", "WebView2");
            await _web.EnsureCoreWebView2Async(await CoreWebView2Environment.CreateAsync(userDataFolder: data));
        }
        catch (Exception ex)
        {
            if (IsDisposed) return; // closed while it was starting
            // No WebView2 runtime (or it failed to start): the page still works in the default browser.
            _log.LogWarning(ex, "WebView2 did not start; opening the default browser");
            Process.Start(new ProcessStartInfo(_url) { UseShellExecute = true });
            Close();
            return;
        }
        if (IsDisposed) return;
        var core = _web.CoreWebView2;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false; // no Ctrl+P / F12 / view-source in what is an app window
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        // No browser menu on a right click, except in a text field (cut, copy, paste).
        core.ContextMenuRequested += (_, e) => e.Handled = !e.ContextMenuTarget.IsEditable;
        // The window shows only Glamour's own origin; anything else (links in the UI) opens in the real browser.
        core.NewWindowRequested += (_, e) => { e.Handled = true; OpenExternal(e.Uri); };
        core.NavigationStarting += (_, e) =>
        {
            if (!e.Uri.StartsWith(_url, StringComparison.OrdinalIgnoreCase) && !e.Uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) { e.Cancel = true; OpenExternal(e.Uri); }
        };
        // A crashed page would leave the window blank: reload it; if the whole browser process died, close the window (the tray opens a fresh one).
        core.ProcessFailed += (_, e) =>
        {
            _log.LogWarning("WebView2 process failed: {Kind}", e.ProcessFailedKind);
            if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.RenderProcessExited) core.Reload();
            else if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.BrowserProcessExited) BeginInvoke(Close);
        };
        _web.Source = new Uri(_url + "?" + TrayApp.UiStamp);
    }

    private static void OpenExternal(string uri)
    {
        if (Uri.TryCreate(uri, UriKind.Absolute, out var u) && u.Scheme is "http" or "https") Process.Start(new ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true });
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e); // Windows resizes the window itself when it moves to a monitor with another scale; the minimum follows here
        MinimumSize = new Size(Smallest.Width * e.DeviceDpiNew / 96, Smallest.Height * e.DeviceDpiNew / 96);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        var dark = 1; // DWMWA_USE_IMMERSIVE_DARK_MODE: the UI is dark only, so the title bar is too
        DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int));
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public void Bring()
    {
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Show();
        Activate();
    }
}
