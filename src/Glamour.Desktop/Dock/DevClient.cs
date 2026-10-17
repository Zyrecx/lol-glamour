using Glamour.Core;

namespace Glamour.Desktop.Dock;

/// <summary>
/// Dev only (<c>"fakeClient": "&lt;a 1280x720 champ select screenshot&gt;"</c> in desktop.json): a borderless window showing that picture, which the dock takes for
/// the League client in champ select, with a made-up 5v5 draft board. For checking the panel and the marks on the client's cards with no game running;
/// the panel page then shows its own champ select demo. It is in Glamour's own process, so the window events that make the panel follow at once do not
/// fire for it: the one-second tick moves the panel after a drag.
/// </summary>
internal sealed class DevClient : Form
{
    public DevClient(string picture)
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        Text = "Glamour dev client";
        BackgroundImage = Image.FromFile(picture);
        BackgroundImageLayout = ImageLayout.Stretch;
        Bounds = new Rectangle(100, 100, 1280, 720);
        // Dragged anywhere, so the panel and the marks can be watched following it.
        MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) { ReleaseCapture(); SendMessage(Handle, 0xA1 /* WM_NCLBUTTONDOWN */, 2 /* HTCAPTION */, 0); } };
    }

    /// <summary>Five against five, every enemy picked, me in top.</summary>
    public static ChampSelectBoard Board()
    {
        BoardPlayer P(int cell, int champ, string lane, bool me = false) => new(cell, champ, true, lane, 4, 12, 0, "", me);
        return new ChampSelectBoard("FINALIZATION", 0,
            [P(0, 82, "TOP", true), P(1, 105, "JUNGLE"), P(2, 81, "BOTTOM"), P(3, 67, "TOP"), P(4, 78, "UTILITY")],
            [P(5, 4, ""), P(6, 82, ""), P(7, 110, ""), P(8, 143, ""), P(9, 201, "")],
            [], [], [], 30_000);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hwnd, int msg, int w, int l);
}
