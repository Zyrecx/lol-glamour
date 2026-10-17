using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Glamour.Host.Auth;
using Glamour.Host.Push;
using Microsoft.Extensions.Time.Testing;

namespace Glamour.Tests;

public class SecurityHostTests : IDisposable
{
    private readonly TestHost _host = new();

    public void Dispose() => _host.Dispose();

    private async Task<string> NewSecretAsync()
    {
        var r = await _host.Client(TestHost.Loopback).PostAsync("/api/auth/pairing", null);
        r.EnsureSuccessStatusCode();
        var url = (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("url").GetString()!;
        return url[(url.IndexOf('#') + 1)..];
    }

    private static HttpRequestMessage Req(HttpMethod m, string path, string host, object? body = null)
    {
        var r = new HttpRequestMessage(m, path);
        r.Headers.Host = host;
        if (body is not null) r.Content = JsonContent.Create(body);
        return r;
    }

    [Fact]
    public async Task A_session_cookie_on_a_name_rebound_to_loopback_does_not_make_the_caller_the_pc()
    {
        // The attacker got a session for evil.example somehow (e.g. a guessed PIN), then rebinds evil.example to 127.0.0.1.
        var paired = await _host.Client().PostAsJsonAsync("/api/auth/pair", new { secret = await NewSecretAsync() });
        Assert.Equal(HttpStatusCode.OK, paired.StatusCode);
        var cookie = paired.Headers.GetValues("Set-Cookie").First().Split(';')[0];

        var rebound = _host.Client(TestHost.Loopback);
        var mint = Req(HttpMethod.Post, "/api/auth/pairing", "evil.example");
        mint.Headers.Add("Cookie", cookie);
        Assert.Equal(HttpStatusCode.Forbidden, (await rebound.SendAsync(mint)).StatusCode);
        var pin = Req(HttpMethod.Put, "/api/auth/password", "evil.example", new { password = "1234" });
        pin.Headers.Add("Cookie", cookie);
        Assert.Equal(HttpStatusCode.Forbidden, (await rebound.SendAsync(pin)).StatusCode);
    }

    [Theory]
    [InlineData("evil.example", HttpStatusCode.Forbidden)]
    [InlineData("pc.evil.example:5170", HttpStatusCode.Forbidden)]
    [InlineData("192.168.1.5:5170", HttpStatusCode.Unauthorized)] // allowed name, wrong PIN
    [InlineData("[fd00::5]:5170", HttpStatusCode.Unauthorized)]
    [InlineData("loq.local:5171", HttpStatusCode.Unauthorized)]
    [InlineData("loq.tail1234.ts.net", HttpStatusCode.Unauthorized)]
    public async Task Lan_api_calls_must_name_the_pc_by_address_or_local_name(string host, HttpStatusCode expected)
    {
        Assert.Equal(expected, (await _host.Client().SendAsync(Req(HttpMethod.Post, "/api/auth/login", host, new { password = "0000" }))).StatusCode);
    }

    [Fact]
    public async Task Ipv6_addresses_in_one_64_share_a_lock()
    {
        await _host.Client(TestHost.Loopback).PutAsJsonAsync("/api/auth/password", new { password = "4821" });
        for (var i = 1; i <= 6; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await _host.Client($"fd00::{i}").PostAsJsonAsync("/api/auth/login", new { password = "0000" })).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await _host.Client("fd00::99").PostAsJsonAsync("/api/auth/login", new { password = "4821" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _host.Client("fd00:0:0:1::1").PostAsJsonAsync("/api/auth/login", new { password = "4821" })).StatusCode);
    }

    [Fact]
    public async Task Pin_guesses_from_many_addresses_lock_pin_login_but_qr_pairing_still_works()
    {
        await _host.Client(TestHost.Loopback).PutAsJsonAsync("/api/auth/password", new { password = "4821" });
        for (var i = 1; i <= 21; i++)
            await _host.Client($"192.168.2.{i}").PostAsJsonAsync("/api/auth/login", new { password = "0000" });
        Assert.Equal(HttpStatusCode.TooManyRequests, (await _host.Client("192.168.3.1").PostAsJsonAsync("/api/auth/login", new { password = "4821" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _host.Client("192.168.3.2").PostAsJsonAsync("/api/auth/pair", new { secret = await NewSecretAsync() })).StatusCode);
    }
}

public class SecurityThrottleTests
{
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UnixEpoch);

    [Fact]
    public void Shared_budget_locks_every_address_and_a_success_does_not_reset_it()
    {
        var t = new LoginThrottle(_time);
        for (var i = 0; i < 21; i++) { Assert.Null(t.RetryAfter("ip" + i, shared: true)); t.Failure("ip" + i, shared: true); }
        Assert.NotNull(t.RetryAfter("fresh", shared: true));
        Assert.Null(t.RetryAfter("fresh")); // pairing does not use the shared budget
        t.Success("ip0");
        Assert.NotNull(t.RetryAfter("fresh", shared: true));
    }

    [Fact]
    public void Failures_without_the_shared_flag_do_not_spend_it()
    {
        var t = new LoginThrottle(_time);
        for (var i = 0; i < 50; i++) t.Failure("ip" + i);
        Assert.Null(t.RetryAfter("fresh", shared: true));
    }

    [Fact]
    public void Many_addresses_within_the_hour_cannot_grow_memory_without_bound()
    {
        var t = new LoginThrottle(_time);
        for (var i = 0; i < 25_000; i++) t.Failure("ip" + i);
        Assert.True(t.Tracked <= 10_000, $"tracked {t.Tracked}");
    }
}

public class PushEndpointRuleTests
{
    [Theory]
    [InlineData("https://fcm.googleapis.com/fcm/send/abc", true)]
    [InlineData("https://updates.push.services.mozilla.com/wpush/v2/abc", true)]
    [InlineData("https://web.push.apple.com/abc", true)]
    [InlineData("https://wns2-db5p.notify.windows.com/w/?token=abc", true)]
    [InlineData("http://fcm.googleapis.com/x", false)]
    [InlineData("https://127.0.0.1/x", false)]
    [InlineData("https://192.168.1.1/admin", false)]
    [InlineData("https://[::1]:2999/x", false)]
    [InlineData("https://localhost:2999/x", false)]
    [InlineData("https://router/x", false)]
    [InlineData("https://nas.local/x", false)]
    [InlineData("https://printer.home.arpa/x", false)]
    [InlineData("https://user:pw@fcm.googleapis.com/x", false)]
    [InlineData("not a url", false)]
    [InlineData(null, false)]
    public void Only_public_https_push_services_are_accepted(string? endpoint, bool ok) => Assert.Equal(ok, PushEndpoints.IsPushService(endpoint));
}
