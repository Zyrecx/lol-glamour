using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Glamour.Core;
using Glamour.Host.Push;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Glamour.Tests;

public sealed class FakeWebPush : IWebPushSender
{
    public List<PhoneNotification> Sent { get; } = [];
    public int Accepting { get; set; } = 1;

    public Task<int> SendAsync(PhoneNotification n, CancellationToken ct) { lock (Sent) Sent.Add(n); return Task.FromResult(Accepting); }
}

public class PushStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "glamour-push-" + Guid.NewGuid().ToString("N") + ".json");
    public void Dispose() { if (File.Exists(_path)) File.Delete(_path); }

    private PushStore Open() => new(_path, NullLogger<PushStore>.Instance);

    private static byte[] Decode(string b64Url) => Convert.FromBase64String(b64Url.Replace('-', '+').Replace('_', '/').PadRight((b64Url.Length + 3) / 4 * 4, '='));

    [Fact]
    public void Vapid_keys_are_made_once_and_survive_a_restart()
    {
        var first = Open().Vapid(WebPushSender.NewKeys);
        var again = Open().Vapid(() => throw new InvalidOperationException("must not regenerate"));
        Assert.Equal(first, again);
        Assert.Equal(65, Decode(first.Public).Length);
        Assert.Equal(0x04, Decode(first.Public)[0]);
        Assert.Equal(32, Decode(first.Private).Length);
    }

    [Fact]
    public void A_device_has_one_subscription_and_the_same_endpoint_moves_with_a_new_device()
    {
        var s = Open();
        s.Set(new("a", "https://push/1", "k", "x"));
        s.Set(new("a", "https://push/2", "k", "x"));
        Assert.Equal(["https://push/2"], s.All().Select(x => x.Endpoint));
        s.Set(new("b", "https://push/2", "k", "x"));
        Assert.Equal(["b"], s.All().Select(x => x.DeviceId));
        Assert.True(Open().Has("b"));
    }

    [Fact]
    public void Removing_a_device_or_an_endpoint_forgets_it()
    {
        var s = Open();
        s.Set(new("a", "https://push/1", "k", "x"));
        s.Set(new("b", "https://push/2", "k", "x"));
        s.RemoveDevice("a");
        s.RemoveEndpoint("https://push/2");
        Assert.Empty(Open().All());
    }

    [Fact]
    public void Urgent_events_expire_fast_and_titles_become_url_safe_topics()
    {
        Assert.Equal((Lib.Net.Http.WebPush.PushMessageUrgency.High, 30), WebPushSender.Delivery(new("t", "b", 5)));
        Assert.Equal((Lib.Net.Http.WebPush.PushMessageUrgency.Normal, 300), WebPushSender.Delivery(new("t", "b", 3)));
        Assert.Equal("match-found", WebPushSender.Tag(new("Match found", "b", 5)));
        Assert.Equal(32, WebPushSender.Tag(new(new string('a', 80), "b", 3)).Length);
        Assert.Equal("glamour", WebPushSender.Tag(new("!!!", "b", 3)));
    }
}

public class PushHostTests : IDisposable
{
    private readonly TestHost _host = new();
    public void Dispose() => _host.Dispose();

    private async Task<HttpClient> PairedPhoneAsync()
    {
        var pc = _host.Client(TestHost.Loopback);
        var json = await (await pc.PostAsync("/api/auth/pairing", null)).Content.ReadFromJsonAsync<JsonElement>();
        var url = json.GetProperty("url").GetString()!;
        var phone = _host.Client();
        Assert.Equal(HttpStatusCode.OK, (await phone.PostAsJsonAsync("/api/auth/pair", new { secret = url[(url.IndexOf('#') + 1)..], name = "Phone" })).StatusCode);
        return phone;
    }

    private static object Sub(string endpoint = "https://fcm.googleapis.com/send/abc") => new { endpoint, p256dh = "BPk", auth = "sec" };

    [Fact]
    public async Task A_paired_phone_gets_the_public_key_and_can_subscribe_and_unsubscribe()
    {
        var phone = await PairedPhoneAsync();
        var key = (await phone.GetFromJsonAsync<JsonElement>("/api/push/key")).GetProperty("key").GetString();
        Assert.False(string.IsNullOrEmpty(key));
        Assert.Equal(key, (await phone.GetFromJsonAsync<JsonElement>("/api/push/key")).GetProperty("key").GetString());

        Assert.False((await phone.GetFromJsonAsync<JsonElement>("/api/push/status")).GetProperty("subscribed").GetBoolean());
        Assert.Equal(HttpStatusCode.NoContent, (await phone.PutAsJsonAsync("/api/push/subscription", Sub())).StatusCode);
        Assert.True((await phone.GetFromJsonAsync<JsonElement>("/api/push/status")).GetProperty("subscribed").GetBoolean());
        Assert.Equal(HttpStatusCode.NoContent, (await phone.DeleteAsync("/api/push/subscription")).StatusCode);
        Assert.False((await phone.GetFromJsonAsync<JsonElement>("/api/push/status")).GetProperty("subscribed").GetBoolean());
    }

    [Theory]
    [InlineData("http://push.example/x")]
    [InlineData("not a url")]
    [InlineData("")]
    public async Task Only_https_push_endpoints_are_accepted(string endpoint)
    {
        var phone = await PairedPhoneAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await phone.PutAsJsonAsync("/api/push/subscription", Sub(endpoint))).StatusCode);
    }

    [Fact]
    public async Task The_pc_itself_is_not_a_device_and_cannot_subscribe()
    {
        var pc = _host.Client(TestHost.Loopback);
        Assert.Equal(HttpStatusCode.BadRequest, (await pc.PutAsJsonAsync("/api/push/subscription", Sub())).StatusCode);
    }

    [Fact]
    public async Task Revoking_a_device_removes_its_subscription()
    {
        var phone = await PairedPhoneAsync();
        await phone.PutAsJsonAsync("/api/push/subscription", Sub());
        var pc = _host.Client(TestHost.Loopback);
        var devices = await pc.GetFromJsonAsync<JsonElement>("/api/auth/devices");
        var id = devices[0].GetProperty("id").GetString();
        Assert.Equal(HttpStatusCode.NoContent, (await pc.DeleteAsync($"/api/auth/devices/{id}")).StatusCode);
        Assert.Empty(_host.Services.GetRequiredService<PushStore>().All());
    }

    [Fact]
    public async Task Choosing_one_channel_turns_the_other_off_and_both_at_once_is_refused()
    {
        var pc = _host.Client(TestHost.Loopback);
        await pc.PatchAsJsonAsync("/api/settings", new { ntfyEnabled = true });
        var s = await (await pc.PatchAsJsonAsync("/api/settings", new { webPushEnabled = true })).Content.ReadFromJsonAsync<JsonObject>();
        Assert.True(s!["webPushEnabled"]!.GetValue<bool>());
        Assert.False(s["ntfyEnabled"]!.GetValue<bool>());
        s = await (await pc.PatchAsJsonAsync("/api/settings", new { ntfyEnabled = true })).Content.ReadFromJsonAsync<JsonObject>();
        Assert.False(s!["webPushEnabled"]!.GetValue<bool>());
        Assert.Equal(HttpStatusCode.BadRequest, (await pc.PutAsJsonAsync("/api/settings", new { ntfyEnabled = true, webPushEnabled = true })).StatusCode);
    }

    [Fact]
    public async Task Test_endpoint_sends_through_web_push_and_says_so_when_nobody_is_subscribed()
    {
        var pc = _host.Client(TestHost.Loopback);
        await pc.PatchAsJsonAsync("/api/settings", new { webPushEnabled = true });
        Assert.Equal(HttpStatusCode.NoContent, (await pc.PostAsync("/api/notify/test", null)).StatusCode);
        Assert.Single(_host.WebPush.Sent);
        _host.WebPush.Accepting = 0;
        Assert.Equal(HttpStatusCode.BadRequest, (await pc.PostAsync("/api/notify/test", null)).StatusCode);
    }
}
