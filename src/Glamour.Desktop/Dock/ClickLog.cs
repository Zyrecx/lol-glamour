using System.Drawing.Imaging;
using System.Windows.Automation;
using Glamour.Core;
using Glamour.Host.Logging;
using Glamour.Host.Panel;
using Microsoft.Extensions.Logging;

namespace Glamour.Desktop.Dock;

/// <summary>
/// Dev only (`"clickLog": true` in desktop.json): the measurements the click layer needs from one real champ select (SPEC "Click layer on the client's
/// roster"). Every press on the client during champ select goes to glamour.log as fractions of the client window, with the window and the UI Automation
/// element under it; each timer stage (PLANNING, BAN_PICK, FINALIZATION) gets one screenshot of the client in logs\shots, BAN_PICK a second one once an
/// enemy has a champion (its pick part), and a match found one (where the Accept button sits). Reading the element makes
/// the client's Chromium build its accessibility tree, a cost the client should not pay outside this test.
/// </summary>
internal sealed class ClickLog(ILogger log)
{
    private readonly HashSet<string> _shotStages = [];

    /// <summary>Called on a worker thread, after the hook, so a slow UI Automation answer never holds the mouse.</summary>
    public void Click(ScreenClick click, IntPtr client, ChampSelectBoard? board)
    {
        if (!Win32.Exists(client)) return;
        var rect = Win32.ClientRect(client);
        if (RosterLayout.Fraction(rect, click.X, click.Y) is not var (fx, fy)) return;
        // Logged even when another window is on top there (our own panel, a popup, or a window the client takes input through), with that window's class.
        var (root, cls) = Win32.WindowAt(click.X, click.Y);
        var on = root == client ? cls : $"{cls} (not the client window)";
        log.LogInformation("Client click {Button} at {Fx:F4},{Fy:F4} of {W}x{H} on {Class}; stage {Stage}, queue {Queue}, {Mode}, cells mine {Mine} theirs {Theirs}; element {Element}",
            click.Button, fx, fy, rect.Width, rect.Height, on, board?.Stage, board?.QueueId, board?.Mode,
            Cells(board?.MyTeam), Cells(board?.TheirTeam), ElementAt(click.X, click.Y));
    }

    private static string Cells(IReadOnlyList<BoardPlayer>? team) =>
        team is null ? "-" : string.Join(",", team.Select(p => $"{p.CellId}:{p.ChampionId}{(p.IsMe ? "*" : "")}"));

    /// <summary>The element under the point and up to five of its ancestors: control type, name, class, rectangle.</summary>
    private static string ElementAt(int x, int y)
    {
        try
        {
            var el = AutomationElement.FromPoint(new System.Windows.Point(x, y));
            var parts = new List<string>();
            var walker = TreeWalker.RawViewWalker;
            for (var i = 0; el is not null && i < 6; i++, el = walker.GetParent(el))
            {
                var c = el.Current;
                var r = c.BoundingRectangle;
                var at = r.IsEmpty || double.IsInfinity(r.Width) ? "none" : $"{(int)r.X},{(int)r.Y},{(int)r.Width},{(int)r.Height}";
                parts.Add($"{c.ControlType.ProgrammaticName.Replace("ControlType.", "")} \"{Short(c.Name)}\" .{Short(c.ClassName)} [{at}]");
            }
            return string.Join(" < ", parts);
        }
        catch (Exception ex) { return "(" + ex.GetType().Name + ")"; }
    }

    private static string Short(string? s) => s is null ? "" : s.Length <= 60 ? s : s[..60] + "…";

    /// <summary>One screenshot per timer stage of a champ select, and one of a match found; a new champ select or match found starts over.</summary>
    public void Stage(IntPtr client, string? phase, ChampSelectBoard? board)
    {
        if (phase == "ReadyCheck") { if (_shotStages.Add("READY_CHECK")) Shoot(client, "READY_CHECK", null); return; }
        if (phase != "ChampSelect" || board is null) { _shotStages.Clear(); return; }
        var stage = board.Stage == "BAN_PICK" && board.TheirTeam.Any(p => p.ChampionId > 0) ? "BAN_PICK-picks" : board.Stage;
        if (_shotStages.Add(stage)) Shoot(client, stage, board);
    }

    private void Shoot(IntPtr client, string stage, ChampSelectBoard? board)
    {
        // Off the dock thread: drawing the client into a bitmap can take a moment.
        _ = Task.Run(async () =>
        {
            await Task.Delay(1500); // let the stage's own animation settle
            try
            {
                using var bmp = Win32.Capture(client);
                if (bmp is null) return;
                var dir = Path.Combine(FileLog.DefaultDir, "shots");
                Directory.CreateDirectory(dir);
                var file = Path.Combine(dir, $"cs-{DateTime.Now:yyyyMMdd-HHmmss}-{stage}.png");
                bmp.Save(file, ImageFormat.Png);
                log.LogInformation("Client shot {File}: {W}x{H}; {Sizes}; queue {Queue}, cells mine {Mine} theirs {Theirs}",
                    file, bmp.Width, bmp.Height, Win32.Sizes(client), board?.QueueId, Cells(board?.MyTeam), Cells(board?.TheirTeam));
            }
            catch (Exception ex) { log.LogWarning(ex, "Client shot failed"); }
        });
    }
}
