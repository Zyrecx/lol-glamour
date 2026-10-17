using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Glamour.Host;

/// <summary>
/// Made-up names for README shots (SPEC "Publishing"): with `Glamour:ShotNames` on, every player name the host sends to a page (events and `/api` answers)
/// is swapped for a made-up one, and the desktop app paints the same made-up names over the real ones on the client's pixels. One instance per run, so
/// a player keeps one made-up name on the panel, the phone and the client. I am always <see cref="Me"/>; the others get the unused made-up name closest
/// in length to their own (so a name painted over the client's fits the gap). Tags stay when they only name a region.
/// </summary>
public sealed partial class ShotNames
{
    public const string Me = "Tidewren";

    private static readonly string[] Pool =
    [
        "Ivo", "Rue", "Pell", "Juno", "Kesh", "Oriel", "Petrel", "Dunlin", "Sundew", "Quillon", "Glowmoth", "Kestrel", "Larkspur", "Mossback", "Halcyon",
        "Marrowind", "Driftpine", "Saltmarsh", "Emberlynx", "Hollowfen", "Cinderwick", "Ashgrove", "Gloamwing", "Frostfern", "Lumenfox", "Nettlecap",
        "Duskwade", "Fernhollow", "Copperkite", "Sablewing", "Rookmere", "Bramblecap", "Thistledown", "Reedwhistle", "Stoneplover", "Quietbrook",
        "Brightwater", "Wrenfield", "Mirebell", "Pikelet", "Corvanes", "Fellrunner", "Pellucid", "Oakhollow", "Tamsin", "Brisk", "Lumo", "Taro",
    ];
    private static readonly string[] FakeTags = ["MOSS", "7E2", "FEN", "4418", "WREN", "0903", "KITE", "2611"];
    private static readonly HashSet<string> RegionTags = new(StringComparer.OrdinalIgnoreCase)
        { "EUW", "EUNE", "NA", "NA1", "KR", "BR1", "LAN", "LAS", "OCE", "TR1", "RU", "JP1", "EUW1", "EUN1", "ME1", "SG2", "TW2", "VN2", "PH2", "TH2" };
    private static readonly string[] NameKeys = ["riotId", "gameName", "summonerName", "displayName", "playerName", "fromName", "name"];

    private readonly object _gate = new();
    private readonly Dictionary<string, string> _fake = new(StringComparer.OrdinalIgnoreCase); // real game name -> made-up
    private readonly Dictionary<string, string> _tags = new(StringComparer.OrdinalIgnoreCase); // real tag -> made-up tag
    private readonly HashSet<string> _used = new(StringComparer.OrdinalIgnoreCase) { Me };
    private string? _me;

    /// <summary>The real game names learned so far, longest first (what the client's pixels are searched for).</summary>
    public IReadOnlyList<string> Known { get { lock (_gate) return [.. _fake.Keys.OrderByDescending(k => k.Length)]; } }

    /// <summary>The made-up game name for a real one ("Name" or "Name#TAG"), learning it if new.</summary>
    public string Fake(string real, bool isMe = false)
    {
        var (name, tag) = Split(real);
        if (name.Length == 0) return real;
        lock (_gate)
        {
            var fake = FakeName(name, isMe);
            return tag is null ? fake : fake + "#" + FakeTag(tag);
        }
    }

    /// <summary>Learns a name without asking for its made-up one.</summary>
    public void Learn(string real, bool isMe = false) => Fake(real, isMe);

    private string FakeName(string name, bool isMe)
    {
        if (_fake.TryGetValue(name, out var known)) return known;
        if (isMe && _me is null) { _me = name; return _fake[name] = Me; }
        var free = Pool.Where(p => !_used.Contains(p)).ToList();
        var pick = free.Count == 0 ? "Player" + (_fake.Count + 1)
            : free.OrderBy(p => Math.Abs(p.Length - name.Length)).ThenBy(p => (Hash(name) + Array.IndexOf(Pool, p)) % Pool.Length).First();
        _used.Add(pick);
        return _fake[name] = pick;
    }

    private string FakeTag(string tag)
    {
        if (RegionTags.Contains(tag)) return tag;
        if (!_tags.TryGetValue(tag, out var t)) _tags[tag] = t = FakeTags[_tags.Count % FakeTags.Length];
        return t;
    }

    private static (string Name, string? Tag) Split(string real)
    {
        real = real.Trim();
        var hash = real.LastIndexOf('#');
        return hash > 0 && hash < real.Length - 1 ? (real[..hash].Trim(), real[(hash + 1)..].Trim()) : (real.TrimEnd('#'), null);
    }

    private static int Hash(string s)
    {
        var h = 2166136261u;
        foreach (var c in s.ToLowerInvariant()) h = (h ^ c) * 16777619u;
        return (int)(h % 1_000_003);
    }

    /// <summary>Free text: every Riot ID and every learned name (as a whole word) swapped. Unchanged text comes back as the same string.</summary>
    public string Text(string s)
    {
        if (s.Length == 0) return s;
        var r = RiotId().Replace(s, m => Fake(m.Value));
        foreach (var name in Known)
            if (name.Length >= 3) r = ReplaceWhole(r, name, Fake(name));
        return r == s ? s : r;
    }

    /// <summary>A JSON answer or event body: learns its players (me first), then swaps their names and every name inside other strings.</summary>
    public JsonNode? Rewrite(JsonNode? node)
    {
        LearnMe(node);
        LearnAll(node);
        return Swap(node, null);
    }

    private void LearnMe(JsonNode? node)
    {
        switch (node)
        {
            case JsonArray a: foreach (var x in a) LearnMe(x); break;
            case JsonObject o:
                if (o["isMe"] is JsonValue me && me.GetValueKind() == JsonValueKind.True && PlayerName(o) is { } n) Learn(n, isMe: true);
                // The signed-in saved account (`profiles`): state "active".
                if (Str(o, "state") is "active" or "Active" && Str(o, "riotId") is { Length: > 0 } rid) Learn(rid, isMe: true);
                foreach (var (_, v) in o) LearnMe(v);
                break;
        }
    }

    private void LearnAll(JsonNode? node)
    {
        switch (node)
        {
            case JsonArray a: foreach (var x in a) LearnAll(x); break;
            case JsonObject o:
                if (IsPlayer(o))
                    foreach (var k in NameKeys)
                        if (Str(o, k) is { Length: > 0 } n) Learn(n);
                if (Str(o, "gameName") is { Length: > 0 } g && Str(o, "tagLine") is { Length: > 0 } t) Learn(g + "#" + t);
                foreach (var (_, v) in o) LearnAll(v);
                break;
        }
    }

    private JsonNode? Swap(JsonNode? node, string? key)
    {
        switch (node)
        {
            case JsonArray a:
                for (var i = 0; i < a.Count; i++)
                    if (a[i] is JsonValue v) { if (SwapValue(v) is var nv && !ReferenceEquals(nv, v)) a[i] = nv; }
                    else Swap(a[i], key);
                return a;
            case JsonObject o:
                var player = IsPlayer(o);
                foreach (var k in o.Select(p => p.Key).ToList())
                {
                    var v = o[k];
                    if (player && k == "tagLine" && v is JsonValue tv && tv.GetValueKind() == JsonValueKind.String) o[k] = FakeTagLocked(tv.GetValue<string>());
                    else if (player && NameKeys.Contains(k) && v is JsonValue nv && nv.GetValueKind() == JsonValueKind.String && nv.GetValue<string>().Length > 0) o[k] = Fake(nv.GetValue<string>());
                    else if (v is JsonValue sv) { if (SwapValue(sv) is var nv2 && !ReferenceEquals(nv2, sv)) o[k] = nv2; }
                    else Swap(v, k);
                }
                return o;
            case JsonValue value:
                return SwapValue(value);
            default:
                return node;
        }
    }

    private string FakeTagLocked(string tag) { lock (_gate) return FakeTag(tag); }

    private JsonNode SwapValue(JsonValue v)
    {
        if (v.GetValueKind() != JsonValueKind.String) return v;
        var s = v.GetValue<string>();
        var t = Text(s);
        return ReferenceEquals(s, t) ? v : JsonValue.Create(t);
    }

    /// <summary>"name" is a player's only beside a player's other fields (a queue, champion, skin or device has a name too).</summary>
    private static bool IsPlayer(JsonObject o) =>
        o.ContainsKey("puuid") || o.ContainsKey("summonerId") || o.ContainsKey("cellId") || o.ContainsKey("isMe") || o.ContainsKey("riotId") || o.ContainsKey("gameName");

    private static string? PlayerName(JsonObject o) => Str(o, "riotId") is { Length: > 0 } r ? r : Str(o, "gameName") is { Length: > 0 } g ? g : Str(o, "name") is { Length: > 0 } n ? n : null;

    private static string? Str(JsonObject o, string key) => o[key] is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;

    /// <summary>Replaces <paramref name="find"/> where it is not part of a longer word, ignoring case.</summary>
    private static string ReplaceWhole(string s, string find, string with)
    {
        var at = s.IndexOf(find, StringComparison.OrdinalIgnoreCase);
        if (at < 0) return s;
        var b = new System.Text.StringBuilder(s.Length);
        var from = 0;
        while (at >= 0)
        {
            var end = at + find.Length;
            var whole = (at == 0 || !char.IsLetterOrDigit(s[at - 1])) && (end == s.Length || !char.IsLetterOrDigit(s[end]));
            if (whole) { b.Append(s, from, at - from).Append(with); from = end; }
            at = s.IndexOf(find, whole ? end : at + 1, StringComparison.OrdinalIgnoreCase);
        }
        return b.Append(s, from, s.Length - from).ToString();
    }

    /// <summary>name#tag as Riot shows it.</summary>
    [GeneratedRegex(@"(?<![\p{L}\p{N}_.#/])[\p{L}\p{N}_.]{2,16}#[\p{L}\p{N}]{2,5}(?![\p{L}\p{N}])")]
    private static partial Regex RiotId();

    // ---- screen text (OCR lines from the client's pixels)

    /// <summary>
    /// Which words of an OCR line are a known real name: (first word, word count, what to paint instead) per hit. A name can span words ("quiet tide guy")
    /// and OCR slips a letter now and then, so a name of 5+ letters also matches at one edit away. A tag after the name ("Omar #020", "Omar#020") is part
    /// of the hit and gets the made-up tag. Punctuation round the name ("(Omar)") is kept.
    /// </summary>
    public IReadOnlyList<(int First, int Count, string Fake)> Hits(IReadOnlyList<string> words)
    {
        var hits = new List<(int, int, string)>();
        var known = Known;
        for (var i = 0; i < words.Count; i++)
        {
            foreach (var name in known)
            {
                var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (i + parts.Length > words.Count) continue;
                var ok = true;
                for (var j = 0; j < parts.Length && ok; j++) ok = Same(Word(words[i + j]), parts[j]);
                if (!ok) continue;
                var count = parts.Length;
                var last = words[i + count - 1];
                var shown = Lead(words[i]) + Fake(name);
                if (last.IndexOf('#') is > 0 and var glued) shown += "#" + Tag(last[(glued + 1)..]);
                else if (i + count < words.Count && words[i + count].StartsWith('#') && words[i + count].Length > 1) shown += " #" + Tag(words[i + count++][1..]);
                else shown += Trail(last);
                hits.Add((i, count, shown));
                i += count - 1;
                break;
            }
        }
        return hits;
    }

    /// <summary>The made-up tag for a real one (a region stays).</summary>
    public string Tag(string tag)
    {
        tag = tag.TrimEnd(')', ']', ',', '.', ':', ';');
        lock (_gate) return FakeTag(tag);
    }

    private static string Lead(string w) => new([.. w.TakeWhile(c => c is '(' or '[' or '"' or '\'')]);
    private static string Trail(string w) => new([.. w.Reverse().TakeWhile(c => c is ')' or ']' or ',' or '.' or ':' or ';' or '"' or '\'').Reverse()]);

    /// <summary>A word without the punctuation OCR hangs on it and without a "#tag" glued to it.</summary>
    private static string Word(string w)
    {
        var hash = w.IndexOf('#');
        if (hash > 0) w = w[..hash];
        return w.Trim('(', ')', '[', ']', ',', '.', ':', ';', '"', '\'');
    }

    private static bool Same(string ocr, string real)
    {
        if (ocr.Equals(real, StringComparison.OrdinalIgnoreCase)) return true;
        if (real.Length < 5 || Math.Abs(ocr.Length - real.Length) > 1) return false;
        return Edits(ocr.ToLowerInvariant(), real.ToLowerInvariant()) <= 1;
    }

    private static int Edits(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (var j = 1; j <= b.Length; j++)
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (prev, cur) = (cur, prev);
        }
        return prev[b.Length];
    }
}
