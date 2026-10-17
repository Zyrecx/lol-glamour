using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Glamour.Host.Panel;
using Microsoft.Extensions.Logging;

namespace Glamour.Desktop.Dock;

/// <summary>
/// The Accept ring (SPEC "Click layer"): while auto-accept waits (last second or a delay), a line round the client's own Accept button that fills from the
/// top's middle down both sides and meets at the bottom when Glamour accepts. Nothing on an instant accept (nothing waits). Draw only, like
/// <see cref="RosterOverlay"/>: every click passes through to the button. It asks <c>wait</c> each frame, so it goes the moment the accept is sent,
/// answered in the client or cancelled from the phone.
/// </summary>
internal sealed class AcceptOverlay : IDisposable
{
    /// <summary>How far outside the button's gold frame the line runs, in base px (1280x720).</summary>
    public const double Offset = 4;

    private static readonly Color Teal = Color.FromArgb(45, 226, 196), Magenta = Color.FromArgb(226, 79, 166), Light = Color.FromArgb(240, 230, 210);

    private readonly Func<(DateTimeOffset From, DateTimeOffset At)?> _wait;
    private readonly ILogger _log;
    private readonly Thread _thread;
    private readonly AutoResetEvent _wake = new(false);
    private readonly object _gate = new();
    private (IReadOnlyList<(double X, double Y)>? Outline, IntPtr Owner) _pending; // from the dock thread, under _gate
    private bool _dirty;
    private volatile bool _stop;
    private LayerForm? _form; // everything below is on the overlay thread
    private readonly LayerSurface _surface = new();
    private Ring? _ring;
    private IntPtr _owner;
    private byte[]? _still;
    private long _shownAt, _zChecked; // ticks

    public IntPtr Handle { get; private set; }

    public AcceptOverlay(Func<(DateTimeOffset From, DateTimeOffset At)?> wait, ILogger log)
    {
        _wait = wait;
        _log = log;
        using var ready = new ManualResetEventSlim();
        _thread = new Thread(() => Run(ready)) { IsBackground = true, Name = "Glamour accept ring" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        ready.Wait(TimeSpan.FromSeconds(5));
    }

    /// <summary>Where the button is on screen (<see cref="AcceptLayout.Outline"/>), or null to stand down. Any thread.</summary>
    public void Set(IReadOnlyList<(double X, double Y)>? outline, IntPtr owner)
    {
        lock (_gate)
        {
            if (Same(_pending.Outline, outline) && _pending.Owner == owner) return;
            _pending = (outline, owner);
            _dirty = true;
        }
        _wake.Set();
    }

    private static bool Same(IReadOnlyList<(double X, double Y)>? a, IReadOnlyList<(double X, double Y)>? b) =>
        a is null ? b is null : b is not null && a.SequenceEqual(b);

    private void Run(ManualResetEventSlim ready)
    {
        _form = new LayerForm();
        Handle = _form.Handle;
        ready.Set();
        while (!_stop)
        {
            try
            {
                Apply();
                var wait = _ring is null ? null : _wait();
                if (wait is { } w)
                {
                    if (!_form.Visible)
                    {
                        Render(w);
                        Win32.ShowAbove(_form, _owner);
                        _zChecked = Environment.TickCount64;
                        _shownAt = Environment.TickCount64;
                        _log.LogInformation("Accept ring shown at {Bounds}; client topmost {Topmost}, ring above the client {Above}, owner set {Owned}",
                            _ring!.Bounds, Win32.IsTopmost(_owner), Win32.IsAbove(Handle, _owner), Win32.OwnerOf(Handle) == _owner);
                    }
                    else
                    {
                        if (DwmFlush() != 0) Thread.Sleep(16);
                        if (Environment.TickCount64 - _zChecked > 250) { _zChecked = Environment.TickCount64; Win32.KeepAbove(Handle, _owner); } // the client may go always-on-top at match found
                        Render(w);
                    }
                }
                else
                {
                    if (_form.Visible)
                    {
                        _form.Hide();
                        _log.LogInformation("Accept ring hidden after {Ms} ms", Environment.TickCount64 - _shownAt);
                    }
                    _wake.WaitOne(_ring is null ? 500 : 100); // the wait starts with the ready check: look again soon
                }
                Application.DoEvents();
            }
            catch (Exception) { Thread.Sleep(100); /* a drawing error must not end the thread */ }
        }
        _form.Dispose();
        _surface.Dispose();
    }

    private void Apply()
    {
        IReadOnlyList<(double X, double Y)>? outline;
        lock (_gate)
        {
            if (!_dirty) return;
            _dirty = false;
            (outline, _owner) = _pending;
        }
        _ring = outline is null ? null : new Ring(outline);
        _still = null;
        if (_ring is null && _form!.Visible) _form.Hide();
    }

    private void Render((DateTimeOffset From, DateTimeOffset At) wait)
    {
        if (_ring is not { } ring) return;
        var b = ring.Bounds;
        _surface.Ensure(b.Width, b.Height);
        var bytes = b.Width * b.Height * 4;
        using (var bmp = new Bitmap(b.Width, b.Height, b.Width * 4, PixelFormat.Format32bppPArgb, _surface.Bits))
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TranslateTransform(-b.Left, -b.Top);
            if (_still is null || _still.Length != bytes)
            {
                g.Clear(Color.Transparent);
                DrawStill(g, ring);
                _still = new byte[bytes];
                Marshal.Copy(_surface.Bits, _still, 0, bytes);
            }
            else Marshal.Copy(_still, 0, _surface.Bits, bytes);
            var total = (wait.At - wait.From).TotalMilliseconds;
            var done = total <= 0 ? 1 : Math.Clamp((DateTimeOffset.UtcNow - wait.From).TotalMilliseconds / total, 0, 1);
            DrawFill(g, ring, done);
        }
        _surface.Push(Handle, b);
    }

    /// <summary>Everything in screen pixels: the whole ring with <paramref name="done"/> (0..1) of the wait gone. For checks on a shot.</summary>
    internal static void Draw(Graphics g, IReadOnlyList<(double X, double Y)> outline, double done)
    {
        var ring = new Ring(outline);
        DrawStill(g, ring);
        DrawFill(g, ring, done);
    }

    /// <summary>The faint whole ring: a soft halo and a thin line.</summary>
    private static void DrawStill(Graphics g, Ring r)
    {
        foreach (var (width, alpha) in new[] { (r.Scale * 9, 14), (r.Scale * 5, 28) })
        {
            using var halo = new Pen(Color.FromArgb(alpha, Teal), (float)width) { LineJoin = LineJoin.Round };
            g.DrawPolygon(halo, r.Points);
        }
        using var line = new Pen(Color.FromArgb(70, Teal), (float)Math.Max(1, r.Scale * 1.2)) { LineJoin = LineJoin.Round };
        g.DrawPolygon(line, r.Points);
    }

    /// <summary>The filled part: from the top's middle down both sides, teal at the top turning magenta as it nears the bottom, with a bright head on each end.</summary>
    private static void DrawFill(Graphics g, Ring r, double done)
    {
        if (done <= 0) return;
        var half = r.Length / 2 * done;
        var tone = Mix(Teal, Magenta, done);
        var right = r.Walk(0, half);
        var left = r.Walk(r.Length, r.Length - half);
        foreach (var path in new[] { right, left })
        {
            if (path.Length < 2) continue;
            using var glow = new Pen(Color.FromArgb(70, tone), (float)(r.Scale * 5)) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
            using var core = new Pen(Color.FromArgb(235, tone), (float)Math.Max(2, r.Scale * 2.2)) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLines(glow, path);
            g.DrawLines(core, path);
            var head = path[^1];
            var d = (float)(r.Scale * 5);
            using var light = new SolidBrush(Color.FromArgb(220, Light));
            using var halo = new SolidBrush(Color.FromArgb(60, Light));
            g.FillEllipse(halo, head.X - d, head.Y - d, d * 2, d * 2);
            g.FillEllipse(light, head.X - d / 2.5f, head.Y - d / 2.5f, d / 1.25f, d / 1.25f);
        }
    }

    private static Color Mix(Color a, Color b, double t) =>
        Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

    /// <summary>The outline with its length measured, for walking along it.</summary>
    private sealed class Ring
    {
        public readonly PointF[] Points;
        private readonly double[] _at; // distance along the outline to each point
        public double Length => _at[^1];
        public readonly double Scale; // screen px per base px
        public readonly PixelRect Bounds;

        public Ring(IReadOnlyList<(double X, double Y)> outline)
        {
            Points = [.. outline.Select(p => new PointF((float)p.X, (float)p.Y))];
            _at = new double[Points.Length];
            for (var i = 1; i < Points.Length; i++)
                _at[i] = _at[i - 1] + Math.Sqrt(Math.Pow(Points[i].X - Points[i - 1].X, 2) + Math.Pow(Points[i].Y - Points[i - 1].Y, 2));
            // The outline's width is about 230 base px at any offset used here; the pad leaves room for the glow.
            Scale = (outline.Max(p => p.X) - outline.Min(p => p.X)) / 230;
            var pad = Scale * 10;
            Bounds = new((int)Math.Floor(outline.Min(p => p.X) - pad), (int)Math.Floor(outline.Min(p => p.Y) - pad),
                (int)Math.Ceiling(outline.Max(p => p.X) + pad), (int)Math.Ceiling(outline.Max(p => p.Y) + pad));
        }

        /// <summary>The points from distance <paramref name="from"/> to <paramref name="to"/> along the outline (either direction), ends included.</summary>
        public PointF[] Walk(double from, double to)
        {
            var list = new List<PointF> { PointAt(from) };
            if (to > from) { for (var i = 0; i < Points.Length; i++) if (_at[i] > from && _at[i] < to) list.Add(Points[i]); }
            else { for (var i = Points.Length - 1; i >= 0; i--) if (_at[i] < from && _at[i] > to) list.Add(Points[i]); }
            list.Add(PointAt(to));
            return [.. list];
        }

        private PointF PointAt(double d)
        {
            d = Math.Clamp(d, 0, Length);
            var i = Array.FindIndex(_at, a => a >= d);
            if (i <= 0) return Points[0];
            var t = (float)((d - _at[i - 1]) / Math.Max(1e-9, _at[i] - _at[i - 1]));
            return new(Points[i - 1].X + (Points[i].X - Points[i - 1].X) * t, Points[i - 1].Y + (Points[i].Y - Points[i - 1].Y) * t);
        }
    }

    public void Dispose()
    {
        _stop = true;
        _wake.Set();
        _thread.Join(TimeSpan.FromSeconds(1));
    }

    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
}
