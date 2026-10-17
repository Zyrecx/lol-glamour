using System.Text.Json.Nodes;

namespace Glamour.Core;

/// <summary>
/// Turns raw client snapshots into UI events by diffing against the previous one, so only meaningful changes reach the
/// stream (never timer ticks). Also holds the current derived state for the SSE snapshot. Catalog: docs/SPEC.md "Event stream".
/// </summary>
public sealed class EventTracker(EventHub events, TimeProvider? time = null)
{
    /// <summary>How long a ready check stays open. Not in the payload (its `timer` counts up); measured from expired checks, INSIGHTS "Ready check".</summary>
    public static readonly TimeSpan ReadyCheckWindow = TimeSpan.FromSeconds(13);

    private static readonly HashSet<string> SwapEvents = ["RECEIVED", "SENT", "ACCEPTED", "DECLINED", "CANCELLED"];
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly object _gate = new();
    private string _phase = "None";
    private string _queueState = "None";
    private int _searchQueueId;
    private double _estimate;
    private DateTimeOffset? _searchStart; // host clock; only elapsed times leave the host, the phone's clock may differ
    private ReadyCheckState _ready = ReadyCheckState.None;
    private DateTimeOffset? _readyStart;
    private HonorBallotView _honor = HonorBallotView.Empty;
    private DodgeState _dodge = DodgeState.None;
    private string _stage = "";
    private (int Id, string Type)? _turn;
    private Dictionary<(string Kind, int Id), ChampSelectSwap> _swaps = [];
    private ChampSelectBoard? _board;
    /// <summary>The board last published, null outside champ select.</summary>
    public ChampSelectBoard? Board => _board;
    private long _mySummonerId;
    private int _queueId;
    /// <summary>The lobby's queue (kept through champ select and the game).</summary>
    public int QueueId => _queueId;
    private LobbyState _lobby = LobbyState.None;
    private List<PendingInvitation> _invitations = [];
    private GameParties _parties = GameParties.Empty;

    /// <summary>What the phone needs of the lobby. Positions are the client's words (TOP, JUNGLE, MIDDLE, BOTTOM, UTILITY, FILL, UNSELECTED); empty when the mode has no lane picker.</summary>
    public sealed record LobbyState(bool CanStart, int QueueId, bool ShowPositions, string First, string Second, int Members, IReadOnlyList<LobbyMember>? Roster = null, bool Custom = false)
    {
        public static readonly LobbyState None = new(true, 0, false, "", "", 0);

        public bool Equals(LobbyState? o) =>
            o is not null && CanStart == o.CanStart && QueueId == o.QueueId && ShowPositions == o.ShowPositions && First == o.First && Second == o.Second
            && Members == o.Members && Custom == o.Custom && (Roster ?? []).SequenceEqual(o.Roster ?? []);
        public override int GetHashCode() => HashCode.Combine(QueueId, Members, First, Second);
    }

    private sealed record ReadyCheckState(string State, string PlayerResponse, long[] DeclinerIds, string DodgeWarning)
    {
        public static readonly ReadyCheckState None = new("None", "None", [], "None");
        public bool SameAs(ReadyCheckState o) => State == o.State && PlayerResponse == o.PlayerResponse && DodgeWarning == o.DodgeWarning && DeclinerIds.SequenceEqual(o.DeclinerIds);
    }

    private sealed record DodgeState(string State, long[] DodgeIds)
    {
        public static readonly DodgeState None = new("Invalid", []);
    }

    /// <summary>Everything a freshly connected UI needs to be correct without having seen earlier events.</summary>
    public object Snapshot()
    {
        lock (_gate)
            return new
            {
                queue = Queue(),
                lobby = _lobby,
                invitations = _invitations,
                readyCheck = ReadyCheck(),
                honor = _honor,
                champSelect = new
                {
                    stage = _stage,
                    turn = _turn is { } t ? new { actionId = t.Id, type = t.Type } : null,
                    swaps = _swaps.Values.Where(s => IsLive(s.State)).ToList(),
                    board = _board,
                },
                dodge = new { _dodge.State, _dodge.DodgeIds },
                parties = _parties,
            };
    }

    /// <summary>Who queued with whom in the running game (Empty outside a game).</summary>
    public GameParties CurrentParties { get { lock (_gate) return _parties; } }

    public void OnParties(GameParties next)
    {
        lock (_gate)
        {
            if (next.SameAs(_parties)) return;
            _parties = next;
            events.Publish("parties", next);
        }
    }

    public void OnPhase(string phase)
    {
        lock (_gate)
        {
            var old = _phase;
            _phase = phase;
            if (old == "ReadyCheck" && phase != "ReadyCheck")
                events.Publish("ready-check-result", new { result = phase == "ChampSelect" ? "passed" : "failed", declinerIds = _ready.DeclinerIds });
            if (phase == "ChampSelect" && old != "ChampSelect")
                events.Publish("champselect-start", new { queueId = _queueId });
            if (old == "ChampSelect" && phase != "ChampSelect")
            {
                EndTurn();
                _stage = "";
                _swaps = [];
                _board = null;
                events.Publish("champselect-end", new { outcome = phase is "GameStart" or "InProgress" or "Reconnect" ? "game-start" : "ended" });
            }
        }
    }

    /// <summary>/lol-lobby/v2/received-invitations (the whole list, refetched on every change).</summary>
    public void OnInvitations(JsonNode? json)
    {
        lock (_gate)
        {
            var next = Parties.PendingFrom(json);
            if (next.SequenceEqual(_invitations)) return;
            _invitations = next;
            events.Publish("invitations", next);
        }
    }

    /// <summary>/lol-lobby/v2/lobby (null when not in a lobby). `canStart` is the local member's start permission: a non-leader cannot queue.</summary>
    public void OnLobby(JsonNode? json)
    {
        lock (_gate)
        {
            var next = json is null ? LobbyState.None : new LobbyState(
                json["localMember"]?["allowedStartActivity"]?.GetValue<bool>() ?? true,
                json["gameConfig"]?["queueId"]?.GetValue<int>() ?? 0,
                json["gameConfig"]?["showPositionSelector"]?.GetValue<bool>() ?? false,
                json["localMember"]?["firstPositionPreference"]?.GetValue<string>() ?? "",
                json["localMember"]?["secondPositionPreference"]?.GetValue<string>() ?? "",
                (json["members"] as JsonArray)?.Count ?? 0,
                LobbyMember.FromLobby(json),
                json["gameConfig"]?["isCustom"]?.GetValue<bool>() ?? false);
            if (next == _lobby) return;
            _lobby = next;
            events.Publish("lobby", next);
        }
    }

    /// <summary>/lol-matchmaking/v1/search (null when not queued).</summary>
    public void OnSearch(JsonNode? json)
    {
        lock (_gate)
        {
            var state = json?["searchState"]?.GetValue<string>() ?? "None";
            var queueId = json?["queueId"]?.GetValue<int>() ?? 0;
            if (queueId != 0) _queueId = queueId;
            // `timeInQueue` counts whole seconds (LIVE), so the start is read once and the phone counts from the elapsed time.
            if (json is null) _searchStart = null;
            else if (_searchStart is null && json["timeInQueue"] is JsonValue t && t.TryGetValue<double>(out var inQueue))
                _searchStart = _time.GetUtcNow() - TimeSpan.FromSeconds(inQueue);
            if (state == _queueState) return;
            _queueState = state;
            _searchQueueId = queueId;
            _estimate = json?["estimatedQueueTime"]?.GetValue<double>() ?? 0;
            events.Publish("queue", Queue());
        }
    }

    private object Queue() => new { state = _queueState, queueId = _searchQueueId, estimatedQueueTime = _estimate, elapsedMs = Elapsed(_searchStart) };

    private long Elapsed(DateTimeOffset? since) => since is { } s ? (long)Math.Max(0, (_time.GetUtcNow() - s).TotalMilliseconds) : 0;

    /// <summary>/lol-matchmaking/v1/ready-check (null when none). Republished on field changes only; the `timer` ticks give the start, sent as an elapsed time.</summary>
    public void OnReadyCheck(JsonNode? json)
    {
        lock (_gate)
        {
            var next = json is null ? ReadyCheckState.None : new ReadyCheckState(
                json["state"]?.GetValue<string>() ?? "None", json["playerResponse"]?.GetValue<string>() ?? "None",
                (json["declinerIds"] as JsonArray)?.Select(x => x!.GetValue<long>()).ToArray() ?? [], json["dodgeWarning"]?.GetValue<string>() ?? "None");
            if (next.State != "InProgress") _readyStart = null;
            else if (_readyStart is null && json?["timer"] is JsonValue t && t.TryGetValue<double>(out var seconds))
                _readyStart = _time.GetUtcNow() - TimeSpan.FromSeconds(seconds);
            if (next.SameAs(_ready)) return;
            _ready = next;
            events.Publish("ready-check", ReadyCheck());
        }
    }

    private object ReadyCheck() => new
    {
        _ready.State, _ready.PlayerResponse, _ready.DeclinerIds, _ready.DodgeWarning,
        elapsedMs = Elapsed(_readyStart), windowMs = (long)ReadyCheckWindow.TotalMilliseconds,
    };

    /// <summary>The post-game ballot (Empty when there is none). Published on every change so the honor screen opens, follows votes made anywhere, and closes itself.</summary>
    public void OnHonor(HonorBallotView next)
    {
        lock (_gate)
        {
            if (next.SameAs(_honor)) return;
            _honor = next;
            events.Publish("honor", next);
        }
    }

    /// <summary>/lol-gameflow/v1/session: only <c>gameDodge</c> is read.</summary>
    public void OnGameflowSession(JsonNode? json)
    {
        lock (_gate)
        {
            var d = json?["gameDodge"];
            var next = d is null ? DodgeState.None : new DodgeState(d["state"]?.GetValue<string>() ?? "Invalid",
                (d["dodgeIds"] as JsonArray)?.Select(x => x!.GetValue<long>()).ToArray() ?? []);
            if (next.State == _dodge.State && next.DodgeIds.SequenceEqual(_dodge.DodgeIds)) return;
            _dodge = next;
            if (next.State != "Invalid")
                events.Publish("dodge", new { next.State, next.DodgeIds, byMe = _mySummonerId != 0 && next.DodgeIds.Contains(_mySummonerId) });
        }
    }

    public void OnChampSelect(ChampSelectSession session)
    {
        lock (_gate)
        {
            if (session.LocalSummonerId != 0) _mySummonerId = session.LocalSummonerId;
            if (session.Timer.Phase != _stage && session.Timer.Phase != "")
            {
                _stage = session.Timer.Phase;
                events.Publish("cs-stage", new { stage = _stage });
            }
            PublishTurn(session);
            PublishSwaps(session);
            PublishBoard(session);
        }
    }

    /// <summary>The screen's data: teams, bans, who acts. Republished only when something in it changes, never for the countdown.</summary>
    private void PublishBoard(ChampSelectSession session)
    {
        if (session.Board is not { } next || next.SameAs(_board)) return;
        _board = next;
        events.Publish("board", next);
    }

    private void PublishTurn(ChampSelectSession session)
    {
        // In PLANNING every ban is already marked in progress (LIVE, tools/logs/livetest-1790967581988.jsonl), but nobody can act yet.
        if (session.Timer.Phase == "PLANNING" || session.MyActiveAction is not { } a) { EndTurn(); return; }
        if (_turn?.Id == a.Id) return;
        EndTurn(); // a new live action replaces the old one: end then start
        _turn = (a.Id, a.Type);
        events.Publish("myturn", new { actionId = a.Id, type = a.Type, stage = session.Timer.Phase, lane = session.Lane });
    }

    private void EndTurn()
    {
        if (_turn is null) return;
        _turn = null;
        events.Publish("myturn-end");
    }

    /// <summary>
    /// A swap is live while RECEIVED (a teammate asked me) or SENT (I asked). Its end must be published too, whatever it ends as
    /// (accepted, timed out back to AVAILABLE, INVALID, or gone from the session), or the phone keeps showing buttons for a dead request.
    /// </summary>
    private void PublishSwaps(ChampSelectSession session)
    {
        var next = (session.Swaps ?? []).ToDictionary(s => (s.Kind, s.Id));
        foreach (var (key, s) in next)
        {
            _swaps.TryGetValue(key, out var old);
            if (old?.State != s.State && (SwapEvents.Contains(s.State) || IsLive(old?.State)))
                events.Publish("swap", new { kind = s.Kind, id = s.Id, cellId = s.CellId, state = s.State });
        }
        foreach (var (key, old) in _swaps)
            if (!next.ContainsKey(key) && IsLive(old.State))
                events.Publish("swap", new { kind = old.Kind, id = old.Id, cellId = old.CellId, state = "GONE" });
        _swaps = next;
    }

    private static bool IsLive(string? state) => state is "RECEIVED" or "SENT";
}
