using System.Text.Json.Nodes;

namespace Glamour.Lcu;

/// <summary>The running game's Live Client Data API (port 2999, no auth, read-only). Null when no game answers.</summary>
public interface ILiveGameClient
{
    Task<JsonNode?> GetAsync(string path, CancellationToken ct = default);
}

public sealed class LiveGameHttpClient : ILiveGameClient, IDisposable
{
    public const int Port = 2999;
    private readonly HttpClient _http;

    public LiveGameHttpClient()
    {
        // The game serves a self-signed certificate; this client only ever talks to 127.0.0.1:2999.
        var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true };
        _http = new HttpClient(handler) { BaseAddress = new Uri($"https://127.0.0.1:{Port}"), Timeout = TimeSpan.FromSeconds(2) };
    }

    public async Task<JsonNode?> GetAsync(string path, CancellationToken ct = default)
    {
        try
        {
            using var response = await _http.GetAsync(path, ct);
            return response.IsSuccessStatusCode ? JsonNode.Parse(await response.Content.ReadAsStringAsync(ct)) : null;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException { InnerException: TimeoutException } or System.Text.Json.JsonException)
        {
            return null; // no game, or it is loading
        }
    }

    public void Dispose() => _http.Dispose();
}
