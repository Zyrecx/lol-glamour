using System.Text.Json.Nodes;
using Glamour.Host.Panel;

namespace Glamour.Desktop.Dock;

/// <summary>The desktop-only choices: whether the side panel docks to the League client (off until it has been seen working on a real client, then flip the default) and which side it takes. Kept in its own small file next to settings.json.</summary>
internal static class DockPrefs
{
    private static string PathOf => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Glamour", "desktop.json");

    public static bool Enabled
    {
        get => Read()?["dockPanel"] is JsonValue v && v.TryGetValue(out bool on) && on;
        set => Write("dockPanel", value);
    }

    /// <summary>Whether the panel opens by itself in champ select (on unless turned off); else it stays a rail until the user opens it.</summary>
    public static bool AutoOpen
    {
        get => Read()?["panelAutoOpen"] is not JsonValue v || !v.TryGetValue(out bool on) || on;
        set => Write("panelAutoOpen", value);
    }

    /// <summary>Dev only, set by hand: log every press on the client in champ select and screenshot each stage (<see cref="ClickLog"/>). Read on every tick, so no restart.</summary>
    public static bool ClickLog => Read()?["clickLog"] is JsonValue v && v.TryGetValue(out bool on) && on;

    /// <summary>Dev only, set by hand: a champ select screenshot to show as a stand-in League client (<see cref="DevClient"/>). Read at start.</summary>
    public static string? FakeClient => (Read()?["fakeClient"] as JsonValue)?.TryGetValue(out string? s) == true && File.Exists(s) ? s : null;

    /// <summary>Dev only, set by hand: README shots (made-up names in pages, the shot keys; Shots.ShotKeys). Read at start.</summary>
    public static bool ShotMode => Read()?["shotMode"] is JsonValue v && v.TryGetValue(out bool on) && on;

    /// <summary>null: whichever side has room (right first).</summary>
    public static PanelSide? Side
    {
        get => (Read()?["panelSide"] as JsonValue)?.TryGetValue(out string? s) == true && Enum.TryParse<PanelSide>(s, ignoreCase: true, out var side) ? side : null;
        set => Write("panelSide", value?.ToString().ToLowerInvariant() ?? "auto");
    }

    private static readonly object Gate = new();
    private static (DateTime Stamp, string Text)? _cached; // the dock asks every tick: read the file again only when it changed

    private static JsonObject? Read()
    {
        try
        {
            var stamp = File.GetLastWriteTimeUtc(PathOf);
            string text;
            lock (Gate)
            {
                if (_cached is not { } c || c.Stamp != stamp) _cached = (stamp, File.ReadAllText(PathOf));
                text = _cached.Value.Text;
            }
            return JsonNode.Parse(text) as JsonObject;
        }
        catch (Exception) { return null; }
    }

    private static void Write(string key, JsonNode? value)
    {
        try
        {
            var all = Read() ?? new JsonObject();
            all[key] = value;
            Directory.CreateDirectory(Path.GetDirectoryName(PathOf)!);
            var tmp = PathOf + ".tmp";
            File.WriteAllText(tmp, all.ToJsonString());
            File.Move(tmp, PathOf, overwrite: true);
        }
        catch (Exception) { /* the choice then lasts until the app exits */ }
    }
}
