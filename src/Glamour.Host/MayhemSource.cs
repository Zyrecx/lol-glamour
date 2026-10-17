using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Glamour.Core;

namespace Glamour.Host;

/// <summary>
/// Mayhem (queue 2400) champion numbers for bench advice (INSIGHTS "Matchup data"): win rates from arammayhem.com's MCP `get_tier_list` (no key, 60 calls a
/// minute, all champions in pages of 30) while <see cref="GlamourSettings.MayhemWinRates"/> is on and its patch is the client's; otherwise u.gg's tier letters
/// (one file per patch, the previous patch's while the current one has none). Both cached on disk and fetched again once a day; a failure is "no data" (null).
/// </summary>
public sealed class MayhemSource(IHttpClientFactory http, ChampionCatalog champions, GlamourSettings settings, string cacheDir, ILogger<MayhemSource> log) : IModeWinRateSource
{
    private const string McpUrl = "https://arammayhem.com/mcp";
    private const string TiersUrl = "https://static.bigbrain.gg/custom-aram-mayhem/{0}/tierlist-champions-{0}.json";
    private const int Page = 30;

    // The panel and the phone ask at once; one fetch at a time keeps the site's call budget and the cache files whole.
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<ModeWinRates?> GetModeAsync(string mode, CancellationToken ct)
    {
        if (mode != "mayhem") return null;
        await gate.WaitAsync(ct);
        try
        {
            if (await champions.GetAsync(ct) is not { } list || LolalyticsMatchupSource.Patches(list.Version).ToList() is not { Count: > 0 } patches) return null;
            if (settings.MayhemWinRates && await WinRatesAsync(list, patches[0]) is { } rates) return rates;
            foreach (var patch in patches)
                if (await TiersAsync(patch) is { } tiers) return tiers;
            return null;
        }
        finally { gate.Release(); }
    }

    /// <summary>arammayhem.com's list, when its patch is the client's. Champions are named by their Data Dragon key, matched to the icon's file name.</summary>
    private async Task<ModeWinRates?> WinRatesAsync(ChampionList list, string clientPatch)
    {
        var json = await CachedAsync(Path.Combine(cacheDir, "mayhem", "arammayhem.json"), FetchWinRatesAsync);
        if (json is null || ParseWinRates(json, list) is not { } parsed) return null;
        if (!SamePatch(parsed.SitePatch, clientPatch))
        {
            log.LogInformation("Mayhem win rates are for {Site}, the client is on {Client}: tiers instead", parsed.SitePatch, clientPatch);
            return null;
        }
        return new ModeWinRates("mayhem", clientPatch, parsed.Rows, "arammayhem.com");
    }

    private async Task<ModeWinRates?> TiersAsync(string patch)
    {
        var json = await CachedAsync(Path.Combine(cacheDir, patch, "ugg-mayhem-tiers.json"), () => FetchTiersAsync(patch));
        return json is not null && ParseTiers(json) is { Count: > 0 } tiers ? new ModeWinRates("mayhem", patch, [], "u.gg", tiers) : null;
    }

    /// <summary>The cached file, fetched again when missing or a day old; a failed fetch keeps (and touches) the cached one.</summary>
    private async Task<string?> CachedAsync(string file, Func<Task<string?>> fetch)
    {
        var cached = File.Exists(file) ? File.ReadAllText(file) : null;
        if (cached is not null && DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < LolalyticsMatchupSource.Refresh) return cached;
        string? fresh = null;
        try { fresh = await fetch(); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            log.LogWarning(ex, "Could not fetch Mayhem data for {File}", Path.GetFileName(file));
        }
        if (fresh is null)
        {
            if (cached is not null) Touch(file);
            return cached;
        }
        Save(file, fresh);
        return fresh;
    }

    /// <summary>Every page of `get_tier_list`, kept as {patch, entries: [{championId, winRate}]}; null when an answer is not one.</summary>
    private async Task<string?> FetchWinRatesAsync()
    {
        var client = http.CreateClient("mayhem");
        string? patch = null;
        var entries = new JsonArray();
        for (var offset = 0; ; offset += Page)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, McpUrl)
            {
                Content = JsonContent.Create(new { jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name = "get_tier_list", arguments = new { limit = Page, offset } } }),
            };
            request.Headers.Accept.ParseAdd("application/json");
            request.Headers.Accept.ParseAdd("text/event-stream");
            using var response = await client.SendAsync(request);
            response.EnsureSuccessStatusCode();
            if (PageOf(await response.Content.ReadAsStringAsync()) is not { } page) return null;
            patch ??= page["patch"]?.GetValue<string>();
            var got = page["entries"] as JsonArray ?? [];
            foreach (var e in got) entries.Add(new JsonObject { ["championId"] = e?["championId"]?.DeepClone(), ["winRate"] = e?["winRate"]?.DeepClone() });
            var total = page["total"]?.GetValue<int>() ?? 0;
            if (got.Count == 0 || offset + Page >= total) break;
        }
        return patch is null || entries.Count == 0 ? null : new JsonObject { ["patch"] = patch, ["entries"] = entries }.ToJsonString();
    }

    /// <summary>One MCP answer (a JSON body or an event stream's `data:` line) to its `structuredContent`.</summary>
    public static JsonObject? PageOf(string body)
    {
        var line = body.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("data:", StringComparison.Ordinal));
        var root = JsonNode.Parse(line is null ? body : line[5..]);
        return root?["result"]?["structuredContent"] as JsonObject;
    }

    private async Task<string?> FetchTiersAsync(string patch)
    {
        var site = patch.Replace('.', '_');
        using var response = await http.CreateClient("mayhem").GetAsync(string.Format(CultureInfo.InvariantCulture, TiersUrl, site));
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null; // the new patch's file comes days after it
        response.EnsureSuccessStatusCode();
        var text = await response.Content.ReadAsStringAsync();
        return ParseTiers(text) is { Count: > 0 } ? text : null;
    }

    /// <summary>A kept win rate list: rows for every champion found in the catalog by key, and the site's patch ("26.20").</summary>
    public static (string SitePatch, IReadOnlyList<MatchupRow> Rows)? ParseWinRates(string json, ChampionList list)
    {
        var root = JsonNode.Parse(json);
        if (root?["patch"]?.GetValue<string>() is not { } patch || root["entries"] is not JsonArray entries) return null;
        var byKey = list.Champions.GroupBy(c => KeyOf(c)).ToDictionary(g => g.Key, g => g.First().Id, StringComparer.OrdinalIgnoreCase);
        var rows = new List<MatchupRow>();
        foreach (var e in entries)
            if (e?["championId"]?.GetValue<string>() is { } key && byKey.TryGetValue(key, out var id) && Percent(e["winRate"]?.GetValue<string>()) is { } wr)
                rows.Add(new MatchupRow(id, wr, 0));
        return rows.Count == 0 ? null : (patch, rows);
    }

    /// <summary>u.gg's file: `tiers` maps a letter to champion ids.</summary>
    public static Dictionary<int, string>? ParseTiers(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("tiers", out var tiers) || tiers.ValueKind != JsonValueKind.Object) return null;
        var map = new Dictionary<int, string>();
        foreach (var t in tiers.EnumerateObject())
            if (ModeTiers.Order.Contains(t.Name) && t.Value.ValueKind == JsonValueKind.Array)
                foreach (var id in t.Value.EnumerateArray())
                    if (id.TryGetInt32(out var n)) map.TryAdd(n, t.Name);
        return map;
    }

    /// <summary>
    /// The site names patches by year ("26.20"), the client by season ("16.20"): the same patch when the minor numbers match and the majors are equal or ten
    /// apart. Anything else counts as another patch.
    /// </summary>
    public static bool SamePatch(string site, string client)
    {
        static (int, int)? Of(string p) => p.Split('.') is [var a, var b, ..] && int.TryParse(a, out var x) && int.TryParse(b, out var y) ? (x, y) : null;
        return Of(site) is (var sMajor, var sMinor) && Of(client) is (var cMajor, var cMinor) && sMinor == cMinor && (sMajor == cMajor || sMajor == cMajor + 10);
    }

    /// <summary>The Data Dragon key, the icon's file name ("MonkeyKing", "KSante").</summary>
    private static string KeyOf(ChampionInfo c) => Path.GetFileNameWithoutExtension(new Uri(c.Icon).AbsolutePath);

    private static double? Percent(string? text) =>
        text is not null && double.TryParse(text.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    private void Save(string file, string text)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var temp = $"{file}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(temp, text);
            File.Move(temp, file, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { log.LogWarning(ex, "Could not cache Mayhem data at {Path}", file); }
    }

    private static void Touch(string file)
    {
        try { File.SetLastWriteTimeUtc(file, DateTime.UtcNow); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>Which source answers which mode: ARAM from Lolalytics, Mayhem from <see cref="MayhemSource"/>.</summary>
public sealed class ModeWinRateSources(LolalyticsMatchupSource aram, MayhemSource mayhem) : IModeWinRateSource
{
    public Task<ModeWinRates?> GetModeAsync(string mode, CancellationToken ct) => mode switch
    {
        "aram" => aram.GetModeAsync(mode, ct),
        "mayhem" => mayhem.GetModeAsync(mode, ct),
        _ => Task.FromResult<ModeWinRates?>(null),
    };
}
