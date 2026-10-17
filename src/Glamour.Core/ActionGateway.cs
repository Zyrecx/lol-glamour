using System.Text.Json.Nodes;
using Glamour.Lcu;
using Microsoft.Extensions.Logging;

namespace Glamour.Core;

/// <summary>The single path for every write to the client: logging and dry-run live here.</summary>
public interface IActionGateway
{
    Task<LcuResponse> AcceptReadyCheckAsync(CancellationToken ct = default);
    Task<LcuResponse> DeclineReadyCheckAsync(CancellationToken ct = default);
    Task<LcuResponse> CreateLobbyAsync(int queueId, CancellationToken ct = default);
    Task<LcuResponse> LeaveLobbyAsync(CancellationToken ct = default);
    Task<LcuResponse> StartQueueAsync(CancellationToken ct = default);
    Task<LcuResponse> CancelQueueAsync(CancellationToken ct = default);
    /// <summary>Leaves a matchmade champ select (a dodge, with its queue penalty). Live: my own dodge showed in `gameDodge` (INSIGHTS "Lobby").</summary>
    Task<LcuResponse> DodgeAsync(CancellationToken ct = default);
    /// <summary>The champ select's own Quit (`lol-lobby-team-builder`), for a custom after my lock where the dodge call is refused (400, LIVE). From the client spec; never run live.</summary>
    Task<LcuResponse> QuitChampSelectAsync(CancellationToken ct = default);
    /// <summary>Clears the search a left custom champ select leaves behind (INSIGHTS "Exit a custom champ select").</summary>
    Task<LcuResponse> ClearSearchAsync(CancellationToken ct = default);
    /// <summary>Rejoins the running game after a crash or disconnect (phase Reconnect). From the client source; never run live.</summary>
    Task<LcuResponse> ReconnectAsync(CancellationToken ct = default);
    /// <summary>Closes and reopens the client's window (UX) while its backend keeps the lobby, queue and champ select. From the client source; never run live.</summary>
    Task<LcuResponse> RestartClientUxAsync(CancellationToken ct = default);
    Task<LcuResponse> HoverAsync(int actionId, int championId, CancellationToken ct = default);
    Task<LcuResponse> CompleteAsync(int actionId, int championId, string type, CancellationToken ct = default);
    /// <summary>Request, cancel, accept or decline a swap. Kind is position, pick-order or trade (trades are the client's champion-swaps). Request/cancel of position and pick-order were live; accept/decline and every trade call never were.</summary>
    Task<LcuResponse> SwapAsync(string kind, int id, string verb, CancellationToken ct = default);
    /// <summary>Takes a champion from the bench (ARAM, Mayhem). Live: 204 (INSIGHTS "Champ select: ARAM Mayhem").</summary>
    Task<LcuResponse> BenchSwapAsync(int championId, CancellationToken ct = default);
    /// <summary>Rerolls my champion (ARAM, Mayhem). From the client spec; never run live.</summary>
    Task<LcuResponse> RerollAsync(CancellationToken ct = default);
    /// <summary>Sets my summoner spells and/or skin in champ select (only the non-null fields are sent). Live: 204; an unowned skin is ignored silently, so read it back.</summary>
    Task<LcuResponse> SetSelectionAsync(int? spell1Id, int? spell2Id, int? skinId, CancellationToken ct = default);
    /// <summary>Creates a temporary rune page and makes it current. Live: 200, the user's own pages untouched (INSIGHTS Runes).</summary>
    Task<LcuResponse> CreateTempRunePageAsync(object body, CancellationToken ct = default);
    /// <summary>Deletes one rune page by id. Only ever called with a page Glamour itself created. From the client spec; never run live.</summary>
    Task<LcuResponse> DeleteRunePageAsync(int pageId, CancellationToken ct = default);
    /// <summary>Rewrites one rune page in place (`PUT /lol-perks/v1/pages/{id}`). Only ever called with a page Glamour itself created. Live: 201 on a temporary page.</summary>
    Task<LcuResponse> UpdateRunePageAsync(int pageId, object body, CancellationToken ct = default);
    /// <summary>Replaces the whole item-set list (`body` from <see cref="ItemSets.With"/>). Live: 201, read back exactly (INSIGHTS "Item sets").</summary>
    Task<LcuResponse> PutItemSetsAsync(long summonerId, JsonObject body, CancellationToken ct = default);
    Task<LcuResponse> HonorAsync(string recipientPuuid, string honorType, CancellationToken ct = default);
    Task<LcuResponse> SubmitBallotAsync(CancellationToken ct = default);
    /// <summary>Finishes the post-game honor screen without voting. Live: 204, phase EndOfGame 0.5 s later (INSIGHTS "Post-game").</summary>
    Task<LcuResponse> SkipHonorAsync(CancellationToken ct = default);
    /// <summary>Finishes one PreEndOfGame sequence screen by name (INSIGHTS "Post-game"). Name must come from <see cref="SkippableScreens"/>.</summary>
    Task<LcuResponse> CompleteSequenceAsync(string name, CancellationToken ct = default);
    /// <summary>Dismisses the end-of-game stats screen. From the client spec; never run live.</summary>
    Task<LcuResponse> DismissStatsAsync(CancellationToken ct = default);
    /// <summary>Writes my chat presence (`PUT /lol-chat/v1/me`). Live for the merged `lol` object; the status message is [UNVERIFIED].</summary>
    Task<LcuResponse> SetPresenceAsync(object body, CancellationToken ct = default);
    /// <summary>Sets my lane preferences in the lobby. Live: 201; rejected (400) while queued.</summary>
    Task<LcuResponse> SetPositionsAsync(string first, string second, CancellationToken ct = default);
    /// <summary>Removes another player from my lobby (leader only). From the client spec; never run live.</summary>
    Task<LcuResponse> KickMemberAsync(string puuid, CancellationToken ct = default);
    /// <summary>Hands the party lead to another member. From the client spec; never run live.</summary>
    Task<LcuResponse> PromoteMemberAsync(string puuid, CancellationToken ct = default);
    /// <summary>Joins a friend's open party. From the client spec; never run live.</summary>
    Task<LcuResponse> JoinPartyAsync(string partyId, CancellationToken ct = default);
    /// <summary>Accepts or declines a received invitation. From the client spec; never run live.</summary>
    Task<LcuResponse> ReplyInvitationAsync(string invitationId, bool accept, CancellationToken ct = default);
}

public sealed class ActionGateway(ILcuClient client, GlamourSettings settings, ILogger<ActionGateway> log, IUsage? usage = null) : IActionGateway
{
    public Task<LcuResponse> AcceptReadyCheckAsync(CancellationToken ct = default) => Send(HttpMethod.Post, "/lol-matchmaking/v1/ready-check/accept", null, "accept", ct);
    public Task<LcuResponse> DeclineReadyCheckAsync(CancellationToken ct = default) => Send(HttpMethod.Post, "/lol-matchmaking/v1/ready-check/decline", null, "decline", ct);
    public Task<LcuResponse> CreateLobbyAsync(int queueId, CancellationToken ct = default) => Send(HttpMethod.Post, "/lol-lobby/v2/lobby", new { queueId }, "lobby", ct);
    public Task<LcuResponse> LeaveLobbyAsync(CancellationToken ct = default) => Send(HttpMethod.Delete, "/lol-lobby/v2/lobby", null, "leave_lobby", ct);
    public Task<LcuResponse> StartQueueAsync(CancellationToken ct = default) => Send(HttpMethod.Post, "/lol-lobby/v2/lobby/matchmaking/search", null, "queue", ct);
    public Task<LcuResponse> CancelQueueAsync(CancellationToken ct = default) => Send(HttpMethod.Delete, "/lol-lobby/v2/lobby/matchmaking/search", null, "cancel_queue", ct);
    public Task<LcuResponse> DodgeAsync(CancellationToken ct = default) => Send(HttpMethod.Post, "/lol-gameflow/v1/session/dodge", null, "dodge", ct);
    public Task<LcuResponse> QuitChampSelectAsync(CancellationToken ct = default) => Send(HttpMethod.Post, "/lol-lobby-team-builder/champ-select/v1/session/quit", null, "quit", ct);
    public Task<LcuResponse> ClearSearchAsync(CancellationToken ct = default) => Send(HttpMethod.Delete, "/lol-matchmaking/v1/search", null, "clear_search", ct);
    public Task<LcuResponse> ReconnectAsync(CancellationToken ct = default) => Send(HttpMethod.Post, "/lol-gameflow/v1/reconnect", null, "reconnect", ct);
    public Task<LcuResponse> RestartClientUxAsync(CancellationToken ct = default) => Send(HttpMethod.Post, "/riotclient/kill-and-restart-ux", null, "restart_ux", ct);

    public Task<LcuResponse> HoverAsync(int actionId, int championId, CancellationToken ct = default) =>
        Send(HttpMethod.Patch, $"/lol-champ-select/v1/session/actions/{actionId}", new { championId }, "hover", ct);

    // Ban and lock both go through PATCH completed:true. Only works while timer.phase is BAN_PICK; PLANNING drops it silently.
    public Task<LcuResponse> CompleteAsync(int actionId, int championId, string type, CancellationToken ct = default) =>
        Send(HttpMethod.Patch, $"/lol-champ-select/v1/session/actions/{actionId}", new { championId, completed = true, type }, type == "ban" ? "ban" : "lock", ct);

    public static readonly string[] SwapVerbs = ["request", "cancel", "accept", "decline"];

    public Task<LcuResponse> SwapAsync(string kind, int id, string verb, CancellationToken ct = default)
    {
        var collection = kind switch { "position" => "position-swaps", "pick-order" => "pick-order-swaps", "trade" => "champion-swaps", _ => throw new ArgumentException("kind", nameof(kind)) };
        if (!SwapVerbs.Contains(verb)) throw new ArgumentException("verb", nameof(verb));
        return Send(HttpMethod.Post, $"/lol-champ-select/v1/session/{collection}/{id}/{verb}", null, $"swap_{kind.Replace('-', '_')}_{verb}", ct);
    }

    public Task<LcuResponse> BenchSwapAsync(int championId, CancellationToken ct = default) =>
        Send(HttpMethod.Post, $"/lol-champ-select/v1/session/bench/swap/{championId}", null, "bench", ct);

    public Task<LcuResponse> RerollAsync(CancellationToken ct = default) => Send(HttpMethod.Post, "/lol-champ-select/v1/session/my-selection/reroll", null, "reroll", ct);

    public Task<LcuResponse> SetSelectionAsync(int? spell1Id, int? spell2Id, int? skinId, CancellationToken ct = default)
    {
        var body = new Dictionary<string, int>();
        if (spell1Id is { } a) body["spell1Id"] = a;
        if (spell2Id is { } b) body["spell2Id"] = b;
        if (skinId is { } c) body["selectedSkinId"] = c;
        return Send(HttpMethod.Patch, "/lol-champ-select/v1/session/my-selection", body, skinId is null ? "spells" : "skin", ct);
    }

    public Task<LcuResponse> CreateTempRunePageAsync(object body, CancellationToken ct = default) => Send(HttpMethod.Post, "/lol-perks/v1/pages", body, "runes", ct);
    public Task<LcuResponse> DeleteRunePageAsync(int pageId, CancellationToken ct = default) => Send(HttpMethod.Delete, $"/lol-perks/v1/pages/{pageId}", null, "delete_runes", ct);
    public Task<LcuResponse> UpdateRunePageAsync(int pageId, object body, CancellationToken ct = default) => Send(HttpMethod.Put, $"/lol-perks/v1/pages/{pageId}", body, "runes", ct);
    public Task<LcuResponse> PutItemSetsAsync(long summonerId, JsonObject body, CancellationToken ct = default) =>
        Send(HttpMethod.Put, $"/lol-item-sets/v1/item-sets/{summonerId}/sets", body, "item_set", ct);

    // Staged vote, then submit. Live: one vote plus one submit reached the recipient (INSIGHTS "Post-game").
    public Task<LcuResponse> HonorAsync(string recipientPuuid, string honorType, CancellationToken ct = default) =>
        Send(HttpMethod.Post, "/lol-honor/v1/honor", new { honorType, recipientPuuid }, "honor", ct);
    public Task<LcuResponse> SubmitBallotAsync(CancellationToken ct = default) => Send(HttpMethod.Post, "/lol-honor/v1/ballot", null, "honor_submit", ct);

    public Task<LcuResponse> SkipHonorAsync(CancellationToken ct = default) => Send(HttpMethod.Post, "/lol-pre-end-of-game/v1/skip-pre-end-of-game", null, "skip_honor", ct);

    public Task<LcuResponse> CompleteSequenceAsync(string name, CancellationToken ct = default) =>
        Send(HttpMethod.Post, $"/lol-pre-end-of-game/v1/complete/{Uri.EscapeDataString(name)}", null, "skip_screen", ct);

    public Task<LcuResponse> DismissStatsAsync(CancellationToken ct = default) => Send(HttpMethod.Post, "/lol-end-of-game/v1/state/dismiss-stats", null, "dismiss_stats", ct);

    public Task<LcuResponse> SetPresenceAsync(object body, CancellationToken ct = default) => Send(HttpMethod.Put, "/lol-chat/v1/me", body, "presence", ct);

    public Task<LcuResponse> SetPositionsAsync(string first, string second, CancellationToken ct = default) =>
        Send(HttpMethod.Put, "/lol-lobby/v2/lobby/members/localMember/position-preferences", new { firstPreference = first, secondPreference = second }, "positions", ct);

    public Task<LcuResponse> KickMemberAsync(string puuid, CancellationToken ct = default) =>
        Send(HttpMethod.Post, $"/lol-lobby/v3/lobby/members/{Uri.EscapeDataString(puuid)}/kick", null, "kick", ct);

    public Task<LcuResponse> PromoteMemberAsync(string puuid, CancellationToken ct = default) =>
        Send(HttpMethod.Post, $"/lol-lobby/v3/lobby/members/{Uri.EscapeDataString(puuid)}/promote", null, "promote", ct);

    public Task<LcuResponse> JoinPartyAsync(string partyId, CancellationToken ct = default) =>
        Send(HttpMethod.Post, $"/lol-lobby/v2/party/{Uri.EscapeDataString(partyId)}/join", null, "join_party", ct);

    public Task<LcuResponse> ReplyInvitationAsync(string invitationId, bool accept, CancellationToken ct = default) =>
        Send(HttpMethod.Post, $"/lol-lobby/v2/received-invitations/{Uri.EscapeDataString(invitationId)}/{(accept ? "accept" : "decline")}", null, "invitation", ct);

    /// <param name="what">This write's word in the usage count (<see cref="IUsage"/>).</param>
    private async Task<LcuResponse> Send(HttpMethod method, string path, object? body, string what, CancellationToken ct)
    {
        if (settings.DryRun)
        {
            log.LogInformation("DRY-RUN {Method} {Path} {Body}", method, path, body);
            return new LcuResponse(204, null);
        }
        var response = await client.SendAsync(method, path, body, ct);
        // A refusal's own words (the client's error body has a message) say why, for reading the log back.
        if (response.Status >= 400 && response.Body?["message"] is JsonValue m) log.LogInformation("{Method} {Path} -> {Status}: {Message}", method, path, response.Status, m.ToString());
        else log.LogInformation("{Method} {Path} -> {Status}", method, path, response.Status);
        if (response.Status < 400) usage?.Count(UsageOrigin.Current + "_" + what);
        return response;
    }
}
