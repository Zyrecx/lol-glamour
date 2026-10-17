using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Glamour.Desktop;

/// <summary>
/// The Blossom mark (web/public/favicon.svg: five petals under one teal-violet-magenta gradient, gold core, dark tile) drawn at any size:
/// the tray and window icons at run time, and with <c>--write-icons</c> the exe's <c>glamour.ico</c> and the web app's PNGs.
/// </summary>
internal static class BlossomIcon
{
    private static readonly Color Back = Color.FromArgb(6, 10, 18);
    private static readonly Color Core = Color.FromArgb(200, 170, 110);

    public static Icon Create(int size)
    {
        using var bmp = Render(size, Tile.Rounded, .9f);
        var handle = bmp.GetHicon();
        try
        {
            using var borrowed = Icon.FromHandle(handle); // does not own the handle; the clone gets its own
            return (Icon)borrowed.Clone();
        }
        finally { DestroyIcon(handle); }
    }

    public enum Tile { Rounded, Square }

    /// <summary>The mark on its tile, the flower at <paramref name="flower"/> of the tile's size (as the SVG's scale).</summary>
    public static Bitmap Render(int size, Tile tile, float flower)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.ScaleTransform(size / 100f, size / 100f);
        using (var back = new SolidBrush(Back))
        {
            if (tile == Tile.Square) g.FillRectangle(back, 0, 0, 100, 100);
            else using (var bg = RoundedRect(0, 0, 100, 100, 22)) g.FillPath(back, bg);
        }
        g.TranslateTransform(50 - 50 * flower, 50 - 50 * flower);
        g.ScaleTransform(flower, flower);
        g.TranslateTransform(0, Drop);

        // One gradient over the whole flower (not one per petal), along (22,14) -> (78,86) like the SVG; the line is drawn 10% longer each
        // way with the end colours held, since GDI+ repeats a gradient past its ends where SVG holds them.
        using var petals = new GraphicsPath();
        for (var i = 0; i < 5; i++)
        {
            using var petal = new GraphicsPath();
            petal.AddEllipse(50 - 13, 27 - 22, 26, 44);
            using var turn = new Matrix();
            turn.RotateAt(i * 72, new PointF(50, 50));
            petal.Transform(turn);
            petals.AddPath(petal, false);
        }
        petals.FillMode = FillMode.Winding;
        var from = new PointF(22 - 5.6f, 14 - 7.2f);
        var to = new PointF(78 + 5.6f, 86 + 7.2f);
        using var paint = new LinearGradientBrush(from, to, Color.Black, Color.Black)
        {
            InterpolationColors = new ColorBlend
            {
                Colors = [Teal, Teal, Violet, Magenta, Magenta],
                Positions = [0f, 1 / 12f, .5f, 11 / 12f, 1f],
            },
        };
        g.FillPath(paint, petals);
        using var core = new SolidBrush(Core);
        g.FillEllipse(core, 50 - 9.5f, 50 - 9.5f, 19, 19);
        return bmp;
    }

    /// <summary>The petals reach from y 5 to 88 around a core at 50: lowered by 3.5 the flower sits centred in its tile (the SVG's translate(0 3.5)).</summary>
    private const float Drop = 3.5f;

    private static readonly Color Teal = Color.FromArgb(45, 226, 196);
    private static readonly Color Violet = Color.FromArgb(140, 123, 242);
    private static readonly Color Magenta = Color.FromArgb(226, 79, 166);

    /// <summary><c>Glamour.exe --write-icons &lt;dir&gt;</c>: glamour.ico (16 to 256 px) and the web app's icons, all from <see cref="Render"/>.</summary>
    public static void Write(string dir)
    {
        Directory.CreateDirectory(dir);
        void Png(string name, int size, Tile tile, float flower)
        {
            using var bmp = Render(size, tile, flower);
            bmp.Save(Path.Combine(dir, name), ImageFormat.Png);
        }
        Png("icon-192.png", 192, Tile.Rounded, .9f);
        Png("icon-512.png", 512, Tile.Rounded, .9f);
        Png("apple-touch-icon.png", 180, Tile.Square, .8f); // iOS rounds the corners itself
        Png("icon-maskable-512.png", 512, Tile.Square, .62f); // inside the 80% safe circle Android may crop to

        // An .ico: 32-bit bitmap entries below 256 px (every reader takes those; old ones show PNG entries as noise), PNG at 256.
        int[] sizes = [16, 20, 24, 32, 40, 48, 64, 256];
        var images = sizes.Select(s =>
        {
            using var bmp = Render(s, Tile.Rounded, .9f);
            using var ms = new MemoryStream();
            if (s >= 256) bmp.Save(ms, ImageFormat.Png);
            else WriteDib(ms, bmp);
            return ms.ToArray();
        }).ToArray();
        using var ico = new BinaryWriter(File.Create(Path.Combine(dir, "glamour.ico")));
        ico.Write((short)0);
        ico.Write((short)1);
        ico.Write((short)sizes.Length);
        var offset = 6 + 16 * sizes.Length;
        for (var i = 0; i < sizes.Length; i++)
        {
            ico.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
            ico.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
            ico.Write((byte)0);
            ico.Write((byte)0);
            ico.Write((short)1);
            ico.Write((short)32);
            ico.Write(images[i].Length);
            ico.Write(offset);
            offset += images[i].Length;
        }
        foreach (var image in images) ico.Write(image);
    }

    /// <summary>An icon entry's bitmap: BITMAPINFOHEADER (height doubled for the mask), BGRA rows bottom-up, then an all-clear AND mask.</summary>
    private static void WriteDib(Stream to, Bitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height;
        var maskRow = (w + 31) / 32 * 4;
        using var o = new BinaryWriter(to, System.Text.Encoding.UTF8, leaveOpen: true);
        o.Write(40); o.Write(w); o.Write(h * 2); o.Write((short)1); o.Write((short)32);
        o.Write(0); o.Write(w * h * 4 + maskRow * h); o.Write(0); o.Write(0); o.Write(0); o.Write(0);
        for (var y = h - 1; y >= 0; y--)
            for (var x = 0; x < w; x++)
            {
                var c = bmp.GetPixel(x, y);
                o.Write(c.B); o.Write(c.G); o.Write(c.R); o.Write(c.A);
            }
        o.Write(new byte[maskRow * h]);
    }

    private static GraphicsPath RoundedRect(float x, float y, float w, float h, float r)
    {
        var p = new GraphicsPath();
        p.AddArc(x, y, r * 2, r * 2, 180, 90);
        p.AddArc(x + w - r * 2, y, r * 2, r * 2, 270, 90);
        p.AddArc(x + w - r * 2, y + h - r * 2, r * 2, r * 2, 0, 90);
        p.AddArc(x, y + h - r * 2, r * 2, r * 2, 90, 90);
        p.CloseFigure();
        return p;
    }

    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr handle);
}
