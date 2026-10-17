using Glamour.Core;
using Glamour.Host.Profiles;
using Glamour.Lcu;

namespace Glamour.Host;

/// <summary>Keeps one live connection to the League client: find it, resync, pump events, retry when it drops.</summary>
public sealed class ConnectionService(SwappableLcuClient client, Engine engine, EventHub events, IRiotClient riot, ILogger<ConnectionService> log) : BackgroundService
{
    private bool _connected;
    private string _stage = "none";
    public bool Connected
    {
        get => _connected;
        private set
        {
            if (_connected == value) return;
            _connected = value;
            _stage = value ? "connected" : _stage;
            events.Publish("connection", new { connected = value, stage = Stage });
        }
    }

    /// <summary>How far the client is: "none" (no Riot or League process), "riot-client" (the Riot Client is open, League is not started),
    /// "league-starting" (the League client runs but its API is not up yet) or "connected".</summary>
    public string Stage => _connected ? "connected" : _stage;

    /// <summary>Whether the game process ran at the last look (every couple of seconds while there is no connection; stale while connected).</summary>
    public bool GameProcessRunning { get; private set; }

    /// <summary>Looks at the processes while there is no connection (the wait loop calls it every couple of seconds).</summary>
    private void UpdateStage()
    {
        IReadOnlyList<string> running;
        try { running = riot.Running(); }
        catch (Exception e) // also called from the loop's finally: a throw here would end the service, and with it the whole host
        {
            log.LogWarning("Process list unavailable: {Message}", e.Message);
            return;
        }
        GameProcessRunning = running.Contains("League of Legends"); // the game clock asks too: one process list for both
        var stage = running.Any(n => n.StartsWith("LeagueClient", StringComparison.Ordinal) || n == "League of Legends") ? "league-starting"
            : running.Any(n => n.StartsWith("RiotClient", StringComparison.Ordinal)) ? "riot-client" : "none";
        if (stage == _stage) return;
        _stage = stage;
        if (!_connected) events.Publish("connection", new { connected = false, stage });
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var credentials = LcuDiscovery.Find();
                if (credentials is null) { UpdateStage(); await Task.Delay(2000, stoppingToken); continue; }

                using var http = new LcuHttpClient(credentials);
                client.Current = http;
                log.LogInformation("Connected to the client on port {Port}", credentials.Port);
                await engine.ResyncAsync(stoppingToken);
                Connected = true;
                await foreach (var e in LcuEventStream.ReadAsync(credentials, stoppingToken))
                    await engine.HandleAsync(e, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception e) { log.LogWarning("Connection lost: {Message}", e.Message); }
            finally { Connected = false; client.Current = null; UpdateStage(); }
            await Task.Delay(2000, stoppingToken);
        }
    }
}

/// <summary>Lets features hold one client while the underlying connection changes across client restarts.</summary>
public sealed class SwappableLcuClient : ILcuClient, ILcuAssets
{
    public ILcuClient? Current { get; set; }

    public Task<LcuAsset> GetAssetAsync(string path, CancellationToken ct = default) =>
        Current is ILcuAssets a ? a.GetAssetAsync(path, ct) : Task.FromResult(new LcuAsset(503, null, null));

    public Task<LcuResponse> SendAsync(HttpMethod method, string path, object? body = null, CancellationToken ct = default) =>
        Current is { } c ? c.SendAsync(method, path, body, ct) : Task.FromResult(new LcuResponse(503, null));
}
