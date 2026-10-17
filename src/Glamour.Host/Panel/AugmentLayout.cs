namespace Glamour.Host.Panel;

/// <summary>
/// Where the game draws a Mayhem augment offer (SPEC "PC app direction" 5, INSIGHTS "Mayhem augments (in game)"). Measured by ARAM Mayhem Tracker
/// (Copyright (c) 2026 Sygnano, MIT License, github.com/Sygnano/aram-mayhem-tracker, `mayhem-core/src/layout.rs`) at 1024x768 to 3440x1440 on 2026-09-29/30: the offer is anchored at the
/// game window's horizontal centre and scales with its height only, so every number here is in units of the window height H, horizontal ones from the centre.
/// </summary>
public static class AugmentLayout
{
    public const int Slots = 3;
    private static readonly double[] CardCenters = [-0.3346, 0.0062, 0.3470];
    private const double CardTop = 0.1793, CardWidth = 0.2949, CardHeight = 0.4871;
    // A one-line title spans 0.404-0.421H; a wrapped one grows upwards a line (0.025H) at a time, its last line staying in this band.
    private const double TitleTop = 0.398, TitleHeight = 0.029, TitleHalfWidth = 0.125, TitlePitch = 0.025;
    public const int TitleLines = 3;
    // The "hide augments" button's plate grown by 8 px at 1080p, the footprint of the bundled template; centred, top 0.7637H.
    private const double ButtonTop = 0.7637, ButtonWidth = 0.1926, ButtonHeight = 0.0593;
    // Top of the reroll button under each card: tags stay above it.
    private const double RerollTop = 0.6875;

    private static PixelRect Box(PixelRect client, double cx, double top, double width, double height)
    {
        double h = client.Height, mid = client.Left + client.Width / 2.0;
        var left = (int)Math.Floor(mid + (cx - width / 2) * h);
        var y = (int)Math.Floor(client.Top + top * h);
        return new(left, y, (int)Math.Ceiling(mid + (cx + width / 2) * h), (int)Math.Ceiling(client.Top + (top + height) * h));
    }

    public static PixelRect Card(PixelRect client, int i) => Box(client, CardCenters[i], CardTop, CardWidth, CardHeight);

    /// <summary>The title band of card <paramref name="i"/>: <paramref name="lines"/> lines tall, the bottom line being a one-line title's band.</summary>
    public static PixelRect Title(PixelRect client, int i, int lines = TitleLines) =>
        Box(client, CardCenters[i], TitleTop - (lines - 1) * TitlePitch, TitleHalfWidth * 2, TitleHeight + (lines - 1) * TitlePitch);

    /// <summary>The "hide augments" button with a 1% H search margin round it: it is on screen exactly while an augment can be picked.</summary>
    public static PixelRect ButtonSearch(PixelRect client)
    {
        var b = Box(client, 0, ButtonTop, ButtonWidth, ButtonHeight);
        var m = (int)Math.Round(client.Height * 0.01);
        return new(b.Left - m, b.Top - m, b.Right + m, b.Bottom + m);
    }

    /// <summary>The button's own size in pixels, for scaling the template.</summary>
    public static (int Width, int Height) ButtonSize(PixelRect client) =>
        ((int)Math.Round(ButtonWidth * client.Height), (int)Math.Round(ButtonHeight * client.Height));

    /// <summary>The y a card's tag sits on: inside the card, just above the reroll button.</summary>
    public static int TagBottom(PixelRect client) => (int)Math.Floor(client.Top + (RerollTop - 0.006) * client.Height);

    /// <summary>The whole offer (three cards), the area the overlay draws in.</summary>
    public static PixelRect Offer(PixelRect client)
    {
        PixelRect a = Card(client, 0), c = Card(client, Slots - 1);
        return new(a.Left, a.Top, c.Right, TagBottom(client) + 4);
    }

    /// <summary>The offer fits the window (a very narrow window would push the side cards out of it).</summary>
    public static bool Fits(PixelRect client) =>
        client.Height >= 400 && Card(client, 0).Left >= client.Left && Card(client, Slots - 1).Right <= client.Right;
}

/// <summary>An 8-bit greyscale image, row-major.</summary>
public sealed class GrayImage(int width, int height, byte[] pixels)
{
    public int Width { get; } = width;
    public int Height { get; } = height;
    public byte[] Pixels { get; } = pixels;
    public byte this[int x, int y] => Pixels[y * Width + x];

    /// <summary>Area-averaging downscale (or a plain copy at the same size); enlarging is not needed here.</summary>
    public GrayImage Resize(int w, int h)
    {
        if (w == Width && h == Height) return this;
        var px = new byte[w * h];
        for (var y = 0; y < h; y++)
        {
            int y0 = y * Height / h, y1 = Math.Max(y0 + 1, (y + 1) * Height / h);
            for (var x = 0; x < w; x++)
            {
                int x0 = x * Width / w, x1 = Math.Max(x0 + 1, (x + 1) * Width / w), sum = 0;
                for (var yy = y0; yy < y1; yy++)
                    for (var xx = x0; xx < x1; xx++) sum += Pixels[yy * Width + xx];
                px[y * w + x] = (byte)(sum / ((y1 - y0) * (x1 - x0)));
            }
        }
        return new(w, h, px);
    }

    /// <summary>The raw file of `tools/gray.ps1`: int32 width, int32 height, a byte per pixel.</summary>
    public static GrayImage Read(Stream stream)
    {
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        var all = buffer.ToArray();
        int w = BitConverter.ToInt32(all, 0), h = BitConverter.ToInt32(all, 4);
        if (w <= 0 || h <= 0 || all.Length != 8 + w * h) throw new InvalidDataException("not a grey image");
        return new(w, h, all[8..]);
    }

    public GrayImage Crop(int left, int top, int w, int h)
    {
        left = Math.Clamp(left, 0, Width); top = Math.Clamp(top, 0, Height);
        w = Math.Min(w, Width - left); h = Math.Min(h, Height - top);
        var px = new byte[Math.Max(0, w * h)];
        for (var y = 0; y < h; y++) Array.Copy(Pixels, (top + y) * Width + left, px, y * w, w);
        return new(w, h, px);
    }
}

/// <summary>Finding the "hide augments" button: greyscale normalised cross-correlation (blind to brightness and contrast) at a fixed working width.</summary>
public static class AugmentButton
{
    /// <summary>The cut between "button" and "no button" (ARAM Mayhem Tracker's, set midway between its measured 0.825-0.987 on the button and 0.237 off it).</summary>
    public const double Present = 0.60;
    // Matching width of the button: 96 px scores 0.707-0.961 on the offer screens and 0.072 on champ select in ~2 ms; the tracker's 128 px gives 0.645-0.940
    // at three times the cost, 64 px drops the 1024x768 screen to 0.605.
    private const int WorkWidth = 96;

    /// <summary>The button, cut from a 1080p screenshot by ARAM Mayhem Tracker (`assets/button/hide-augments.png`, MIT), greyed by `tools/gray.ps1`.</summary>
    public static GrayImage Template { get; } = GrayImage.Read(typeof(AugmentButton).Assembly.GetManifestResourceStream("Glamour.Host.Panel.Assets.hide-augments.gray")!);

    /// <summary>The best correlation of <paramref name="template"/> (the button, any size) inside <paramref name="region"/>, the capture of
    /// <see cref="AugmentLayout.ButtonSearch"/>, where the button is <paramref name="buttonW"/> x <paramref name="buttonH"/> pixels.</summary>
    public static double Score(GrayImage region, GrayImage template, int buttonW, int buttonH)
    {
        if (buttonW <= 0 || region.Width < buttonW || region.Height < buttonH) return 0;
        var scale = Math.Min(1.0, (double)WorkWidth / buttonW);
        var work = region.Resize(Math.Max(1, (int)Math.Round(region.Width * scale)), Math.Max(1, (int)Math.Round(region.Height * scale)));
        int tw = Math.Max(8, (int)Math.Round(buttonW * scale)), th = Math.Max(8, (int)Math.Round(buttonH * scale));
        if (work.Width < tw || work.Height < th) return 0;
        var prepared = _prepared;
        if (prepared is null || prepared.Source != template || prepared.Width != tw || prepared.Height != th)
            _prepared = prepared = new Prepared(template, tw, th, ZeroMean(template.Resize(tw, th), out var variance), variance);
        return BestNcc(work, prepared);
    }

    /// <summary>The template at the last working size, zero-mean: the size only changes with the game window's.</summary>
    private sealed record Prepared(GrayImage Source, int Width, int Height, float[] Zero, double Variance);
    private static Prepared? _prepared;

    private static float[] ZeroMean(GrayImage t, out double variance)
    {
        var mean = t.Pixels.Average(p => (double)p);
        var z = t.Pixels.Select(p => (float)(p - mean)).ToArray();
        variance = z.Sum(v => (double)v * v);
        return z;
    }

    public static double BestNcc(GrayImage img, GrayImage t) => BestNcc(img, new Prepared(t, t.Width, t.Height, ZeroMean(t, out var v), v));

    /// <summary>
    /// Best normalised correlation over every placement. The template is zero-mean, so the cross term needs no image mean; the image's sum and sum of
    /// squares under each placement come from summed-area tables, so each placement costs one multiply-add per template pixel.
    /// </summary>
    private static double BestNcc(GrayImage img, Prepared t)
    {
        if (t.Variance <= 0) return 0;
        int w = img.Width, h = img.Height, n = t.Width * t.Height, sw = w + 1;
        var sum = new long[sw * (h + 1)];
        var sq = new long[sw * (h + 1)];
        for (var y = 0; y < h; y++)
        {
            long row = 0, rowSq = 0;
            for (var x = 0; x < w; x++)
            {
                long v = img.Pixels[y * w + x];
                row += v; rowSq += v * v;
                sum[(y + 1) * sw + x + 1] = sum[y * sw + x + 1] + row;
                sq[(y + 1) * sw + x + 1] = sq[y * sw + x + 1] + rowSq;
            }
        }
        long Box(long[] s, int x, int y) => s[(y + t.Height) * sw + x + t.Width] - s[y * sw + x + t.Width] - s[(y + t.Height) * sw + x] + s[y * sw + x];
        var best = 0.0;
        var z = t.Zero;
        for (var oy = 0; oy + t.Height <= h; oy++)
            for (var ox = 0; ox + t.Width <= w; ox++)
            {
                double s1 = Box(sum, ox, oy), iVar = Box(sq, ox, oy) - s1 * s1 / n;
                if (iVar <= 1e-6) continue;
                float cross = 0;
                for (var y = 0; y < t.Height; y++)
                {
                    int row = (oy + y) * w + ox, trow = y * t.Width;
                    for (var x = 0; x < t.Width; x++) cross += img.Pixels[row + x] * z[trow + x];
                }
                var score = cross / Math.Sqrt(iVar * t.Variance);
                if (score > best) best = score;
            }
        return best;
    }

    /// <summary>A coarse 16x4 thumbnail of a title band: the titles count as changed (a reroll, the cards hidden, the shop over them) when any
    /// cell moves more than <see cref="Changed"/> grey levels on average.</summary>
    public static byte[] Fingerprint(GrayImage band) => band.Resize(16, 4).Pixels;

    public const double Changed = 4.0;

    public static bool Differs(byte[]? a, byte[] b) =>
        a is null || a.Length != b.Length || a.Zip(b, (x, y) => (double)Math.Abs(x - y)).Average() > Changed;
}
