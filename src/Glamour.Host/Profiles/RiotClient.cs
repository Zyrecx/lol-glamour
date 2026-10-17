using System.Diagnostics;
using System.Text.Json;

namespace Glamour.Host.Profiles;

/// <summary>The Riot Client and its games as processes. A seam so tests never kill or launch anything real.</summary>
public interface IRiotClient
{
    /// <summary>Names of the Riot Client, League and VALORANT processes that are running (what a switch would close).</summary>
    IReadOnlyList<string> Running();
    Task StopAsync(CancellationToken ct);
    /// <param name="products">Riot product ids (<see cref="Products"/>) to open again once the Riot Client has signed in; empty opens the Riot Client alone.</param>
    void Start(IReadOnlyList<string> products);
    /// <summary>Who is signed in, from the running Riot Client's local API. Null when the client is not running or does not answer.</summary>
    Task<RiotIdentity?> IdentityAsync(CancellationToken ct);
}

/// <param name="RiotId">Game name and tag, "Name#Tag".</param>
/// <param name="Region">The platform the account plays League on (for example EUN1), when the client says.</param>
public sealed record RiotIdentity(string RiotId, string? Region);

/// <summary>The games a switch closes and opens again: Riot product id and the process that shows it is running.</summary>
public static class Products
{
    public static readonly (string Id, string Process, string Name)[] All = [("league_of_legends", "LeagueClient", "League of Legends"), ("valorant", "VALORANT", "VALORANT")];

    /// <summary>The products whose process is in <paramref name="running"/> (what was open before a switch closed it).</summary>
    public static IReadOnlyList<string> Open(IReadOnlyList<string> running) => All.Where(p => running.Contains(p.Process)).Select(p => p.Id).ToList();

    /// <summary>The open games by the name a player knows them by (for "closes the Riot Client and ..."), not their processes.</summary>
    public static IReadOnlyList<string> OpenNames(IReadOnlyList<string> running) =>
        All.Where(p => running.Any(r => r.StartsWith(p.Process, StringComparison.OrdinalIgnoreCase) || r == p.Name)).Select(p => p.Name).ToList();
}

public sealed class WindowsRiotClient(ILogger<WindowsRiotClient> log) : IRiotClient
{
    // Not Vanguard (vgc, vgtray): it is not part of the sign-in and must stay alone.
    private static readonly string[] Names =
    [
        "RiotClientServices", "RiotClientUx", "RiotClientUxRender", "RiotClientCrashHandler",
        "LeagueClient", "LeagueClientUx", "LeagueClientUxRender", "League of Legends",
        "VALORANT", "VALORANT-Win64-Shipping",
    ];

    /// <summary>One process snapshot for all names (each GetProcessesByName takes a full snapshot of its own; this runs every 2 s while League is closed).</summary>
    public IReadOnlyList<string> Running()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcesses())
            using (p) seen.Add(p.ProcessName);
        return Names.Where(seen.Contains).ToList();
    }

    /// <summary>Kills, never the client's own "Log out": that revokes the session server-side.</summary>
    public async Task StopAsync(CancellationToken ct)
    {
        foreach (var name in Names)
            foreach (var p in Process.GetProcessesByName(name))
                try { p.Kill(); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { log.LogDebug(ex, "Could not kill {Name}", name); }
        for (var i = 0; i < 50 && Running().Count > 0; i++) await Task.Delay(100, ct);
        if (Running() is { Count: > 0 } left) throw new IOException($"Could not close {string.Join(", ", left)}.");
    }

    // The client's local API serves a self-signed certificate on 127.0.0.1 only; the lockfile holds its port and password. INSIGHTS "Riot Client local API".
    private static readonly HttpClient Local = new(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true }) { Timeout = TimeSpan.FromSeconds(2) };
    private static readonly string Lockfile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Riot Games", "Riot Client", "Config", "lockfile");

    /// <summary>A request to the running client's local API, or null when it has no lockfile (not running yet).</summary>
    private static HttpRequestMessage? LocalRequest(HttpMethod method, string path)
    {
        if (!File.Exists(Lockfile)) return null;
        var parts = System.Text.Encoding.UTF8.GetString(ProfileStore.ReadShared(Lockfile)).Split(':');
        if (parts.Length < 4 || !int.TryParse(parts[2], out var port)) return null;
        var req = new HttpRequestMessage(method, $"https://127.0.0.1:{port}{path}");
        req.Headers.Authorization = new("Basic", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("riot:" + parts[3])));
        return req;
    }

    public async Task<RiotIdentity?> IdentityAsync(CancellationToken ct)
    {
        try
        {
            using var req = LocalRequest(HttpMethod.Get, "/riot-client-auth/v1/userinfo");
            if (req is null) return null;
            using var res = await Local.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;
            if (!root.TryGetProperty("acct", out var acct)) return null;
            var name = acct.TryGetProperty("game_name", out var n) ? n.GetString() : null;
            var tag = acct.TryGetProperty("tag_line", out var t) ? t.GetString() : null;
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(tag)) return null;
            var region = root.TryGetProperty("lol", out var lol) && lol.TryGetProperty("cpid", out var c) ? c.GetString() : null;
            return new RiotIdentity($"{name}#{tag}", string.IsNullOrWhiteSpace(region) ? null : region);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or TaskCanceledException or JsonException or UnauthorizedAccessException)
        {
            log.LogDebug(ex, "Riot Client identity unavailable");
            return null;
        }
    }

    public void Start(IReadOnlyList<string> products)
    {
        var installs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Riot Games", "RiotClientInstalls.json");
        var exe = JsonDocument.Parse(File.ReadAllText(installs)).RootElement.TryGetProperty("rc_default", out var p) ? p.GetString() : null;
        if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) throw new FileNotFoundException("Riot Client not found (RiotClientInstalls.json).");
        var start = new ProcessStartInfo(exe) { UseShellExecute = false };
        if (products.Count > 0) { start.ArgumentList.Add("--launch-product=" + products[0]); start.ArgumentList.Add("--launch-patchline=live"); }
        Process.Start(start);
        if (products.Count > 0) _ = EnsureLaunchedAsync(products);
    }

    /// <summary>The launch argument alone is not enough after a session swap: the client tries before the swapped-in sign-in is restored, is refused
    /// (game:play UNAUTHORIZED) and drops it. So once it is signed in, ask it again for each product that did not come up. INSIGHTS "Riot Client local API".</summary>
    private async Task EnsureLaunchedAsync(IReadOnlyList<string> products)
    {
        try
        {
            var until = DateTime.UtcNow.AddSeconds(90);
            while (await IdentityAsync(CancellationToken.None) is null)
            {
                if (DateTime.UtcNow > until) { log.LogInformation("Riot Client not signed in after 90 s: {Products} not opened", string.Join(", ", products)); return; }
                await Task.Delay(1000);
            }
            await Task.Delay(5000); // its own launch, when it works, shows up within a second or two of the sign-in
            var running = Running();
            foreach (var id in products.Where(id => !running.Contains(Products.All.First(x => x.Id == id).Process)))
            {
                using var req = LocalRequest(HttpMethod.Post, $"/product-launcher/v1/products/{id}/patchlines/live");
                if (req is null) return;
                using var res = await Local.SendAsync(req);
                log.LogInformation("Riot Client did not open {Product} itself; asked it: {Status}", id, (int)res.StatusCode);
            }
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or TaskCanceledException or UnauthorizedAccessException)
        {
            log.LogInformation(ex, "Could not ask the Riot Client to open {Products}", string.Join(", ", products));
        }
    }
}
