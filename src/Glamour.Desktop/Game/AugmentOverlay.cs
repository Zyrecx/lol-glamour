using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Glamour.Core;
using Glamour.Desktop.Dock;
using Glamour.Host.Panel;
using Microsoft.Extensions.Logging;

namespace Glamour.Desktop.Game;

/// <summary>What the augment overlay draws: the game window's client area, each card's pick (null: not read) and the best card (-1: none).</summary>
internal sealed record AugmentMarks(PixelRect Client, AugmentPick?[] Picks, int Best)
{
    public string Key => $"{Client}|{Best}|{string.Join(",", Picks.Select(p => p is null ? "-" : $"{p.Id}:{p.DeltaPoints}:{p.Source}:{p.LowSample}"))}";
}

/// <summary>
/// Augment tags on the game's own offer (SPEC "PC app direction" 5): a pill on each card above its reroll button with the win-rate difference on my
/// champion, a teal frame round the best card. Draw only, like <see cref="BenchOverlay"/>: a click-through layered window owned by the game window, and
/// hidden (not merely empty) whenever there is nothing to draw, so a borderless game keeps its full-screen fast path the rest of the time.
/// </summary>
internal sealed class AugmentOverlay : IDisposable
{
    private static readonly Color Good = Color.FromArgb(10, 200, 185), Plain = Color.FromArgb(160, 155, 140), Bad = Color.FromArgb(225, 95, 80), Ink = Color.FromArgb(1, 10, 19);

    private readonly Thread _thread;
    private readonly AutoResetEvent _wake = new(false);
    private readonly object _gate = new();
    private (AugmentMarks? Marks, IntPtr Owner) _pending;
    private bool _dirty;
    private volatile bool _stop;
    private LayerForm? _form;
    private readonly LayerSurface _surface = new();
    private readonly ILogger _log;
    private IntPtr _owner;
    private bool _zLogged;

    public AugmentOverlay(ILogger log)
    {
        _log = log;
        using var ready = new ManualResetEventSlim();
        _thread = new Thread(() => Run(ready)) { IsBackground = true, Name = "Glamour augment tags" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        ready.Wait(TimeSpan.FromSeconds(5));
    }

    public void Show(AugmentMarks marks, IntPtr game) => Set(marks, game);

    public void Hide() => Set(null, IntPtr.Zero);

    private void Set(AugmentMarks? marks, IntPtr owner)
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
        AugmentMarks? marks;
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

    /// <summary>The first offer's tags stayed under the game until an alt-tab (user, live): see <see cref="Win32.KeepAbove"/>.</summary>
    private void FollowOwnerZ()
    {
        var handle = _form!.Handle;
        if (!_zLogged)
        {
            _zLogged = true;
            _log.LogInformation("Augment tags shown: game topmost {Topmost}, tags topmost {Mine}, tags above the game {Above}", Win32.IsTopmost(_owner), Win32.IsTopmost(handle), Win32.IsAbove(handle, _owner));
        }
        Win32.KeepAbove(handle, _owner);
    }

    private static PixelRect BoundsOf(PixelRect client)
    {
        var offer = AugmentLayout.Offer(client);
        var pad = (int)Math.Ceiling(client.Height * 0.012);
        return new(offer.Left - pad, offer.Top - pad, offer.Right + pad, offer.Bottom + pad);
    }

    /// <summary>The tag's colour: teal for a clear gain, red for a clear loss, grey in between or when its number is thin or every champion's.</summary>
    private static Color Tone(AugmentPick p) =>
        p.LowSample || p.Source is AugmentSource.Global or AugmentSource.None ? Plain : p.Grade switch { "S" or "A" => Good, "C" or "D" => Bad, _ => Plain };

    /// <summary>"+3.2", "all +1.0" for an every-champion number, "+9.0?" when under 200 games.</summary>
    internal static string Text(AugmentPick p) =>
        (p.Source == AugmentSource.Global ? "all " : "") + AugmentNames.Points(p.DeltaPoints) + (p.LowSample ? "?" : "");

    /// <summary>In screen pixels; the text is a filled path (a layered window's clear pixels would give ClearType text dark fringes).</summary>
    internal static void Draw(Graphics g, AugmentMarks m)
    {
        var s = m.Client.Height / 1080f;
        using var family = new FontFamily("Segoe UI");
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        var bottom = AugmentLayout.TagBottom(m.Client);
        for (var i = 0; i < m.Picks.Length && i < AugmentLayout.Slots; i++)
        {
            if (m.Picks[i] is not { } pick) continue;
            var card = AugmentLayout.Card(m.Client, i);
            var tone = Tone(pick);
            if (i == m.Best)
            {
                using var frame = new Pen(Good, 4 * s);
                using var outline = Rounded(new RectangleF(card.Left - 6 * s, card.Top - 6 * s, card.Width + 12 * s, card.Height + 12 * s), 18 * s);
                g.DrawPath(frame, outline);
            }
            var (w, h) = (128 * s, 34 * s);
            var box = new RectangleF(card.Left + (card.Width - w) / 2, bottom - h, w, h);
            using var pill = Rounded(box, h / 2);
            using var fill = new SolidBrush(Color.FromArgb(235, Ink));
            using var edge = new Pen(Color.FromArgb(200, tone), 2 * s);
            g.FillPath(fill, pill);
            g.DrawPath(edge, pill);
            using var text = new GraphicsPath();
            text.AddString(Text(pick), family, (int)FontStyle.Bold, 20 * s, box, format);
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
