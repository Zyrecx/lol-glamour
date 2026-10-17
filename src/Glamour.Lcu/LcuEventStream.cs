using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;

namespace Glamour.Lcu;

/// <summary>WAMP-style event feed of the client. Ends when the socket closes.</summary>
public static class LcuEventStream
{
    public static async IAsyncEnumerable<LcuEvent> ReadAsync(LcuCredentials credentials, [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var socket = new ClientWebSocket();
        socket.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        socket.Options.SetRequestHeader("Authorization", "Basic " + credentials.BasicAuth);
        await socket.ConnectAsync(new Uri($"wss://127.0.0.1:{credentials.Port}/"), ct);
        await socket.SendAsync(Encoding.UTF8.GetBytes("[5,\"OnJsonApiEvent\"]"), WebSocketMessageType.Text, true, ct);

        var buffer = new byte[16 * 1024];
        while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            using var message = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer, ct);
                message.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);
            if (result.MessageType == WebSocketMessageType.Close) yield break;
            if (Parse(Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length)) is { } e) yield return e;
        }
    }

    /// <summary>Messages look like [8, "OnJsonApiEvent", {uri, eventType, data}]. The subscribe ack is empty.
    /// A malformed message is skipped (null): throwing here would end the stream and force a reconnect.</summary>
    public static LcuEvent? Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try
        {
            if (JsonNode.Parse(text) is not JsonArray { Count: 3 } array || array[2] is not JsonObject payload) return null;
            var uri = payload["uri"] is JsonValue u && u.TryGetValue<string>(out var us) ? us : null;
            var type = payload["eventType"] is JsonValue t && t.TryGetValue<string>(out var ts) ? ts : null;
            return uri is null || type is null ? null : new LcuEvent(uri, type, payload["data"]);
        }
        catch (System.Text.Json.JsonException) { return null; }
    }
}
