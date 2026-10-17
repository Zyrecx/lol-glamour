using System.Text.Json.Nodes;
using Glamour.Lcu;
using Microsoft.Extensions.Logging;

namespace Glamour.Core;

/// <summary>Turns client events into feature calls. State is rebuilt from GETs on every resync, so a reconnect loses nothing.</summary>
public sealed class Engine(ILcuClient client, IEnumerable<IFeature> features, EventHub events, ILogger<Engine> log, TimeProvider? time = null)
{
    private const string BallotPath = "/lol-honor-v2/v1/ballot";
    private readonly IFeature[] _features = features.ToArray();
    public EventTracker Tracker { get; } = new(events, time);
    public string Phase { get; private set; } = "None";

    public async Task ResyncAsync(CancellationToken ct)
    {
        var phase = await client.GetAsync("/lol-gameflow/v1/gameflow-phase", ct);
        await SetPhaseAsync(phase.Body?.GetValue<string>() ?? "None", ct);
        Tracker.OnLobby(await Ok("/lol-lobby/v2/lobby", ct));
        Tracker.OnInvitations(await Ok("/lol-lobby/v2/received-invitations", ct));
        Tracker.OnSearch(await Ok("/lol-matchmaking/v1/search", ct));
        Tracker.OnReadyCheck(await Ok("/lol-matchmaking/v1/ready-check", ct));
        var gameflow = await Ok("/lol-gameflow/v1/session", ct);
        Tracker.OnGameflowSession(gameflow);
        await OnPartiesAsync(gameflow, ct);
        await OnBallotAsync(await Ok(BallotPath, ct), ct);
        if (Phase == "ChampSelect") await HandleSessionAsync((await client.GetAsync("/lol-champ-select/v1/session", ct)).Body, ct);
        foreach (var f in _features) await f.OnResyncAsync(ct);
    }

    /// <summary>The body of a successful GET, or null (a 404 error body must not be read as state).</summary>
    private async Task<JsonNode?> Ok(string path, CancellationToken ct) => await client.GetAsync(path, ct) is { Status: 200 } r ? r.Body : null;

    public async Task HandleAsync(LcuEvent e, CancellationToken ct)
    {
        try
        {
            if (e.Uri.StartsWith("/lol-lobby/v2/received-invitations", StringComparison.Ordinal))
            {
                Tracker.OnInvitations(await Ok("/lol-lobby/v2/received-invitations", ct)); // events carry one invitation; the list is the state
                return;
            }
            foreach (var f in _features) await f.OnEventAsync(e, ct);
            switch (e.Uri)
            {
                case "/lol-gameflow/v1/gameflow-phase":
                    // The session's Create comes just before the phase (LIVE, tools/logs/events-2026-10-02T10-45-15-101Z.jsonl) and is dropped then;
                    // the next update may not come before BAN_PICK, so the session is read once on entry.
                    if (await SetPhaseAsync(e.Data?.GetValue<string>() ?? "None", ct) && Phase == "ChampSelect")
                        await HandleSessionAsync(await Ok("/lol-champ-select/v1/session", ct), ct);
                    break;
                case "/lol-lobby/v2/lobby":
                    Tracker.OnLobby(e.EventType == "Delete" ? null : e.Data);
                    break;
                case "/lol-matchmaking/v1/search":
                    Tracker.OnSearch(e.EventType == "Delete" ? null : e.Data);
                    break;
                case "/lol-matchmaking/v1/ready-check":
                    Tracker.OnReadyCheck(e.EventType == "Delete" ? null : e.Data);
                    break;
                case "/lol-gameflow/v1/session":
                    Tracker.OnGameflowSession(e.EventType == "Delete" ? null : e.Data);
                    await OnPartiesAsync(e.EventType == "Delete" ? null : e.Data, ct);
                    break;
                case "/lol-champ-select/v1/session" when e.EventType != "Delete":
                    await HandleSessionAsync(e.Data, ct);
                    await RetryPartiesAsync(ct);
                    break;
                case BallotPath:
                    await OnBallotAsync(e.EventType == "Delete" ? null : e.Data, ct);
                    break;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "Handling {Uri} failed", e.Uri);
        }
    }

    /// <summary>False when the phase did not change.</summary>
    private async Task<bool> SetPhaseAsync(string phase, CancellationToken ct)
    {
        if (phase == Phase) return false;
        log.LogInformation("Phase {Old} -> {New}", Phase, phase);
        Phase = phase;
        _offered = null;
        _banOwners = new();
        events.Publish("phase", new { phase });
        Tracker.OnPhase(phase);
        if (phase != "ChampSelect") _partiesFound = -1;
        if (!GameParties.Shown(phase)) { _partiesRead = default; Tracker.OnParties(GameParties.Empty); }
        foreach (var f in _features) await f.OnPhaseAsync(phase, ct);
        return true;
    }

    private (long GameId, bool InGame) _partiesRead;
    private long _partiesTried;
    private int _partiesFound = -1;

    /// <summary>The gameflow session may not have its teams (or their party ids) yet when champ select starts, and it does not always send another update.
    /// So while the groups are unknown or empty, look again on champ select updates, at most every 2 s.</summary>
    private async Task RetryPartiesAsync(CancellationToken ct)
    {
        if (Phase != "ChampSelect" || _partiesFound > 0 || Environment.TickCount64 - _partiesTried < 2000) return;
        _partiesTried = Environment.TickCount64;
        await OnPartiesAsync(await Ok("/lol-gameflow/v1/session", ct), ct, again: true);
    }

    /// <summary>
    /// Parties are worked out once in champ select (groups only) and once more when the game starts, then with names (blank in the session)
    /// read by puuid. Never named in champ select: the client hides those names there.
    /// </summary>
    private async Task OnPartiesAsync(JsonNode? gameflow, CancellationToken ct, bool again = false)
    {
        var gameId = gameflow?["gameData"]?["gameId"] is JsonValue g && g.TryGetValue<long>(out var id) ? id : 0;
        var sessionPhase = gameflow?["phase"]?.GetValue<string>() ?? "";
        var inGame = GameParties.InGamePhases.Contains(sessionPhase);
        // Back in the lobby the session keeps the last game's id with its teams gone (LIVE, glamour.log 2026-10-08 21:57): nothing to read there.
        if (gameId == 0 || !GameParties.Shown(sessionPhase) || (!again && (gameId, inGame) == _partiesRead)) return;
        // At champ select's start the gameflow session still holds the previous game, teams and all (LIVE: every champ select read in glamour.log
        // 2026-10-06..10 had the last game's id; INSIGHTS "Parties"). Only groups of this champ select's own game count; until then, look again.
        if (sessionPhase == "ChampSelect" && await ChampSelectGameIdAsync(ct) != gameId)
        {
            if (!again) log.LogInformation("Parties: the gameflow session still holds game {GameId}, not this champ select's", gameId);
            return;
        }
        var me = (await Ok("/lol-summoner/v1/current-summoner", ct))?["puuid"]?.GetValue<string>() ?? "";
        if (GameParties.From(gameflow, me) is not { } found)
        {
            if (!again) log.LogInformation("Parties: none to show yet (game {GameId}; no teams, or champ select off Summoner's Rift)", gameId);
            return;
        }
        if (!again || _partiesRead != (gameId, inGame)) log.LogInformation("Parties read: {Count} group(s), {Ids}", found.Parties.Count, string.Join(" | ", new[] { "teamOne", "teamTwo" }.Select(t => t + " " + string.Join(",", (gameflow?["gameData"]?[t] as JsonArray)?.Select(p => (p?["teamParticipantId"]?.ToString() ?? "-") + (p?["selectedPosition"]?.ToString() is { Length: > 0 } pos ? "/" + pos : "")) ?? []))));
        _partiesRead = (gameId, inGame);
        _partiesFound = found.Parties.Count;
        if (!inGame) { Tracker.OnParties(found); return; }
        var named = new List<GameParty>();
        foreach (var party in found.Parties)
        {
            var players = new List<PartyPlayer>();
            foreach (var p in party.Players)
            {
                var s = await Ok($"/lol-summoner/v2/summoners/puuid/{p.Puuid}", ct);
                var name = s?["gameName"]?.GetValue<string>() is { Length: > 0 } n ? $"{n}#{s["tagLine"]?.GetValue<string>()}" : "";
                players.Add(p with { Name = name });
            }
            named.Add(party with { Players = players });
        }
        Tracker.OnParties(found with { Parties = named });
    }

    private async Task<long> ChampSelectGameIdAsync(CancellationToken ct) =>
        (await Ok("/lol-champ-select/v1/session", ct))?["gameId"] is JsonValue g && g.TryGetValue<long>(out var id) ? id : 0;

    private (long GameId, JsonNode? Friends) _ballotFriends;

    /// <summary>
    /// The ballot comes with Create, Update and Delete events of the same shape as its GET (LIVE, tools/logs/events-2026-10-01T19-00-13-348Z.jsonl).
    /// The friends list only marks friends and names them, so it is read once per game.
    /// </summary>
    private async Task OnBallotAsync(JsonNode? ballot, CancellationToken ct)
    {
        var gameId = ballot?["gameId"] is JsonValue g && g.TryGetValue<long>(out var id) ? id : 0;
        if (gameId == 0) { Tracker.OnHonor(HonorBallotView.Empty); return; }
        if (_ballotFriends.GameId != gameId) _ballotFriends = (gameId, await Ok("/lol-chat/v1/friends", ct));
        Tracker.OnHonor(HonorBallotView.From(ballot, _ballotFriends.Friends));
    }

    private IReadOnlyList<int>? _offered;
    private BanOwners _banOwners = new();

    private async Task<IReadOnlyList<int>?> FetchOfferedAsync(CancellationToken ct)
    {
        var r = await client.GetAsync("/lol-lobby-team-builder/champ-select/v1/subset-champion-list", ct);
        var ids = r.Body is JsonArray a ? a.Select(n => n?.GetValue<int>() ?? 0).Where(i => i > 0).ToList() : [];
        return ids.Count > 0 ? ids : null; // not cached while empty, so the next session update asks again
    }

    private async Task HandleSessionAsync(JsonNode? json, CancellationToken ct)
    {
        if (Phase != "ChampSelect")
        {
            // A late update after the phase left must not revive stale state; one before the phase is read again on entry.
            if (json is not null) log.LogInformation("Champ select update ignored in phase {Phase}", Phase);
            return;
        }
        if (ChampSelectSession.FromJson(json) is not { } session) return;
        if (session.Board is { } b && json is JsonObject o) session = session with { Board = _banOwners.Follow(b, o) };
        if (session.Board is { Mode: ChampSelectMode.Mayhem } board)
        {
            // The two or three champions dealt to me. Constant for the whole champ select (LIVE, INSIGHTS "Champ select: ARAM Mayhem"), so asked for once.
            _offered ??= await FetchOfferedAsync(ct);
            if (_offered is { Count: > 0 }) session = session with { Board = board with { Offered = _offered } };
        }
        Tracker.OnChampSelect(session);
        foreach (var f in _features) await f.OnChampSelectAsync(session, ct);
    }
}
