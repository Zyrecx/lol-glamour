using System.Text.Json;
using Glamour.Core;
using Lib.Net.Http.WebPush;
using Lib.Net.Http.WebPush.Authentication;

namespace Glamour.Host.Push;

/// <summary>Sends one notification to every stored subscription. Faked in tests: the real one talks to the browser vendors' push services.</summary>
public interface IWebPushSender
{
    /// <summary>How many subscriptions accepted the message.</summary>
    Task<int> SendAsync(PhoneNotification n, CancellationToken ct);
}

public sealed class WebPushSender(PushStore store, IConfiguration config, ILogger<WebPushSender> log) : IWebPushSender
{
    private readonly PushServiceClient _client = new();

    public static (string Public, string Private) NewKeys()
    {
        // P-256 pair, both halves url-safe base64: the public point uncompressed (65 bytes), the private scalar (32 bytes).
        using var key = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        var p = key.ExportParameters(true);
        return (B64([0x04, .. p.Q.X!, .. p.Q.Y!]), B64(p.D!));
    }

    private static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>The VAPID subject goes to the push service. A real address is never needed; set <c>Glamour:PushSubject</c> to use one.</summary>
    private string Subject => config["Glamour:PushSubject"] is { Length: > 0 } s ? s : "mailto:glamour@example.com";

    /// <summary>Time-critical events (priority 4 and 5: match found, your turn) expire quickly so a late push cannot arrive after the window closed.</summary>
    public static (PushMessageUrgency Urgency, int Ttl) Delivery(PhoneNotification n) => n.Priority >= 4 ? (PushMessageUrgency.High, 30) : (PushMessageUrgency.Normal, 300);

    /// <summary>Same title replaces the previous one instead of stacking. Also the Topic header: url-safe characters, at most 32.</summary>
    public static string Tag(PhoneNotification n)
    {
        var t = new string(n.Title.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
        return t.Length > 32 ? t[..32] : t.Length == 0 ? "glamour" : t;
    }

    public async Task<int> SendAsync(PhoneNotification n, CancellationToken ct)
    {
        var subs = store.All();
        if (subs.Count == 0) return 0;
        var (pub, priv) = store.Vapid(NewKeys);
        var auth = new VapidAuthentication(pub, priv) { Subject = Subject };
        var (urgency, ttl) = Delivery(n);
        var tag = Tag(n);
        var payload = JsonSerializer.Serialize(new { title = n.Title, body = n.Body, tag, urgent = n.Priority >= 4, action = n.Action });
        var sent = 0;
        foreach (var s in subs)
        {
            var sub = new PushSubscription { Endpoint = s.Endpoint };
            sub.SetKey(PushEncryptionKeyName.P256DH, s.P256dh);
            sub.SetKey(PushEncryptionKeyName.Auth, s.Auth);
            try
            {
                await _client.RequestPushMessageDeliveryAsync(sub, new PushMessage(payload) { Urgency = urgency, TimeToLive = ttl, Topic = tag }, auth, ct);
                sent++;
            }
            catch (PushServiceClientException ex) when (ex.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Gone)
            {
                store.RemoveEndpoint(s.Endpoint); // unsubscribed or uninstalled
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning("Web push to a device failed: {Message}", ex.Message);
            }
        }
        return sent;
    }
}
