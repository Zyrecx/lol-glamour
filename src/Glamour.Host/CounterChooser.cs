using System.Text.Json.Nodes;
using Glamour.Core;
using Glamour.Lcu;

namespace Glamour.Host;

/// <summary>
/// The counter view the panel gets (<c>GET /api/counter-pick</c>) and what "Best counter" locks, from the same inputs: the board, the opponent the user
/// chose (<see cref="CounterChoice"/>, set by the panel's or phone's last request) and the champions I can pick. As a feature it fetches my bot lane partner's
/// ally table as soon as the partner shows a champion, so Best counter's last look (1.5 s limit) finds it cached whether or not a panel is open.
/// </summary>
public sealed class CounterChooser(GlamourSettings settings, IMatchupSource matchups, IChampionProfileSource profiles, IAllySource allies, ILcuClient client, CounterChoice choice)
    : ICounterChooser, IFeature
{
    /// <param name="inChampSelect">The gameflow phase is ChampSelect: only then can the client say which champions I can pick.</param>
    public async Task<CounterPickView> ViewAsync(ChampSelectBoard? board, bool inChampSelect, CancellationToken ct) =>
        await CounterPick.ViewAsync(board, settings, matchups, ct, choice.Opponent, inChampSelect ? await PlayableAsync(ct) : null, profiles, allies);

    private readonly HashSet<(int, string)> _warmed = [];

    public Task OnPhaseAsync(string phase, CancellationToken ct)
    {
        if (phase != "ChampSelect") lock (_warmed) _warmed.Clear();
        return Task.CompletedTask;
    }

    public Task OnChampSelectAsync(ChampSelectSession session, CancellationToken ct)
    {
        if (session.Board is { Mode: ChampSelectMode.Draft } board && board.MyTeam.FirstOrDefault(p => p.IsMe) is { Lane.Length: > 0 } me
            && CounterPick.Partner(board, me.Lane) is { } partner)
        {
            bool fresh;
            lock (_warmed) fresh = _warmed.Add((partner.ChampionId, partner.Lane));
            if (fresh) _ = allies.GetAlliesAsync(partner.ChampionId, partner.Lane, CancellationToken.None); // failures are the source's ("no data")
        }
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<int>> ChooseAsync(ChampSelectBoard board, CancellationToken ct) => BestCounterPick.Order(await ViewAsync(board, true, ct));

    /// <summary>Owned plus free rotation, so counter picks only name champions I can lock. Null (no limit) on a failed read or an empty list.</summary>
    private async Task<HashSet<int>?> PlayableAsync(CancellationToken ct)
    {
        try
        {
            return await client.GetAsync("/lol-champ-select/v1/pickable-champion-ids", ct) is { Status: 200, Body: JsonArray ids } && ids.Count > 0
                ? [.. ids.Select(n => n!.GetValue<int>())]
                : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return null; }
    }
}
