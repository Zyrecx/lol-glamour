using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Glamour.Core;
using Glamour.Desktop.Dock;
using Glamour.Host.Panel;
using Microsoft.Extensions.Logging;

namespace Glamour.Desktop.Game;

/// <summary>What the loading-screen overlay draws: the game window's client area and the party members' cards.</summary>
internal sealed record LoadingMarks(PixelRect Client, IReadOnlyList<LoadingCard> Cards)
{
    public string Key => $"{Client}|{string.Join(",", Cards.Select(c => $"{c.Top}:{c.Slot}:{c.Party}:{c.Size}"))}";
}

/// <summary>
/// Party marks on the game's loading screen (SPEC "Parties on the loading screen"): a frame in the party's colour round each member's card and a
/// "Duo" / "3-stack" pill at its top left. Draw only, like <see cref="AugmentOverlay"/>: a click-through layered window owned by the game window,
/// hidden whenever there is nothing to draw.
/// </summary>
internal sealed class LoadingOverlay : IDisposable
{
    /// <summary>One colour per party, mine (listed first) teal; never two alike in one game (at most two parties a team).</summary>
    private static readonly Color[] Colors = [Color.FromArgb(10, 200, 185), Color.FromArgb(240, 170, 60), Color.FromArgb(175, 125, 255), Color.FromArgb(240, 100, 160)];
    private static readonly Color Ink = Color.FromArgb(1, 10, 19);

    private readonly Thread _thread;
    private readonly AutoResetEvent _wake = new(false);
    private readonly object _gate = new();
    private (LoadingMarks? Marks, IntPtr Owner) _pending;
    private bool _dirty;
    private volatile bool _stop;
    private LayerForm? _form;
    private readonly LayerSurface _surface = new();
    private readonly ILogger _log;
    private IntPtr _owner;
    private bool _zLogged;

    public LoadingOverlay(ILogger log)
    {
        _log = log;
        using var ready = new ManualResetEventSlim();
        _thread = new Thread(() => Run(ready)) { IsBackground = true, Name = "Glamour loading marks" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        ready.Wait(TimeSpan.FromSeconds(5));
    }

    public void Show(LoadingMarks marks, IntPtr game) => Set(marks, game);

    public void Hide() => Set(null, IntPtr.Zero);

    private void Set(LoadingMarks? marks, IntPtr owner)
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
                if (_form.Visible) FollowOwnerZ();
                _wake.WaitOne(250);
                Application.DoEvents();
            }
            catch (Exception) { Thread.Sleep(100); }
        }
        _form.Dispose();
        _surface.Dispose();
    }

    private void Apply()
    {
        LoadingMarks? marks;
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
            _zLogged = false;
            return;
        }
        _owner = owner;
        var handle = _form!.Handle;
        if (Win32.OwnerOf(handle) != owner) Win32.SetOwner(handle, owner); // owned by the game: above it, never above an app in front of it
        var bounds = BoundsOf(marks.Client);
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
        if (Win32.OwnerOf(handle) != owner) Win32.SetOwner(handle, owner);
    }

    /// <summary>Shown without activation, a layered window can stay under the game (see <see cref="Win32.KeepAbove"/>).</summary>
    private void FollowOwnerZ()
    {
        var handle = _form!.Handle;
        if (!_zLogged)
        {
            _zLogged = true;
            _log.LogInformation("Loading parties shown: game topmost {Topmost}, marks above the game {Above}", Win32.IsTopmost(_owner), Win32.IsAbove(handle, _owner));
        }
        Win32.KeepAbove(handle, _owner);
    }

    /// <summary>Both card rows with room for the frames.</summary>
    private static PixelRect BoundsOf(PixelRect client)
    {
        PixelRect a = LoadingLayout.Card(client, true, 0), b = LoadingLayout.Card(client, false, LoadingCards.PerRow - 1);
        var pad = (int)Math.Ceiling(client.Height * 0.01);
        return new(a.Left - pad, a.Top - pad, b.Right + pad, b.Bottom + pad);
    }

    /// <summary>In screen pixels; the text is a filled path (a layered window's clear pixels would give ClearType text dark fringes).</summary>
    internal static void Draw(Graphics g, LoadingMarks m)
    {
        var s = m.Client.Height / 1080f;
        using var family = new FontFamily("Segoe UI");
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        foreach (var c in m.Cards)
        {
            var card = LoadingLayout.Card(m.Client, c.Top, c.Slot);
            var tone = Colors[c.Party % Colors.Length];
            using (var frame = new Pen(tone, 4 * s))
            using (var outline = Rounded(new RectangleF(card.Left - 3 * s, card.Top - 3 * s, card.Width + 6 * s, card.Height + 6 * s), 6 * s))
                g.DrawPath(frame, outline);
            var label = LoadingCards.Label(c.Size);
            var (w, h) = ((label.Length * 11 + 26) * s, 30 * s);
            var box = new RectangleF(card.Left + 10 * s, card.Top + 12 * s, w, h);
            using var pill = Rounded(box, h / 2);
            using var fill = new SolidBrush(Color.FromArgb(235, Ink));
            using var edge = new Pen(tone, 2 * s);
            g.FillPath(fill, pill);
            g.DrawPath(edge, pill);
            using var text = new GraphicsPath();
            text.AddString(label, family, (int)FontStyle.Bold, 17 * s, box, format);
            using var ink = new SolidBrush(tone);
            g.FillPath(ink, text);
        }
    }

    private static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
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
