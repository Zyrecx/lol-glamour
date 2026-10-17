using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Glamour.Host.Logging;

/// <summary>
/// Takes who-is-who out of log text before it leaves the PC (SPEC "Publishing": the log zip and error reports). Riot IDs, names, puuids and
/// summoner ids become Player1, Player2, .. and game ids Game1, ..; tokens, passwords, IPs and the Windows user and PC names go.
/// One instance per zip or report: the same player keeps one number inside it, and nothing is kept or matched across instances.
/// Feed lines oldest first, so a name learned from an event also goes from the plain lines after it.
/// </summary>
public sealed partial class Scrubber
{
    private readonly Dictionary<string, string> _players = new(StringComparer.Ordinal); // "p:<puuid>", "s:<summonerId>", "n:<riot id or name>" -> PlayerN
    private readonly Dictionary<string, string> _games = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _ids = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(string Text, string Token)> _names = []; // learned names, replaced in plain text, longest first
    private readonly string? _user, _machine;
    private int _playerCount;

    /// <param name="user">The Windows user name (also in paths); null for none.</param>
    /// <param name="machine">The PC's name (also as host.local); null for none.</param>
    public Scrubber(string? user, string? machine)
    {
        _user = user is { Length: >= 3 } ? user : null;
        _machine = machine is { Length: >= 3 } ? machine : null;
    }

    /// <summary>For this PC: its Windows user and machine names.</summary>
    public static Scrubber ForThisPc() => new(Environment.UserName, Environment.MachineName);

    /// <summary>One log line. An event line (`Glamour.Events: kind {json}`) has its JSON rewritten field by field; the rest goes through the text rules.</summary>
    public string Line(string line)
    {
        var json = JsonStart(line);
        if (json > 0)
        {
            JsonNode? node = null;
            try { node = JsonNode.Parse(line.AsSpan(json).ToString()); } catch (JsonException) { /* not JSON after all */ }
            if (node is not null)
            {
                try
                {
                    Learn(node);
                    Rewrite(node);
                    return Text(line[..json]) + node.ToJsonString(Compact);
                }
                catch (InvalidOperationException) { /* an odd shape: the text rules still run */ }
            }
        }
        return Text(line);
    }

    /// <summary>Free text: learned names first, then the patterns (Riot IDs, uuids, game and summoner ids, IPs, secrets, paths).</summary>
    public string Text(string s)
    {
        if (s.Length == 0) return s;
        s = Secrets(s);
        foreach (var (text, token) in _names) s = ReplaceWhole(s, text, token);
        s = RiotId().Replace(s, m => Player("n:" + m.Value));
        s = Uuid().Replace(s, m => _players.TryGetValue("p:" + m.Value.ToLowerInvariant(), out var p) ? p : Id(m.Value));
        s = GameIdText().Replace(s, m => m.Groups[1].Value + Game(m.Groups[2].Value));
        s = SummonerPath().Replace(s, m => m.Groups[1].Value + Player("s:" + m.Groups[2].Value));
        s = LongNumber().Replace(s, m => _players.TryGetValue("s:" + m.Value, out var p) ? p : _games.TryGetValue(m.Value, out var g) ? g : m.Value);
        s = Ipv4().Replace(s, m => m.Value is "127.0.0.1" or "0.0.0.0" ? m.Value : "x.x.x.x");
        s = Ipv6().Replace(s, m => m.Value.Any(char.IsAsciiLetter) || m.Value.Count(c => c == ':') >= 5 ? m.Value is "::1" ? m.Value : "x::x" : m.Value);
        s = UsersPath().Replace(s, @"$1\User");
        s = SourceRoot().Replace(s, "");
        if (_user is not null) s = ReplaceWhole(s, _user, "User", StringComparison.OrdinalIgnoreCase);
        if (_machine is not null) s = ReplaceWhole(s, _machine, "PC", StringComparison.OrdinalIgnoreCase);
        return s;
    }

    private static string Secrets(string s)
    {
        s = UrlCredentials().Replace(s, "$1…@");
        s = SecretValue().Replace(s, "$1…");
        s = PairFragment().Replace(s, "$1…");
        s = PrivateUrl().Replace(s, "$1/…");
        return s;
    }

    // ---- JSON events

    private static readonly JsonSerializerOptions Compact = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Where the JSON of an event line starts (after "Glamour.Events: kind "), or -1.</summary>
    private static int JsonStart(string line)
    {
        var at = line.IndexOf("Glamour.Events: ", StringComparison.Ordinal);
        if (at < 0) return -1;
        for (var i = at + 16; i < line.Length; i++)
            if (line[i] is '{' or '[') return i;
        return -1;
    }

    private static readonly string[] NameKeys = ["riotId", "gameName", "summonerName", "displayName", "playerName", "fromName", "name"];

    /// <summary>Gives every player object one number for all its ids and names, so a puuid in one event and the Riot ID in another read as the same player.</summary>
    private void Learn(JsonNode? node)
    {
        switch (node)
        {
            case JsonArray a:
                foreach (var x in a) Learn(x);
                break;
            case JsonObject o:
                var keys = PlayerKeys(o);
                if (keys.Count > 0)
                {
                    var token = keys.Select(k => _players.GetValueOrDefault(k)).FirstOrDefault(t => t is not null) ?? NextPlayer();
                    foreach (var k in keys) Remember(k, token);
                }
                foreach (var (_, v) in o) Learn(v);
                break;
        }
    }

    private static List<string> PlayerKeys(JsonObject o)
    {
        var keys = new List<string>();
        if (Str(o, "puuid") is { Length: > 0 } puuid) keys.Add("p:" + puuid.ToLowerInvariant());
        if (o["summonerId"] is JsonValue sv && sv.ToString() is { Length: > 0 } sid && sid != "0") keys.Add("s:" + sid);
        // "name" is a player's only beside a player's other fields (a queue or champion has a name too).
        var isPlayer = keys.Count > 0 || o.ContainsKey("cellId") || o.ContainsKey("isMe") || o.ContainsKey("riotId");
        foreach (var k in NameKeys)
        {
            if (k == "name" && !isPlayer) continue;
            if (Str(o, k) is { Length: > 0 } n) keys.Add("n:" + n);
        }
        if (Str(o, "gameName") is { Length: > 0 } g && Str(o, "tagLine") is { Length: > 0 } t) keys.Add("n:" + g + "#" + t);
        return keys;
    }

    private void Remember(string key, string token)
    {
        if (!_players.TryAdd(key, token)) return;
        if (key.StartsWith("n:", StringComparison.Ordinal) && key.Length >= 6) // 4+ characters: a 2-letter name would eat "IV" out of a rank
        {
            _names.Add((key[2..], token));
            _names.Sort((a, b) => b.Text.Length.CompareTo(a.Text.Length));
        }
        // The game name alone shows too (read off the screen, "the cards read as .. / Name / ..").
        if (key.StartsWith("n:", StringComparison.Ordinal) && key.IndexOf('#') is > 5 and var hash) Remember(key[..hash], token);
    }

    /// <summary>Learns the players of a line without rewriting it. A zip learns from every line first, so a name read off the screen goes even before an event names its player.</summary>
    public void Learn(string line)
    {
        var json = JsonStart(line);
        if (json > 0)
            try { if (JsonNode.Parse(line.AsSpan(json).ToString()) is { } node) Learn(node); }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException) { /* not JSON after all */ }
        foreach (Match m in RiotId().Matches(line)) Player("n:" + m.Value);
    }

    /// <summary>Replaces the values: player fields by the player's number, game ids by the game's, every other string through the text rules.</summary>
    private void Rewrite(JsonNode? node, string? parentKey = null)
    {
        switch (node)
        {
            case JsonArray a:
                for (var i = 0; i < a.Count; i++)
                    if (a[i] is JsonValue v) { if (Value(v, parentKey) is var nv && !ReferenceEquals(nv, v)) a[i] = nv; }
                    else Rewrite(a[i], parentKey);
                break;
            case JsonObject o:
                var player = PlayerKeys(o).Select(k => _players.GetValueOrDefault(k)).FirstOrDefault(t => t is not null);
                foreach (var key in o.Select(p => p.Key).ToList())
                {
                    var v = o[key];
                    if (player is not null && (key is "puuid" or "summonerId" or "tagLine" || (NameKeys.Contains(key) && v is JsonValue nv && nv.ToString().Length > 0)))
                        o[key] = player;
                    else if (SecretKey().IsMatch(key) && v is JsonValue)
                        o[key] = "…";
                    else if (key is "level" or "levelPercent" or "lp" or "wins" or "losses" or "iconId" or "profileIconId" && v is JsonValue)
                        o[key] = null; // with the region, level and record find the account on a stats site
                    else if (key is "gameId" && v is JsonValue gv && gv.ToString() is { Length: > 0 } gid && gid != "0")
                        o[key] = Game(gid);
                    else if (v is JsonValue sv)
                    {
                        if (Value(sv, key) is var nv2 && !ReferenceEquals(nv2, sv)) o[key] = nv2;
                    }
                    else
                        Rewrite(v, key);
                }
                break;
        }
    }

    /// <summary>A string through the text rules; a long number that is a known player or game, or sits under a summoner-id key ("dodgeIds"), by its token.</summary>
    private JsonNode? Value(JsonValue v, string? key)
    {
        if (v.GetValueKind() == JsonValueKind.String) return Text(v.GetValue<string>());
        if (v.GetValueKind() != JsonValueKind.Number || v.ToString() is not { Length: >= 7 } n) return v;
        if (_players.TryGetValue("s:" + n, out var p)) return p;
        if (_games.TryGetValue(n, out var g)) return g;
        return key is not null && (key.Contains("summoner", StringComparison.OrdinalIgnoreCase) || key.Contains("dodge", StringComparison.OrdinalIgnoreCase)) ? Player("s:" + n) : v;
    }

    private static string? Str(JsonObject o, string key) => o[key] is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;

    // ---- numbering

    private string NextPlayer() => "Player" + ++_playerCount;

    private string Player(string key)
    {
        if (_players.TryGetValue(key, out var token)) return token;
        token = NextPlayer();
        Remember(key, token);
        return token;
    }

    private string Game(string id)
    {
        if (!_games.TryGetValue(id, out var token)) _games[id] = token = "Game" + (_games.Count + 1);
        return token;
    }

    private string Id(string uuid)
    {
        if (!_ids.TryGetValue(uuid, out var token)) _ids[uuid] = token = "Id" + (_ids.Count + 1);
        return token;
    }

    /// <summary>Replaces `find` where it is not part of a longer word (so a name inside another name or a rank stays).</summary>
    private static string ReplaceWhole(string s, string find, string with, StringComparison cmp = StringComparison.Ordinal)
    {
        var at = s.IndexOf(find, cmp);
        if (at < 0) return s;
        var b = new System.Text.StringBuilder(s.Length);
        var from = 0;
        while (at >= 0)
        {
            var end = at + find.Length;
            var whole = (at == 0 || !char.IsLetterOrDigit(s[at - 1])) && (end == s.Length || !char.IsLetterOrDigit(s[end]));
            if (whole) { b.Append(s, from, at - from).Append(with); from = end; }
            at = s.IndexOf(find, whole ? end : at + 1, cmp);
        }
        return b.Append(s, from, s.Length - from).ToString();
    }

    // ---- patterns

    /// <summary>name#tag as Riot shows it (no spaces in the name: one with spaces goes only once learned from an event).</summary>
    [GeneratedRegex(@"(?<![\p{L}\p{N}_.#/])[\p{L}\p{N}_.]{2,16}#[\p{L}\p{N}]{3,5}(?![\p{L}\p{N}])")]
    private static partial Regex RiotId();

    [GeneratedRegex(@"\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b")]
    private static partial Regex Uuid();

    /// <summary>"game 7000000107", "gameId=..", "game id: ..".</summary>
    [GeneratedRegex(@"(?i)(\bgame(?:\s?id)?[\s=:#]+)(\d{7,})\b")]
    private static partial Regex GameIdText();

    /// <summary>LCU paths keyed by a summoner id (the item sets are).</summary>
    [GeneratedRegex(@"(/(?:item-sets|summoners?)/)(\d{5,})\b")]
    private static partial Regex SummonerPath();

    [GeneratedRegex(@"\b\d{7,}\b")]
    private static partial Regex LongNumber();

    [GeneratedRegex(@"\b(?:25[0-5]|2[0-4]\d|1?\d?\d)(?:\.(?:25[0-5]|2[0-4]\d|1?\d?\d)){3}\b")]
    private static partial Regex Ipv4();

    /// <summary>Anything shaped like IPv6; the caller keeps a clock ("20:35:12": no hex letter, few groups), "::1" and "::".</summary>
    [GeneratedRegex(@"(?i)(?<![\w:.])(?:[0-9a-f]{0,4}:){2,7}[0-9a-f]{0,4}(?![\w:])")]
    private static partial Regex Ipv6();

    [GeneratedRegex(@"(?i)(https?://[^\s:/@]+:)[^\s@/]+@")]
    private static partial Regex UrlCredentials();

    [GeneratedRegex(@"(?i)(\b(?:token|access_token|password|passwd|secret|pin|authorization|topic|cookie)\b[""']?\s*[=:]\s*[""']?(?:Basic |Bearer )?)[^\s""'&,;}]+")]
    private static partial Regex SecretValue();

    [GeneratedRegex(@"(?i)^(?:token|accessToken|access_token|password|passwd|secret|pin|authorization|topic|cookie|p256dh|endpoint)$")]
    private static partial Regex SecretKey();

    /// <summary>The pairing secret rides in the pair page's URL fragment.</summary>
    [GeneratedRegex(@"(?i)(pair\.html#)\S+")]
    private static partial Regex PairFragment();

    /// <summary>ntfy topics and Web Push endpoints are secrets in their paths: keep the host only.</summary>
    [GeneratedRegex(@"(?i)(https?://(?:[\w.-]*ntfy[\w.-]*|fcm\.googleapis\.com|[\w.-]*push[\w.-]*|[\w.-]*notify\.windows\.com))/\S*")]
    private static partial Regex PrivateUrl();

    [GeneratedRegex(@"(?i)\b([a-z]:\\Users)\\[^\\\s""]+")]
    private static partial Regex UsersPath();

    /// <summary>Where the sources were built from (in stack traces and the dev content root): kept from src\ or tests\ on.</summary>
    [GeneratedRegex(@"(?i)\b[a-z]:\\(?:[^\\\s:*?""<>|]+\\)*?(?=(?:src|tests)\\Glamour\.)")]
    private static partial Regex SourceRoot();
}
