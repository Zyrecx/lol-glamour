using System.Text.Json;
using System.Text.Json.Nodes;
using Glamour.Core;

namespace Glamour.Host;

/// <summary>Everything the augment tags need for one champion: the Mayhem pool (names to read cards by) and aramkit's tables.</summary>
public sealed record AugmentData(IReadOnlyList<AugmentInfo> Pool, AugmentTable? Champion, AugmentTable? Global);

/// <summary>
/// Mayhem augment numbers for the in-game augment tags (SPEC "PC app direction" 5, INSIGHTS "Matchup data"): aramkit's public data files
/// (`data.aramkit.com/data/versions.json`, then `{dataPath}/stats/all/champion-details/{id}.json` and `augment-rankings.json`), and the Mayhem pool from
/// CommunityDragon (`augment-lists.json` modes KIWI and KIWI_JADE joined to `cherry-augments.json` by name id; Arena's augments share the file). Each file is fetched at most
/// once a day (a data path's files never change, so once), when a Mayhem game starts, cached on disk; a failure is "no data". Early in a patch a champion with under
/// <see cref="ThinPatch"/> augment picks (about a quarter as many games) is read from the previous data version instead.
/// </summary>
public sealed class AugmentDataSource(IHttpClientFactory http, ChampionCatalog champions, string cacheDir, ILogger<AugmentDataSource> log)
{
    private const string Aramkit = "https://data.aramkit.com";
    private const string Cdragon = "https://raw.communitydragon.org/{0}/plugins/rcp-be-lol-game-data/global/default/v1/{1}";
    public const int ThinPatch = 20_000;

    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<AugmentData?> GetAsync(int championId, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (await champions.GetAsync(ct) is not { } list || LolalyticsMatchupSource.Patches(list.Version).FirstOrDefault() is not { } patch) return null;
            var pool = await PoolAsync(patch);
            if (pool.Count == 0) return null;
            var versions = await CachedAsync(Path.Combine(cacheDir, "aramkit", "versions.json"), $"{Aramkit}/data/versions.json", daily: true);
            var paths = versions is null ? [] : DataPaths(versions, patch);
            AugmentTable? champion = null, global = null;
            foreach (var (version, dataPath) in paths)
            {
                var c = await TableAsync(version, dataPath, $"champion-details/{championId}.json", champ: true);
                champion ??= c;
                global ??= await TableAsync(version, dataPath, "augment-rankings.json", champ: false);
                if (c is not null && SamplesOf(c) >= ThinPatch) { champion = c; break; }
            }
            return new(pool, champion, global);
        }
        finally { gate.Release(); }
    }

    private static int SamplesOf(AugmentTable t) => t.Augments.Values.Sum(e => e.All?.Samples ?? 0);

    /// <summary>The data versions to try, best first: the client's patch, then the one before it (aramkit lists the latest three).</summary>
    public static IReadOnlyList<(string Version, string DataPath)> DataPaths(string versionsJson, string clientPatch)
    {
        var all = JsonNode.Parse(versionsJson)?["versions"]?.AsArray()
            .Select(v => (Version: (string?)v?["version"], Path: (string?)v?["dataPath"]))
            .Where(v => v.Version is not null && v.Path is not null && !v.Path.Contains("..") && v.Path.StartsWith("data/", StringComparison.Ordinal))
            .Select(v => (v.Version!, v.Path!)).ToList() ?? [];
        var at = all.FindIndex(v => v.Item1 == clientPatch);
        return at < 0 ? [.. all.Take(1)] : [.. all.Skip(at).Take(2)];
    }

    private async Task<AugmentTable?> TableAsync(string version, string dataPath, string file, bool champ)
    {
        var json = await CachedAsync(Path.Combine(cacheDir, "aramkit", dataPath.Replace('/', '_'), file.Replace('/', '_')), $"{Aramkit}/{dataPath}/stats/all/{file}", daily: false);
        return json is null ? null : ParseTable(json, version, champ);
    }

    /// <summary>A champion-details file (`champion.stats.winRate`, `augments`) or the augment-rankings file (the `augments` shape at its top).</summary>
    public static AugmentTable? ParseTable(string json, string version, bool champion)
    {
        try
        {
            var root = JsonNode.Parse(json);
            var augments = champion ? root?["augments"] : root;
            if (augments is null) return null;
            var baseline = champion ? (double?)root?["champion"]?["stats"]?["winRate"] ?? 0.5 : 0.5;
            var all = Rows(augments["all"]);
            var stages = (augments["stages"] as JsonObject)?.Where(s => int.TryParse(s.Key, out _))
                .ToDictionary(s => int.Parse(s.Key), s => Rows(s.Value)) ?? [];
            var ids = all.Keys.Concat(stages.Values.SelectMany(s => s.Keys)).Distinct();
            var table = ids.ToDictionary(id => id, id => new AugmentEntry(all.TryGetValue(id, out var a) ? a : null,
                stages.Where(s => s.Value.ContainsKey(id)).ToDictionary(s => s.Key, s => s.Value[id])));
            return table.Count == 0 ? null : new(version, baseline, table);
        }
        catch (JsonException) { return null; }
        catch (InvalidOperationException) { return null; }
    }

    private static Dictionary<int, AugmentRow> Rows(JsonNode? list) =>
        (list as JsonArray)?.Where(r => r?["id"] is not null && r["winRate"] is not null)
            .GroupBy(r => (int)r!["id"]!).ToDictionary(g => g.Key, g => new AugmentRow((int?)g.First()!["sampleCount"] ?? 0, (double)g.First()!["winRate"]!)) ?? [];

    private async Task<IReadOnlyList<AugmentInfo>> PoolAsync(string patch)
    {
        foreach (var dir in new[] { patch, "latest" })
        {
            var lists = await CachedAsync(Path.Combine(cacheDir, dir, "augment-lists.json"), string.Format(Cdragon, dir, "augment-lists.json"), daily: true);
            var augments = await CachedAsync(Path.Combine(cacheDir, dir, "cherry-augments.json"), string.Format(Cdragon, dir, "cherry-augments.json"), daily: true);
            if (lists is not null && augments is not null && ParsePool(lists, augments) is { Count: > 0 } pool) return pool;
        }
        return [];
    }

    /// <summary>The KIWI and KIWI_JADE (Mayhem) lists' entries ("Maps/ModeSpecificData/Augments/ShrinkEngine") matched to `cherry-augments.json` by `augmentNameId`.</summary>
    public static IReadOnlyList<AugmentInfo> ParsePool(string listsJson, string augmentsJson)
    {
        try
        {
            var names = (JsonNode.Parse(listsJson)?.AsArray() ?? []).Where(l => ((string?)l?["modeName"])?.StartsWith("KIWI", StringComparison.Ordinal) == true).SelectMany(l => l!["augmentList"]?.AsArray() ?? [])
                .Select(e => ((string?)e)?.Split('/')[^1]).OfType<string>().ToHashSet() ?? [];
            return [.. (JsonNode.Parse(augmentsJson)?.AsArray() ?? [])
                .Where(a => a?["augmentNameId"] is not null && names.Contains((string)a["augmentNameId"]!))
                .Select(a => new AugmentInfo((int)a!["id"]!, (string?)a["nameTRA"] ?? "", ((string?)a["rarity"] ?? "").TrimStart('k').ToLowerInvariant()))
                .Where(a => a.Name.Length > 0)];
        }
        catch (JsonException) { return []; }
        catch (InvalidOperationException) { return []; }
    }

    /// <summary>The cached file, fetched when missing (or a day old with <paramref name="daily"/>); a failed fetch keeps the cached one.</summary>
    private async Task<string?> CachedAsync(string file, string url, bool daily)
    {
        var cached = File.Exists(file) ? File.ReadAllText(file) : null;
        if (cached is not null && (!daily || DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < LolalyticsMatchupSource.Refresh)) return cached;
        try
        {
            using var response = await http.CreateClient("augments").GetAsync(url);
            if (response.IsSuccessStatusCode)
            {
                var text = await response.Content.ReadAsStringAsync();
                Save(file, text);
                return text;
            }
            log.LogInformation("Augment data {Url}: {Status}", url, (int)response.StatusCode);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            log.LogWarning(ex, "Could not fetch augment data {Url}", url);
        }
        if (cached is not null)
            try { File.SetLastWriteTimeUtc(file, DateTime.UtcNow); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return cached;
    }

    private void Save(string file, string text)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var temp = $"{file}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(temp, text);
            File.Move(temp, file, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { log.LogWarning(ex, "Could not cache augment data at {Path}", file); }
    }
}
