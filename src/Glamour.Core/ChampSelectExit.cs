using Glamour.Lcu;

namespace Glamour.Core;

/// <summary>
/// Leaving champ select on the user's tap (the UI asks twice first). Matchmade: the client's dodge, with its queue penalty. Custom before my lock:
/// leaving the lobby, then clearing the search it leaves behind (as `tools/g.mjs bail`). Custom after my lock: leaving the lobby would still launch
/// the game, but the client's own Quit there is a dodge back to the lobby, so the champ select's quit call, then the dodge call (INSIGHTS "Exit a custom champ select").
/// </summary>
public static class ChampSelectExit
{
    public sealed record Outcome(int Status, string? Error, string How);

    public static async Task<Outcome> LeaveAsync(ILcuClient client, IActionGateway gateway, string phase, CancellationToken ct)
    {
        if (phase != "ChampSelect") return new(409, "Not in champion select.", "");
        var session = ChampSelectSession.FromJson((await client.GetAsync("/lol-champ-select/v1/session", ct)).Body);
        if (session is null) return new(409, "Not in champion select.", "");
        if (session.Timer.Phase == "GAME_STARTING") return new(409, "The game is starting.", "");
        var lobby = await client.GetAsync("/lol-lobby/v2/lobby", ct);
        var custom = lobby.Status == 200 && lobby.Body?["gameConfig"]?["isCustom"]?.GetValue<bool>() == true;
        var locked = session.Actions.Any(a => a.ActorCellId == session.LocalPlayerCellId && a.Type == "pick" && a.Completed);
        if (!custom)
        {
            var r = await gateway.DodgeAsync(ct);
            return r.IsSuccess ? new(r.Status, null, "dodge") : new(r.Status, $"The client refused the dodge ({r.Status}).", "dodge");
        }
        if (locked || session.Timer.Phase == "FINALIZATION")
        {
            // The dodge call answered 400 here (LIVE, glamour.log 2026-10-08 21:35:22); the champ select's own Quit first, the dodge as a fallback.
            var q = await gateway.QuitChampSelectAsync(ct);
            if (q.IsSuccess) return new(q.Status, null, "quit");
            var r = await gateway.DodgeAsync(ct);
            return r.IsSuccess ? new(r.Status, null, "dodge") : new(r.Status, "Couldn't leave. Use Quit in the client.", "quit");
        }
        var left = await gateway.LeaveLobbyAsync(ct);
        if (!left.IsSuccess) return new(left.Status, $"The client refused to leave ({left.Status}).", "leave");
        await Task.Delay(500, ct); // the leftover search shows up shortly after the leave
        if ((await client.GetAsync("/lol-matchmaking/v1/search", ct)).Status == 200) await gateway.ClearSearchAsync(ct);
        return new(left.Status, null, "leave");
    }
}
