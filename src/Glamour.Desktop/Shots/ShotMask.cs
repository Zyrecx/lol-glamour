using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Glamour.Host;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace Glamour.Desktop.Shots;

/// <summary>
/// Paints made-up names over the real ones in a shot of the client (SPEC "Publishing", README shots). Windows OCR reads every line; the words that are
/// a known real name (<see cref="ShotNames.Hits"/>) are painted out row by row from the pixels either side, and the made-up name is written in their
/// place in the colour the real one had. Then the shot is read again: any known name still found is a leak, and the caller keeps the shot back.
/// </summary>
internal static class ShotMask
{
    public sealed record Result(int Painted, IReadOnlyList<string> Leaks, IReadOnlyList<string> Before, IReadOnlyList<string> After);

    private sealed record Word(string Text, Rectangle Box);

    public static OcrEngine? Engine() => OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("en-US")) ?? OcrEngine.TryCreateFromUserProfileLanguages();

    /// <param name="client">Where the League client is in the shot, when known.</param>
    /// <param name="champSelect">The client shows champ select: its five ally name lines are then read one by one as well (see <see cref="Bands"/>).</param>
    public static Result Apply(Bitmap image, ShotNames names, OcrEngine ocr, Rectangle? client = null, bool champSelect = false)
    {
        var whole = new Rectangle(0, 0, image.Width, image.Height);
        var lines = Read(ocr, image, whole);
        var painted = 0;
        // The client's champ select chat: "<name> joined the lobby" lines, and above the top one a line half faded out that OCR cannot read but a reader
        // can. All of it goes (an empty chat looks normal); a made-up "joined" line would only invite the faded one back.
        var lobby = lines.Where(l => Text(l).Contains("the lobby", StringComparison.OrdinalIgnoreCase)).ToList();
        if (lobby.Count > 0)
        {
            var area = lobby.SelectMany(l => l).Select(w => w.Box).Aggregate(Rectangle.Union);
            var lineHeight = lobby.Max(l => l.Max(w => w.Box.Height));
            Clear(image, Rectangle.FromLTRB(area.Left - 2, area.Top - lineHeight * 2, area.Right + lineHeight, area.Bottom + 2));
            painted += lobby.Count;
        }
        var bands = client is { } c && champSelect ? Bands(c) : [];
        foreach (var line in lines.Except(lobby).Where(l => !bands.Any(b => b.IntersectsWith(l[0].Box))))
            painted += PaintHits(image, names, line);
        // The ally name lines, by position: an alias alone (ranked hides strangers behind a jungle camp's name) stays, "Omar (Gromp)" becomes "Gromp",
        // anything else is cleared (user: no drawn names on the client). A line OCR cannot read at all (often over splash art) is cleared too.
        foreach (var band in bands)
        {
            var read = Read(ocr, image, band);
            var text = string.Join(' ', read.Select(Text)).Trim();
            if (Aliases.Contains(text)) continue;
            var words = read.SelectMany(l => l).ToList();
            var open = words.FindIndex(w => w.Text.StartsWith('('));
            var close = words.FindLastIndex(w => w.Text.EndsWith(')'));
            if (open > 0 && close >= open && Aliases.Contains(string.Join(' ', words.Skip(open).Take(close - open + 1).Select(w => w.Text)).Trim('(', ')')))
                MoveOver(image, words[0].Box, words.Skip(open).Take(close - open + 1).Select(w => w.Box).Aggregate(Rectangle.Union), words.Skip(close + 1).Select(w => w.Box));
            else
                ClearDown(image, NameLine(band, words));
            painted++;
        }        var after = Read(ocr, image, whole);
        var leaks = after.Where(l => names.Hits([.. l.Select(w => w.Text)]).Count > 0).Select(Text).ToList();
        foreach (var b in bands)
            leaks.AddRange(Read(ocr, image, b).Where(l => names.Hits([.. l.Select(w => w.Text)]).Count > 0).Select(l => "ally row: " + Text(l)));
        return new(painted, leaks, [.. lines.Select(Text)], [.. after.Select(Text)]);
    }

    private static int PaintHits(Bitmap image, ShotNames names, List<Word> line)
    {
        var n = 0;
        foreach (var (first, count, fake) in names.Hits([.. line.Select(w => w.Text)]))
        {
            var words = line.Skip(first).Take(count).ToList();
            var box = words.Select(w => w.Box).Aggregate(Rectangle.Union);
            // Ranked shows a friend as "Omar (Gromp)": the alias in brackets is what everyone else sees, so it simply moves over the name, in the
            // client's own letters (user: a drawn font looks fake).
            var after = line.Skip(first + count).ToList();
            var close = after.FindIndex(w => w.Text.EndsWith(')'));
            if (after.Count > 0 && after[0].Text.StartsWith('(') && close >= 0)
                MoveOver(image, box, after.Take(close + 1).Select(w => w.Box).Aggregate(Rectangle.Union));
            else
                Paint(image, box, string.Join(' ', words.Select(w => w.Text)), fake);
            n++;
        }
        return n;
    }

    /// <summary>The names ranked champ select gives hidden players: jungle camps. One not listed here is cleared like a name, which is safe.</summary>
    private static readonly HashSet<string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        "Gromp", "Krug", "Murk Wolf", "Raptor", "Scuttle Crab", "Blue Sentinel", "Red Brambleback", "Ancient Krug", "Crimson Raptor", "Greater Murk Wolf",
        "Rift Scuttler", "Rift Herald", "Baron Nashor", "Voidgrub", "Atakhan",
    };

    /// <summary>Where a band's name sits: its words, or with nothing read the band's middle rows (the name is 16 px of the band's 24 at 720 p).</summary>
    private static Rectangle NameLine(Rectangle band, List<Word> words)
    {
        var pad = Math.Max(2, band.Height / 8);
        // Nothing read: from the band's start as far as a 16-letter name reaches (170 px at 720 p), not the whole band.
        if (words.Count == 0) return Rectangle.FromLTRB(band.Left, band.Top + band.Height / 6, band.Left + Math.Min(band.Width, band.Height * 170 / 24), band.Bottom - band.Height / 8);
        var u = words.Select(w => w.Box).Aggregate(Rectangle.Union);
        return Rectangle.FromLTRB(u.Left - pad, Math.Min(u.Top - pad / 2, band.Top + band.Height / 6), u.Right + pad, Math.Max(u.Bottom + pad / 2, band.Bottom - band.Height / 8));
    }

    /// <summary>
    /// Fills <paramref name="r"/> smoothly from all four edges (each pixel the average of its neighbours, repeated until it settles): over splash art a
    /// soft patch, where a blend in one direction leaves streaks.
    /// </summary>
    private static void ClearDown(Bitmap image, Rectangle r)
    {
        r = Rectangle.Intersect(r, new Rectangle(1, 1, image.Width - 2, image.Height - 2));
        if (r.Width <= 0 || r.Height <= 0) return;
        int w = r.Width + 2, h = r.Height + 2;
        var px = new double[3, w, h];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var edge = x == 0 || y == 0 || x == w - 1 || y == h - 1;
                // Inside starts as the blend of the pixels above and below; the edge ring stays as it is.
                var c = edge ? image.GetPixel(r.Left - 1 + x, r.Top - 1 + y) : Blend(image.GetPixel(r.Left - 1 + x, r.Top - 1), image.GetPixel(r.Left - 1 + x, r.Bottom), y / (double)(h - 1));
                px[0, x, y] = c.R; px[1, x, y] = c.G; px[2, x, y] = c.B;
            }
        for (var pass = 0; pass < 300; pass++)
            for (var y = 1; y < h - 1; y++)
                for (var x = 1; x < w - 1; x++)
                    for (var k = 0; k < 3; k++)
                        px[k, x, y] = (px[k, x - 1, y] + px[k, x + 1, y] + px[k, x, y - 1] + px[k, x, y + 1]) / 4;
        for (var y = 1; y < h - 1; y++)
            for (var x = 1; x < w - 1; x++)
                image.SetPixel(r.Left - 1 + x, r.Top - 1 + y, Color.FromArgb(255, (int)px[0, x, y], (int)px[1, x, y], (int)px[2, x, y]));
    }

    private static Color Blend(Color a, Color b, double t) => Color.FromArgb(255, Mix(a.R, b.R, t), Mix(a.G, b.G, t), Mix(a.B, b.B, t));
    /// <summary>
    /// Clears the name and the "(alias)" after it, then puts the alias's own letters back where the name started, without its brackets: the client
    /// shows a stranger's alias bare ("Krug").
    /// </summary>
    private static void MoveOver(Bitmap image, Rectangle name, Rectangle alias, IEnumerable<Rectangle>? rest = null)
    {
        var pad = Math.Max(2, alias.Height / 5);
        var src = Rectangle.Intersect(Rectangle.Inflate(alias, 1, pad / 2), new Rectangle(0, 0, image.Width, image.Height));
        src = InsideBrackets(image, src);
        using var piece = image.Clone(src, PixelFormat.Format32bppArgb);
        var span = rest?.Aggregate(alias, Rectangle.Union) ?? alias;
        Clear(image, Rectangle.FromLTRB(name.Left - pad, src.Top, span.Right + pad, src.Bottom));
        using var g = Graphics.FromImage(image);
        g.DrawImageUnscaled(piece, name.Left, src.Top);
    }

    /// <summary>The columns of <paramref name="r"/> between its first and last run of letter pixels (the brackets), so the alias moves without them.</summary>
    private static Rectangle InsideBrackets(Bitmap image, Rectangle r)
    {
        var bg = image.GetPixel(r.Left, r.Top);
        var ink = new bool[r.Width];
        for (var x = 0; x < r.Width; x++)
            for (var y = 0; y < r.Height && !ink[x]; y++)
            {
                var c = image.GetPixel(r.Left + x, r.Top + y);
                ink[x] = Math.Abs(c.R - bg.R) + Math.Abs(c.G - bg.G) + Math.Abs(c.B - bg.B) > 120;
            }
        // Skip the first run of ink and the gap after it, likewise from the right.
        int left = 0, right = r.Width - 1;
        while (left < r.Width && !ink[left]) left++;
        while (left < r.Width && ink[left]) left++;
        while (left < r.Width && !ink[left]) left++;
        while (right >= 0 && !ink[right]) right--;
        while (right >= 0 && ink[right]) right--;
        while (right >= 0 && !ink[right]) right--;
        return right - left < 4 ? r : Rectangle.FromLTRB(r.Left + left - 1, r.Top, r.Left + right + 1, r.Bottom);
    }
    /// <summary>
    /// The five ally name lines in champ select, draft and ARAM alike (measured on the 1280x720 shots: rows 80 px apart from y 95, the name 41-57 px into
    /// its row, from x 97 in draft and 127 in ARAM; `RosterLayout`), a little wider and taller than the text.
    /// </summary>
    internal static Rectangle[] Bands(Rectangle client)
    {
        var s = client.Height / 720.0;
        return [.. Enumerable.Range(0, 5).Select(i => new Rectangle(client.Left + (int)(88 * s), client.Top + (int)((95 + 80 * i + 37) * s), (int)(272 * s), (int)(24 * s)))];
    }

    private static string Text(List<Word> line) => string.Join(' ', line.Select(w => w.Text));

    /// <summary>
    /// Every OCR line in <paramref name="area"/> with its words' boxes in image pixels. The area is enlarged first: the client's small grey names read
    /// badly at 1x. A whole shot goes to about 2160 px high; a single line of text to about 100 px.
    /// </summary>
    private static List<List<Word>> Read(OcrEngine ocr, Bitmap image, Rectangle area)
    {
        area = Rectangle.Intersect(area, new Rectangle(0, 0, image.Width, image.Height));
        if (area.Width < 4 || area.Height < 4) return [];
        var scale = area.Height < 80 ? Math.Clamp(100.0 / area.Height, 1, 6) : Math.Clamp(2160.0 / area.Height, 1, 3);
        scale = Math.Min(scale, (OcrEngine.MaxImageDimension - 1) / (double)Math.Max(area.Width, area.Height));
        using var big = new Bitmap((int)(area.Width * scale), (int)(area.Height * scale), PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(big))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(image, new Rectangle(0, 0, big.Width, big.Height), area, GraphicsUnit.Pixel);
        }
        var data = big.LockBits(new Rectangle(0, 0, big.Width, big.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var bytes = new byte[big.Width * 4 * big.Height];
        for (var y = 0; y < big.Height; y++) Marshal.Copy(data.Scan0 + y * data.Stride, bytes, y * big.Width * 4, big.Width * 4);
        big.UnlockBits(data);
        using var soft = SoftwareBitmap.CreateCopyFromBuffer(bytes.AsBuffer(), BitmapPixelFormat.Bgra8, big.Width, big.Height, BitmapAlphaMode.Ignore);
        var result = ocr.RecognizeAsync(soft).AsTask().GetAwaiter().GetResult();
        return [.. result.Lines.Where(l => l.Words.Count > 0).Select(l => l.Words.Select(w => new Word(w.Text, new Rectangle(
            area.Left + (int)Math.Floor(w.BoundingRect.X / scale), area.Top + (int)Math.Floor(w.BoundingRect.Y / scale),
            (int)Math.Ceiling(w.BoundingRect.Width / scale) + 1, (int)Math.Ceiling(w.BoundingRect.Height / scale) + 1))).ToList())];
    }
    /// <summary>Fills <paramref name="r"/> row by row with a blend of the pixels just left and right of it: text on a plain or smooth background goes.</summary>
    private static void Clear(Bitmap image, Rectangle r)
    {
        r = Rectangle.Intersect(r, new Rectangle(1, 0, image.Width - 2, image.Height));
        for (var y = r.Top; y < r.Bottom; y++)
        {
            var left = image.GetPixel(r.Left - 1, y);
            var right = image.GetPixel(Math.Min(r.Right, image.Width - 1), y);
            for (var x = 0; x < r.Width; x++)
            {
                var t = (x + 1) / (double)(r.Width + 1);
                image.SetPixel(r.Left + x, y, Color.FromArgb(255, Mix(left.R, right.R, t), Mix(left.G, right.G, t), Mix(left.B, right.B, t)));
            }
        }
    }

    /// <summary>
    /// Replaces the real name in <paramref name="box"/> (OCR read it as <paramref name="real"/>) by <paramref name="text"/>: cleared, then written in the
    /// old letters' colour, with the same capital height and on the same baseline, measured on the old letters themselves.
    /// </summary>
    private static void Paint(Bitmap image, Rectangle box, string real, string text)
    {
        var pad = Math.Max(2, box.Height / 5);
        var r = Rectangle.Intersect(Rectangle.Inflate(box, pad, pad / 2), new Rectangle(1, 0, image.Width - 2, image.Height));
        if (r.Width <= 0 || r.Height <= 0) return;
        var old = new Color[r.Width, r.Height];
        for (var y = 0; y < r.Height; y++)
            for (var x = 0; x < r.Width; x++) old[x, y] = image.GetPixel(r.Left + x, r.Top + y);
        Clear(image, r);
        // The colour: of the old pixels inside the word box, the brightest tenth's average (a letter's middle; the client writes names lighter than
        // what is behind them).
        var inside = new List<Color>();
        for (var y = box.Top - r.Top; y < Math.Min(r.Height, box.Bottom - r.Top); y++)
            for (var x = box.Left - r.Left; x < Math.Min(r.Width, box.Right - r.Left); x++)
                if (x >= 0 && y >= 0) inside.Add(old[x, y]);
        if (inside.Count == 0) return;
        var top = inside.OrderByDescending(c => c.GetBrightness()).Take(Math.Max(1, inside.Count / 10)).ToList();
        var ink = Color.FromArgb(255, (int)top.Average(c => c.R), (int)top.Average(c => c.G), (int)top.Average(c => c.B));
        // From the box's height to the capital height and baseline: OCR boxes hug the letters, so a name without capitals or tall letters stands
        // x-height high, and a descender (g, j, p, q, y) hangs a quarter below the baseline.
        var tall = real.Any(c => char.IsUpper(c) || char.IsDigit(c) || "bdfhklt#()".Contains(c));
        var deep = real.Any(c => "gjpqy(),".Contains(c));
        var cap = box.Height / ((tall ? 1.0 : 0.72) + (deep ? 0.24 : 0));
        var baseline = box.Top + (tall ? cap : cap * 0.72);        using var family = new FontFamily("Segoe UI");
        var capRatio = 0.7; // Segoe UI's capital height over its em size
        var em = (float)Math.Max(6, cap / capRatio);
        using var font = new Font(family, em, FontStyle.Regular, GraphicsUnit.Pixel);
        var ascent = em * family.GetCellAscent(FontStyle.Regular) / family.GetEmHeight(FontStyle.Regular);
        using var g = Graphics.FromImage(image);
        g.TextRenderingHint = TextRenderingHint.AntiAlias;
        using var brush = new SolidBrush(ink);
        var format = StringFormat.GenericTypographic;
        var width = g.MeasureString(text, font, PointF.Empty, format).Width;
        var y0 = (float)(baseline - ascent);
        // A made-up name wider than the old one plus its gap is squeezed, so it never runs into the text after it.
        var room = r.Right - box.Left;
        if (width > room && width > 0)
        {
            var state = g.Save();
            g.TranslateTransform(box.Left, y0);
            g.ScaleTransform(room / width, 1);
            g.DrawString(text, font, brush, 0, 0, format);
            g.Restore(state);
        }
        else g.DrawString(text, font, brush, box.Left, y0, format);
    }
    private static int Mix(int a, int b, double t) => (int)Math.Round(a + (b - a) * t);

    /// <summary>
    /// `Glamour.exe --mask-shot <png> [--client x,y,w,h] [--champ] [real name ...]`: paints made-up names over the given real names on a saved shot. Writes `<png>.masked.png` and
    /// `<png>.mask.txt` (the OCR lines before and after, and any leak). For trying the mask on old shots; the shots taken with the shot keys learn
    /// their names from the host.
    /// </summary>
    public static void Shot(string path, IReadOnlyList<string> args)
    {
        var names = new ShotNames();
        Rectangle? client = null;
        var champ = false;
        for (var i = 0; i < args.Count; i++)
            if (args[i] == "--client" && i + 1 < args.Count && args[++i].Split(',').Select(int.Parse).ToArray() is [var x, var y, var w, var h]) client = new Rectangle(x, y, w, h);
            else if (args[i] == "--champ") champ = true;
            else names.Learn(args[i]);
        using var image = new Bitmap(path);
        using var copy = new Bitmap(image.Width, image.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(copy)) g.DrawImage(image, 0, 0, image.Width, image.Height);
        var ocr = Engine();
        if (ocr is null) { File.WriteAllText(path + ".mask.txt", "Windows has no OCR language installed."); return; }
        var result = Apply(copy, names, ocr, client ?? (Math.Abs(image.Width * 9 - image.Height * 16) < 32 ? new Rectangle(0, 0, image.Width, image.Height) : null), champ);
        copy.Save(path + ".masked.png", ImageFormat.Png);
        File.WriteAllLines(path + ".mask.txt", [
            $"{Path.GetFileName(path)} {image.Width}x{image.Height}: {result.Painted} name(s) painted, {result.Leaks.Count} leak(s)",
            .. result.Leaks.Select(l => "LEAK: " + l), "", "-- before", .. result.Before, "", "-- after", .. result.After]);
    }

    /// <summary>
    /// `Glamour.exe --mask-frames <folder> [--client x,y,w,h] [--champ-from N] [--names file] [--me name] [real name ...]`: masks every `raw\NNNN.png` of a clip
    /// cut by `tools/readme-rec.mjs` into `masked\NNNN.png` (`.LEAK.png` when a name is still read), the layout `tools/readme-gif.ps1` reads.
    /// Frames from number N on are treated as champ select. `--names`: one real name per line, the first is me. Writes `masked.txt` (one line per frame).
    /// </summary>
    public static void Frames(string folder, IReadOnlyList<string> args)
    {
        var names = new ShotNames();
        Rectangle? client = null;
        var champFrom = int.MaxValue;
        for (var i = 0; i < args.Count; i++)
            if (args[i] == "--client" && i + 1 < args.Count && args[++i].Split(',').Select(int.Parse).ToArray() is [var x, var y, var w, var h]) client = new Rectangle(x, y, w, h);
            else if (args[i] == "--champ-from" && i + 1 < args.Count) champFrom = int.Parse(args[++i]);
            else if (args[i] == "--me" && i + 1 < args.Count) names.Learn(args[++i], isMe: true);
            else if (args[i] == "--names" && i + 1 < args.Count)
                foreach (var (line, n) in File.ReadAllLines(args[++i]).Select((l, n) => (l.Trim(), n)).Where(x => x.Item1.Length > 0)) names.Learn(line, isMe: n == 0);
            else names.Learn(args[i]);
        var masked = Path.Combine(folder, "masked");
        Directory.CreateDirectory(masked);
        var ocr = Engine();
        if (ocr is null) { File.WriteAllText(Path.Combine(folder, "masked.txt"), "Windows has no OCR language installed."); return; }
        var lines = new List<string>();
        foreach (var file in Directory.GetFiles(Path.Combine(folder, "raw"), "*.png").Order())
        {
            var n = int.Parse(Path.GetFileNameWithoutExtension(file));
            using var image = new Bitmap(file);
            using var copy = new Bitmap(image.Width, image.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(copy)) g.DrawImage(image, 0, 0, image.Width, image.Height);
            var result = Apply(copy, names, ocr, client, n >= champFrom);
            var leak = result.Leaks.Count > 0;
            copy.Save(Path.Combine(masked, $"{n:0000}" + (leak ? ".LEAK.png" : ".png")), ImageFormat.Png);
            lines.Add($"{n:0000}: {result.Painted} painted, {result.Leaks.Count} leak(s)" + string.Concat(result.Leaks.Select(l => " | LEAK: " + l)));
        }
        File.WriteAllLines(Path.Combine(folder, "masked.txt"), lines);
    }
}
