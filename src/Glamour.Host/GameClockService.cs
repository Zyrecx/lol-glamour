using Glamour.Core;
using Glamour.Lcu;

namespace Glamour.Host;

/// <summary>Reads the in-game clock while a game runs: often until it is found, then now and then to catch pauses (<see cref="GameClock"/>).
/// Once per game it logs every player's champion and position, the answer key for the enemy lane guess (`--lane-backtest`).</summary>
public sealed class GameClockService(GameClock clock, ILiveGameClient game, Engine engine, ConnectionService connection, ILogger<GameClockService> log) : BackgroundService
{
    private static readonly TimeSpan Idle = TimeSpan.FromSeconds(2), Running = TimeSpan.FromSeconds(10);
    private bool _lanesLogged;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                clock.OnPhase(engine.Phase, connection.Connected);
                var gameProcess = !connection.Connected && connection.GameProcessRunning;
                if (GameClock.ShouldPoll(engine.Phase, connection.Connected, gameProcess) || clock.InGame)
                    clock.Update(await game.GetAsync("/liveclientdata/gamestats", ct));
                if (!clock.InGame) _lanesLogged = false;
                else if (!_lanesLogged && await game.GetAsync("/liveclientdata/playerlist", ct) is System.Text.Json.Nodes.JsonArray { Count: > 0 } players)
                {
                    _lanesLogged = true;
                    log.LogInformation("Lanes in game: {Players}", string.Join(" ", players.Select(p =>
                        $"{(string?)p?["team"]}:{((string?)p?["rawChampionName"])?.Replace("game_character_displayname_", "")}@{(string?)p?["position"]}")));
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception e) { log.LogWarning("Reading the game clock failed: {Message}", e.Message); } // never take the host down mid-game
            try { await Task.Delay(clock.InGame ? Running : Idle, ct); }
            catch (OperationCanceledException) { return; }
        }
    }
}
