using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Glamour.Core;

namespace Glamour.Host;

/// <summary>
/// Matchup builds from OP.GG's public MCP endpoint (JSON-RPC, no key; INSIGHTS "Matchup data"): `lol_get_lane_matchup_guide` for a pair, which answers JSON,
/// and `lol_get_champion_analysis` for the general build, which answers a constructor text (<see cref="ParseText"/>). A call takes 5-7 s, so answers are
/// cached on disk per patch and a load already running is shared. A failure is "no build" (null), never an exception.
/// </summary>
public sealed class OpggBuildSource(IHttpClientFactory http, ChampionCatalog champions, string cacheDir, ILogger<OpggBuildSource> log) : IMatchupBuildSource
{
    private const string Endpoint = "https://mcp-api.op.gg/mcp";
    private static readonly string[] GeneralFields =
    [
        "data.summoner_spells.{ids[],play,win}",
        "data.runes.{primary_page_id,primary_rune_ids[],secondary_page_id,secondary_rune_ids[],stat_mod_ids[],play,win}",
        "data.starter_items.{ids[],play,win}",
        "data.boots.{ids[],play,win}",
        "data.last_items[].{ids[],play,win}",
        "data.core_items.{ids[],play,win}",
        "data.fourth_items[].{ids[],play,win}",
        "data.fifth_items[].{ids[],play,win}",
        "data.sixth_items[].{ids[],play,win}",
        "data.skill_masteries.{ids[],play,win}",
    ];

    public async Task<MatchupBuild?> GetAsync(int championId, int enemyId, string lane, CancellationToken ct)
    {
        try
        {
            var list = await champions.GetAsync(ct);
            var me = list?.Champions.FirstOrDefault(c => c.Id == championId);
            var enemy = enemyId == 0 ? null : list?.Champions.FirstOrDefault(c => c.Id == enemyId);
            if (list is null || me is null || (enemyId != 0 && enemy is null) || Position(lane) is not { } position) return null;
            var patch = LolalyticsMatchupSource.Patches(list.Version).FirstOrDefault();
            if (patch is null) return null;
            var file = Path.Combine(cacheDir, "opgg2", patch, $"{championId}-{(enemyId == 0 ? "all" : enemyId)}-{position}.json");
            return await loading.GetOrAdd(file, f =>
            {
                Lazy<Task<MatchupBuild?>>? shared = null;
                shared = new(() => LoadAsync(f, championId, enemyId, lane, Name(me.Name), enemy is null ? null : Name(enemy.Name), position)
                    .ContinueWith(t => { loading.TryRemove(new(f, shared!)); return t; }, TaskScheduler.Default).Unwrap());
                return shared;
            }).Value.WaitAsync(ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException or UnauthorizedAccessException or FormatException
            && !ct.IsCancellationRequested)
        {
            log.LogWarning(ex, "No build for champion {Id} vs {Enemy} in {Lane}", championId, enemyId, lane);
            return null;
        }
    }

    private readonly ConcurrentDictionary<string, Lazy<Task<MatchupBuild?>>> loading = new();

    /// <summary>The cached build, fetched again when missing or a day old (the patch keeps filling); a failed refresh keeps the cached one.</summary>
    private async Task<MatchupBuild?> LoadAsync(string file, int championId, int enemyId, string lane, string me, string? enemy, string position)
    {
        var cached = File.Exists(file) ? JsonSerializer.Deserialize<MatchupBuild>(File.ReadAllText(file), Json) : null;
        if (cached is not null && DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < LolalyticsMatchupSource.Refresh) return cached;
        MatchupBuild? fresh = null;
        try
        {
            var text = await CallAsync(enemy is null
                ? new { name = "lol_get_champion_analysis", arguments = (object)new { game_mode = "ranked", champion = me, position, desired_output_fields = GeneralFields } }
                : new { name = "lol_get_lane_matchup_guide", arguments = (object)new { my_champion = me, opponent_champion = enemy, position, lang = "en_US" } });
            var data = text is null ? null : enemy is null ? ParseText(text)?["data"] : JsonNode.Parse(text)?["data"];
            fresh = data is JsonObject d ? FromData(d, championId, enemyId, lane) : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or FormatException && cached is not null)
        {
            log.LogWarning(ex, "Could not refresh the build for {Me} vs {Enemy} in {Position}; using the cached one", me, enemy ?? "anyone", position);
        }
        if (fresh is null) return cached;
        Save(file, JsonSerializer.Serialize(fresh, Json));
        return fresh;
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>One `tools/call`; the answer is JSON or one server-sent `data:` line. Null for a refusal (an unknown name or position).</summary>
    private async Task<string?> CallAsync(object call)
    {
        var body = JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method = "tools/call", @params = call });
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        using var response = await http.CreateClient("opgg").SendAsync(request);
        response.EnsureSuccessStatusCode();
        var text = await response.Content.ReadAsStringAsync();
        var line = text.Split('\n').FirstOrDefault(l => l.StartsWith("data:", StringComparison.Ordinal));
        var answer = JsonNode.Parse(line is null ? text : line[5..]);
        if (answer?["error"] is { } error)
        {
            log.LogWarning("OP.GG refused {Call}: {Error}", body, error.ToJsonString());
            return null;
        }
        return answer?["result"]?["content"]?[0]?["text"]?.GetValue<string>();
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { log.LogWarning(ex, "Could not cache the build at {Path}", file); }
    }

    /// <summary>OP.GG's champion name: the display name upper-cased, spaces to `_`, `'` `.` `&amp;` dropped (`DR_MUNDO`, `KOGMAW`, `NUNU_WILLUMP`); checked against all 250 names.</summary>
    public static string Name(string displayName) =>
        string.Join('_', new string([.. displayName.ToUpperInvariant().Where(ch => ch is not ('\'' or '.' or '&'))]).Split(' ', StringSplitOptions.RemoveEmptyEntries));

    public static string? Position(string lane) => lane switch
    {
        "TOP" => "top", "JUNGLE" => "jungle", "MIDDLE" => "mid", "BOTTOM" => "adc", "SUPPORT" => "support", _ => null,
    };

    /// <summary>
    /// The `data` object of either answer: lists (the guide) or single entries (the general build) of `{ids, play, win}` for spells, starters, boots and
    /// `last_items` (single items), rune pages with `primary_page_id`, `primary_rune_ids[4]`, `secondary_page_id`, `secondary_rune_ids[2]`, `stat_mod_ids[3]`.
    /// </summary>
    public static MatchupBuild FromData(JsonObject data, int championId, int enemyId, string lane)
    {
        static IEnumerable<JsonObject> Entries(JsonNode? n) => n switch { JsonArray a => a.OfType<JsonObject>(), JsonObject o => [o], _ => [] };
        static int Int(JsonNode? n) => n is JsonValue v && v.TryGetValue<double>(out var d) ? (int)d : 0;
        static List<int> Ids(JsonNode? n) => n is JsonArray a ? [.. a.Select(Int)] : [];
        static List<BuildChoice> Choices(JsonNode? n) =>
            [.. Entries(n).Select(e => new BuildChoice(Ids(e["ids"]), Int(e["play"]), Int(e["win"]))).Where(c => c.Ids.Count > 0).OrderByDescending(c => c.Games)];
        var runes = Entries(data["runes"]).Select(r => new BuildRunes(Int(r["primary_page_id"]), Int(r["secondary_page_id"]),
            [.. Ids(r["primary_rune_ids"]), .. Ids(r["secondary_rune_ids"]), .. Ids(r["stat_mod_ids"])], Int(r["play"]), Int(r["win"])))
            .Where(r => r.PrimaryStyleId > 0 && r.SubStyleId > 0).OrderByDescending(r => r.Games).ToList();
        // Only the general build has later slots; the guide's are an empty list, which the planner reads as "no slot data", not "slots too thin".
        var later = new[] { "fourth_items", "fifth_items", "sixth_items" }.Where(data.ContainsKey).Select(k => (IReadOnlyList<BuildChoice>)Choices(data[k])).ToList();
        var skills = Entries(data["skill_masteries"]).Select(e => e["ids"]).OfType<JsonArray>().FirstOrDefault(a => a.Count > 0)
            ?.Select(x => x?.GetValue<string>() ?? "").Where(x => x.Length > 0).ToList();
        return new MatchupBuild(championId, enemyId, lane, Choices(data["summoner_spells"]), runes, Choices(data["starter_items"]), Choices(data["boots"]),
            Choices(data["last_items"]), data["opponent_champion_tip"]?.GetValue<string>(), data["lane_advantage_champion"]?.GetValue<string>(),
            Choices(data["core_items"]), later, skills);
    }

    /// <summary>
    /// The constructor text `lol_get_champion_analysis` answers when fields are chosen: `class Name: field,field` header lines, then one value built of
    /// `Name(arg, ...)`, `[...]`, `"text"` and numbers. Each `Name(...)` becomes an object with the header's field names in order. Null when it does not read.
    /// </summary>
    public static JsonObject? ParseText(string text)
    {
        var fields = new Dictionary<string, string[]>();
        var lines = text.Split('\n');
        foreach (var l in lines.Where(l => l.StartsWith("class ", StringComparison.Ordinal)))
        {
            var colon = l.IndexOf(':');
            if (colon > 6) fields[l[6..colon].Trim()] = l[(colon + 1)..].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        }
        var value = string.Join('\n', lines.Where(l => !l.StartsWith("class ", StringComparison.Ordinal))).Trim();
        var i = 0;
        return Value(value, ref i, fields) as JsonObject;
    }

    private static JsonNode? Value(string s, ref int i, Dictionary<string, string[]> fields)
    {
        while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        if (i >= s.Length) throw new FormatException("Unexpected end");
        var c = s[i];
        if (c == '[')
        {
            i++;
            var list = new JsonArray();
            foreach (var item in Args(s, ref i, ']', fields)) list.Add(item);
            return list;
        }
        if (c == '"')
        {
            var sb = new StringBuilder();
            for (i++; i < s.Length && s[i] != '"'; i++) sb.Append(s[i] == '\\' && i + 1 < s.Length ? s[++i] : s[i]);
            i++;
            return JsonValue.Create(sb.ToString());
        }
        var start = i;
        while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] is '_' or '.' or '-')) i++;
        var word = s[start..i];
        if (i < s.Length && s[i] == '(')
        {
            i++;
            var args = Args(s, ref i, ')', fields);
            var names = fields.GetValueOrDefault(word) ?? [];
            var obj = new JsonObject();
            for (var k = 0; k < args.Count; k++) obj[k < names.Length ? names[k] : $"_{k}"] = args[k];
            return obj;
        }
        if (word is "None" or "null") return null;
        if (word is "True" or "False") return JsonValue.Create(word == "True");
        return double.TryParse(word, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? JsonValue.Create(d) : throw new FormatException($"Unexpected '{word}' at {start}");
    }

    private static List<JsonNode?> Args(string s, ref int i, char close, Dictionary<string, string[]> fields)
    {
        var args = new List<JsonNode?>();
        while (true)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
            if (i < s.Length && s[i] == close) { i++; return args; }
            args.Add(Value(s, ref i, fields));
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
            if (i < s.Length && s[i] == ',') i++;
            else if (i >= s.Length) throw new FormatException("Unclosed list");
        }
    }
}
