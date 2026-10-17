using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Glamour.Core;

/// <summary>Keeps the shared default <see cref="GlamourSettings"/> in a JSON file (per-account overrides live with the profiles, never here).
/// A missing or corrupt file leaves the defaults (auto-lock stays off).</summary>
public sealed class SettingsStore(GlamourSettings live, string path, ILogger<SettingsStore> log)
{
    public GlamourSettings Defaults => live;

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    private readonly object _gate = new();

    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Glamour", "settings.json");

    public void Load()
    {
        try
        {
            if (!File.Exists(path)) return;
            var loaded = JsonSerializer.Deserialize<GlamourSettings>(File.ReadAllText(path), Options);
            if (loaded is not null) live.CopyFrom(loaded);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            log.LogWarning(ex, "Could not read {Path}; using defaults", path);
            // The next save would overwrite it: keep the unreadable file so the user's choices can still be recovered by hand.
            if (ex is JsonException) try { File.Copy(path, path + ".corrupt", overwrite: true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>Writes to a temp file, flushes it to disk and renames, so a crash or power cut cannot leave a truncated or empty file.</summary>
    public void Save()
    {
        try
        {
            lock (_gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var tmp = path + ".tmp";
                using (var file = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(file, live, Options);
                    file.Flush(flushToDisk: true);
                }
                File.Move(tmp, path, overwrite: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogError(ex, "Could not save settings to {Path}", path);
        }
    }
}
