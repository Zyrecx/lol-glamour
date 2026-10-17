namespace Glamour.Host.Panel;

/// <summary>
/// Whether the client shows its rune window (recommender or editor) over champ select. The client tells no one: no API event, nothing in UI Automation
/// (tools/popup-watch.mjs, INSIGHTS "Champ select: draft"). Both tabs sit in the same frame, a 2 px gold line (#463714) at x 111-112 and 1168-1169 and on row
/// 81-82 of the 1280x720 base layout (shots popup-1..4, ranked draft). Like <see cref="RosterLayout"/> the frame is the same fraction of any 16:9 window.
/// Pure: the dock reads the screen in <see cref="Spots"/> and asks <see cref="IsGold"/>.
/// </summary>
public static class ClientPopup
{
    // The left side, the right side and the top line in three places. Each was gold in both open shots and in neither closed one; the left side higher
    // up (row 200) also matched a gold line of the closed screen, and lower down (row 600) the frame was not there.
    private static readonly (double X, double Y)[] Base = [(111.5, 400), (1168.5, 250), (300, 81.5), (640, 81.5), (900, 81.5)];
    /// <summary>How far round each spot to look, in base px: the line is 2 px and scaling rounds.</summary>
    private const double Reach = 3;

    /// <summary>The boxes to read, in screen pixels; the popup is open when every one holds a gold pixel. Null when the window is not 16:9.</summary>
    public static IReadOnlyList<PixelRect>? Spots(PixelRect client)
    {
        if (!RosterLayout.Trusted(client)) return null;
        var s = client.Height / 720.0;
        return [.. Base.Select(p => new PixelRect(
            client.Left + (int)Math.Floor((p.X - Reach) * s), client.Top + (int)Math.Floor((p.Y - Reach) * s),
            client.Left + (int)Math.Ceiling((p.X + Reach) * s), client.Top + (int)Math.Ceiling((p.Y + Reach) * s)))];
    }

    /// <summary>The frame's gold, with room for a thin line blended into the dark beside it at a smaller window.</summary>
    public static bool IsGold(byte r, byte g, byte b) =>
        r is >= 0x30 and <= 0x5C && g is >= 0x24 and <= 0x4A && b is >= 0x08 and <= 0x24 && r > g && g > b;
}
