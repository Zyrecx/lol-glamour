using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Glamour.Core;
using Glamour.Desktop.Dock;
using Glamour.Host;
using Glamour.Host.Panel;
using Glamour.Lcu;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Glamour.Desktop.Game;

/// <summary>
/// Premades on the game's loading screen (SPEC "Parties on the loading screen"): with <see cref="GlamourSettings.LoadingParties"/> on and a party in the
/// game, marks each party's cards while the loading screen shows. The cards come from the game's `playerlist` order with my card moved to where its
/// gold name is (<see cref="LoadingCards"/>, <see cref="LoadingLayout.MyCard"/>); the loading screen counts as up while most cards show their name plate (<see cref="LoadingLayout"/>), read off the screen with the game in front and
/// through PrintWindow (works for a borderless game, LIVE) once a second when it is behind. Once the plates are gone the marks are done for that game.
/// </summary>
internal sealed class LoadingWatcher : IDisposable
{
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(250), Behind = TimeSpan.FromSeconds(1), Idle = TimeSpan.FromSeconds(1);
    /// <summary>The watch ends this long after the game window showed, whatever was seen (the loading screen took ~25 s, LIVE): a layout that never matches,
    /// or a windowed game behind another app (its PrintWindow is not the client area), is not polled into the game.</summary>
    private static readonly TimeSpan GiveUp = TimeSpan.FromMinutes(3);

    private readonly Func<IServiceProvider?> _services;
    private readonly ILogger _log;
    private readonly LoadingOverlay _overlay;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _stop = new(false);

    // One game, on the watcher thread.
    private long _gameId;
    private bool _done;
    private DateTime? _windowAt, _shownAt;
    private Task<Lineup?>? _lineup;
    private (bool Top, int Slot)? _myCard;
    private bool _myCardMissLogged;
    private int _misses;

    /// <summary>The game's player list, my Riot ID in it and the champion keys to match party members by.</summary>
    private sealed record Lineup(IReadOnlyList<LivePlayer> Players, string? Me, Func<int, string?> Key);

    public LoadingWatcher(Func<IServiceProvider?> services, ILogger log)
    {
        _services = services;
        _log = log;
        _overlay = new LoadingOverlay(log);
        _thread = new Thread(Run) { IsBackground = true, Name = "Glamour loading parties", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    private void Run()
    {
        while (!_stop.IsSet)
        {
            TimeSpan wait;
            try { wait = Step(); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _log.LogWarning(ex, "Loading parties: a tick failed");
                Finish("failed");
                wait = Idle;
            }
            _stop.Wait(wait);
        }
        _overlay.Hide();
    }

    private TimeSpan Step()
    {
        var sp = _services();
        var engine = sp?.GetService<Engine>();
        var parties = engine?.Tracker.CurrentParties ?? GameParties.Empty;
        if (sp?.GetService<GlamourSettings>()?.LoadingParties != true || engine?.Phase is not ("GameStart" or "InProgress" or "Reconnect") || parties.GameId == 0)
        {
            if (_gameId != 0) Finish("left the game");
            _gameId = 0;
            _done = false;
            return Idle;
        }
        if (parties.GameId != _gameId)
        {
            _gameId = parties.GameId;
            _done = parties.Parties.Count == 0;
            _windowAt = _shownAt = null;
            _lineup = null;
            _myCard = null;
            _myCardMissLogged = false;
            _misses = 0;
        }
        if (_done) return Idle;

        var game = Win32.FindGame();
        if (game == IntPtr.Zero || !Win32.IsShown(game) || Win32.IsMinimized(game)) return Tick;
        var client = Win32.ClientArea(game);
        if (client.Height < 400) return Tick; // the window starts at 1x1 (LIVE)
        _windowAt ??= DateTime.UtcNow;
        if (!LoadingLayout.Fits(client)) { Finish($"the cards do not fit {client.Width}x{client.Height}"); return Idle; }
        if (DateTime.UtcNow - _windowAt > GiveUp) { Finish(_shownAt is null ? "no loading screen seen" : "still marked after the time limit"); return Idle; }

        var front = Win32.Foreground() == game;
        var pixel = front ? ScreenPixels(client) : PixelsBehind(game, client);
        if (pixel is null) return Behind;
        var plates = LoadingLayout.PlatesShown(client, pixel);
        if (plates < LoadingLayout.PlatesNeeded)
        {
            if (_shownAt is { } at && ++_misses >= 2) { Finish(FormattableString.Invariant($"loading screen gone after {(DateTime.UtcNow - at).TotalSeconds:0.0} s")); return Idle; }
            return front ? Tick : Behind;
        }
        _misses = 0;
        _lineup ??= LineupAsync(sp!);
        if (!_lineup.IsCompleted) return Tick;
        if (_lineup.Result is not { } lineup) { Finish("no player list"); return Idle; }
        if (_myCard is null && lineup.Me is not null && LoadingLayout.MyCard(client, pixel) is { } mine)
        {
            _myCard = mine;
            _log.LogInformation("Loading parties: my card is {Row} {Slot}", mine.Top ? "top" : "bottom", mine.Slot + 1);
        }
        var cards = LoadingCards.Match(parties, lineup.Players, lineup.Key, lineup.Me, _myCard);
        var waiting = _myCard is null && lineup.Me is not null; // my row's marks wait for my card: a card still loading has no name yet
        if (cards.Count == 0)
        {
            if (!waiting) { Finish("no party card found"); return Idle; }
            _overlay.Hide();
            return front ? Tick : Behind;
        }
        if (waiting && !_myCardMissLogged)
        {
            _myCardMissLogged = true;
            _log.LogInformation("Loading parties: my card not found yet, my row left unmarked");
        }
        if (_shownAt is null)
        {
            _shownAt = DateTime.UtcNow;
            _services()?.GetService<Glamour.Core.IUsage>()?.Count("shown_loading_parties");
            _log.LogInformation("Loading parties shown at {Width}x{Height}, {Plates} plates, in front {Front}: {Cards}", client.Width, client.Height, plates, front,
                string.Join(", ", cards.Select(c => $"{(c.Top ? "top" : "bottom")} {c.Slot + 1} party {c.Party + 1}")));
        }
        _overlay.Show(new LoadingMarks(client, cards), game);
        return front ? Tick : Behind;
    }

    private void Finish(string why)
    {
        if (_gameId != 0 && !_done) _log.LogInformation("Loading parties: done for game {Game}, {Why}", _gameId, why);
        _done = true;
        _overlay.Hide();
    }

    /// <summary>The game's player list (asked until it answers: the game API comes up during the loading screen).</summary>
    private async Task<Lineup?> LineupAsync(IServiceProvider sp)
    {
        try
        {
            var live = sp.GetRequiredService<ILiveGameClient>();
            var list = await sp.GetRequiredService<ChampionCatalog>().GetAsync(default);
            for (var attempt = 0; attempt < 30 && !_stop.IsSet; attempt++)
            {
                if (await live.GetAsync("/liveclientdata/playerlist") is JsonArray { Count: > 0 } players)
                {
                    var rows = players.Select(p => new LivePlayer((string?)p?["team"] ?? "", (string?)p?["riotId"] ?? "", ((string?)p?["rawChampionName"])?.Split('_')[^1] ?? "")).ToList();
                    string? Key(int id) => list?.Champions.FirstOrDefault(c => c.Id == id) is { } c ? Path.GetFileNameWithoutExtension(new Uri(c.Icon).AbsolutePath) : null;
                    var me = (string?)(await live.GetAsync("/liveclientdata/activeplayername"));
                    if (rows.All(r => r.RiotId != me)) me = null;
                    _log.LogInformation("Loading parties: player list {Teams}, I am on {Mine}",
                        string.Join(" ", rows.Select(r => r.Team.FirstOrDefault() + ":" + r.ChampionKey)), rows.FirstOrDefault(r => r.RiotId == me)?.Team ?? "?");
                    return new Lineup(rows, me, Key);
                }
                await Task.Delay(TimeSpan.FromSeconds(1));
            }
            _log.LogWarning("Loading parties: the game's player list did not answer");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { _log.LogWarning(ex, "Loading parties: reading the player list failed"); }
        return null;
    }

    /// <summary>The screen's colour at a point, from one 1-pixel-high capture of the client's width per row asked (two per tick: one per card row).</summary>
    private static Func<int, int, (int R, int G, int B)> ScreenPixels(PixelRect client)
    {
        var rows = new Dictionary<int, byte[]>();
        return (x, y) =>
        {
            if (!rows.TryGetValue(y, out var row))
            {
                using var bmp = new Bitmap(client.Width, 1, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(client.Left, y, 0, 0, bmp.Size);
                var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, 1), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                row = new byte[bmp.Width * 4];
                Marshal.Copy(data.Scan0, row, 0, row.Length);
                bmp.UnlockBits(data);
                rows[y] = row;
            }
            var i = Math.Clamp(x - client.Left, 0, client.Width - 1) * 4;
            return (row[i + 2], row[i + 1], row[i]);
        };
    }

    /// <summary>The game's pixels through PrintWindow while it is behind another window; null when the capture does not match the client area.</summary>
    private static Func<int, int, (int R, int G, int B)>? PixelsBehind(IntPtr game, PixelRect client)
    {
        using var bmp = Win32.Capture(game);
        if (bmp is null || bmp.Width != client.Width || bmp.Height != client.Height) return null; // not borderless: the frame shifts every point
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var stride = data.Stride;
        var pixels = new byte[stride * bmp.Height];
        Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
        bmp.UnlockBits(data);
        var (w, h) = (bmp.Width, bmp.Height);
        return (x, y) =>
        {
            var i = Math.Clamp(y - client.Top, 0, h - 1) * stride + Math.Clamp(x - client.Left, 0, w - 1) * 4;
            return (pixels[i + 2], pixels[i + 1], pixels[i]);
        };
    }

    /// <summary>
    /// `Glamour.exe --loading-shot <image> [top|bottom slot party size ...]`: draws the marks on a loading-screen shot (as if it were the screen) to
    /// `<image>.marks.png` and writes the plate count and my card to `<image>.txt`. Without cards it marks a made-up duo and 3-stack.
    /// </summary>
    public static void Shot(string path, string[] spec)
    {
        using var image = new Bitmap(path);
        var client = new PixelRect(0, 0, image.Width, image.Height);
        (int, int, int) Pixel(int x, int y) { var p = image.GetPixel(Math.Clamp(x, 0, image.Width - 1), Math.Clamp(y, 0, image.Height - 1)); return (p.R, p.G, p.B); }
        var plates = LoadingLayout.PlatesShown(client, Pixel);
        var mine = LoadingLayout.MyCard(client, Pixel) is { } m ? $"{(m.Top ? "top" : "bottom")} {m.Slot + 1}" : "not found";
        File.WriteAllLines(path + ".txt", [$"{Path.GetFileName(path)} {image.Width}x{image.Height}, fits {LoadingLayout.Fits(client)}, plates {plates} (loading at {LoadingLayout.PlatesNeeded}), my card {mine}"]);
        List<LoadingCard> cards = spec.Length >= 4
            ? [.. spec.Chunk(4).Where(c => c.Length == 4).Select(c => new LoadingCard(c[0] == "top", int.Parse(c[1]), int.Parse(c[2]), int.Parse(c[3])))]
            : [new(true, 0, 0, 2), new(true, 3, 0, 2), new(false, 1, 1, 3), new(false, 2, 1, 3), new(false, 4, 1, 3)];
        using var preview = new Bitmap(image.Width, image.Height, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(preview))
        {
            g.DrawImage(image, 0, 0, image.Width, image.Height);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            LoadingOverlay.Draw(g, new LoadingMarks(client, cards));
        }
        preview.Save(path + ".marks.png", ImageFormat.Png);
    }

    public void Dispose()
    {
        _stop.Set();
        _thread.Join(TimeSpan.FromSeconds(2));
        _overlay.Dispose();
    }
}
