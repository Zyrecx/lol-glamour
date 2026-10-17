namespace Glamour.Host.Profiles;

/// <summary>Keeps the active profile's card (icon, level, rank, banner) fresh while the League client runs: after it connects, when a game ends
/// or the lobby opens (level and rank change then), and now and then. Retries until the client has finished logging in.</summary>
public sealed class ProfileCardService(ProfileService profiles, ConnectionService connection, Glamour.Core.Engine engine, ILogger<ProfileCardService> log) : BackgroundService
{
    private static readonly TimeSpan Every = TimeSpan.FromMinutes(15), Retry = TimeSpan.FromSeconds(20);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        string? lastPhase = null;
        var wasConnected = false;
        var next = DateTimeOffset.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken); } catch (OperationCanceledException) { return; }
            var phase = engine.Phase;
            var due = connection.Connected && (!wasConnected || (phase != lastPhase && phase is "EndOfGame" or "Lobby" or "None") || DateTimeOffset.UtcNow >= next);
            wasConnected = connection.Connected;
            lastPhase = phase;
            if (!due) continue;
            try
            {
                next = DateTimeOffset.UtcNow + (await profiles.RefreshCardAsync(stoppingToken) ? Every : Retry); // no answer yet: the client may still be logging in
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) { log.LogDebug(ex, "Profile card not read"); next = DateTimeOffset.UtcNow + Retry; }
        }
    }
}
