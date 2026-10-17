using System.Security.Cryptography;
using System.Text.Json;

namespace Glamour.Host.Auth;

public sealed class DeviceRecord
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>SHA-256 of the session token. The token itself is never stored.</summary>
    public string TokenHash { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastSeen { get; set; }
}

public sealed record DeviceInfo(string Id, string Name, DateTimeOffset CreatedAt, DateTimeOffset LastSeen);
public sealed record Issued(DeviceInfo Device, string Token);
public sealed record Authenticated(DeviceInfo Device, bool RefreshCookie);
public sealed record PairingCode(string Secret, DateTimeOffset ExpiresAt);

/// <summary>
/// Paired devices (one random token each, so any number of phones and browsers), one-time pairing secrets for the QR, and an optional password.
/// Only hashes reach the disk. Session lifetime slides: every use renews it.
/// </summary>
public sealed class AuthService
{
    public const string CookieName = "glamour_session";
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromDays(30);
    public static readonly TimeSpan PairingLifetime = TimeSpan.FromMinutes(2);
    public const int MaxDevices = 20, MaxPendingPairings = 5, MinPasswordLength = 4, MaxPasswordLength = 12;

    private const int Iterations = 210_000;
    private static readonly TimeSpan CookieRefreshAfter = TimeSpan.FromDays(1), SaveLastSeenEvery = TimeSpan.FromMinutes(10);
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private sealed class FileModel
    {
        public string? PasswordSalt { get; set; }
        public string? PasswordHash { get; set; }
        public List<DeviceRecord> Devices { get; set; } = [];
    }

    private readonly TimeProvider _time;
    private readonly string _path;
    private readonly ILogger<AuthService> _log;
    private readonly object _gate = new();
    private readonly List<DeviceRecord> _devices;
    private readonly Dictionary<string, DateTimeOffset> _pairings = []; // hash of secret -> expiry
    private readonly Dictionary<string, CancellationTokenSource> _revocation = [];
    private string? _passwordSalt, _passwordHash;
    private DateTimeOffset _lastSave;

    public AuthService(TimeProvider time, string path, ILogger<AuthService> log)
    {
        _time = time;
        _path = path;
        _log = log;
        var file = Load();
        _devices = file.Devices;
        _passwordSalt = file.PasswordSalt;
        _passwordHash = file.PasswordHash;
        _lastSave = time.GetUtcNow();
    }

    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Glamour", "auth.json");

    public bool HasPassword { get { lock (_gate) return _passwordHash is not null; } }

    // ---- pairing (QR) ----

    public PairingCode CreatePairing()
    {
        var secret = RandomToken(16);
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            foreach (var k in _pairings.Where(p => p.Value <= now).Select(p => p.Key).ToArray()) _pairings.Remove(k);
            while (_pairings.Count >= MaxPendingPairings)
                _pairings.Remove(_pairings.MinBy(p => p.Value).Key);
            var expires = now + PairingLifetime;
            _pairings[Hash(secret)] = expires;
            return new PairingCode(secret, expires);
        }
    }

    /// <summary>Spends a pairing secret (single use) and signs the caller in. Null when the secret is unknown, used or expired.</summary>
    public Issued? Redeem(string secret, string? existingToken, string? name, string? userAgent)
    {
        var cancel = new List<CancellationTokenSource>();
        try
        {
            lock (_gate)
            {
                var now = _time.GetUtcNow();
                if (!_pairings.Remove(Hash(secret), out var expires) || expires <= now) return null;
                return IssueLocked(existingToken, name, userAgent, cancel);
            }
        }
        finally { Cancel(cancel); }
    }

    // ---- password ----

    public bool SetPassword(string password)
    {
        if (password.Length is < MinPasswordLength or > MaxPasswordLength || !password.All(char.IsAsciiDigit)) return false;
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Pbkdf2(password, salt);
        lock (_gate)
        {
            _passwordSalt = Convert.ToBase64String(salt);
            _passwordHash = Convert.ToBase64String(hash);
            SaveLocked();
        }
        return true;
    }

    public void ClearPassword()
    {
        lock (_gate)
        {
            _passwordSalt = _passwordHash = null;
            SaveLocked();
        }
    }

    public Issued? Login(string password, string? existingToken, string? name, string? userAgent)
    {
        string? salt, expected;
        lock (_gate) (salt, expected) = (_passwordSalt, _passwordHash);
        // Always do the hashing work, so "no password set" and "wrong password" take the same time.
        var actual = Pbkdf2(password, salt is null ? new byte[16] : Convert.FromBase64String(salt));
        if (expected is null || !CryptographicOperations.FixedTimeEquals(actual, Convert.FromBase64String(expected))) return null;

        var cancel = new List<CancellationTokenSource>();
        try
        {
            lock (_gate) return IssueLocked(existingToken, name, userAgent, cancel);
        }
        finally { Cancel(cancel); }
    }

    // ---- sessions ----

    public Authenticated? Authenticate(string token)
    {
        var hash = Hash(token);
        var cancel = new List<CancellationTokenSource>();
        try
        {
            lock (_gate)
            {
                var d = _devices.Find(x => x.TokenHash == hash);
                if (d is null) return null;
                var now = _time.GetUtcNow();
                if (now - d.LastSeen > SessionLifetime)
                {
                    Drop(d, cancel);
                    SaveLocked();
                    return null;
                }
                var refresh = now - d.LastSeen > CookieRefreshAfter;
                d.LastSeen = now;
                if (refresh || now - _lastSave > SaveLastSeenEvery) SaveLocked();
                return new Authenticated(Info(d), refresh);
            }
        }
        finally { Cancel(cancel); }
    }

    public IReadOnlyList<DeviceInfo> Devices()
    {
        lock (_gate) return _devices.OrderByDescending(d => d.LastSeen).Select(Info).ToList();
    }

    public bool Revoke(string id)
    {
        var cancel = new List<CancellationTokenSource>();
        try
        {
            lock (_gate)
            {
                var d = _devices.Find(x => x.Id == id);
                if (d is null) return false;
                Drop(d, cancel);
                SaveLocked();
                return true;
            }
        }
        finally { Cancel(cancel); }
    }

    public void RevokeAll()
    {
        var cancel = new List<CancellationTokenSource>();
        try
        {
            lock (_gate)
            {
                foreach (var d in _devices.ToArray()) Drop(d, cancel);
                SaveLocked();
            }
        }
        finally { Cancel(cancel); }
    }

    /// <summary>Cancelled when this device is revoked, so long-lived streams (SSE) can end at once.</summary>
    public CancellationToken RevocationToken(string deviceId)
    {
        lock (_gate)
        {
            // Already revoked (or never existed): hand back a cancelled token so the stream ends at once and no entry is leaked.
            if (!_devices.Exists(d => d.Id == deviceId)) return new CancellationToken(true);
            if (!_revocation.TryGetValue(deviceId, out var cts)) _revocation[deviceId] = cts = new CancellationTokenSource();
            return cts.Token;
        }
    }

    // ---- internals ----

    /// <summary>Signs in a device. A browser that already holds a valid session keeps its slot (token rotated) instead of adding a duplicate.</summary>
    private Issued IssueLocked(string? existingToken, string? name, string? userAgent, List<CancellationTokenSource> cancel)
    {
        var now = _time.GetUtcNow();
        var token = RandomToken(32);
        var existingHash = existingToken is null ? null : Hash(existingToken);
        var device = existingHash is null ? null : _devices.Find(x => x.TokenHash == existingHash && now - x.LastSeen <= SessionLifetime);
        if (device is null)
        {
            device = new DeviceRecord { Id = Guid.NewGuid().ToString("N")[..12], CreatedAt = now };
            _devices.Add(device);
        }
        device.TokenHash = Hash(token);
        device.LastSeen = now;
        device.Name = DeviceName(name, userAgent, device.Name);

        while (_devices.Count > MaxDevices)
            Drop(_devices.Where(d => d != device).MinBy(d => d.LastSeen)!, cancel);
        SaveLocked();
        return new Issued(Info(device), token);
    }

    /// <summary>Raised (under the auth lock, so handlers must be quick and never call back in) whenever a device stops existing: revoked, signed out or evicted.</summary>
    public event Action<string>? DeviceRemoved;

    private void Drop(DeviceRecord d, List<CancellationTokenSource> cancel)
    {
        _devices.Remove(d);
        DeviceRemoved?.Invoke(d.Id);
        if (_revocation.Remove(d.Id, out var cts)) cancel.Add(cts);
    }

    private static void Cancel(List<CancellationTokenSource> list)
    {
        foreach (var c in list) { c.Cancel(); c.Dispose(); }
    }

    private static DeviceInfo Info(DeviceRecord d) => new(d.Id, d.Name, d.CreatedAt, d.LastSeen);

    /// <summary>Name the user typed, else the old name, else a guess from the User-Agent.</summary>
    public static string DeviceName(string? given, string? userAgent, string previous)
    {
        var clean = new string((given ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (clean.Length > 0) return clean.Length > 40 ? clean[..40] : clean;
        if (previous.Length > 0) return previous;
        var ua = userAgent ?? "";
        var os = ua.Contains("iPhone") ? "iPhone" : ua.Contains("iPad") ? "iPad" : ua.Contains("Android") ? "Android"
            : ua.Contains("Windows") ? "Windows" : ua.Contains("Mac OS") ? "Mac" : ua.Contains("Linux") ? "Linux" : "Device";
        var browser = ua.Contains("Edg/") ? "Edge" : ua.Contains("Firefox/") ? "Firefox" : ua.Contains("Chrome/") || ua.Contains("CriOS") ? "Chrome"
            : ua.Contains("Safari/") ? "Safari" : "";
        return browser.Length > 0 ? $"{browser} on {os}" : os;
    }

    private static string RandomToken(int bytes) => Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static string Hash(string token) => Convert.ToBase64String(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token)));
    private static byte[] Pbkdf2(string password, byte[] salt) => Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);

    private FileModel Load()
    {
        try
        {
            if (File.Exists(_path)) return JsonSerializer.Deserialize<FileModel>(File.ReadAllText(_path), Json) ?? new FileModel();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "Could not read {Path}; all devices must pair again", _path);
        }
        return new FileModel();
    }

    private void SaveLocked()
    {
        _lastSave = _time.GetUtcNow();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(new FileModel { PasswordSalt = _passwordSalt, PasswordHash = _passwordHash, Devices = _devices }, Json));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogError(ex, "Could not save {Path}", _path);
        }
    }
}
