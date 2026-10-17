namespace Glamour.Core;

public interface IFeature
{
    /// <summary>Gameflow phase changed (None, Lobby, Matchmaking, ReadyCheck, ChampSelect, ...).</summary>
    Task OnPhaseAsync(string phase, CancellationToken ct);

    /// <summary>Champ select session changed. Called only while the phase is ChampSelect.</summary>
    Task OnChampSelectAsync(ChampSelectSession session, CancellationToken ct);

    /// <summary>The Engine finished rebuilding its state after a (re)connect.</summary>
    Task OnResyncAsync(CancellationToken ct) => Task.CompletedTask;

    /// <summary>Every raw client event, for features that watch something the Engine does not track itself.</summary>
    Task OnEventAsync(Glamour.Lcu.LcuEvent e, CancellationToken ct) => Task.CompletedTask;
}
