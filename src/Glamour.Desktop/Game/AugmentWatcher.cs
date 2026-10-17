using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json.Nodes;
using Glamour.Core;
using Glamour.Desktop.Dock;
using Glamour.Host;
using Glamour.Host.Panel;
using Glamour.Lcu;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace Glamour.Desktop.Game;

/// <summary>
/// Reads Mayhem augment offers off the screen and tags them (SPEC "PC app direction" 5, INSIGHTS "Mayhem augments (in game)"; the method is ARAM Mayhem
/// Tracker's). Only with <see cref="GlamourSettings.AugmentTips"/> on, in a Mayhem game, with the game window in front. Each tick looks for the "hide augments"
/// button alone (a few ms); only while it is there are the three title bands captured, and only when one of them changed are they read with Windows' own
/// OCR (a reroll, the cards hidden, the shop over them). The tags show while at least two cards read as Mayhem augments, so hiding the cards or opening
/// the shop takes them away on the next read. Hovering a card takes the button away (user, live): once the tags are up they stay while the titles still
/// read, and a card that reads as nothing keeps its last tag. Screen capture skips layered windows, so the tags never read themselves.
/// </summary>
internal sealed class AugmentWatcher : IDisposable
{
    private static readonly TimeSpan Offer = TimeSpan.FromMilliseconds(300), Watch = TimeSpan.FromMilliseconds(300), Idle = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan Reread = TimeSpan.FromSeconds(2);
    /// <summary>A button score this close under <see cref="AugmentButton.Present"/> is worth a screenshot: the threshold or the layout may be off.</summary>
    private const double NearMiss = 0.4;

    private readonly Func<IServiceProvider?> _services;
    private readonly ILogger _log;
    private readonly AugmentOverlay _overlay;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _stop = new(false);
    private OcrEngine? _ocr; // made on the watcher thread, where it is used

    // One game's state, on the watcher thread.
    private bool _inGame;
    private Task<AugmentData?>? _data;
    private AugmentOffers _offers = new();
    private byte[]?[] _prints = new byte[]?[AugmentLayout.Slots];
    private DateTime _readAt;
    private string? _lastLogged;
    private bool _shown;
    private int?[] _shownIds = new int?[AugmentLayout.Slots];
    // For the log: the game window as first seen, the best button score, a screenshot of the first offer and of a near miss, unread cards.
    private bool _windowLogged, _offerShot, _nearShot, _goneShot;
    private double _bestScore;
    private int _misreads;
    private DateTime _failedAt;

    public AugmentWatcher(Func<IServiceProvider?> services, ILogger log)
    {
        _services = services;
        _log = log;
        _overlay = new AugmentOverlay(log);
        _thread = new Thread(Run) { IsBackground = true, Name = "Glamour augment watcher", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    private void Run()
    {
        _ocr = OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("en-US")) ?? OcrEngine.TryCreateFromUserProfileLanguages();
        if (_ocr is null) _log.LogWarning("Augment tags: Windows has no OCR language installed, cards cannot be read");
        while (!_stop.IsSet)
        {
            TimeSpan wait;
            try { wait = Tick(); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                if (DateTime.UtcNow - _failedAt > TimeSpan.FromMinutes(1)) _log.LogWarning(ex, "Augment tags: a tick failed"); // at most once a minute
                _failedAt = DateTime.UtcNow;
                Hide();
                wait = Idle;
            }
            _stop.Wait(wait);
        }
        _overlay.Hide();
    }

    private TimeSpan Tick()
    {
        var sp = _services();
        var settings = sp?.GetService<GlamourSettings>();
        var mode = sp?.GetService<GameClock>()?.Mode;
        if (sp is null || settings?.AugmentTips != true || mode != "KIWI" || _ocr is null)
        {
            EndGame();
            return Idle;
        }
        if (!_inGame)
        {
            _inGame = true;
            _data = LoadAsync(sp);
        }
        var game = Win32.FindGame();
        if (game == IntPtr.Zero || Win32.IsMinimized(game) || Win32.Foreground() != game) { Hide(); return Watch; } // capture reads the screen: only what the game shows
        var client = Win32.ClientArea(game);
        if (!_windowLogged)
        {
            _windowLogged = true;
            _log.LogInformation("Augment tags watching the game window at {Left},{Top} {Width}x{Height}, offer fits {Fits}", client.Left, client.Top, client.Width, client.Height, AugmentLayout.Fits(client));
        }
        if (!AugmentLayout.Fits(client)) { Hide(); return Idle; }

        var search = AugmentLayout.ButtonSearch(client);
        var (bw, bh) = AugmentLayout.ButtonSize(client);
        double score;
        using (var shot = Grab(search)) score = AugmentButton.Score(Gray(shot), AugmentButton.Template, bw, bh);
        _bestScore = Math.Max(_bestScore, score);
        if (score < AugmentButton.Present && _shown)
        {
            // A card hovered, or the offer over (picked, the shop over it): the titles tell which.
            if (!_goneShot) { _goneShot = true; Screenshot(client, FormattableString.Invariant($"gone-{score:0.00}")); }
        }
        else if (score < AugmentButton.Present)
        {
            if (score >= NearMiss && !_nearShot) { _nearShot = true; Screenshot(client, FormattableString.Invariant($"near-{score:0.00}")); }
            Hide();
            _prints = new byte[]?[AugmentLayout.Slots];
            return Watch;
        }
        else if (!_offerShot) { _offerShot = true; Screenshot(client, FormattableString.Invariant($"offer-{score:0.00}")); }

        // The offer is open: read the titles when they changed (or now and then, in case a change slipped under the fingerprint).
        if (_data is not { IsCompleted: true }) return Offer; // the numbers are still on their way: read once they are here
        if (_data.Result is not { } data) { Hide(); return Idle; }
        var bands = Enumerable.Range(0, AugmentLayout.Slots).Select(i => Grab(AugmentLayout.Title(client, i))).ToArray();
        try
        {
            var prints = bands.Select(b => AugmentButton.Fingerprint(Gray(b))).ToArray();
            var changed = prints.Select((p, i) => AugmentButton.Differs(_prints[i], p)).Any(c => c);
            if (!changed && DateTime.UtcNow - _readAt < Reread) return Offer;
            _prints = prints;
            _readAt = DateTime.UtcNow;
            var lines = bands.Select(ReadLines).ToArray();
            var ids = lines.Select(l => AugmentNames.MatchLines(l, data.Pool)?.Id).ToArray();
            if (ids.Count(id => id is not null) < 2) // hidden, under the shop, or not an augment offer (stat anvils)
            {
                if (score >= AugmentButton.Present && _misreads++ < 5) _log.LogInformation("Augment tags: the button is up but the cards read as {Lines}", string.Join(" / ", lines.Select(l => string.Join(" | ", l))));
                Hide();
                return Offer;
            }
            ids = [.. ids.Select((id, i) => id ?? _shownIds[i])]; // a hovered or rerolling card that reads as nothing keeps its last tag
            var level = Level(sp);
            var stage = _offers.StageOf(ids, AugmentAdvice.StagesUnlocked(level));
            var (picks, best) = AugmentAdvice.Rank(ids, data.Champion, data.Global, stage);
            _overlay.Show(new AugmentMarks(client, picks, best), game);
            _shown = true;
            _shownIds = ids;
            LogOffer(data, stage, level, picks, best);
            return Offer;
        }
        finally { foreach (var b in bands) b.Dispose(); }
    }

    private void Hide()
    {
        if (!_shown) return;
        _overlay.Hide();
        _shown = false;
        _shownIds = new int?[AugmentLayout.Slots];
    }

    private void EndGame()
    {
        Hide();
        if (!_inGame) return;
        _log.LogInformation("Augment tags: game over, {Offers} offers seen, best button score {Score:0.000}", _offers.Seen, _bestScore);
        _inGame = false;
        _data = null;
        _offers = new();
        _prints = new byte[]?[AugmentLayout.Slots];
        _lastLogged = null;
        _windowLogged = _offerShot = _nearShot = _goneShot = false;
        _bestScore = 0;
        _misreads = 0;
    }

    /// <summary>The whole game screen to logs\shots, once per game for the first offer and once for a near miss: what to measure if the reading is off.</summary>
    private void Screenshot(PixelRect client, string what)
    {
        try
        {
            var dir = Path.Combine(Glamour.Host.Logging.FileLog.DefaultDir, "shots");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, $"augment-{DateTime.Now:yyyyMMdd-HHmmss}-{what}.png");
            using var bmp = Grab(client);
            bmp.Save(file, ImageFormat.Png);
            _log.LogInformation("Augment tags: screen saved to {File}", file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ExternalException) { _log.LogWarning(ex, "Augment tags: could not save the screen"); }
    }

    private void LogOffer(AugmentData data, int stage, int level, AugmentPick?[] picks, int best)
    {
        var line = string.Join(", ", picks.Select((p, i) => p is null ? "?" : $"{data.Pool.First(a => a.Id == p.Id).Name} {AugmentOverlay.Text(p)} ({p.Source})" + (i == best ? " best" : "")));
        if (line == _lastLogged) return;
        _lastLogged = line;
        _log.LogInformation("Augment offer {Stage} at level {Level}: {Cards}", stage, level, line);
        _services()?.GetService<Glamour.Core.IUsage>()?.Count("shown_augment_tags"); // per offer and reroll
    }

    /// <summary>My champion from the game's Live Client Data (my Riot ID in the player list, its `rawChampionName` key), then its numbers.</summary>
    private async Task<AugmentData?> LoadAsync(IServiceProvider sp)
    {
        try
        {
            var live = sp.GetRequiredService<ILiveGameClient>();
            var catalog = sp.GetRequiredService<ChampionCatalog>();
            for (var attempt = 0; attempt < 30 && !_stop.IsSet; attempt++)
            {
                var me = (await live.GetAsync("/liveclientdata/activeplayername"))?.GetValue<string>();
                var players = await live.GetAsync("/liveclientdata/playerlist") as JsonArray;
                var mine = players?.FirstOrDefault(p => me is not null && ((string?)p?["riotId"] == me || (string?)p?["summonerName"] == me));
                if (mine is not null && await catalog.GetAsync(default) is { } list)
                {
                    var key = ((string?)mine["rawChampionName"])?.Split('_')[^1];
                    var name = (string?)mine["championName"];
                    var champ = list.Champions.FirstOrDefault(c => string.Equals(Path.GetFileNameWithoutExtension(new Uri(c.Icon).AbsolutePath), key, StringComparison.OrdinalIgnoreCase))
                        ?? list.Champions.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
                    if (champ is null) { _log.LogWarning("Augment tags: champion {Name} not in the champion list", name); return null; }
                    var data = await sp.GetRequiredService<AugmentDataSource>().GetAsync(champ.Id, default);
                    _log.LogInformation("Augment tags for {Champion}: {Pool} augments, champion numbers {Champ}, all-champion numbers {Global}",
                        champ.Name, data?.Pool.Count ?? 0, data?.Champion?.Patch ?? "none", data?.Global?.Patch ?? "none");
                    return data;
                }
                await Task.Delay(TimeSpan.FromSeconds(2));
            }
            _log.LogWarning("Augment tags: could not tell my champion from the game");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { _log.LogWarning(ex, "Augment tags: loading the numbers failed"); }
        return null;
    }

    private static int Level(IServiceProvider sp)
    {
        try { return (int?)sp.GetRequiredService<ILiveGameClient>().GetAsync("/liveclientdata/activeplayer").GetAwaiter().GetResult()?["level"] ?? 1; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return 1; }
    }

    /// <summary>What is on screen in <paramref name="r"/> (BitBlt without CAPTUREBLT: layered windows, the tags among them, are left out).</summary>
    private static Bitmap Grab(PixelRect r)
    {
        var bmp = new Bitmap(Math.Max(1, r.Width), Math.Max(1, r.Height), PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.CopyFromScreen(r.Left, r.Top, 0, 0, bmp.Size);
        return bmp;
    }

    /// <summary>Greys as `tools/gray.ps1` does, so the template and the test screens match what is captured.</summary>
    internal static GrayImage Gray(Bitmap bmp)
    {
        var (w, h) = (bmp.Width, bmp.Height);
        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var src = new byte[data.Stride * h];
        Marshal.Copy(data.Scan0, src, 0, src.Length);
        bmp.UnlockBits(data);
        var px = new byte[w * h];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var i = y * data.Stride + x * 4;
                px[y * w + x] = (byte)Math.Min(255, (int)(0.114 * src[i] + 0.587 * src[i + 1] + 0.299 * src[i + 2] + 0.5));
            }
        return new(w, h, px);
    }

    /// <summary>The text lines of a title band, top to bottom, read by Windows OCR on the band enlarged to ~40 px a line (small text reads worse).</summary>
    internal IReadOnlyList<string> ReadLines(Bitmap band) => ReadLines(_ocr!, band);

    internal static IReadOnlyList<string> ReadLines(OcrEngine ocr, Bitmap band)
    {
        var scale = Math.Clamp(120.0 / band.Height, 1, 4);
        using var big = new Bitmap((int)(band.Width * scale), (int)(band.Height * scale), PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(big))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(band, 0, 0, big.Width, big.Height);
        }
        var data = big.LockBits(new Rectangle(0, 0, big.Width, big.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var bytes = new byte[data.Stride * big.Height];
        Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
        big.UnlockBits(data);
        var tight = data.Stride == big.Width * 4 ? bytes : Repack(bytes, data.Stride, big.Width, big.Height);
        using var soft = SoftwareBitmap.CreateCopyFromBuffer(tight.AsBuffer(), BitmapPixelFormat.Bgra8, big.Width, big.Height, BitmapAlphaMode.Ignore);
        var result = ocr.RecognizeAsync(soft).AsTask().GetAwaiter().GetResult();
        return [.. result.Lines.OrderBy(l => l.Words.Count > 0 ? l.Words[0].BoundingRect.Y : 0).Select(l => l.Text)];
    }

    private static byte[] Repack(byte[] bytes, int stride, int w, int h)
    {
        var tight = new byte[w * h * 4];
        for (var y = 0; y < h; y++) Array.Copy(bytes, y * stride, tight, y * w * 4, w * 4);
        return tight;
    }

    /// <summary>
    /// `Glamour.exe --augment-shot <image>`: runs the screen reading on a screenshot (as if it were the screen) and writes what it found to
    /// `<image>.txt`: the button's score and each card's OCR lines. For checking a new resolution or a patch that restyled the cards.
    /// </summary>
    public static void Shot(string path)
    {
        using var image = new Bitmap(path);
        var client = new PixelRect(0, 0, image.Width, image.Height);
        var lines = new List<string> { $"{Path.GetFileName(path)} {image.Width}x{image.Height}, fits {AugmentLayout.Fits(client)}" };
        Bitmap Crop(PixelRect r) => image.Clone(Rectangle.Intersect(new Rectangle(r.Left, r.Top, r.Width, r.Height), new Rectangle(0, 0, image.Width, image.Height)), PixelFormat.Format32bppArgb);
        var (bw, bh) = AugmentLayout.ButtonSize(client);
        using (var b = Crop(AugmentLayout.ButtonSearch(client)))
            lines.Add($"button {AugmentButton.Score(Gray(b), AugmentButton.Template, bw, bh):0.000} (present at {AugmentButton.Present})");
        var ocr = OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("en-US"));
        for (var i = 0; i < AugmentLayout.Slots; i++)
        {
            using var band = Crop(AugmentLayout.Title(client, i));
            lines.Add($"card {i}: " + (ocr is null ? "no OCR" : string.Join(" | ", ReadLines(ocr, band))));
        }
        File.WriteAllLines(path + ".txt", lines);
        // The tags as they would sit on this screen, with made-up numbers: one of each look.
        using var preview = new Bitmap(image.Width, image.Height, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(preview))
        {
            g.DrawImage(image, 0, 0, image.Width, image.Height);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            AugmentOverlay.Draw(g, new AugmentMarks(client, [new(1, 4.6, AugmentSource.Stage, false), new(2, -2.3, AugmentSource.Champion, false), new(3, 0.8, AugmentSource.Global, false)], 0));
        }
        preview.Save(path + ".tags.png", ImageFormat.Png);
    }

    public void Dispose()
    {
        _stop.Set();
        _thread.Join(TimeSpan.FromSeconds(2));
        _overlay.Dispose();
    }
}
