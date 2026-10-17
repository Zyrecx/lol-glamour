namespace Glamour.Core;

/// <summary>One player of the game's Live Client Data `playerlist`, in its order: `team` (ORDER/CHAOS), `riotId` ("#" when hidden), the champion's key.</summary>
public sealed record LivePlayer(string Team, string RiotId, string ChampionKey);

/// <summary>A loading-screen card of a party member: its row (ORDER on top), its place in the row, which party (for the colour) and how big.</summary>
public sealed record LoadingCard(bool Top, int Slot, int Party, int Size);

/// <summary>
/// Which loading-screen cards belong to a premade (SPEC "Parties on the loading screen"). ORDER's five are on top and CHAOS's below, in `playerlist`
/// order except me: the list puts me first on my team, the screen anywhere (LIVE, five games; INSIGHTS "Connection and client lifecycle"). So my row is
/// placed only once my card is found on screen (<c>LoadingLayout.MyCard</c>); until then it gets no marks. A party member is found by Riot ID, else by
/// champion.
/// </summary>
public static class LoadingCards
{
    public const int PerRow = 5;

    /// <param name="championKey">A champion id's key as `rawChampionName` ends ("MonkeyKing"), null when unknown.</param>
    /// <param name="me">My Riot ID (`activeplayername`); null leaves both rows in list order.</param>
    /// <param name="myCard">Where my card is on screen, null while not found.</param>
    public static IReadOnlyList<LoadingCard> Match(GameParties parties, IReadOnlyList<LivePlayer> players, Func<int, string?> championKey,
        string? me = null, (bool Top, int Slot)? myCard = null)
    {
        var order = players.Where(p => p.Team == "ORDER").ToList();
        var chaos = players.Where(p => p.Team == "CHAOS").ToList();
        if (order.Count > PerRow || chaos.Count > PerRow || order.Count + chaos.Count != players.Count) return []; // not the two-row screen (Arena)
        bool? hidden = null; // the row whose cards are not known yet (true: the top one)
        if (me is not null && players.FirstOrDefault(p => p.RiotId == me) is { } self)
        {
            var row = self.Team == "ORDER" ? order : chaos;
            if (myCard is { } at && at.Top == (row == order) && at.Slot < row.Count)
            {
                row.Remove(self);
                row.Insert(at.Slot, self);
            }
            else hidden = row == order; // where the others sit depends on my place: nothing on my row yet
        }
        var cards = new List<LoadingCard>();
        for (var i = 0; i < parties.Parties.Count; i++)
        {
            var party = parties.Parties[i];
            var found = party.Players.Select(m => Find(m, order, chaos, championKey)).Where(c => c is not null && c.Value.Top != hidden).Select(c => c!.Value).Distinct().ToList();
            if (found.Count < 2) continue; // one card alone says nothing
            cards.AddRange(found.Select(c => new LoadingCard(c.Top, c.Slot, i, party.Players.Count)));
        }
        return cards;
    }

    private static (bool Top, int Slot)? Find(PartyPlayer m, List<LivePlayer> order, List<LivePlayer> chaos, Func<int, string?> championKey)
    {
        foreach (var by in new Func<LivePlayer, bool>[]
        {
            p => m.Name.Contains('#') && string.Equals(p.RiotId, m.Name, StringComparison.OrdinalIgnoreCase),
            p => championKey(m.ChampionId) is { Length: > 0 } key && string.Equals(p.ChampionKey, key, StringComparison.OrdinalIgnoreCase),
        })
        {
            var hits = order.Select((p, i) => (p, Top: true, i)).Concat(chaos.Select((p, i) => (p, Top: false, i))).Where(x => by(x.p)).ToList();
            if (hits.Count == 1) return (hits[0].Top, hits[0].i); // a mirror pick matches twice: not this way
        }
        return null;
    }

    /// <summary>"Duo", "3-stack" ...</summary>
    public static string Label(int size) => size == 2 ? "Duo" : $"{size}-stack";
}
