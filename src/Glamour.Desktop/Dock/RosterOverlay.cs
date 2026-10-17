using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Glamour.Host.Panel;

namespace Glamour.Desktop.Dock;

/// <summary>
/// What the overlay draws on the client's enemy column: every portrait on screen, the one I face (-1: none), which cards have a champion (a click there
/// chooses it), whether the band runs on to the panel, whether it may move (the panel's "Reduce motion" stops it), my champion's win rate per card, and the
/// picked enemies' damage by type and my team's.
/// </summary>
internal sealed record RosterMarks(IReadOnlyList<RosterPortrait> Portraits, int Chosen, bool[] Choosable, PixelRect Client, bool Band, bool Still, MarkTag?[]? Tags = null,
    DamageShare? Damage = null, DamageShare? OurDamage = null)
{
    public string Key => FormattableString.Invariant(
        $"{Chosen}|{string.Join("", Choosable.Select(c => c ? '1' : '0'))}|{Client}|{Portraits[0].X:0.#}|{Band}|{Still}|{string.Join(",", (Tags ?? []).Select(t => t?.ToString()))}|{Damage}|{OurDamage}");
}

/// <summary>A team's damage as whole percents of physical, magic and true (adding up to 100).</summary>
internal readonly record struct DamageShare(int Physical, int Magic, int True)
{
    /// <summary>The page's "damage:P,M,T" list, or null when it is empty or not three percents adding up to 100.</summary>
    public static DamageShare? Parse(string list)
    {
        var parts = list.Split(',');
        if (parts.Length != 3 || !int.TryParse(parts[0], out var p) || !int.TryParse(parts[1], out var m) || !int.TryParse(parts[2], out var t)) return null;
        return p >= 0 && m >= 0 && t >= 0 && p + m + t == 100 ? new DamageShare(p, m, t) : null;
    }
}

/// <summary>My hovered or locked champion's win rate (percent) against the enemy on one card; <see cref="Few"/>: from few games.</summary>
internal readonly record struct MarkTag(double WinRate, bool Few)
{
    public string Text => FormattableString.Invariant($"{WinRate:0.0}%");

    /// <summary>The page's "tags:" list: one item per enemy card by cell order, "48.2", "48.2~" (few games) or empty.</summary>
    public static MarkTag?[] ParseAll(string list) =>
        [.. list.Split(',').Take(5).Select(item =>
        {
            var few = item.EndsWith('~');
            return double.TryParse(few ? item[..^1] : item, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var wr)
                && wr is >= 0 and <= 100 ? new MarkTag(wr, few) : (MarkTag?)null;
        })];
}

/// <summary>
/// Draws on the League client's own champ select cards (SPEC "Click layer on the client's roster"): a ring around the enemy I face, with a light that runs
/// round it and out along a band to the client's edge, where the panel page carries it on to the counter list; faint rings on the other enemies that can
/// be clicked; on each enemy the page has a number for, my champion's win rate against them in a small tag over the bottom of the portrait. Draw only: a layered window that every click passes through (<c>WS_EX_TRANSPARENT</c>), never activated, owned by the client so it stays
/// just above it, hides when it is minimized and goes behind other apps with it. Its bounds cover the enemy column only, never the panel.
/// The light runs one lap (<see cref="Period"/>) when the enemy I face changes, then the ring stays still: a light running all the time looked like the
/// client's own "this player is picking" glow (user). The page starts its half of the band on the same change, so the two meet.
/// The window lives on its own thread, which draws once per screen refresh (<c>DwmFlush</c>) while the light moves: a WinForms timer on the dock thread
/// (which also runs the panel's WebView2) ticked unevenly at about 30 a second and looked choppy (user, first live draft).
/// </summary>
internal sealed class RosterOverlay : IDisposable
{
    /// <summary>One turn of the light round the ring, in ms: the one lap. The panel page uses the same period (`marks.svelte.ts`).</summary>
    public const int Period = 3000;
    /// <summary>The part of the period the light takes to run from the ring to the client's edge; the page starts its half there.</summary>
    public const double BandShare = 0.05;

    private static readonly Color Teal = Color.FromArgb(45, 226, 196), Magenta = Color.FromArgb(226, 79, 166), Light = Color.FromArgb(240, 230, 210);
    // The panel's own win rate colours (lol.css --l-blue, --l-grey1) and its dark background.
    private static readonly Color Winning = Color.FromArgb(10, 200, 185), Losing = Color.FromArgb(160, 155, 140), Ink = Color.FromArgb(1, 10, 19);

    private readonly Thread _thread;
    private readonly AutoResetEvent _wake = new(false);
    private readonly object _gate = new();
    private (RosterMarks? Marks, IntPtr Owner) _pending; // from the dock thread, under _gate
    private bool _dirty;
    private volatile bool _stop;
    private LayerForm? _form; // everything below is on the overlay thread
    private RosterMarks? _marks;
    private PixelRect _bounds;
    private readonly LayerSurface _surface = new();
    private byte[]? _still; // the still part of the current marks, see Render
    private string? _stillKey;
    private Bitmap? _sprite;
    private Bitmap? _tagLayer; // the tags, drawn over the moving light
    private int _lapChosen = -1; // the enemy the last lap was for
    private IntPtr _owner;
    private long _lapStart = long.MinValue / 2; // wall clock ms
    private bool _lapDrawn; // a frame with the light is on screen

    private long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private bool Lapping => _marks is { Chosen: >= 0, Still: false } && Now - _lapStart < Period;

    /// <summary>The overlay's window, for telling a click on it from one on something else. Read from any thread.</summary>
    public IntPtr Handle { get; private set; }

    public RosterOverlay()
    {
        using var ready = new ManualResetEventSlim();
        _thread = new Thread(() => Run(ready)) { IsBackground = true, Name = "Glamour marks" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        ready.Wait(TimeSpan.FromSeconds(5));
    }

    /// <summary>Draws these marks above <paramref name="owner"/> (any thread). Returns false when there is nothing to show.</summary>
    public bool Show(RosterMarks marks, IntPtr owner)
    {
        var any = marks.Chosen >= 0 || marks.Choosable.Any(c => c);
        Set(any ? marks : null, owner);
        return any;
    }

    public void Hide() => Set(null, IntPtr.Zero);

    private void Set(RosterMarks? marks, IntPtr owner)
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
        Handle = _form.Handle;
        ready.Set();
        while (!_stop)
        {
            try
            {
                Apply();
                if (Lapping && _form.Visible)
                {
                    if (DwmFlush() != 0) Thread.Sleep(16); // waits for the next composition: one frame per refresh, evenly spaced
                    Render();
                    _lapDrawn = true;
                }
                else
                {
                    if (_lapDrawn && _form.Visible) Render(); // the lap is over: once more without the light
                    _lapDrawn = false;
                    if (_form.Visible) Win32.KeepAbove(Handle, _owner);
                    _wake.WaitOne(250);
                }
                Application.DoEvents(); // the window's own messages
            }
            catch (Exception) { Thread.Sleep(100); /* a drawing error must not end the thread */ }
        }
        _form.Dispose();
        _sprite?.Dispose();
        _tagLayer?.Dispose();
        _surface.Dispose();
    }

    /// <summary>Takes what the dock last asked for.</summary>
    private void Apply()
    {
        RosterMarks? marks;
        IntPtr owner;
        lock (_gate)
        {
            if (!_dirty) return;
            _dirty = false;
            (marks, owner) = _pending;
        }
        if (marks is null)
        {
            _marks = null;
            if (_form!.Visible) _form.Hide();
            return;
        }
        // The owner before it shows: an unowned window would show above whatever app is over League.
        if (Win32.OwnerOf(Handle) != owner) Win32.SetOwner(Handle, owner);
        _marks = marks;
        if (marks.Chosen != _lapChosen)
        {
            _lapChosen = marks.Chosen;
            if (marks.Chosen >= 0) _lapStart = Now;
        }
        _bounds = BoundsOf(marks);
        Render();
        if (!_form!.Visible) Win32.ShowAbove(_form, owner);
        if (Win32.OwnerOf(Handle) != owner) Win32.SetOwner(Handle, owner); // WinForms may reset it while it shows the window
        _owner = owner;
        Win32.KeepAbove(Handle, owner);
    }

    /// <summary>The enemy column, with room for the glow, to the client's right edge; down to the damage strips under it when there are any.</summary>
    private static PixelRect BoundsOf(RosterMarks m)
    {
        var p = m.Portraits;
        var pad = p[0].R * 1.7;
        var column = new PixelRect((int)Math.Floor(p.Min(x => x.X) - pad), (int)Math.Floor(p[0].Y - pad), m.Client.Right, (int)Math.Ceiling(p[^1].Y + pad));
        if (m.Damage is null && m.OurDamage is null) return column;
        var strip = DamageStrip(m, m.OurDamage is null ? 0 : 1);
        return new(Math.Min(column.Left, (int)Math.Floor(strip.Left)), column.Top, column.Right, Math.Max(column.Bottom, (int)Math.Ceiling(strip.Bottom) + 1));
    }

    /// <summary>
    /// Where the damage strip goes: in the empty space under the last enemy card, right edge level with the portraits' (FINALIZATION and BAN_PICK shots at
    /// 1280x720: nothing of the client's own between the fifth card and the queue name at the bottom right). Sized by the portrait, so it scales with the client.
    /// Row 0 is theirs, row 1 my team's right under it; each keeps its place whether the other shows or not.
    /// </summary>
    private static RectangleF DamageStrip(RosterMarks m, int row)
    {
        var last = m.Portraits[^1];
        var (w, h) = ((float)(last.R * 8.4), (float)(last.R * 1.25));
        return new RectangleF((float)(last.X + last.R) - w, (float)(last.Y + last.R * 1.75 + row * (h + last.R * 0.15)), w, h);
    }

    /// <summary>
    /// One frame. The still part (rings, glows, band) is drawn once per change and kept; a frame copies it back and adds only the moving light, a sprite
    /// drawn once and rotated into place. Drawing every glow each frame cost about a sixth of a core at 60 a second.
    /// </summary>
    private void Render()
    {
        if (_marks is not { } m || _bounds.Width <= 0 || _bounds.Height <= 0) return;
        _surface.Ensure(_bounds.Width, _bounds.Height);
        var bytes = _surface.Width * _surface.Height * 4;
        using (var bmp = new Bitmap(_surface.Width, _surface.Height, _surface.Width * 4, PixelFormat.Format32bppPArgb, _surface.Bits))
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TranslateTransform(-_bounds.Left, -_bounds.Top);
            if (_still is null || _stillKey != m.Key || _still.Length != bytes)
            {
                g.Clear(Color.Transparent);
                DrawStill(g, m);
                _still = new byte[bytes];
                Marshal.Copy(_surface.Bits, _still, 0, bytes);
                _stillKey = m.Key;
                _sprite?.Dispose();
                _sprite = m.Chosen >= 0 && m.Chosen < m.Portraits.Count ? LightSprite(m.Portraits[m.Chosen].R) : null;
                _tagLayer?.Dispose();
                _tagLayer = TagLayer(m, _bounds);
            }
            else Marshal.Copy(_still, 0, _surface.Bits, bytes);
            // Reduce motion keeps the light at one spot; otherwise it shows only during the lap.
            if (m.Still) DrawLight(g, m, 0.18, _sprite);
            else if (Lapping) DrawLight(g, m, (Now - _lapStart) / (double)Period, _sprite);
            if (_tagLayer is not null) g.DrawImageUnscaled(_tagLayer, _bounds.Left, _bounds.Top);
        }
        _surface.Push(Handle, _bounds);
    }

    /// <summary>Everything in screen pixels. <paramref name="phase"/> 0..1 is where the light is in its period: on the ring's right side at 0.</summary>
    internal static void Draw(Graphics g, RosterMarks m, double phase)
    {
        DrawStill(g, m);
        using var sprite = m.Chosen >= 0 && m.Chosen < m.Portraits.Count ? LightSprite(m.Portraits[m.Chosen].R) : null;
        DrawLight(g, m, phase, sprite);
        DrawTags(g, m);
    }

    /// <summary>The tags alone on a clear layer the size of the window, or null when there are none.</summary>
    private static Bitmap? TagLayer(RosterMarks m, PixelRect bounds)
    {
        if (m.Tags is null || m.Tags.All(t => t is null)) return null;
        var bmp = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TranslateTransform(-bounds.Left, -bounds.Top);
        DrawTags(g, m);
        return bmp;
    }

    /// <summary>
    /// A dark pill over the bottom of each tagged portrait with the win rate, blue when my champion wins the matchup, grey when it loses, faded on few games.
    /// The text is a filled path: a layered window's clear pixels would give ClearType text dark fringes.
    /// </summary>
    private static void DrawTags(Graphics g, RosterMarks m)
    {
        if (m.Tags is null) return;
        using var family = new FontFamily("Segoe UI");
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        foreach (var p in m.Portraits)
        {
            if (p.Index >= m.Tags.Length || m.Tags[p.Index] is not { } tag) continue;
            var (w, h) = ((float)(p.R * 1.6), (float)(p.R * 0.52));
            var box = new RectangleF((float)p.X - w / 2, (float)(p.Y + p.R * 0.98) - h / 2, w, h);
            var tone = tag.WinRate >= 50 ? Winning : Losing;
            var alpha = tag.Few ? 150 : 255;
            using var pill = Pill(box);
            using var fill = new SolidBrush(Color.FromArgb(235, Ink));
            using var edge = new Pen(Color.FromArgb(alpha * 2 / 3, tone), Math.Max(1, (float)p.R * 0.04f));
            g.FillPath(fill, pill);
            g.DrawPath(edge, pill);
            using var text = new GraphicsPath();
            text.AddString(tag.Text, family, (int)FontStyle.Bold, (float)(p.R * 0.38), box, format);
            using var ink = new SolidBrush(Color.FromArgb(alpha, tone));
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

    private static double RingRadius(double r) => r * 1.13;
    private static float LineWidth(double r) => (float)Math.Max(2, r * 0.1);

    /// <summary>What does not move: the damage strips, the faint rings, the chosen ring with its glow, the band.</summary>
    private static void DrawStill(Graphics g, RosterMarks m)
    {
        DrawDamage(g, m, m.Damage, 0, "THEIR DAMAGE");
        DrawDamage(g, m, m.OurDamage, 1, "YOUR DAMAGE");
        foreach (var p in m.Portraits)
        {
            if (p.Index == m.Chosen || p.Index >= m.Choosable.Length || !m.Choosable[p.Index]) continue;
            // Clickable: a faint ring just outside the client's own.
            var r = RingRadius(p.R);
            using var halo = new Pen(Color.FromArgb(12, Teal), (float)(p.R * 0.16));
            using var line = new Pen(Color.FromArgb(55, Teal), (float)Math.Max(1, p.R * 0.04));
            g.DrawEllipse(halo, Circle(p.X, p.Y, r));
            g.DrawEllipse(line, Circle(p.X, p.Y, r));
        }
        if (m.Chosen < 0 || m.Chosen >= m.Portraits.Count) return;
        var c = m.Portraits[m.Chosen];
        var rr = RingRadius(c.R);
        var w = LineWidth(c.R);

        // The band first, so the ring sits on top of where it leaves.
        if (m.Band && m.Client.Right > c.X + rr + 1)
        {
            var (x0, x1, y) = ((float)(c.X + rr), (float)m.Client.Right, (float)c.Y);
            foreach (var (width, alpha) in new[] { (c.R * 0.42, 22), (c.R * 0.22, 45) })
            {
                using var halo = new Pen(Color.FromArgb(alpha, Teal), (float)width);
                g.DrawLine(halo, x0, y, x1, y);
            }
            using var core = new Pen(Color.FromArgb(220, Teal), w);
            g.DrawLine(core, x0, y, x1, y);
        }

        // The ring: a soft halo, then a teal-to-magenta line.
        foreach (var (width, alpha) in new[] { (c.R * 0.7, 16), (c.R * 0.42, 30), (c.R * 0.22, 60) })
        {
            using var halo = new Pen(Color.FromArgb(alpha, Teal), (float)width);
            g.DrawEllipse(halo, Circle(c.X, c.Y, rr));
        }
        var box = Circle(c.X, c.Y, rr);
        using var brush = new LinearGradientBrush(new RectangleF(box.X - 1, box.Y - 1, box.Width + 2, box.Height + 2), Teal, Magenta, LinearGradientMode.ForwardDiagonal);
        using var ring = new Pen(brush, w);
        g.DrawEllipse(ring, box);
    }

    // The panel's damage colours (DamageBar.svelte): physical, magic, true.
    private static readonly Color Physical = Color.FromArgb(217, 138, 61), Magic = Color.FromArgb(92, 143, 230), TrueDamage = Color.FromArgb(232, 228, 216);

    /// <summary>
    /// A team's damage under the enemy cards: its <paramref name="title"/> and the percents on one line, a bar under it, on a dark plate so it reads on any splash.
    /// Text as filled paths, like the tags.
    /// </summary>
    private static void DrawDamage(Graphics g, RosterMarks m, DamageShare? damage, int row, string title)
    {
        if (damage is not { } d) return;
        var box = DamageStrip(m, row);
        var r = (float)m.Portraits[^1].R;
        using (var plate = new SolidBrush(Color.FromArgb(200, Ink))) g.FillRectangle(plate, box);
        var inset = r * 0.3f;
        var line = new RectangleF(box.X + inset, box.Y + inset * 0.6f, box.Width - inset * 2, r * 0.5f);
        using var family = new FontFamily("Segoe UI");
        var size = r * 0.36f;
        using (var label = new GraphicsPath())
        using (var grey = new SolidBrush(Losing))
        {
            label.AddString(title, family, (int)FontStyle.Bold, size, line, new StringFormat { LineAlignment = StringAlignment.Center });
            g.FillPath(grey, label);
        }
        // The percents right-aligned, each in its own colour: true last and only when there is any.
        var parts = new List<(string Text, Color Tone)> { ($"{d.Physical}% AD", Physical), ($"{d.Magic}% AP", Magic) };
        if (d.True > 0) parts.Add(($"{d.True}% true", TrueDamage));
        var right = line.Right;
        var gap = r * 0.25f;
        for (var i = parts.Count - 1; i >= 0; i--)
        {
            using var path = new GraphicsPath();
            path.AddString(parts[i].Text, family, (int)FontStyle.Bold, size, PointF.Empty, StringFormat.GenericTypographic);
            var b = path.GetBounds();
            using var move = new Matrix();
            move.Translate(right - b.Right, line.Y + (line.Height - size) / 2 + size * 0.1f);
            path.Transform(move);
            using var ink = new SolidBrush(parts[i].Tone);
            g.FillPath(ink, path);
            right -= b.Width + gap;
        }
        var bar = new RectangleF(line.X, box.Bottom - inset * 0.6f - r * 0.18f, line.Width, r * 0.18f);
        var x = bar.X;
        foreach (var (share, tone) in new[] { (d.Physical, Physical), (d.Magic, Magic), (d.True, TrueDamage) })
        {
            var w = bar.Width * share / 100f;
            if (w <= 0) continue;
            using var fill = new SolidBrush(tone);
            g.FillRectangle(fill, x, bar.Y, w, bar.Height);
            x += w;
        }
    }

    /// <summary>The light running round the ring, head on the right (angle 0) and its tail behind it, centred in a square: rotated into place each frame.</summary>
    private static Bitmap LightSprite(double r)
    {
        var rr = RingRadius(r);
        var w = LineWidth(r);
        var half = (int)Math.Ceiling(rr + w * 2 + 2);
        var bmp = new Bitmap(half * 2, half * 2, PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        var box = Circle(half, half, rr);
        // A bright head with a fading tail, clockwise: drawn tail first so the head sits on top.
        const int Steps = 40;
        const double Tail = Math.PI * 0.55;
        for (var i = Steps - 1; i >= 0; i--)
        {
            var a0 = -Tail * (i + 1) / Steps;
            var a1 = -Tail * i / Steps;
            var fade = 1 - i / (double)Steps;
            using var pen = new Pen(Color.FromArgb((int)(230 * fade * fade), Mix(Light, Teal, i / (double)Steps)), (float)(w * (1 + 0.9 * fade))) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawArc(pen, box, (float)(a0 * 180 / Math.PI), (float)((a1 - a0) * 180 / Math.PI));
        }
        return bmp;
    }

    /// <summary>What moves: the light round the ring, and in the first part of the period the light leaving along the band.</summary>
    private static void DrawLight(Graphics g, RosterMarks m, double phase, Bitmap? sprite)
    {
        if (sprite is null || m.Chosen < 0 || m.Chosen >= m.Portraits.Count) return;
        var c = m.Portraits[m.Chosen];
        var rr = RingRadius(c.R);
        if (m.Band && phase < BandShare && m.Client.Right > c.X + rr + 1)
        {
            // Crosses to the client's edge, then the panel page carries it.
            var (x0, x1, y) = ((float)(c.X + rr), (float)m.Client.Right, (float)c.Y);
            var head = x0 + (x1 - x0) * (float)(phase / BandShare);
            using var glow = new Pen(Color.FromArgb(150, Light), (float)(c.R * 0.3)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLine(glow, Math.Max(x0, head - (float)c.R * 0.6f), y, head, y);
        }
        var state = g.Save();
        g.InterpolationMode = InterpolationMode.Bilinear;
        g.TranslateTransform((float)c.X, (float)c.Y);
        g.RotateTransform((float)(phase * 360));
        g.DrawImage(sprite, -sprite.Width / 2f, -sprite.Height / 2f, sprite.Width, sprite.Height);
        g.Restore(state);
    }

    private static RectangleF Circle(double x, double y, double r) => new((float)(x - r), (float)(y - r), (float)(r * 2), (float)(r * 2));

    private static Color Mix(Color a, Color b, double t) =>
        Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

    public void Dispose()
    {
        _stop = true;
        _wake.Set();
        _thread.Join(TimeSpan.FromSeconds(1));
    }

    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
}
