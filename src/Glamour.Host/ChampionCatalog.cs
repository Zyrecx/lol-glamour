using System.Text.Json;

namespace Glamour.Host;

/// <summary>
/// Positions are lanes (TOP, JUNGLE, MIDDLE, BOTTOM, SUPPORT) and are empty when the lane source was unreachable.
/// Splash (wide) and Portrait (tall loading-screen art) are URL templates: replace <c>{num}</c> with the skin number (skin id % 1000, 0 = base).
/// Null in a list cached before they existed.
/// </summary>
public sealed record ChampionInfo(int Id, string Name, string Icon, IReadOnlyList<string>? Positions = null, string? Splash = null, string? Portrait = null);
public sealed record ChampionList(string Version, IReadOnlyList<ChampionInfo> Champions);

public interface IChampionSource
{
    Task<ChampionList> FetchAsync(CancellationToken ct);
    /// <summary>The newest patch's version alone (a small file), to tell whether the whole list needs fetching again.</summary>
    Task<string> LatestVersionAsync(CancellationToken ct);
}

/// <summary>
/// Champion names and icons from Riot's public Data Dragon CDN. It needs no League client, so the pickers also work before the client is open.
/// Lanes come from Meraki Analytics' champion file (Data Dragon only has classes, not lanes); if that fails the list just has no lanes.
/// </summary>
public sealed class DataDragonChampionSource(IHttpClientFactory http) : IChampionSource
{
    private const string Base = "https://ddragon.leagueoflegends.com";
    private const string LanesUrl = "https://cdn.merakianalytics.com/riot/lol/resources/latest/en-US/champions.json";

    public async Task<ChampionList> FetchAsync(CancellationToken ct)
    {
        var client = http.CreateClient("ddragon");
        var version = await LatestVersionAsync(ct);
        using var data = JsonDocument.Parse(await client.GetStringAsync($"{Base}/cdn/{version}/data/en_US/champion.json", ct));
        return Parse(version, data.RootElement.GetProperty("data"), await FetchLanesAsync(client, ct));
    }

    public async Task<string> LatestVersionAsync(CancellationToken ct)
    {
        using var versions = JsonDocument.Parse(await http.CreateClient("ddragon").GetStringAsync($"{Base}/api/versions.json", ct));
        return versions.RootElement[0].GetString() ?? throw new InvalidDataException("no version");
    }

    private static async Task<Dictionary<int, string[]>?> FetchLanesAsync(HttpClient client, CancellationToken ct)
    {
        try { return ParseLanes(await client.GetStringAsync(LanesUrl, ct)); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException && !ct.IsCancellationRequested) { return null; }
    }

    /// <summary>Meraki entries carry the numeric champion `id` and `positions`.</summary>
    public static Dictionary<int, string[]> ParseLanes(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var lanes = new Dictionary<int, string[]>();
        foreach (var p in doc.RootElement.EnumerateObject())
            if (p.Value.TryGetProperty("id", out var id) && id.TryGetInt32(out var n) && p.Value.TryGetProperty("positions", out var pos) && pos.ValueKind == JsonValueKind.Array)
                lanes[n] = [.. pos.EnumerateArray().Select(x => x.GetString()).OfType<string>()];
        return lanes;
    }

    /// <summary>Each entry has `key` (the numeric champion id as text), `id` (the file name) and `name`.</summary>
    public static ChampionList Parse(string version, JsonElement data, IReadOnlyDictionary<int, string[]>? lanes = null) => new(version,
        [.. data.EnumerateObject()
            .Select(p => (id: int.Parse(p.Value.GetProperty("key").GetString()!), file: p.Value.GetProperty("id").GetString()!) is var c
                ? new ChampionInfo(c.id, p.Value.GetProperty("name").GetString()!, $"{Base}/cdn/{version}/img/champion/{c.file}.png",
                    lanes is not null && lanes.TryGetValue(c.id, out var l) ? l : [],
                    $"{Base}/cdn/img/champion/splash/{c.file}_{{num}}.jpg", $"{Base}/cdn/img/champion/loading/{c.file}_{{num}}.jpg")
                : throw new InvalidDataException())
            .OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)]);
}

public static class ClientLanes
{
    /// <summary>`GET /lol-perks/v1/recommended-champion-positions`, the data behind the client's own lane filter: `{"&lt;id&gt;":{"recommendedPositions":["MIDDLE","UTILITY"]}}`. The client says UTILITY for support.</summary>
    public static Dictionary<int, string[]> Parse(System.Text.Json.Nodes.JsonNode? json)
    {
        var lanes = new Dictionary<int, string[]>();
        if (json is not System.Text.Json.Nodes.JsonObject map) return lanes;
        foreach (var (key, value) in map)
            if (int.TryParse(key, out var id) && value?["recommendedPositions"] is System.Text.Json.Nodes.JsonArray a)
                lanes[id] = [.. a.Select(x => x?.GetValue<string>()).OfType<string>().Select(p => p == "UTILITY" ? "SUPPORT" : p)];
        return lanes;
    }

    /// <summary>The client's lanes win over the downloaded ones for every champion it knows.</summary>
    public static ChampionList Overlay(ChampionList list, IReadOnlyDictionary<int, string[]> lanes) =>
        lanes.Count == 0 ? list : list with { Champions = [.. list.Champions.Select(c => lanes.TryGetValue(c.Id, out var l) ? c with { Positions = l } : c)] };
}

/// <summary>
/// Keeps the list in memory and on disk, so a failed refresh or a missing connection still returns the last good list. The patch version is checked
/// every hour (a small file) and the whole list fetched again only when it changed, or after 12 hours for lane updates: a new champion shows within
/// an hour of the patch, and the matchup sources, which take their patch from this list, move to it as fast.
/// </summary>
public sealed class ChampionCatalog(IChampionSource source, TimeProvider time, string path, ILogger<ChampionCatalog> log)
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(12);
    private static readonly TimeSpan CheckEvery = TimeSpan.FromHours(1);
    private static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(5);
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ChampionList? _list;
    private DateTimeOffset _fetched;
    private DateTimeOffset _checked;

    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Glamour", "champions.json");

    public async Task<ChampionList?> GetAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var now = time.GetUtcNow();
            if (_list is not null && now - _checked < CheckEvery) return _list;
            try
            {
                if (_list is not null && now - _fetched < MaxAge && await source.LatestVersionAsync(ct) == _list.Version)
                {
                    _checked = now;
                    return _list;
                }
                _list = await source.FetchAsync(ct);
                _fetched = _checked = now;
                Save(_list);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidDataException or KeyNotFoundException or FormatException or InvalidOperationException && !ct.IsCancellationRequested)
            {
                log.LogWarning(ex, "Could not refresh the champion list");
                _list ??= Load();
                _checked = now - CheckEvery + RetryAfter; // retry in 5 minutes, not on every request
            }
            return _list;
        }
        finally { _gate.Release(); }
    }

    private void Save(ChampionList list)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(list));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { log.LogWarning(ex, "Could not cache the champion list at {Path}", path); }
    }

    private ChampionList? Load()
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<ChampionList>(File.ReadAllText(path), Json) : null; }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }
}
