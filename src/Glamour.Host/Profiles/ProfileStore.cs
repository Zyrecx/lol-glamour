using System.Text.Json;
using System.Text.RegularExpressions;

namespace Glamour.Host.Profiles;

/// <param name="Overrides">This account's settings that differ from the shared defaults (camelCase keys, see <c>SettingsOverlay</c>); null or empty means none.</param>
/// <param name="Card">Icon, level, rank and banner as last read from the League client while this account was signed in; null until then.</param>
/// <param name="RiotId">"Name#Tag" read from the Riot Client when the account was signed in; the default display name.</param>
public sealed record Profile(string Id, string Name, DateTimeOffset? CapturedAt, System.Text.Json.Nodes.JsonObject? Overrides = null, string? RiotId = null, string? Region = null, ProfileCard? Card = null);

public sealed class ProfileData
{
    public List<Profile> Profiles { get; set; } = [];
    /// <summary>The profile whose session is in the Riot Client's live file right now.</summary>
    public string? ActiveId { get; set; }
    /// <summary>Set while an "add account" is open: the live file was moved here and the client shows the login screen.</summary>
    public bool Adding { get; set; }
}

/// <summary>Profile list plus one saved copy of the Riot Client session file per profile. The copies are a disposable cache
/// (they hold live tokens and expire), never the source of truth; a missing copy just means "needs sign-in".</summary>
public sealed partial class ProfileStore(string dir, ILogger<ProfileStore> log)
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static string DefaultDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Glamour", "profiles");
    public static string DefaultLiveSessionFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Riot Games", "Riot Client", "Data", "RiotGamesPrivateSettings.yaml");

    public ProfileData Data { get; private set; } = new();
    public string SessionPath(string id) => Path.Combine(dir, "sessions", id + ".yaml");
    public string AsidePath => Path.Combine(dir, "sessions", "_aside.yaml");
    public string IconPath(string id) => Path.Combine(dir, "icons", id + ".jpg");
    public bool HasSession(string id) => File.Exists(SessionPath(id));
    private string IndexPath => Path.Combine(dir, "profiles.json");

    public void Load()
    {
        try { if (File.Exists(IndexPath)) Data = JsonSerializer.Deserialize<ProfileData>(File.ReadAllText(IndexPath), Options) ?? new(); }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { log.LogWarning(ex, "Could not read {Path}; no profiles", IndexPath); }
    }

    public void Save()
    {
        Directory.CreateDirectory(dir);
        WriteAtomic(IndexPath, JsonSerializer.SerializeToUtf8Bytes(Data, Options));
    }

    /// <summary>Reads while the Riot Client may still hold the file open.</summary>
    public static byte[] ReadShared(string path)
    {
        using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>Temp file in the same folder, then rename: a crash cannot leave a half-written session.</summary>
    public static void WriteAtomic(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>A signed-in session file has a refresh token. Looked for by text, no YAML parser, and the value is never read out.</summary>
    public static bool IsSignedIn(string path) => File.Exists(path) && RefreshToken().IsMatch(System.Text.Encoding.UTF8.GetString(ReadShared(path)));

    /// <summary>Identifies one sign-in across token rotations (the refresh token itself changes every launch).</summary>
    public static string? SessionId(string path) =>
        File.Exists(path) && SessionIdKey().Match(System.Text.Encoding.UTF8.GetString(ReadShared(path))) is { Success: true } m ? m.Groups[1].Value : null;

    [GeneratedRegex(@"^\s*refresh_token:\s*\S", RegexOptions.Multiline)] private static partial Regex RefreshToken();
    [GeneratedRegex(@"^\s*refresh_tokens_session_id:\s*(\S+)", RegexOptions.Multiline)] private static partial Regex SessionIdKey();
}
