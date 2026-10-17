using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Glamour.Core;

namespace Glamour.Host;

/// <summary>
/// Matchup tables from Lolalytics' site API (undocumented, no key; INSIGHTS "Matchup data"). One call per champion and lane gives every opponent, so the
/// answers are cached on disk per patch and a failure is just "no data" (null), never an exception. Thin matchups of the current patch also count the previous
/// patch's games (<see cref="MatchupBlend"/>); a patch without data yet is the previous one whole. The same goes for champion profiles (damage and the win rate
/// against every lane), from the champion page's data, and for ally tables (the win rate with every champion in each lane), from `build-team`. ARAM's win rate
/// of every champion is one list per patch (`ep=list`), blended the same way per champion.
/// </summary>
public sealed class LolalyticsMatchupSource(IHttpClientFactory http, ChampionCatalog champions, string cacheDir, ILogger<LolalyticsMatchupSource> log)
    : IMatchupSource, IChampionProfileSource, IAllySource, IModeWinRateSource
{
    private const string CounterUrl = "https://a1.lolalytics.com/mega/?ep=counter&p=d&v=1&patch={0}&c={1}&lane={2}&tier=emerald_plus&queue=ranked&region=all";
    // ~100 KB per answer, cached trimmed to what ParseProfile reads.
    private const string FullUrl = "https://a1.lolalytics.com/mega/?ep=build-full&p=d&v=1&patch={0}&c={1}&lane={2}&tier=emerald_plus&queue=ranked&region=all";
    // ~20 KB per answer, cached trimmed like the full one.
    private const string TeamUrl = "https://a1.lolalytics.com/mega/?ep=build-team&p=d&v=1&patch={0}&c={1}&lane={2}&tier=emerald_plus&queue=ranked&region=all";
    // Every champion in ARAM, ~15 KB. No tier filter in ARAM (all ranks play one queue).
    private const string AramUrl = "https://a1.lolalytics.com/mega/?ep=list&v=1&patch={0}&tier=all&queue=aram&region=all";

    public static string DefaultDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Glamour", "matchups");

    /// <summary>One kind of answer: its cache file suffix (or whole name, for one file per patch), URL, how a valid answer starts, what is kept of it and how it is read.</summary>
    private sealed record Kind(string Suffix, string Url, string Start, Func<string, string> Keep, Func<int, string, string, object?> Read, string? Name = null)
    {
        public static readonly Kind Counter = new("", CounterUrl, "{\"stats\"", json => json, (id, lane, json) => Parse(id, lane, json));
        public static readonly Kind Full = new("-full", FullUrl, "{\"header\"", TrimFull, (id, lane, json) => ParseProfile(id, lane, json));
        public static readonly Kind Team = new("-team", TeamUrl, "{\"team_h\"", TrimTeam, (id, lane, json) => ParseAllies(id, lane, json));
        public static readonly Kind Aram = new("", AramUrl, "{", TrimList, (_, _, json) => ParseList(json), "aram-list");
    }

    /// <summary>ARAM only: Mayhem has no Lolalytics queue (INSIGHTS "Matchup data").</summary>
    public async Task<ModeWinRates?> GetModeAsync(string mode, CancellationToken ct)
    {
        if (mode != "aram") return null;
        try
        {
            var patches = await champions.GetAsync(ct) is { } list ? Patches(list.Version).ToList() : [];
            if (patches.Count == 0) return null;
            var current = LoadAsync(Kind.Aram, patches[0], true, 0, "", "", ct);
            var previous = patches.Count > 1 ? LoadAsync(Kind.Aram, patches[1], false, 0, "", "", ct) : Task.FromResult<object?>(null);
            await Task.WhenAll(current, previous);
            var blended = MatchupBlend.Blend((ChampionMatchups?)current.Result, (ChampionMatchups?)previous.Result, patches.ElementAtOrDefault(1) ?? "");
            return blended is null ? null : new ModeWinRates(mode, current.Result is null ? patches[1] : patches[0], blended.Rows, "lolalytics.com");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException or UnauthorizedAccessException && !ct.IsCancellationRequested)
        {
            log.LogWarning(ex, "No {Mode} win rates", mode);
            return null;
        }
    }

    public Task<ChampionMatchups?> GetAsync(int championId, string lane, CancellationToken ct) =>
        GetBothAsync<ChampionMatchups>(Kind.Counter, championId, lane, MatchupBlend.Blend, ct);

    public Task<ChampionProfile?> GetProfileAsync(int championId, string lane, CancellationToken ct) =>
        GetBothAsync<ChampionProfile>(Kind.Full, championId, lane, BlendProfile, ct);

    private async Task<T?> GetBothAsync<T>(Kind kind, int championId, string lane, Func<T?, T?, string, T?> blend, CancellationToken ct) where T : class
    {
        try
        {
            var list = await champions.GetAsync(ct);
            var info = list?.Champions.FirstOrDefault(c => c.Id == championId);
            if (list is null || info is null || !LaneNames.All.Contains(lane)) return null;
            var slug = Slug(info);
            var patches = Patches(list.Version).ToList();
            if (patches.Count == 0) return null;
            if (patches.Count < 2) return (T?)await LoadAsync(kind, patches[0], true, championId, slug, lane, ct);
            // Both at once: a cold champ select fetches six tables, each would otherwise wait for two round trips in a row.
            var current = LoadAsync(kind, patches[0], true, championId, slug, lane, ct);
            var previous = LoadAsync(kind, patches[1], false, championId, slug, lane, ct);
            await Task.WhenAll(current, previous);
            return blend((T?)current.Result, (T?)previous.Result, patches[1]);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException or UnauthorizedAccessException && !ct.IsCancellationRequested)
        {
            log.LogWarning(ex, "No matchup data for champion {Id} in {Lane}", championId, lane);
            return null;
        }
    }

    public Task<ChampionAllies?> GetAlliesAsync(int championId, string lane, CancellationToken ct) =>
        GetBothAsync<ChampionAllies>(Kind.Team, championId, lane, BlendAllies, ct);

    /// <summary>Each lane's rows blended like a counter table.</summary>
    private static ChampionAllies? BlendAllies(ChampionAllies? current, ChampionAllies? previous, string previousPatch)
    {
        if ((current ?? previous) is not { } basis) return null;
        return basis with { With = LaneNames.All.ToDictionary(l => l, l => (IReadOnlyList<MatchupRow>)(MatchupBlend.Blend(Table(current, l), Table(previous, l), previousPatch)?.Rows ?? [])) };

        static ChampionMatchups? Table(ChampionAllies? a, string lane) => a is null ? null : new(a.ChampionId, lane, new Dictionary<string, double>(), a.With.GetValueOrDefault(lane) ?? []);
    }

    /// <summary>Each lane's rows blended like a counter table; the damage is the current patch's when it has one.</summary>
    private static ChampionProfile? BlendProfile(ChampionProfile? current, ChampionProfile? previous, string previousPatch)
    {
        if ((current ?? previous) is not { } basis) return null;
        var against = LaneNames.All.ToDictionary(l => l, l => (IReadOnlyList<MatchupRow>)(MatchupBlend.Blend(Table(current, l), Table(previous, l), previousPatch)?.Rows ?? []));
        return basis with { Damage = current?.Damage ?? previous?.Damage, Against = against };

        static ChampionMatchups? Table(ChampionProfile? p, string lane) => p is null ? null : new(p.ChampionId, lane, new Dictionary<string, double>(), p.Against.GetValueOrDefault(lane) ?? []);
    }

    // Loads in flight by file: the panel, PC Home and the phone ask for the same table at once, and two writers replacing one file is "access denied".
    private readonly ConcurrentDictionary<string, Lazy<Task<object?>>> loading = new();

    /// <summary>One patch's answer, shared with any load of the same file already running.</summary>
    private async Task<object?> LoadAsync(Kind kind, string patch, bool current, int championId, string slug, string lane, CancellationToken ct)
    {
        var file = Path.Combine(cacheDir, patch, $"{kind.Name ?? $"{slug}-{lane.ToLowerInvariant()}{kind.Suffix}"}.json");
        return await loading.GetOrAdd(file, f =>
        {
            Lazy<Task<object?>>? shared = null;
            shared = new(() => LoadFileAsync(kind, f, patch, current, championId, slug, lane)
                .ContinueWith(t => { loading.TryRemove(new(f, shared!)); return t; }, TaskScheduler.Default).Unwrap());
            return shared;
        }).Value.WaitAsync(ct);
    }

    /// <summary>The cached file, fetched when missing (or, for the current patch, a day old). Not cancelled by one caller, since others may share it.</summary>
    private async Task<object?> LoadFileAsync(Kind kind, string file, string patch, bool current, int championId, string slug, string lane)
    {
        var ct = CancellationToken.None;
        var cached = File.Exists(file) ? File.ReadAllText(file) : null;
        // The current patch keeps filling for weeks (16.20 cached early had 4M games against 26M for all of 16.19), so its files are fetched again once a day.
        var stale = cached is not null && current && DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > Refresh;
        var fresh = cached is null || stale ? await TryFetchAsync(kind, patch, slug, lane, cached is not null || !current, ct) : null;
        if (fresh is not null) fresh = kind.Keep(fresh);
        if ((fresh is null ? null : kind.Read(championId, lane, fresh)) is { } parsedFresh)
        {
            Save(file, fresh!);
            return parsedFresh;
        }
        if (stale) Touch(file); // a failed refresh tries again in a day, not on every look
        return cached is null ? null : kind.Read(championId, lane, cached);
    }

    /// <summary>How long a cached file of the current patch is used before it is fetched again.</summary>
    public static readonly TimeSpan Refresh = TimeSpan.FromDays(1);

    /// <summary>With <paramref name="quiet"/> (a refresh of a cached file, or the previous patch next to the current one) a failure is only logged.</summary>
    private async Task<string?> TryFetchAsync(Kind kind, string patch, string slug, string lane, bool quiet, CancellationToken ct)
    {
        try { return await FetchAsync(kind, patch, slug, lane, ct); }
        catch (Exception ex) when (quiet && ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            log.LogWarning(ex, "Could not fetch matchups for {Slug} in {Lane} on {Patch}; using what is cached", slug, lane, patch);
            return null;
        }
    }

    private async Task<string?> FetchAsync(Kind kind, string patch, string slug, string lane, CancellationToken ct)
    {
        var text = await http.CreateClient("lolalytics").GetStringAsync(string.Format(CultureInfo.InvariantCulture, kind.Url, patch, slug, lane.ToLowerInvariant()), ct);
        return text.TrimStart().StartsWith(kind.Start, StringComparison.Ordinal) ? text : null; // {"status":404} and "invalid end point" are answers too
    }

    private void Save(string file, string json)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var temp = $"{file}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, file, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { log.LogWarning(ex, "Could not cache matchups at {Path}", file); }
    }

    private static void Touch(string file)
    {
        try { File.SetLastWriteTimeUtc(file, DateTime.UtcNow); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>The site's name for a champion: the Data Dragon file name in lowercase (`MonkeyKing` is `wukong` there).</summary>
    public static string Slug(ChampionInfo info)
    {
        var file = Path.GetFileNameWithoutExtension(new Uri(info.Icon).AbsolutePath).ToLowerInvariant();
        return file == "monkeyking" ? "wukong" : file;
    }

    /// <summary>"16.19.1" becomes "16.19", then the patch before it ("16.18"); the site's patch is major.minor.</summary>
    public static IEnumerable<string> Patches(string version)
    {
        var parts = version.Split('.');
        if (parts.Length < 2 || !int.TryParse(parts[0], out var major) || !int.TryParse(parts[1], out var minor)) yield break;
        yield return $"{major}.{minor}";
        if (minor > 1) yield return $"{major}.{minor - 1}";
    }

    /// <summary>The site's lane names (`middle`, `bottom`, `support`) as <see cref="LaneNames.All"/>; null for anything else.</summary>
    private static string? LaneOf(string name) => name.ToUpperInvariant() is var upper && LaneNames.All.Contains(upper) ? upper : null;

    /// <summary>
    /// `stats.lanes` is the lane share per lane (percent, `middle` and `bottom` in the site's words); `counters[]` has `cid`, `vsWr` (this champion's win rate against it, percent) and `n` games.
    /// Every table leans toward its own champion by about its `stats.avgWr` minus 50 (Garen vs Darius: 54.2 in Garen's table, 48.7 from Darius's, ~1,030 games each;
    /// INSIGHTS "Matchup data"), so that lean is taken off each win rate: then both tables of a pair agree to within noise.
    /// </summary>
    public static ChampionMatchups? Parse(int championId, string lane, string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("stats", out var stats) || !doc.RootElement.TryGetProperty("counters", out var counters) || counters.ValueKind != JsonValueKind.Array) return null;
        var shares = new Dictionary<string, double>();
        if (stats.TryGetProperty("lanes", out var lanes) && lanes.ValueKind == JsonValueKind.Object)
            foreach (var p in lanes.EnumerateObject())
                if (p.Value.TryGetDouble(out var v) && LaneOf(p.Name) is { } name) shares[name] = v;
        var lean = stats.TryGetProperty("avgWr", out var avg) && avg.TryGetDouble(out var a) ? a - 50 : 0;
        var rows = new List<MatchupRow>();
        foreach (var c in counters.EnumerateArray())
            if (c.TryGetProperty("cid", out var cid) && cid.TryGetInt32(out var id) && c.TryGetProperty("vsWr", out var wr) && wr.TryGetDouble(out var w) && c.TryGetProperty("n", out var n) && n.TryGetInt32(out var games))
                rows.Add(new MatchupRow(id, Math.Round(w - lean, 2), games));
        return new ChampionMatchups(championId, lane, shares, rows);
    }

    /// <summary>
    /// What a profile needs of a `build-full` answer: `header.damage` (`physical`, `magic`, `true`: damage to champions per game), the top-level `avgWr`
    /// (the lean, as in the counter table; `header.avgWr` is another number) and `enemy` (per lane, rows in the order of `enemy_h`: id, wr, d1, d2, pr, n).
    /// Its `enemy` rows for its own lane are the counter table's rows plus those under ~100 games (LIVE, Zed mid 16.20). An answer missing them is null.
    /// </summary>
    public static string TrimFull(string json)
    {
        var root = JsonNode.Parse(json);
        if (root?["header"]?["damage"] is not JsonObject damage || root["enemy_h"] is not JsonArray head || root["enemy"] is not JsonObject enemy) return "";
        var columns = head.Select(h => h?.GetValue<string>()).ToList();
        int id = columns.IndexOf("id"), wr = columns.IndexOf("wr"), n = columns.IndexOf("n");
        if (id < 0 || wr < 0 || n < 0) return "";
        var lanes = new JsonObject();
        foreach (var (lane, rows) in enemy)
            if (rows is JsonArray list)
                lanes[lane] = new JsonArray([.. list.OfType<JsonArray>().Select(r => (JsonNode)new JsonArray(r[id]?.DeepClone(), r[wr]?.DeepClone(), r[n]?.DeepClone()))]);
        return new JsonObject { ["header"] = new JsonObject { ["damage"] = damage.DeepClone() }, ["avgWr"] = root["avgWr"]?.DeepClone(), ["enemy"] = lanes }.ToJsonString();
    }

    /// <summary>
    /// What an ally table needs of a `build-team` answer: `team` (per lane, rows in the order of `team_h`: id, wr, d1, d2, pr, n; LIVE, Jinx bottom and
    /// Thresh support 16.20), kept as [id, wr, n]. No `avgWr` comes with it: the rows keep the champion's own lean, which ranking within one table ignores.
    /// </summary>
    public static string TrimTeam(string json)
    {
        var root = JsonNode.Parse(json);
        if (root?["team_h"] is not JsonArray head || root["team"] is not JsonObject team) return "";
        var columns = head.Select(h => h?.GetValue<string>()).ToList();
        int id = columns.IndexOf("id"), wr = columns.IndexOf("wr"), n = columns.IndexOf("n");
        if (id < 0 || wr < 0 || n < 0) return "";
        var lanes = new JsonObject();
        foreach (var (lane, rows) in team)
            if (rows is JsonArray list)
                lanes[lane] = new JsonArray([.. list.OfType<JsonArray>().Select(r => (JsonNode)new JsonArray(r[id]?.DeepClone(), r[wr]?.DeepClone(), r[n]?.DeepClone()))]);
        return new JsonObject { ["team"] = lanes }.ToJsonString();
    }

    /// <summary>
    /// What the ARAM list needs: `cid` (champion id -> `wr` percent, `games`; also `rank`, `tier`, `pr`), kept as {id: [wr, games]}. An answer for another
    /// queue is dropped: unknown queue names silently answer ranked (`queue` 420; INSIGHTS "Matchup data"). The list's `avgWr` is 50, so no lean to take off.
    /// </summary>
    public static string TrimList(string json)
    {
        var root = JsonNode.Parse(json);
        if (root?["queue"]?.GetValueKind() != JsonValueKind.Number || root["queue"]!.GetValue<int>() != 450 || root["cid"] is not JsonObject cid) return "";
        var kept = new JsonObject();
        foreach (var (id, row) in cid)
            if (row?["wr"] is JsonValue wr && row["games"] is JsonValue games) kept[id] = new JsonArray(wr.DeepClone(), games.DeepClone());
        return new JsonObject { ["cid"] = kept }.ToJsonString();
    }

    /// <summary>A trimmed ARAM list (<see cref="TrimList"/>) as one table whose rows are the champions.</summary>
    public static ChampionMatchups? ParseList(string json)
    {
        if (json.Length == 0) return null;
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("cid", out var cid) || cid.ValueKind != JsonValueKind.Object) return null;
        var rows = cid.EnumerateObject()
            .Where(p => int.TryParse(p.Name, out _) && p.Value.ValueKind == JsonValueKind.Array && p.Value.GetArrayLength() >= 2 && p.Value[0].TryGetDouble(out _) && p.Value[1].TryGetInt32(out _))
            .Select(p => new MatchupRow(int.Parse(p.Name, CultureInfo.InvariantCulture), p.Value[0].GetDouble(), p.Value[1].GetInt32())).ToList();
        return rows.Count == 0 ? null : new ChampionMatchups(0, "ARAM", new Dictionary<string, double>(), rows);
    }

    /// <summary>A trimmed `build-team` answer (<see cref="TrimTeam"/>): rows are [id, wr, n].</summary>
    public static ChampionAllies? ParseAllies(int championId, string lane, string json)
    {
        if (json.Length == 0) return null;
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("team", out var team) || team.ValueKind != JsonValueKind.Object) return null;
        var with = new Dictionary<string, IReadOnlyList<MatchupRow>>();
        foreach (var l in team.EnumerateObject())
            if (LaneOf(l.Name) is { } name && l.Value.ValueKind == JsonValueKind.Array)
                with[name] = [.. l.Value.EnumerateArray().Where(r => r.ValueKind == JsonValueKind.Array && r.GetArrayLength() >= 3 && r[0].TryGetInt32(out _) && r[1].TryGetDouble(out _) && r[2].TryGetInt32(out _))
                    .Select(r => new MatchupRow(r[0].GetInt32(), r[1].GetDouble(), r[2].GetInt32()))];
        return new ChampionAllies(championId, lane, with);
    }

    /// <summary>A trimmed `build-full` answer (<see cref="TrimFull"/>): rows are [id, wr, n], the lean taken off like <see cref="Parse"/> does.</summary>
    public static ChampionProfile? ParseProfile(int championId, string lane, string json)
    {
        if (json.Length == 0) return null;
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("enemy", out var enemy) || enemy.ValueKind != JsonValueKind.Object) return null;
        var lean = root.TryGetProperty("avgWr", out var avg) && avg.TryGetDouble(out var a) ? a - 50 : 0;
        DamageSplit? damage = null;
        if (root.TryGetProperty("header", out var header) && header.TryGetProperty("damage", out var d) && d.ValueKind == JsonValueKind.Object)
        {
            double Of(string type) => d.TryGetProperty(type, out var v) && v.TryGetDouble(out var x) ? x : 0;
            damage = new(Of("physical"), Of("magic"), Of("true"));
        }
        var against = new Dictionary<string, IReadOnlyList<MatchupRow>>();
        foreach (var l in enemy.EnumerateObject())
            if (LaneOf(l.Name) is { } name && l.Value.ValueKind == JsonValueKind.Array)
                against[name] = [.. l.Value.EnumerateArray().Where(r => r.ValueKind == JsonValueKind.Array && r.GetArrayLength() >= 3 && r[0].TryGetInt32(out _) && r[1].TryGetDouble(out _) && r[2].TryGetInt32(out _))
                    .Select(r => new MatchupRow(r[0].GetInt32(), Math.Round(r[1].GetDouble() - lean, 2), r[2].GetInt32()))];
        return new ChampionProfile(championId, lane, damage, against);
    }
}
