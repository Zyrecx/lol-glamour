using System.Text.Json.Nodes;

namespace Glamour.Core;

public sealed record ChromaInfo(int Id, string Name, IReadOnlyList<string> Colors, string Preview);
/// <summary>Tile is an LCU asset path (the UI loads it through the host's asset route). Only owned skins and chromas are listed.</summary>
public sealed record SkinInfo(int Id, string Name, bool IsBase, string Tile, IReadOnlyList<ChromaInfo> Chromas);

public static class Skins
{
    /// <summary>
    /// `GET /lol-champ-select/v1/skin-carousel-skins` (INSIGHTS "Spells and skins", sample tools/logs/skin-carousel.json): one entry per skin of my
    /// champion, chromas in `childSkins`. A skin stays if it is owned or one of its chromas is; chromas stay only if owned.
    /// With a champion given, entries of another champion are dropped: right after a hover or lock the carousel can still hold the previous one's.
    /// </summary>
    public static List<SkinInfo> OwnedFrom(JsonNode? json, int? championId = null) =>
        (json as JsonArray)?.OfType<JsonObject>()
          .Where(s => championId is null || s["championId"]?.GetValue<int>() == championId)
          .Select(s =>
        {
            var chromas = (s["childSkins"] as JsonArray)?.OfType<JsonObject>().Where(Owned)
                .Select(c => new ChromaInfo(c["id"]!.GetValue<int>(), c["name"]?.GetValue<string>() ?? "",
                    (c["colors"] as JsonArray)?.Select(x => x?.GetValue<string>()).OfType<string>().ToList() ?? [], c["chromaPreviewPath"]?.GetValue<string>() ?? "")).ToList() ?? [];
            return (s, chromas, owned: Owned(s));
        }).Where(x => x.owned || x.chromas.Count > 0)
          .Select(x => new SkinInfo(x.s["id"]!.GetValue<int>(), x.s["name"]?.GetValue<string>() ?? "", x.s["isBase"]?.GetValue<bool>() ?? false,
              x.s["tilePath"]?.GetValue<string>() ?? "", x.chromas)).ToList() ?? [];

    private static bool Owned(JsonObject s) => s["ownership"]?["owned"]?.GetValue<bool>() == true;

    /// <summary>True when this id is an owned skin or owned chroma in the list.</summary>
    public static bool Contains(IEnumerable<SkinInfo> skins, int id) => skins.Any(s => (s.Id == id) || s.Chromas.Any(c => c.Id == id));
}

public sealed record RuneInfo(int Id, string Name, string Icon);

/// <summary>One of Riot's recommended pages. Applying it uses only ids from a fresh fetch, never from the caller.</summary>
public sealed record RunePage(int Index, string RecommendationId, string Keystone, string KeystoneIcon, string Descriptor, int PrimaryStyleId, int SecondaryStyleId,
    IReadOnlyList<RuneInfo> Perks, IReadOnlyList<int> SpellIds, string Position);

public static class Runes
{
    /// <summary>`GET /lol-perks/v1/recommended-pages/champion/{id}/position/{POS}/map/{mapId}` (INSIGHTS "Runes", sample tools/logs/recommended-32-JUNGLE.json).</summary>
    public static List<RunePage> RecommendedFrom(JsonNode? json) =>
        (json as JsonArray)?.OfType<JsonObject>().Select((p, i) => new RunePage(i, p["recommendationId"]?.GetValue<string>() ?? "",
            p["keystone"]?["name"]?.GetValue<string>() ?? "", p["keystone"]?["iconPath"]?.GetValue<string>() ?? "", p["keystone"]?["recommendationDescriptor"]?.GetValue<string>() ?? "",
            p["primaryPerkStyleId"]?.GetValue<int>() ?? 0, p["secondaryPerkStyleId"]?.GetValue<int>() ?? 0,
            (p["perks"] as JsonArray)?.OfType<JsonObject>().Select(x => new RuneInfo(x["id"]!.GetValue<int>(), x["name"]?.GetValue<string>() ?? "", x["iconPath"]?.GetValue<string>() ?? "")).ToList() ?? [],
            (p["summonerSpellIds"] as JsonArray)?.Select(x => x!.GetValue<int>()).ToList() ?? [], p["position"]?.GetValue<string>() ?? "")).ToList() ?? [];

    public const string TempPagePrefix = "Glamour";

    /// <summary>
    /// The temporary-page body, verified live (INSIGHTS "Runes"): `isTemporary` pages bypass the page limit and never touch the user's own pages.
    /// A plain POST without the temporary fields fails with "Max pages reached".
    /// </summary>
    public static object TempPageBody(RunePage page, int championId) => new
    {
        name = $"{TempPagePrefix} {page.Keystone}",
        primaryStyleId = page.PrimaryStyleId,
        subStyleId = page.SecondaryStyleId,
        selectedPerkIds = page.Perks.Select(p => p.Id).ToArray(),
        current = true,
        isTemporary = true,
        recommendationChampionId = championId,
        recommendationIndex = page.Index,
        runeRecommendationId = page.RecommendationId,
    };
}
