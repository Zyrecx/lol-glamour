using Glamour.Core;

namespace Glamour.Tests;

public class SwapPlannerTests
{
    // Blue side ranked: my team picks in turns 1, 4, 4, 7, 7 (2-2 pairs share a turn). I am cell 2, Mid, turn 4.
    private static BoardPlayer P(int cell, string lane, int turn, bool me = false, bool locked = false) =>
        new(cell, locked ? 99 : 0, locked, lane, 4, 14, 0, "", me, turn);

    private static ChampSelectBoard Board(params BoardPlayer[] team) =>
        new("BAN_PICK", team.First(p => p.IsMe).CellId, team, [], [], [], [], 30_000);

    private static ChampSelectBoard Ranked(string myLane = "MIDDLE", int myTurn = 4, bool meLocked = false, int lockedCell = -1) => Board(
        P(0, "TOP", 1, locked: lockedCell == 0), P(1, "JUNGLE", 4, locked: lockedCell == 1), P(2, myLane, myTurn, me: true, locked: meLocked),
        P(3, "BOTTOM", 7, locked: lockedCell == 3), P(4, "SUPPORT", 7, locked: lockedCell == 4));

    private static ChampSelectSwap Pos(int id, int cell, string state = "AVAILABLE") => new("position", id, cell, state);
    private static ChampSelectSwap Order(int id, int cell, string state = "AVAILABLE") => new("pick-order", id, cell, state);

    private static GlamourSettings S(bool laneAccept = false, bool laneAsk = false, bool slotAccept = false, bool slotAsk = false, PickSlot slot = PickSlot.Last) =>
        new() { LaneSwapAccept = laneAccept, LaneSwapAsk = laneAsk, PickSlotAccept = slotAccept, PickSlotAsk = slotAsk, PickSlot = slot };

    [Fact]
    public void Everything_off_sends_nothing()
    {
        var swaps = new[] { Pos(10, 0, "RECEIVED"), Pos(11, 1), Order(20, 3, "RECEIVED") };
        Assert.Null(SwapPlanner.Next(Ranked("SUPPORT"), swaps, "TOP", "MIDDLE", S(), new(), false));
    }

    [Fact]
    public void Accepts_a_lane_I_ranked_higher_and_leaves_a_worse_one()
    {
        // I hold my second lane (Mid): Top's request gives my first, Jungle's an unlisted lane.
        var better = SwapPlanner.Next(Ranked(), [Pos(10, 0, "RECEIVED")], "TOP", "MIDDLE", S(laneAccept: true), new(), false);
        Assert.Equal(new SwapStep("position", 10, "accept", 0, "Accepted Top's lane swap"), better);
        Assert.Null(SwapPlanner.Next(Ranked(), [Pos(11, 1, "RECEIVED")], "TOP", "MIDDLE", S(laneAccept: true), new(), false));
    }

    [Fact]
    public void Holding_my_first_lane_nothing_is_accepted_or_asked()
    {
        Assert.Null(SwapPlanner.Next(Ranked(), [Pos(10, 0, "RECEIVED"), Pos(11, 0)], "MIDDLE", "TOP", S(laneAccept: true, laneAsk: true), new(), false));
    }

    [Fact]
    public void Asks_for_my_first_lane_then_my_second_when_I_hold_neither()
    {
        var memory = new SwapMemory();
        var swaps = new List<ChampSelectSwap> { Pos(10, 0), Pos(11, 3) };
        var first = SwapPlanner.Next(Ranked("SUPPORT"), swaps, "TOP", "BOTTOM", S(laneAsk: true), memory, false);
        Assert.Equal(new SwapStep("position", 10, "request", 0, "Asked Top for their lane", "TOP"), first);
        memory.AskedLanes.Add("TOP");
        var second = SwapPlanner.Next(Ranked("SUPPORT"), swaps, "TOP", "BOTTOM", S(laneAsk: true), memory, false);
        Assert.Equal(new SwapStep("position", 11, "request", 3, "Asked Bot for their lane", "BOTTOM"), second);
        memory.AskedLanes.Add("BOTTOM");
        Assert.Null(SwapPlanner.Next(Ranked("SUPPORT"), swaps, "TOP", "BOTTOM", S(laneAsk: true), memory, false));
    }

    [Fact]
    public void Holding_my_second_lane_only_my_first_is_asked_for()
    {
        var memory = new SwapMemory();
        memory.AskedLanes.Add("TOP");
        Assert.Null(SwapPlanner.Next(Ranked(), [Pos(10, 0), Pos(11, 3)], "TOP", "MIDDLE", S(laneAsk: true), memory, false));
    }

    [Fact]
    public void Nothing_goes_out_while_a_request_is_open_but_an_accept_still_does()
    {
        var swaps = new[] { Pos(10, 0), Pos(11, 3, "SENT") };
        Assert.Null(SwapPlanner.Next(Ranked("SUPPORT"), swaps, "TOP", "BOTTOM", S(laneAsk: true, slotAsk: true), new(), false));
        var accept = SwapPlanner.Next(Ranked("SUPPORT"), [.. swaps, Pos(12, 0, "RECEIVED")], "TOP", "BOTTOM", S(laneAccept: true, laneAsk: true), new(), false);
        Assert.Equal("accept", accept?.Verb);
    }

    [Fact]
    public void Locked_players_are_never_asked_or_answered()
    {
        Assert.Null(SwapPlanner.Next(Ranked("SUPPORT", lockedCell: 0), [Pos(10, 0), Pos(12, 0, "RECEIVED")], "TOP", null, S(laneAccept: true, laneAsk: true), new(), false));
        Assert.Null(SwapPlanner.Next(Ranked("SUPPORT", meLocked: true), [Pos(10, 0), Pos(12, 0, "RECEIVED")], "TOP", null, S(laneAccept: true, laneAsk: true), new(), false));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("FILL")]
    public void No_lane_wish_means_no_lane_swaps(string? first)
    {
        Assert.Null(SwapPlanner.Next(Ranked("SUPPORT"), [Pos(10, 0), Pos(12, 0, "RECEIVED")], first is null ? null : SwapPlanner.FromPreference(first), null, S(laneAccept: true, laneAsk: true), new(), false));
    }

    [Fact]
    public void Lobby_preferences_map_to_lanes()
    {
        Assert.Equal("SUPPORT", SwapPlanner.FromPreference("UTILITY"));
        Assert.Equal("MIDDLE", SwapPlanner.FromPreference("MIDDLE"));
        Assert.Null(SwapPlanner.FromPreference("FILL"));
        Assert.Null(SwapPlanner.FromPreference("UNSELECTED"));
    }

    [Fact]
    public void Wanting_last_accepts_a_later_slot_and_asks_the_last_pick()
    {
        var accept = SwapPlanner.Next(Ranked(), [Order(20, 3, "RECEIVED")], null, null, S(slotAccept: true), new(), false);
        Assert.Equal(new SwapStep("pick-order", 20, "accept", 3, "Accepted a pick-order swap"), accept);
        Assert.Null(SwapPlanner.Next(Ranked(), [Order(21, 0, "RECEIVED")], null, null, S(slotAccept: true), new(), false)); // turn 1 is earlier

        var ask = SwapPlanner.Next(Ranked(), [Order(21, 0), Order(22, 1), Order(23, 3), Order(24, 4)], null, null, S(slotAsk: true), new(), false);
        Assert.Equal(new SwapStep("pick-order", 23, "request", 3, "Asked the last pick to swap pick order"), ask);
    }

    [Fact]
    public void Wanting_first_asks_the_first_pick()
    {
        var ask = SwapPlanner.Next(Ranked(), [Order(21, 0), Order(23, 3)], null, null, S(slotAsk: true, slot: PickSlot.First), new(), false);
        Assert.Equal(new SwapStep("pick-order", 21, "request", 0, "Asked the first pick to swap pick order"), ask);
    }

    [Fact]
    public void Not_first_only_moves_me_off_the_first_pick()
    {
        Assert.Null(SwapPlanner.Next(Ranked(), [Order(23, 3), Order(24, 3, "RECEIVED")], null, null, S(slotAccept: true, slotAsk: true, slot: PickSlot.NotFirst), new(), false));
        var first = Board(P(0, "TOP", 4), P(1, "JUNGLE", 4), P(2, "MIDDLE", 1, me: true), P(3, "BOTTOM", 7), P(4, "SUPPORT", 7));
        var ask = SwapPlanner.Next(first, [Order(21, 0), Order(23, 3)], null, null, S(slotAsk: true, slot: PickSlot.NotFirst), new(), false);
        Assert.Equal(21, ask?.Id); // the nearest later pick
    }

    [Fact]
    public void Already_in_the_wanted_slot_or_asked_once_nothing_more_is_asked()
    {
        var last = Board(P(0, "TOP", 1), P(1, "JUNGLE", 4), P(2, "MIDDLE", 7, me: true), P(3, "BOTTOM", 4), P(4, "SUPPORT", 7));
        Assert.Null(SwapPlanner.Next(last, [Order(21, 0), Order(23, 4)], null, null, S(slotAsk: true), new(), false));
        Assert.Null(SwapPlanner.Next(Ranked(), [Order(23, 3)], null, null, S(slotAsk: true), new() { SlotAsked = true }, false));
    }

    [Fact]
    public void Simultaneous_picks_have_no_pick_order_part()
    {
        var normal = Board(P(0, "TOP", 0), P(1, "JUNGLE", 0), P(2, "MIDDLE", 0, me: true), P(3, "BOTTOM", 0), P(4, "SUPPORT", 0));
        Assert.Null(SwapPlanner.Next(normal, [Order(23, 3), Order(24, 4, "RECEIVED")], null, null, S(slotAccept: true, slotAsk: true), new(), false));
    }

    [Fact]
    public void Pick_order_is_left_alone_when_the_client_says_picks_are_simultaneous()
    {
        // Turns split over groups (as the board counts them) but hasSimultaneousPicks true: no pick-order call, its id may be a lane swap's.
        Assert.Null(SwapPlanner.Next(Ranked(), [Order(23, 3), Order(24, 3, "RECEIVED")], null, null, S(slotAccept: true, slotAsk: true), new(), simultaneousPicks: true));
    }

    [Fact]
    public void Of_two_lane_offers_my_better_lane_is_accepted()
    {
        // I hold Support (unlisted); Bot (my second) asks first in the list, Top (my first) second.
        var step = SwapPlanner.Next(Ranked("SUPPORT"), [Pos(11, 3, "RECEIVED"), Pos(10, 0, "RECEIVED")], "TOP", "BOTTOM", S(laneAccept: true), new(), false);
        Assert.Equal(10, step?.Id);
    }

    [Fact]
    public void The_slot_waits_for_the_lane_ask()
    {
        var swaps = new[] { Pos(10, 0), Order(23, 3) };
        Assert.Equal("position", SwapPlanner.Next(Ranked("SUPPORT"), swaps, "TOP", null, S(laneAsk: true, slotAsk: true), new(), false)?.Kind);
        var memory = new SwapMemory();
        memory.AskedLanes.Add("TOP");
        Assert.Equal("pick-order", SwapPlanner.Next(Ranked("SUPPORT"), swaps, "TOP", null, S(laneAsk: true, slotAsk: true), memory, false)?.Kind);
    }

    [Fact]
    public void An_answered_request_is_not_answered_again()
    {
        var memory = new SwapMemory();
        memory.Answered.Add(("position", 10));
        Assert.Null(SwapPlanner.Next(Ranked(), [Pos(10, 0, "RECEIVED")], "TOP", "MIDDLE", S(laneAccept: true), memory, false));
    }

    [Fact]
    public void Bench_modes_are_left_alone()
    {
        var aram = Ranked() with { Mode = ChampSelectMode.Aram };
        Assert.Null(SwapPlanner.Next(aram, [Pos(10, 0, "RECEIVED")], "TOP", "MIDDLE", S(laneAccept: true), new(), false));
    }
}
