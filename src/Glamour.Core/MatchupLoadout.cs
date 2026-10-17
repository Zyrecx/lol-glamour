using System.Text.Json.Nodes;

namespace Glamour.Core;

/// <summary>One option with how often it was played (spell pair, starter, boots path or one item).</summary>
public sealed record BuildChoice(IReadOnlyList<int> Ids, int Games, int Wins);

/// <summary>A whole rune page: keystone and three runes of the primary tree, two of the secondary, three shards (the order `POST /lol-perks/v1/pages` takes).</summary>
public sealed record BuildRunes(int PrimaryStyleId, int SubStyleId, IReadOnlyList<int> PerkIds, int Games, int Wins);

/// <summary>
/// What players of <see cref="ChampionId"/> in <see cref="Lane"/> take against <see cref="EnemyId"/> (0 = against anyone, the champion's general build).
/// Every list is most played first. <see cref="Items"/> are single finished items by how often they were built, <see cref="Cores"/> the first three
/// finished items in buying order, <see cref="Later"/> the options for the fourth, fifth and sixth item (the general build only; the pair guide has none),
/// <see cref="SkillOrder"/> the order abilities are maxed ("Q", "E", "W").
/// </summary>
public sealed record MatchupBuild(int ChampionId, int EnemyId, string Lane, IReadOnlyList<BuildChoice> Spells, IReadOnlyList<BuildRunes> Runes,
    IReadOnlyList<BuildChoice> Starters, IReadOnlyList<BuildChoice> Boots, IReadOnlyList<BuildChoice> Items, string? Tip = null, string? LaneAdvantage = null,
    IReadOnlyList<BuildChoice>? Cores = null, IReadOnlyList<IReadOnlyList<BuildChoice>>? Later = null, IReadOnlyList<string>? SkillOrder = null);

public interface IMatchupBuildSource
{
    /// <summary>The build for this pair, or the general one with <paramref name="enemyId"/> 0. Null when there is none or the source failed.</summary>
    Task<MatchupBuild?> GetAsync(int championId, int enemyId, string lane, CancellationToken ct);
}

/// <summary>One block of the item set: its title in the shop and its items.</summary>
public sealed record ItemBlock(string Title, IReadOnlyList<int> Ids);

/// <summary>
/// <see cref="Core"/> is the first three items in buying order, <see cref="Later"/> the options per later item slot, <see cref="SkillOrder"/> the order to
/// max abilities (empty when unknown).
/// </summary>
public sealed record ItemSetPlan(IReadOnlyList<int> Starter, IReadOnlyList<int> Core, IReadOnlyList<int> Boots, IReadOnlyList<ItemBlock>? Later = null,
    IReadOnlyList<string>? SkillOrder = null);

/// <summary>
/// The loadout for my champion against my lane opponent (SPEC "Matchup loadout"). Each part says whether it comes from the pair or, the pair being too thin,
/// from the champion's general build. <see cref="Spell1"/>/<see cref="Spell2"/> are 0 when there is no advice.
/// </summary>
public sealed record LoadoutPlan(int ChampionId, int EnemyId, string Lane, int Spell1, int Spell2, bool SpellsVsEnemy, BuildRunes? Runes, bool RunesVsEnemy,
    ItemSetPlan Items, bool ItemsVsEnemy, string? Tip, string? LaneAdvantage);

public static class LoadoutPlanner
{
    /// <summary>A choice of the pair is taken from this many games on; below it the general build is used.</summary>
    public const int MinGames = 100;
    public const int Flash = 4, Smite = 11;
    private const int CoreSize = 3;
    /// <summary>A later item option shows from this many games on: few games reach a sixth item, so its list is mostly noise.</summary>
    public const int MinLaterGames = 15;
    private const int LaterSize = 3;
    private static readonly string[] SlotTitles = ["Fourth item", "Fifth item", "Sixth item"];

    /// <summary>
    /// Builds the plan from the pair's build and the general one (either may be null). Spells: the most played pair with Flash (with Smite in the jungle),
    /// Flash kept in the slot it already is in (<paramref name="spell1"/>, <paramref name="spell2"/> are my current spells), slot 1 when in neither.
    /// </summary>
    public static LoadoutPlan? Plan(MatchupBuild? pair, MatchupBuild? general, string lane, int spell1, int spell2)
    {
        var any = pair ?? general;
        if (any is null) return null;
        var (spells, spellsVs) = Pick(pair?.Spells.Where(s => FitsLane(s, lane)), general?.Spells.Where(s => FitsLane(s, lane)), s => s.Games);
        var (runes, runesVs) = Pick(pair?.Runes.Where(r => r.PerkIds.Count == 9), general?.Runes.Where(r => r.PerkIds.Count == 9), r => r.Games);
        var (starter, _) = Pick(pair?.Starters, general?.Starters, s => s.Games);
        var (boots, _) = Pick(pair?.Boots, general?.Boots, s => s.Games);
        var itemsVs = pair is not null && pair.Items.Sum(i => i.Games) >= MinGames * 3;
        var source = itemsVs ? pair! : general ?? pair!;
        var items = PlanItems(source, general ?? source, starter?.Ids ?? [], boots?.Ids ?? []);
        var (s1, s2) = spells is null ? (0, 0) : Place(spells.Ids, spell1, spell2);
        return new LoadoutPlan(any.ChampionId, pair?.EnemyId ?? 0, lane, s1, s2, spellsVs, runes, runesVs, items, itemsVs, pair?.Tip, pair?.LaneAdvantage);
    }

    /// <summary>
    /// The core is the most played three-item path of <paramref name="source"/> (else its three most built items). Then, from <paramref name="general"/>
    /// (more games), each later slot's options with <see cref="MinLaterGames"/> games; no item is listed twice. A build without slot data lists its next
    /// most built items as one block instead.
    /// </summary>
    private static ItemSetPlan PlanItems(MatchupBuild source, MatchupBuild general, IReadOnlyList<int> starter, IReadOnlyList<int> boots)
    {
        var seen = starter.Concat(boots).ToHashSet();
        var path = source.Cores?.OrderByDescending(c => c.Games).FirstOrDefault(c => c.Ids.Count > 0)?.Ids;
        var singles = source.Items.OrderByDescending(i => i.Games).SelectMany(i => i.Ids).Distinct().ToList();
        var core = (path ?? singles).Where(id => !seen.Contains(id)).Distinct().Take(CoreSize).ToList();
        seen.UnionWith(core);
        var later = new List<ItemBlock>();
        if (general.Later is { Count: > 0 } slots)
        {
            foreach (var (options, title) in slots.Zip(SlotTitles))
            {
                var ids = options.Where(o => o.Games >= MinLaterGames).OrderByDescending(o => o.Games).SelectMany(o => o.Ids)
                    .Where(id => !seen.Contains(id)).Distinct().Take(LaterSize).ToList();
                seen.UnionWith(ids);
                if (ids.Count > 0) later.Add(new(title, ids));
            }
        }
        else if (singles.Where(id => !seen.Contains(id)).Take(LaterSize).ToList() is { Count: > 0 } more) later.Add(new("More items", more));
        return new ItemSetPlan(starter, core, boots, later, general.SkillOrder ?? source.SkillOrder ?? []);
    }

    private static bool FitsLane(BuildChoice s, string lane) => s.Ids.Count == 2 && s.Ids.Contains(Flash) && (lane == "JUNGLE") == s.Ids.Contains(Smite);

    /// <summary>The pair's most played choice when it has <see cref="MinGames"/> games, else the general one's; the flag says which.</summary>
    private static (T? Choice, bool VsEnemy) Pick<T>(IEnumerable<T>? pair, IEnumerable<T>? general, Func<T, int> games) where T : class
    {
        var best = pair?.OrderByDescending(games).FirstOrDefault();
        if (best is not null && games(best) >= MinGames) return (best, true);
        return (general?.OrderByDescending(games).FirstOrDefault() ?? best, false);
    }

    /// <summary>Two spells into the two slots: a spell I already have stays in its slot (Flash on D stays on D), the other takes the free one.</summary>
    public static (int Spell1, int Spell2) Place(IReadOnlyList<int> pair, int spell1, int spell2)
    {
        int a = pair[0], b = pair[1];
        if (a == spell2 || b == spell1) (a, b) = (b, a);
        if (pair.Contains(Flash) && spell1 != Flash && spell2 != Flash && a != Flash) (a, b) = (b, a); // Flash is in neither slot: slot 1
        return (a, b);
    }
}

/// <summary>
/// Glamour's one item set (INSIGHTS "Item sets"): `PUT` replaces the whole list, so the user's sets are carried over as the raw JSON they came in
/// and only the set with <see cref="Uid"/> is replaced.
/// </summary>
public static class ItemSets
{
    public const string Uid = "glamour-matchup";

    /// <summary>The body for `PUT /lol-item-sets/v1/item-sets/{summonerId}/sets`: <paramref name="current"/> (the GET answer) with our set replaced or removed (null).</summary>
    public static JsonObject With(JsonObject current, JsonObject? ours)
    {
        var body = (JsonObject)current.DeepClone();
        if (body["itemSets"] is not JsonArray sets) body["itemSets"] = sets = [];
        foreach (var own in sets.OfType<JsonObject>().Where(s => s["uid"]?.GetValue<string>() == Uid).ToList()) sets.Remove(own);
        if (ours is not null) sets.Add(ours);
        body["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return body;
    }

    public static JsonObject Build(string title, int championId, ItemSetPlan plan) => new()
    {
        ["title"] = title,
        ["uid"] = Uid,
        ["type"] = "custom",
        ["map"] = "any",
        ["mode"] = "any",
        ["sortrank"] = 0,
        ["startedFrom"] = "blank",
        ["associatedChampions"] = new JsonArray(championId),
        ["associatedMaps"] = new JsonArray(11),
        ["preferredItemSlots"] = new JsonArray(),
        ["blocks"] = new JsonArray([.. new[] { new ItemBlock(StarterTitle(plan), plan.Starter), new ItemBlock("Core", plan.Core), new ItemBlock("Boots", plan.Boots) }
            .Concat(plan.Later ?? []).Where(b => b.Ids.Count > 0).Select(b => (JsonNode)Block(b.Title, b.Ids))]),
    };

    /// <summary>The skill order rides on the first block's title, the one line of text the shop shows for a block: "Starter · max Q > E > W".</summary>
    private static string StarterTitle(ItemSetPlan plan) => plan.SkillOrder is { Count: > 0 } order ? $"Starter · max {string.Join(" > ", order)}" : "Starter";

    private static JsonObject Block(string type, IReadOnlyList<int> ids) => new()
    {
        ["type"] = type,
        ["hideIfSummonerSpell"] = "",
        ["showIfSummonerSpell"] = "",
        // A starter with two potions comes as [1056, 2003, 2003]: one entry with count 2.
        ["items"] = new JsonArray([.. ids.GroupBy(i => i).Select(g => (JsonNode)new JsonObject { ["id"] = g.Key.ToString(), ["count"] = g.Count() })]),
    };
}
