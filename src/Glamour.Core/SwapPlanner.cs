namespace Glamour.Core;

/// <summary>Which pick slot the user wants (SPEC "Role and pick-order swaps"). NotFirst is anything but the first ally pick.</summary>
public enum PickSlot { Last, First, NotFirst }

/// <summary>One swap call Glamour wants to make: kind position or pick-order, the swap id, request or accept, the ally's cell, and the line the panel shows.</summary>
public sealed record SwapStep(string Kind, int Id, string Verb, int CellId, string Note, string? Lane = null);

/// <summary>What one champ select already did, so asks are not repeated: lanes asked for, whether the slot was asked for, swap ids answered.</summary>
public sealed class SwapMemory
{
    public HashSet<string> AskedLanes { get; } = [];
    public bool SlotAsked { get; set; }
    public HashSet<(string Kind, int Id)> Answered { get; } = [];
}

/// <summary>
/// Picks the next lane or pick-order swap to send (SPEC "Role and pick-order swaps"). Pure: the feature calls it on every session and once more on a
/// fresh read right before sending. Accepts come first, then a lane ask, then a slot ask; nothing is ever declined, and nothing goes out while any
/// swap of mine is still open (the user's own included) or once either player locked. Teammates' own wishes are unknown (the session does not carry
/// them), so an ask is only an ask.
/// </summary>
public static class SwapPlanner
{
    /// <param name="first">My first lane from the lobby, in <see cref="LaneNames.All"/> form (null, or FILL: no lane wish).</param>
    /// <param name="second">My second lane, same form.</param>
    /// <param name="simultaneousPicks">The session's `hasSimultaneousPicks`: true leaves pick order alone (in Normal Draft the pick-order and lane swap
    /// lists carried the same ids, INSIGHTS "Champ select: draft", so a pick-order call there might be a lane swap).</param>
    public static SwapStep? Next(ChampSelectBoard board, IReadOnlyList<ChampSelectSwap> swaps, string? first, string? second, GlamourSettings s, SwapMemory memory, bool simultaneousPicks)
    {
        if (board.Mode != ChampSelectMode.Draft) return null;
        var me = board.MyTeam.FirstOrDefault(p => p.IsMe);
        if (me is null || me.Locked) return null;
        var ally = board.MyTeam.Where(p => !p.IsMe).ToDictionary(p => p.CellId);
        BoardPlayer? Free(int cell) => ally.TryGetValue(cell, out var p) && !p.Locked ? p : null;
        bool Fresh(ChampSelectSwap w) => !memory.Answered.Contains((w.Kind, w.Id));

        var wish = first is null || !LaneNames.All.Contains(first) ? null : new[] { first, second }.Where(l => l is not null && LaneNames.All.Contains(l)).Distinct().ToList();
        int Rank(string lane) => wish is null ? 0 : wish.IndexOf(lane) is var i and >= 0 ? i : wish.Count;
        var myRank = wish is null || me.Lane == "" ? 0 : Rank(me.Lane);

        var turns = board.MyTeam.Select(p => p.PickTurn).Where(t => t > 0).Distinct().Order().ToList();
        var sequential = !simultaneousPicks && turns.Count > 1 && me.PickTurn > 0;
        bool Closer(int theirs) => s.PickSlot switch
        {
            PickSlot.Last => theirs > me.PickTurn,
            PickSlot.First => theirs > 0 && theirs < me.PickTurn,
            _ => me.PickTurn == turns[0] && theirs > me.PickTurn,
        };
        var atSlot = !sequential || s.PickSlot switch { PickSlot.Last => me.PickTurn == turns[^1], PickSlot.First => me.PickTurn == turns[0], _ => me.PickTurn != turns[0] };

        // Accept: a request that gives me a lane I ranked higher, or moves me toward the slot I want.
        if (s.LaneSwapAccept && wish is not null)
            foreach (var (w, lane) in swaps.Where(w => w.Kind == "position" && w.State == "RECEIVED" && Fresh(w))
                         .Select(w => (Swap: w, Lane: Free(w.CellId)?.Lane ?? "")).Where(t => t.Lane != "").OrderBy(t => Rank(t.Lane))) // my best lane first
                if (Rank(lane) < myRank)
                    return new SwapStep(w.Kind, w.Id, "accept", w.CellId, $"Accepted {Name(lane)}'s lane swap");
        if (s.PickSlotAccept && sequential)
            foreach (var w in swaps.Where(w => w.Kind == "pick-order" && w.State == "RECEIVED" && Fresh(w)))
                if (Free(w.CellId) is { } p && Closer(p.PickTurn))
                    return new SwapStep(w.Kind, w.Id, "accept", w.CellId, "Accepted a pick-order swap");

        if (swaps.Any(w => w.State == "SENT")) return null; // one open request at a time, mine or the user's

        // Ask for my lane: my first lane, then (holding neither) my second, one request each.
        if (s.LaneSwapAsk && wish is not null && myRank > 0)
            foreach (var lane in wish.Take(myRank).Where(l => !memory.AskedLanes.Contains(l)))
                if (swaps.FirstOrDefault(w => w.Kind == "position" && w.State == "AVAILABLE" && Free(w.CellId)?.Lane == lane) is { } ask)
                    return new SwapStep(ask.Kind, ask.Id, "request", ask.CellId, $"Asked {Name(lane)} for their lane", lane);

        // Ask for the slot, once, after the lane part is done (nothing more to ask there).
        if (!s.PickSlotAsk || memory.SlotAsked || atSlot) return null;
        var target = swaps.Where(w => w.Kind == "pick-order" && w.State == "AVAILABLE" && Free(w.CellId) is { } p && Closer(p.PickTurn))
            .Select(w => (Swap: w, Turn: ally[w.CellId].PickTurn))
            .OrderBy(t => s.PickSlot switch { PickSlot.Last => -t.Turn, PickSlot.First => t.Turn, _ => t.Turn }).ThenBy(t => t.Swap.CellId)
            .FirstOrDefault();
        return target.Swap is { } slot ? new SwapStep(slot.Kind, slot.Id, "request", slot.CellId, $"Asked {SlotName(s.PickSlot)} to swap pick order") : null;
    }

    /// <summary>The client's lobby preference (UTILITY for support, FILL, UNSELECTED) in <see cref="LaneNames.All"/> form; null for none or fill.</summary>
    public static string? FromPreference(string? position) => position switch
    {
        "TOP" or "JUNGLE" or "MIDDLE" or "BOTTOM" => position,
        "UTILITY" => "SUPPORT",
        _ => null,
    };

    private static string SlotName(PickSlot slot) => slot switch { PickSlot.Last => "the last pick", PickSlot.First => "the first pick", _ => "a later pick" };

    private static string Name(string lane) => lane switch { "MIDDLE" => "Mid", "BOTTOM" => "Bot", "SUPPORT" => "Support", "JUNGLE" => "Jungle", _ => "Top" };
}
