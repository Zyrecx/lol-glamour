using System.Text.Json;

namespace Glamour.Host.Network;

/// <summary>Settings of the host itself (not of an account): whether phones may connect at all, whether they are served the web UI, whether HTTPS is required. Survives restarts.</summary>
public sealed class HostSettings
{
    private sealed class FileModel
    {
        public bool WebUiEnabled { get; set; } = true;
        public bool RequireHttps { get; set; }
        public bool AllowLan { get; set; }
        public bool Telemetry { get; set; } = true;
        public bool TourDone { get; set; }
        public UpdateMode Updates { get; set; } = UpdateMode.Automatic;
    }

    private readonly string _path;
    private volatile bool _webUiEnabled = true, _requireHttps, _allowLan, _telemetry = true, _tourDone;
    private volatile UpdateMode _updates = UpdateMode.Automatic;

    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Glamour", "host.json");

    public HostSettings(string path, ILogger<HostSettings> log)
    {
        _path = path;
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<FileModel>(File.ReadAllText(path), Json) is { } m) { _webUiEnabled = m.WebUiEnabled; _requireHttps = m.RequireHttps; _allowLan = m.AllowLan; _telemetry = m.Telemetry; _tourDone = m.TourDone; _updates = m.Updates; }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            log.LogWarning(ex, "host.json unreadable, using defaults");
        }
    }

    /// <summary>When off, non-loopback requests get the API only (pair, login, everything else), not the pages. The PC always gets the pages.</summary>
    public bool WebUiEnabled
    {
        get => _webUiEnabled;
        set { _webUiEnabled = value; Save(); }
    }

    /// <summary>When on (and the CA exists), the LAN may only use plain HTTP for the certificate install page; the API and pages need HTTPS.</summary>
    public bool RequireHttps
    {
        get => _requireHttps;
        set { _requireHttps = value; Save(); }
    }

    /// <summary>Whether phones on the network may reach this PC ("Allow phone access"). Read when the host starts, so changing it needs a restart of the host's listeners.</summary>
    public bool AllowLan
    {
        get => _allowLan;
        set { _allowLan = value; Save(); }
    }

    /// <summary>"Help improve Glamour": errors and usage counts are sent (SPEC "Publishing"). On unless the user turned it off.</summary>
    public bool Telemetry
    {
        get => _telemetry;
        set { _telemetry = value; Save(); }
    }

    /// <summary>The first-run tour was finished or skipped on this PC.</summary>
    public bool TourDone
    {
        get => _tourDone;
        set { _tourDone = value; Save(); }
    }

    /// <summary>Settings > Updates.</summary>
    public UpdateMode Updates
    {
        get => _updates;
        set { _updates = value; Save(); }
    }

    /// <summary>The update choice without a host (the desktop app reads it before anything starts). Automatic when unreadable.</summary>
    public static UpdateMode ReadUpdates(string path)
    {
        try { return File.Exists(path) && JsonSerializer.Deserialize<FileModel>(File.ReadAllText(path), Json) is { } m ? m.Updates : UpdateMode.Automatic; }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return UpdateMode.Automatic; }
    }

    private static readonly JsonSerializerOptions Json = new() { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(new FileModel { WebUiEnabled = _webUiEnabled, RequireHttps = _requireHttps, AllowLan = _allowLan, Telemetry = _telemetry, TourDone = _tourDone, Updates = _updates }, Json));
        File.Move(tmp, _path, overwrite: true);
    }
}

/// <summary>Settings > Updates (SPEC "Publishing"). Automatic: downloaded in the background, put in when Glamour exits. Notify: downloaded, put in when the user presses Restart. Off: never looked for.</summary>
public enum UpdateMode { Automatic, Notify, Off }
