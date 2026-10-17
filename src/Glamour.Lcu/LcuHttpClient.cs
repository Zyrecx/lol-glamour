using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace Glamour.Lcu;

public sealed class LcuHttpClient : ILcuClient, ILcuAssets, IDisposable
{
    private readonly HttpClient _http;

    public LcuHttpClient(LcuCredentials credentials)
    {
        // The client serves a self-signed certificate on localhost, so validation is off for this one connection.
        var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true };
        _http = new HttpClient(handler) { BaseAddress = new Uri($"https://127.0.0.1:{credentials.Port}"), Timeout = TimeSpan.FromSeconds(10) };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials.BasicAuth);
    }

    public async Task<LcuResponse> SendAsync(HttpMethod method, string path, object? body = null, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = JsonContent.Create(body);
        using var response = await _http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        JsonNode? json = null;
        if (!string.IsNullOrWhiteSpace(text))
        {
            try { json = JsonNode.Parse(text); } catch (System.Text.Json.JsonException) { json = JsonValue.Create(text); }
        }
        return new LcuResponse((int)response.StatusCode, json);
    }

    public async Task<LcuAsset> GetAssetAsync(string path, CancellationToken ct = default)
    {
        using var response = await _http.GetAsync(path, ct);
        return response.IsSuccessStatusCode
            ? new LcuAsset((int)response.StatusCode, await response.Content.ReadAsByteArrayAsync(ct), response.Content.Headers.ContentType?.MediaType)
            : new LcuAsset((int)response.StatusCode, null, null);
    }

    public void Dispose() => _http.Dispose();
}
