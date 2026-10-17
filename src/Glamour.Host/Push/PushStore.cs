using System.Text.Json;

namespace Glamour.Host.Push;

/// <summary>One browser's push subscription (endpoint and keys from <c>PushManager.subscribe</c>), stored against the paired device that made it.</summary>
public sealed record PushSub(string DeviceId, string Endpoint, string P256dh, string Auth);

/// <summary>The VAPID key pair (made once, kept on the PC) and the per-device subscriptions. Survives restarts; a revoked device loses its subscription.</summary>
public sealed class PushStore
{
    private sealed class FileModel { public string? VapidPublic { get; set; } public string? VapidPrivate { get; set; } public List<PushSub> Subs { get; set; } = []; }

    private readonly object _gate = new();
    private readonly string _path;
    private readonly FileModel _m;

    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Glamour", "push.json");

    public PushStore(string path, ILogger<PushStore> log)
    {
        _path = path;
        _m = new FileModel();
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<FileModel>(File.ReadAllText(path)) is { } m) _m = m;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            log.LogWarning(ex, "push.json unreadable, starting empty (devices will have to subscribe again)");
        }
    }

    /// <summary>Keys are created on first use. Changing them would orphan every subscription, so they are never regenerated.</summary>
    public (string Public, string Private) Vapid(Func<(string, string)> generate)
    {
        lock (_gate)
        {
            if (string.IsNullOrEmpty(_m.VapidPublic) || string.IsNullOrEmpty(_m.VapidPrivate))
            {
                (_m.VapidPublic, _m.VapidPrivate) = generate();
                Save();
            }
            return (_m.VapidPublic!, _m.VapidPrivate!);
        }
    }

    public IReadOnlyList<PushSub> All() { lock (_gate) return [.. _m.Subs]; }

    public bool Has(string deviceId) { lock (_gate) return _m.Subs.Exists(s => s.DeviceId == deviceId); }

    /// <summary>A device has one subscription; a new one replaces the old. The same endpoint under another device is dropped (a browser re-paired).</summary>
    public void Set(PushSub sub)
    {
        lock (_gate)
        {
            _m.Subs.RemoveAll(s => s.DeviceId == sub.DeviceId || s.Endpoint == sub.Endpoint);
            _m.Subs.Add(sub);
            Save();
        }
    }

    public void RemoveDevice(string deviceId) { lock (_gate) { if (_m.Subs.RemoveAll(s => s.DeviceId == deviceId) > 0) Save(); } }

    public void RemoveEndpoint(string endpoint) { lock (_gate) { if (_m.Subs.RemoveAll(s => s.Endpoint == endpoint) > 0) Save(); } }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_m));
            File.Move(tmp, _path, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* keep running with the in-memory copy */ }
    }
}
