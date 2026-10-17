using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace Glamour.Desktop.Dock;

/// <summary>A mouse button press anywhere on screen, in physical pixels.</summary>
internal readonly record struct ScreenClick(int X, int Y, MouseButtons Button, long At);

/// <summary>
/// Watches mouse presses with a low-level hook while it is on (SPEC "Click layer on the client's roster"). Passive: every press goes on to whatever was
/// clicked, the hook never swallows one. The hook runs on its own thread with nothing else on it, because Windows delays every mouse move on the whole
/// machine until the callback returns, and drops the hook silently when it is too slow: the callback only queues the press, and a worker raises
/// <see cref="Clicked"/> off that thread.
/// </summary>
internal sealed class ClickHook : IDisposable
{
    private const int WhMouseLl = 14, WmLButtonDown = 0x0201, WmRButtonDown = 0x0204;

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential)] private struct MsLlHookStruct { public int X, Y; public uint MouseData, Flags, Time; public IntPtr Extra; }
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);

    private readonly ILogger _log;
    private readonly HookProc _proc; // a field: a collected delegate under a live hook crashes the process
    private readonly BlockingCollection<ScreenClick> _queue = new(boundedCapacity: 64);
    private Thread? _thread;
    private Control? _sync;
    private volatile IntPtr _hook;

    /// <summary>The hook is installed: Windows did not refuse it. Read from any thread.</summary>
    public bool Live => _hook != IntPtr.Zero;

    /// <summary>A press, raised on a worker thread, never on the hook's.</summary>
    public event Action<ScreenClick>? Clicked;

    public ClickHook(ILogger log)
    {
        _log = log;
        _proc = OnHook;
        new Thread(Drain) { IsBackground = true, Name = "Glamour clicks" }.Start();
    }

    /// <summary>Installs or removes the hook (any thread). Off costs nothing: no hook, no callback on any mouse move.</summary>
    public void Set(bool on)
    {
        EnsureThread();
        _sync!.BeginInvoke(() =>
        {
            if (on == (_hook != IntPtr.Zero)) return;
            if (on)
            {
                _hook = SetWindowsHookEx(WhMouseLl, _proc, GetModuleHandle(null), 0);
                if (_hook == IntPtr.Zero) _log.LogWarning("Mouse hook refused: error {Error}", Marshal.GetLastWin32Error());
            }
            else
            {
                UnhookWindowsHookEx(_hook);
                _hook = IntPtr.Zero;
            }
        });
    }

    private void EnsureThread()
    {
        if (_thread is not null) return;
        using var ready = new ManualResetEventSlim();
        _thread = new Thread(() =>
        {
            _sync = new Control();
            _ = _sync.Handle;
            ready.Set();
            Application.Run(); // the hook's callbacks arrive through this thread's message loop
        }) { IsBackground = true, Name = "Glamour mouse hook" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        ready.Wait();
    }

    private IntPtr OnHook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && (wParam == WmLButtonDown || wParam == WmRButtonDown))
        {
            var m = Marshal.PtrToStructure<MsLlHookStruct>(lParam);
            _queue.TryAdd(new ScreenClick(m.X, m.Y, wParam == WmLButtonDown ? MouseButtons.Left : MouseButtons.Right, Environment.TickCount64)); // full queue: drop, never wait
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    private void Drain()
    {
        foreach (var click in _queue.GetConsumingEnumerable())
        {
            try { Clicked?.Invoke(click); }
            catch (Exception ex) { _log.LogWarning(ex, "Click handler failed"); }
        }
    }

    public void Dispose()
    {
        if (_sync is not null)
        {
            try
            {
                var done = _sync.BeginInvoke(() =>
                {
                    if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
                    _hook = IntPtr.Zero;
                    Application.ExitThread();
                });
                done.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(1));
            }
            catch (Exception) { /* the thread is already gone */ }
        }
        _queue.CompleteAdding();
    }
}
