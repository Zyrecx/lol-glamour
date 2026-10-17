using System.Text.Json.Nodes;

namespace Glamour.Lcu;

public sealed record LcuCredentials(int Port, string Token)
{
    public string BasicAuth => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"riot:{Token}"));
}

public sealed record LcuResponse(int Status, JsonNode? Body)
{
    public bool IsSuccess => Status is >= 200 and < 300;
}

/// <summary>One OnJsonApiEvent message. EventType is Create, Update or Delete.</summary>
public sealed record LcuEvent(string Uri, string EventType, JsonNode? Data);

public interface ILcuClient
{
    Task<LcuResponse> SendAsync(HttpMethod method, string path, object? body = null, CancellationToken ct = default);
}

public static class LcuClientExtensions
{
    public static Task<LcuResponse> GetAsync(this ILcuClient c, string path, CancellationToken ct = default) => c.SendAsync(HttpMethod.Get, path, null, ct);
}

/// <summary>An image the client serves (champion splash tiles, rune icons). Bytes are null on failure.</summary>
public sealed record LcuAsset(int Status, byte[]? Bytes, string? ContentType);

public interface ILcuAssets
{
    Task<LcuAsset> GetAssetAsync(string path, CancellationToken ct = default);
}
