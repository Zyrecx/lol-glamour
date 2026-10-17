using System.Text.Json.Nodes;
using Glamour.Lcu;

namespace Glamour.Host.Profiles;

/// <summary>One ranked queue. Tier is the LCU's upper-case name (GOLD, EMERALD, ...); a null rank means unranked.</summary>
public sealed record RankInfo(string Tier, string Division, int Lp, int Wins, int Losses);

/// <summary>What the profile list shows about an account, read from the League client while that account is signed in and kept
/// (the client only answers for the account that is running). <c>At</c> is when it was read, so a UI can say how old it is.</summary>
/// <param name="IconId">Profile icon id. The image is cached on the PC: <c>GET /api/profiles/{id}/icon</c>.</param>
/// <param name="LevelPercent">Progress to the next level, 0 to 100.</param>
/// <param name="BannerType">Regalia banner style the account displays (<c>bannerType</c> of the regalia).</param>
/// <param name="CrestType">Regalia crest style (<c>crestType</c> of the regalia).</param>
/// <param name="BannerId">The challenges banner id (<c>bannerIdSelected</c> of the chat presence, read live) and <c>TitleId</c> the selected title (<c>playerTitleSelected</c>); ids as strings, no image or text lookup is built.</param>
/// <param name="FlagTheme">Equipped banner flag (the banners plugin cosmetic); <c>FlagLevel</c> is its level.</param>
public sealed record ProfileCard(int IconId, int Level, int LevelPercent, RankInfo? Solo, RankInfo? Flex, string? BannerType, string? CrestType, string? FlagTheme, int? FlagLevel, string? BannerId, string? TitleId, DateTimeOffset At);

public sealed record ProfileCardRead(string? RiotId, ProfileCard Card, byte[]? Icon);

/// <summary>Reads the card of the account the League client is signed in to. A seam so tests need no client.</summary>
public interface IProfileCardSource
{
    /// <summary>Null when there is no client or it does not answer yet (the summoner endpoint fails until the login finishes).</summary>
    Task<ProfileCardRead?> ReadAsync(DateTimeOffset now, CancellationToken ct);
}

public sealed class LcuProfileCardSource(ILcuClient lcu, ILcuAssets assets) : IProfileCardSource
{
    public async Task<ProfileCardRead?> ReadAsync(DateTimeOffset now, CancellationToken ct)
    {
        var me = await lcu.GetAsync("/lol-summoner/v1/current-summoner", ct);
        if (me.Status != 200 || me.Body is not { } s || Int(s["profileIconId"]) is not { } icon) return null;
        // Each of these is optional: a missing one leaves that part of the card empty rather than dropping the whole card.
        var ranked = await lcu.GetAsync("/lol-ranked/v1/current-ranked-stats", ct);
        var regalia = await lcu.GetAsync("/lol-regalia/v2/current-summoner/regalia", ct);
        var chat = await lcu.GetAsync("/lol-chat/v1/me", ct);
        var flag = await lcu.GetAsync("/lol-banners/v1/current-summoner/flags/equipped", ct);
        var map = ranked.Status == 200 ? ranked.Body?["queueMap"] : null;
        var game = Str(s["gameName"]);
        var riotId = game is null ? null : Str(s["tagLine"]) is { } tag ? $"{game}#{tag}" : game;
        var card = new ProfileCard(icon, Int(s["summonerLevel"]) ?? 0, Math.Clamp(Int(s["percentCompleteForNextLevel"]) ?? 0, 0, 100),
            Rank(map?["RANKED_SOLO_5x5"]), Rank(map?["RANKED_FLEX_SR"]),
            regalia.Status == 200 ? Str(regalia.Body?["bannerType"]) : null, regalia.Status == 200 ? Str(regalia.Body?["crestType"]) : null,
            flag.Status == 200 ? Str(flag.Body?["theme"]) : null, flag.Status == 200 ? Int(flag.Body?["level"]) : null,
            chat.Status == 200 ? Str(chat.Body?["lol"]?["bannerIdSelected"]) : null, chat.Status == 200 ? Str(chat.Body?["lol"]?["playerTitleSelected"]) : null, now);
        var img = await assets.GetAssetAsync($"/lol-game-data/assets/v1/profile-icons/{icon}.jpg", ct);
        return new ProfileCardRead(riotId, card, img.Status == 200 ? img.Bytes : null);
    }

    private static RankInfo? Rank(JsonNode? q) =>
        q is not null && Str(q["tier"]) is { } tier && tier is not ("NA" or "NONE" or "UNRANKED")
            ? new RankInfo(tier, Str(q["division"]) is { } d and not "NA" ? d : "", Int(q["leaguePoints"]) ?? 0, Int(q["wins"]) ?? 0, Int(q["losses"]) ?? 0)
            : null;

    private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;
    private static int? Int(JsonNode? n) => n is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;
}
