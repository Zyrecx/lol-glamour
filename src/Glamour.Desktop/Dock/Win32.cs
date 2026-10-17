using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Glamour.Desktop.Dock;

/// <summary>The few Win32 calls the docked panel needs. The process is PerMonitorV2, so every rectangle here is in physical pixels.</summary>
internal static class Win32
{
    public const uint EventSystemMinimizeStart = 0x0016, EventSystemMinimizeEnd = 0x0017;
    public const uint EventObjectDestroy = 0x8001, EventObjectShow = 0x8002, EventObjectHide = 0x8003, EventObjectLocationChange = 0x800B;
    public const uint WinEventOutOfContext = 0, WinEventSkipOwnProcess = 2;
    public const int ObjidWindow = 0, GwlpHwndParent = -8;
    public const uint SwpNoActivate = 0x0010, SwpNoZOrder = 0x0004;
    public const int WsExToolWindow = 0x80, WsExNoActivate = 0x08000000, WsExAppWindow = 0x40000;

    public delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
    private delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public Rect Monitor, Work; public uint Flags; }

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc proc, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint x, out uint y);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint command);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc proc, uint pid, uint thread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out Rect rect, int size);

    public static bool Exists(IntPtr hwnd) => hwnd != IntPtr.Zero && IsWindow(hwnd);
    public static bool IsMinimized(IntPtr hwnd) => IsIconic(hwnd);
    public static bool IsShown(IntPtr hwnd) => IsWindowVisible(hwnd);

    public static uint ProcessOf(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out var pid);
        return pid;
    }

    /// <summary>The League client's main window: class RCLIENT in a LeagueClientUx process, the biggest visible one if there are several.</summary>
    /// <summary>Dev only: the <see cref="DevClient"/> window, taken for the League client.</summary>
    public static IntPtr DevClient;

    public static IntPtr FindLeagueClient()
    {
        if (DevClient != IntPtr.Zero) return IsWindow(DevClient) ? DevClient : IntPtr.Zero;
        // Runs every second while League is closed: the window class first (cheap), the process only for an RCLIENT window, never a process snapshot.
        var best = IntPtr.Zero;
        long bestArea = 0;
        var name = new StringBuilder(64);
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h)) return true;
            name.Clear();
            if (GetClassName(h, name, name.Capacity) == 0 || name.ToString() != "RCLIENT" || !IsLeagueClientUx(ProcessOf(h))) return true;
            var r = Bounds(h);
            var area = (long)(r.Right - r.Left) * (r.Bottom - r.Top);
            if (area > bestArea) (best, bestArea) = (h, area);
            return true;
        }, IntPtr.Zero);
        return best;
    }

    private static bool IsLeagueClientUx(uint pid)
    {
        try
        {
            using var p = Process.GetProcessById((int)pid);
            return p.ProcessName == "LeagueClientUx";
        }
        catch (ArgumentException) { return false; } // already gone
    }

    private static Rect Bounds(IntPtr hwnd) =>
        DwmGetWindowAttribute(hwnd, 9 /* DWMWA_EXTENDED_FRAME_BOUNDS: without the invisible resize border */, out var r, Marshal.SizeOf<Rect>()) == 0 ? r : GetWindowRect(hwnd, out r) ? r : default;

    public static Glamour.Host.Panel.PixelRect ClientRect(IntPtr hwnd)
    {
        var r = Bounds(hwnd);
        return new(r.Left, r.Top, r.Right, r.Bottom);
    }

    /// <summary>Work area (taskbar excluded) and DPI of the monitor the window is on.</summary>
    public static (Glamour.Host.Panel.PixelRect Work, int Dpi) MonitorOf(IntPtr hwnd)
    {
        var m = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        var monitor = MonitorFromWindow(hwnd, 2 /* nearest */);
        // The monitor's own scale (our process is PerMonitorV2): the client window may be DPI-unaware and report something else.
        var dpi = GetDpiForMonitor(monitor, 0 /* effective */, out var x, out _) == 0 ? (int)x : 96;
        if (!GetMonitorInfo(monitor, ref m)) return (new(0, 0, 1920, 1040), dpi);
        return (new(m.Work.Left, m.Work.Top, m.Work.Right, m.Work.Bottom), dpi);
    }

    public static void Place(IntPtr hwnd, Glamour.Host.Panel.PixelRect r) =>
        SetWindowPos(hwnd, IntPtr.Zero, r.Left, r.Top, r.Width, r.Height, SwpNoActivate | SwpNoZOrder);

    [DllImport("gdi32.dll")] private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("gdi32.dll")] private static extern int CombineRgn(IntPtr dest, IntPtr a, IntPtr b, int mode);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, bool redraw);

    /// <summary>Shows only these parts of the window (window coordinates; clicks elsewhere go to what is under it); none: the whole window again.</summary>
    public static void Clip(IntPtr hwnd, params Rectangle[] parts)
    {
        if (parts.Length == 0) { SetWindowRgn(hwnd, IntPtr.Zero, true); return; }
        var region = CreateRectRgn(0, 0, 0, 0);
        foreach (var p in parts)
        {
            var part = CreateRectRgn(p.Left, p.Top, p.Right, p.Bottom);
            CombineRgn(region, region, part, 2 /* RGN_OR */);
            DeleteObject(part);
        }
        if (SetWindowRgn(hwnd, region, true) == 0) DeleteObject(region); // on success Windows owns it
    }

    /// <summary>Makes `owner` the owner of `hwnd`: it stays above it and hides with it when the owner is minimized. Returns false if Windows refused.</summary>
    public static bool SetOwner(IntPtr hwnd, IntPtr owner)
    {
        Trace?.Invoke($"SetOwner {Describe(hwnd)} owner {Describe(owner)}");
        Marshal.SetLastPInvokeError(0);
        SetWindowLongPtr(hwnd, GwlpHwndParent, owner);
        return Marshal.GetLastPInvokeError() == 0;
    }

    public static IntPtr OwnerOf(IntPtr hwnd) => GetWindow(hwnd, 4 /* GW_OWNER */);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    public static bool IsTopmost(IntPtr hwnd) => (GetWindowLongPtr(hwnd, -20 /* GWL_EXSTYLE */).ToInt64() & 0x8 /* WS_EX_TOPMOST */) != 0;

    /// <summary>Moves the window into or out of the always-on-top band without moving, sizing or activating it.</summary>
    public static void SetTopmost(IntPtr hwnd, bool topmost)
    {
        Trace?.Invoke($"SetTopmost {Describe(hwnd)} {topmost}");
        SetWindowPos(hwnd, topmost ? -1 /* HWND_TOPMOST */ : -2 /* HWND_NOTOPMOST */, 0, 0, 0, 0, 0x1 | 0x2 | 0x10 /* NOSIZE | NOMOVE | NOACTIVATE */);
    }

    /// <summary>
    /// Puts <paramref name="hwnd"/> right above <paramref name="client"/> in the z-order, without activating it. Showing a window puts it on top of every
    /// app, and setting the owner afterwards does not move it back down: with League behind another app the panel stayed over that app (user).
    /// </summary>
    public static void PlaceAbove(IntPtr hwnd, IntPtr client)
    {
        if (!Exists(client)) return;
        var above = GetWindow(client, 3 /* GW_HWNDPREV */);
        var topmost = IsTopmost(hwnd);
        if (above == hwnd && !topmost) return;
        // SetWindowPos puts a window below the one named, so name the window now above the client (placed after a normal window, ours also leaves the
        // always-on-top band). None, ours, or an always-on-top one means the client leads the normal windows: the top of them is right above it.
        var leads = above == IntPtr.Zero || above == hwnd || IsTopmost(above);
        Trace?.Invoke($"PlaceAbove {Describe(hwnd)} under {(leads ? "the always-on-top band" : Describe(above))}");
        SetWindowPos(hwnd, !leads ? above : topmost ? new IntPtr(-2) /* HWND_NOTOPMOST */ : IntPtr.Zero /* HWND_TOP */, 0, 0, 0, 0, 0x1 | 0x2 | 0x10 /* NOSIZE | NOMOVE | NOACTIVATE */);
    }

    /// <summary>
    /// Keeps a draw-only window owned by <paramref name="owner"/> right above it: in the always-on-top band when the owner is, and lifted again when it
    /// fell under it. Owning a window does not do this by itself: a window shown without activation keeps its old place, under a game or client that
    /// opened after it, until the owner is activated again (user, live: the first augment tags and the champ select ring showed only after an alt-tab).
    /// Never <see cref="SetTopmost"/> false on an owned window: HWND_NOTOPMOST on one below its owner drags the owner down under it, i.e. under every
    /// app in between (the client dropping behind other apps at match found, caught live; the same steps against two test windows did it every time).
    /// </summary>
    public static void KeepAbove(IntPtr hwnd, IntPtr owner)
    {
        if (!Exists(owner)) return;
        if (IsTopmost(owner)) { if (!IsTopmost(hwnd) || !IsAbove(hwnd, owner)) SetTopmost(hwnd, true); }
        else if (IsTopmost(hwnd) || !IsAbove(hwnd, owner)) PlaceAbove(hwnd, owner);
    }

    /// <summary>Shows a hidden draw-only window right above <paramref name="owner"/>, owned by it, without moving the owner (see <see cref="KeepAbove"/>).</summary>
    public static void ShowAbove(Form form, IntPtr owner)
    {
        PlaceAbove(form.Handle, owner); // while hidden: owning a window deep in the z-order and then lifting it pulls the owner down
        if (OwnerOf(form.Handle) != owner) SetOwner(form.Handle, owner);
        form.Show();
        if (OwnerOf(form.Handle) != owner) SetOwner(form.Handle, owner); // WinForms may reset it while it shows the window
        KeepAbove(form.Handle, owner);
    }

    /// <summary>Whether <paramref name="a"/> is above <paramref name="b"/> in the z-order (false when <paramref name="b"/> is not found below it).</summary>
    public static bool IsAbove(IntPtr a, IntPtr b)
    {
        for (var (h, n) = (GetWindow(a, 2 /* GW_HWNDNEXT */), 0); h != IntPtr.Zero && n < 5000; h = GetWindow(h, 2), n++)
            if (h == b) return true;
        return false;
    }

    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    public static bool SetForeground(IntPtr hwnd)
    {
        Trace?.Invoke($"SetForeground {Describe(hwnd)}");
        return SetForegroundWindow(hwnd);
    }

    /// <summary>While set, every z-order, owner and foreground call above is reported to it (<see cref="ReadyCheckWatch"/>). Any thread.</summary>
    public static volatile Action<string>? Trace;

    private static readonly Dictionary<uint, string> ProcessNames = [];

    /// <summary>"process/class #handle" for a log line; "none" for zero.</summary>
    public static string Describe(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return "none";
        var name = new StringBuilder(64);
        GetClassName(hwnd, name, name.Capacity);
        var pid = ProcessOf(hwnd);
        string process;
        lock (ProcessNames)
            if (!ProcessNames.TryGetValue(pid, out process!))
            {
                try { using var p = Process.GetProcessById((int)pid); process = p.ProcessName; } catch (Exception) { process = $"pid {pid}"; }
                ProcessNames[pid] = process;
            }
        return $"{process}/{name} #{hwnd:x}";
    }

    /// <summary>The nearest shown window above <paramref name="hwnd"/> and how many shown windows are above it.</summary>
    public static (IntPtr Next, int Count) ShownAbove(IntPtr hwnd)
    {
        var (next, count) = (IntPtr.Zero, 0);
        for (var (h, n) = (GetWindow(hwnd, 3 /* GW_HWNDPREV */), 0); h != IntPtr.Zero && n < 5000; h = GetWindow(h, 3), n++)
        {
            if (!IsWindowVisible(h)) continue;
            if (next == IntPtr.Zero) next = h;
            count++;
        }
        return (next, count);
    }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    public static IntPtr Foreground() => GetForegroundWindow();

    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point p);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hwnd, IntPtr dc, uint flags);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hwnd, ref Point p);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string? className, string? title);

    /// <summary>The game's own window (not the client): class `RiotWindowClass`, title "League of Legends (TM) Client"; zero when no game runs.</summary>
    public static IntPtr FindGame() => FindWindow("RiotWindowClass", "League of Legends (TM) Client");

    /// <summary>A window's client area in screen pixels (the game draws its whole picture there; borderless, it is the monitor).</summary>
    public static Glamour.Host.Panel.PixelRect ClientArea(IntPtr hwnd)
    {
        if (!GetClientRect(hwnd, out var area)) return default;
        var origin = new Point();
        ClientToScreen(hwnd, ref origin);
        return new(origin.X, origin.Y, origin.X + area.Right, origin.Y + area.Bottom);
    }

    /// <summary>Three views of the client's size, to learn which one the page's layout follows: DWM frame, window rect (what PrintWindow draws) and client area.</summary>
    public static string Sizes(IntPtr hwnd)
    {
        var frame = Bounds(hwnd);
        GetWindowRect(hwnd, out var win);
        GetClientRect(hwnd, out var area);
        var origin = new Point();
        ClientToScreen(hwnd, ref origin);
        return $"frame {frame.Left},{frame.Top} {frame.Right - frame.Left}x{frame.Bottom - frame.Top}, window {win.Left},{win.Top} {win.Right - win.Left}x{win.Bottom - win.Top}, client area {origin.X},{origin.Y} {area.Right}x{area.Bottom}";
    }

    /// <summary>The top-level window under a screen point (what a click there lands on), and its own class.</summary>
    public static (IntPtr Root, string Class) WindowAt(int x, int y)
    {
        var hwnd = WindowFromPoint(new Point { X = x, Y = y });
        var name = new StringBuilder(128);
        GetClassName(hwnd, name, name.Capacity);
        return (GetAncestor(hwnd, 2 /* GA_ROOT */), name.ToString());
    }

    /// <summary>The window as it is drawn, even under other windows (PW_RENDERFULLCONTENT: also Chromium's GPU-drawn pages).</summary>
    public static Bitmap? Capture(IntPtr hwnd)
    {
        var r = Bounds(hwnd);
        if (r.Right <= r.Left || r.Bottom <= r.Top) return null;
        var bmp = new Bitmap(r.Right - r.Left, r.Bottom - r.Top);
        using var g = Graphics.FromImage(bmp);
        var dc = g.GetHdc();
        try { PrintWindow(hwnd, dc, 2); }
        finally { g.ReleaseHdc(dc); }
        return bmp;
    }

    /// <summary>Whether any pixel on screen in <paramref name="box"/> passes <paramref name="test"/>: what is shown there, whatever window it belongs to.</summary>
    public static bool AnyPixel(Glamour.Host.Panel.PixelRect box, Func<byte, byte, byte, bool> test)
    {
        if (box.Width <= 0 || box.Height <= 0) return false;
        using var bmp = new Bitmap(box.Width, box.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(box.Left, box.Top, 0, 0, bmp.Size);
        // One copy of the pixels, not GetPixel per pixel (each call locks the bitmap): this runs four times a second in champ select.
        var data = bmp.LockBits(new Rectangle(0, 0, box.Width, box.Height), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var px = new byte[data.Stride * box.Height];
        Marshal.Copy(data.Scan0, px, 0, px.Length);
        bmp.UnlockBits(data);
        for (var y = 0; y < box.Height; y++)
            for (var x = 0; x < box.Width; x++)
            {
                var i = y * data.Stride + x * 4; // BGRA
                if (test(px[i + 2], px[i + 1], px[i])) return true;
            }
        return false;
    }

    public static IntPtr Hook(uint min, uint max, uint pid, WinEventProc proc) =>
        SetWinEventHook(min, max, IntPtr.Zero, proc, pid, 0, WinEventOutOfContext | WinEventSkipOwnProcess);

    public static void Unhook(IntPtr hook) { if (hook != IntPtr.Zero) UnhookWinEvent(hook); }
}
