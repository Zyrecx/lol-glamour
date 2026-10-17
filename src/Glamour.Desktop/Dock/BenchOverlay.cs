using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Glamour.Host.Panel;

namespace Glamour.Desktop.Dock;

/// <summary>What the bench overlay draws: each slot of the client's bench strip and its tag (null: none), by slot.</summary>
internal sealed record BenchMarks(IReadOnlyList<BenchSlot> Slots, BenchTag?[] Tags)
{
    public string Key => FormattableString.Invariant($"{Slots[0].X:0.#},{Slots[0].Y:0.#},{Slots[0].Size:0.#}|{string.Join(",", Tags.Select(t => t?.ToString()))}");
}

/// <summary>
/// Bench advice on the client's own bench strip (SPEC "PC app direction" 5): each bench champion's win rate or tier in a small pill over the top edge of
/// its slot (the bottom edge carries the client's padlock on a just-benched champion), and a teal frame round the ones clearly better than mine.
/// Draw only and still, like <see cref="RosterOverlay"/> without the light: a click-through layered window owned by the client, over the strip alone.
/// </summary>
internal sealed class BenchOverlay : IDisposable
{
    // The panel's colours (lol.css --l-blue, --l-grey1) and its dark background, as the roster tags use.
    private static readonly Color Better = Color.FromArgb(10, 200, 185), Plain = Color.FromArgb(160, 155, 140), Ink = Color.FromArgb(1, 10, 19);

    private readonly Thread _thread;
    private readonly AutoResetEvent _wake = new(false);
    private readonly object _gate = new();
    private (BenchMarks? Marks, IntPtr Owner) _pending; // from the dock thread, under _gate
    private bool _dirty;
    private volatile bool _stop;
    private LayerForm? _form; // everything below is on the overlay thread
    private IntPtr _owner;
    private readonly LayerSurface _surface = new();

    public BenchOverlay()
    {
        using var ready = new ManualResetEventSlim();
        _thread = new Thread(() => Run(ready)) { IsBackground = true, Name = "Glamour bench marks" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        ready.Wait(TimeSpan.FromSeconds(5));
    }

    /// <summary>Draws these tags above <paramref name="owner"/> (any thread); nothing when no slot has one.</summary>
    public void Show(BenchMarks marks, IntPtr owner) => Set(marks.Tags.Any(t => t is not null) ? marks : null, owner);

    public void Hide() => Set(null, IntPtr.Zero);

    private void Set(BenchMarks? marks, IntPtr owner)
    {
        lock (_gate)
        {
            if (_pending.Marks?.Key == marks?.Key && (marks is null || _pending.Owner == owner)) return;
            _pending = (marks, owner);
            _dirty = true;
        }
        _wake.Set();
    }

    private void Run(ManualResetEventSlim ready)
    {
        _form = new LayerForm();
        _ = _form.Handle;
        ready.Set();
        while (!_stop)
        {
            try
            {
                Apply();
                if (_form.Visible) Win32.KeepAbove(_form.Handle, _owner);
                _wake.WaitOne(250);
                Application.DoEvents(); // the window's own messages
            }
            catch (Exception) { Thread.Sleep(100); /* a drawing error must not end the thread */ }
        }
        _form.Dispose();
        _surface.Dispose();
    }

    private void Apply()
    {
        BenchMarks? marks;
        IntPtr owner;
        lock (_gate)
        {
            if (!_dirty) return;
            _dirty = false;
            (marks, owner) = _pending;
        }
        if (marks is null)
        {
            if (_form!.Visible) _form.Hide();
            return;
        }
        var handle = _form!.Handle;
        // The owner before it shows: an unowned window would show above whatever app is over League.
        if (Win32.OwnerOf(handle) != owner) Win32.SetOwner(handle, owner);
        var bounds = BoundsOf(marks);
        _surface.Ensure(bounds.Width, bounds.Height);
        using (var bmp = new Bitmap(_surface.Width, _surface.Height, _surface.Width * 4, PixelFormat.Format32bppPArgb, _surface.Bits))
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);
            g.TranslateTransform(-bounds.Left, -bounds.Top);
            Draw(g, marks);
        }
        _surface.Push(handle, bounds);
        if (!_form.Visible) Win32.ShowAbove(_form, owner);
        if (Win32.OwnerOf(handle) != owner) Win32.SetOwner(handle, owner); // WinForms may reset it while it shows the window
        _owner = owner;
        Win32.KeepAbove(handle, owner);
    }

    /// <summary>The strip with room for the frames and the pills, which stand out over the slots' top edge.</summary>
    private static PixelRect BoundsOf(BenchMarks m)
    {
        var s = m.Slots;
        var pad = s[0].Size * 0.2;
        return new((int)Math.Floor(s[0].X - pad), (int)Math.Floor(s[0].Y - pad), (int)Math.Ceiling(s[^1].X + s[^1].Size + pad), (int)Math.Ceiling(s[0].Y + s[0].Size + pad));
    }

    /// <summary>Everything in screen pixels. The text is a filled path: a layered window's clear pixels would give ClearType text dark fringes.</summary>
    internal static void Draw(Graphics g, BenchMarks m)
    {
        using var family = new FontFamily("Segoe UI");
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        foreach (var slot in m.Slots)
        {
            if (slot.Index >= m.Tags.Length || m.Tags[slot.Index] is not { } tag) continue;
            var side = (float)slot.Size;
            var tone = tag.Better ? Better : Plain;
            if (tag.Better)
            {
                using var frame = new Pen(tone, Math.Max(2, side * 0.04f));
                var o = side * 0.04f;
                g.DrawRectangle(frame, (float)slot.X - o, (float)slot.Y - o, side + 2 * o, side + 2 * o);
            }
            // 44x15 base px, from 7 px above the slot's top edge (preview on the live shots: the padlock and most of the face stay clear).
            var (w, h) = (side * 0.88f, side * 0.3f);
            var box = new RectangleF((float)slot.X + (side - w) / 2, (float)slot.Y - side * 0.14f, w, h);
            using var pill = Pill(box);
            using var fill = new SolidBrush(Color.FromArgb(235, Ink));
            using var edge = new Pen(Color.FromArgb(170, tone), Math.Max(1, side * 0.02f));
            g.FillPath(fill, pill);
            g.DrawPath(edge, pill);
            using var text = new GraphicsPath();
            text.AddString(tag.Text, family, (int)FontStyle.Bold, side * 0.22f, box, format);
            using var ink = new SolidBrush(tone);
            g.FillPath(ink, text);
        }
    }

    private static GraphicsPath Pill(RectangleF r)
    {
        var path = new GraphicsPath();
        var d = r.Height;
        path.AddArc(r.X, r.Y, d, d, 90, 180);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 180);
        path.CloseFigure();
        return path;
    }

    public void Dispose()
    {
        _stop = true;
        _wake.Set();
        _thread.Join(TimeSpan.FromSeconds(1));
    }
}
