using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Glamour.Tests;

public class AuthHostTests : IDisposable
{
    private readonly TestHost _host = new();

    public void Dispose() => _host.Dispose();

    private async Task<string> NewSecretAsync()
    {
        var r = await _host.Client(TestHost.Loopback).PostAsync("/api/auth/pairing", null);
        r.EnsureSuccessStatusCode();
        var json = await r.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("<svg", json.GetProperty("svg").GetString());
        var url = json.GetProperty("url").GetString()!;
        Assert.Contains("/pair.html#", url);
        return url[(url.IndexOf('#') + 1)..];
    }

    private async Task<HttpClient> PairedPhoneAsync(string name = "Phone")
    {
        var phone = _host.Client();
        var r = await phone.PostAsJsonAsync("/api/auth/pair", new { secret = await NewSecretAsync(), name });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return phone;
    }

    [Fact]
    public async Task Pc_itself_needs_no_login()
    {
        Assert.Equal(HttpStatusCode.OK, (await _host.Client(TestHost.Loopback).GetAsync("/api/state")).StatusCode);
    }

    [Fact]
    public async Task Unpaired_lan_device_is_refused_everywhere_except_pair_and_login()
    {
        var lan = _host.Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await lan.GetAsync("/api/state")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await lan.GetAsync("/api/settings")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await lan.GetAsync("/api/events")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await lan.PostAsync("/api/queue", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await lan.PostAsync("/api/auth/pairing", null)).StatusCode); // a stranger cannot mint QR codes
        Assert.Equal(HttpStatusCode.Unauthorized, (await lan.PutAsJsonAsync("/api/auth/password", new { password = "hijack the pc" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await lan.GetAsync("/api/auth/status")).StatusCode);
    }

    [Theory]
    [InlineData("::1")]
    [InlineData("::ffff:127.0.0.1")]
    public async Task Loopback_in_ipv6_forms_is_the_pc(string ip)
    {
        Assert.Equal(HttpStatusCode.OK, (await _host.Client(ip).GetAsync("/api/state")).StatusCode);
    }

    [Fact]
    public async Task Request_with_proxy_headers_does_not_count_as_the_pc()
    {
        var c = _host.Client(TestHost.Loopback);
        c.DefaultRequestHeaders.Add("X-Forwarded-For", "203.0.113.9");
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/state")).StatusCode);
    }

    [Fact]
    public async Task Loopback_request_with_a_foreign_host_name_is_refused_dns_rebinding()
    {
        var req = new HttpRequestMessage(HttpMethod.Get, "/api/state");
        req.Headers.Host = "evil.example";
        Assert.Equal(HttpStatusCode.Forbidden, (await _host.Client(TestHost.Loopback).SendAsync(req)).StatusCode);
    }

    [Theory]
    [InlineData("Origin", "http://evil.example")]
    [InlineData("Origin", "null")]
    [InlineData("Sec-Fetch-Site", "cross-site")]
    public async Task Cross_site_browser_writes_are_refused_even_from_the_pc(string header, string value)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/queue");
        req.Headers.Add(header, value);
        Assert.Equal(HttpStatusCode.Forbidden, (await _host.Client(TestHost.Loopback).SendAsync(req)).StatusCode);
    }

    [Fact]
    public async Task Same_origin_browser_writes_are_allowed()
    {
        var req = new HttpRequestMessage(HttpMethod.Put, "/api/settings") { Content = JsonContent.Create(new { }) };
        req.Headers.Add("Origin", "http://localhost");
        Assert.Equal(HttpStatusCode.OK, (await _host.Client(TestHost.Loopback).SendAsync(req)).StatusCode);
    }

    [Fact]
    public async Task Qr_pairing_signs_a_phone_in_and_the_secret_is_single_use()
    {
        var secret = await NewSecretAsync();
        var phone = _host.Client();
        var paired = await phone.PostAsJsonAsync("/api/auth/pair", new { secret, name = "Pixel" });
        Assert.Equal(HttpStatusCode.OK, paired.StatusCode);
        var cookie = Assert.Single(paired.Headers.GetValues("Set-Cookie"));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(HttpStatusCode.OK, (await phone.GetAsync("/api/state")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _host.Client().PostAsJsonAsync("/api/auth/pair", new { secret })).StatusCode);
    }

    [Fact]
    public async Task Several_devices_work_at_once_and_one_can_be_removed()
    {
        var phone = await PairedPhoneAsync("Phone");
        var laptop = await PairedPhoneAsync("Laptop");
        var pc = _host.Client(TestHost.Loopback);

        var devices = await pc.GetFromJsonAsync<JsonElement>("/api/auth/devices");
        Assert.Equal(2, devices.GetArrayLength());
        var phoneId = devices.EnumerateArray().Single(d => d.GetProperty("name").GetString() == "Phone").GetProperty("id").GetString();

        Assert.Equal(HttpStatusCode.NoContent, (await pc.DeleteAsync($"/api/auth/devices/{phoneId}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await phone.GetAsync("/api/state")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await laptop.GetAsync("/api/state")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await pc.DeleteAsync($"/api/auth/devices/{phoneId}")).StatusCode);
    }

    [Fact]
    public async Task A_paired_device_sees_itself_marked_current()
    {
        var phone = await PairedPhoneAsync("Phone");
        await PairedPhoneAsync("Laptop");
        var devices = await phone.GetFromJsonAsync<JsonElement>("/api/auth/devices");
        var current = devices.EnumerateArray().Single(d => d.GetProperty("current").GetBoolean());
        Assert.Equal("Phone", current.GetProperty("name").GetString());
    }

    [Fact]
    public async Task Logout_ends_only_this_browser()
    {
        var phone = await PairedPhoneAsync();
        var laptop = await PairedPhoneAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await phone.PostAsync("/api/auth/logout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await phone.GetAsync("/api/state")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await laptop.GetAsync("/api/state")).StatusCode);
    }

    [Fact]
    public async Task Logout_all_ends_every_device()
    {
        var phone = await PairedPhoneAsync();
        var laptop = await PairedPhoneAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await _host.Client(TestHost.Loopback).PostAsync("/api/auth/logout-all", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await phone.GetAsync("/api/state")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await laptop.GetAsync("/api/state")).StatusCode);
    }

    [Fact]
    public async Task Revoking_a_device_ends_its_open_event_stream()
    {
        var phone = await PairedPhoneAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var response = await phone.GetAsync("/api/events", HttpCompletionOption.ResponseHeadersRead, cts.Token);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cts.Token));
        Assert.Contains("event: snapshot", await HostEventsTests.ReadEventAsync(reader));

        await _host.Client(TestHost.Loopback).PostAsync("/api/auth/logout-all", null, cts.Token);

        var rest = await reader.ReadToEndAsync(cts.Token); // returns because the server closed the stream, not because of the timeout
        Assert.False(cts.IsCancellationRequested);
        Assert.DoesNotContain("event: settings", rest);
    }

    [Fact]
    public async Task Password_is_set_on_the_pc_and_used_by_other_browsers()
    {
        var pc = _host.Client(TestHost.Loopback);
        Assert.Equal(HttpStatusCode.BadRequest, (await pc.PutAsJsonAsync("/api/auth/password", new { password = "short" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await pc.PutAsJsonAsync("/api/auth/password", new { password = "4821" })).StatusCode);

        var other = _host.Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await other.PostAsJsonAsync("/api/auth/login", new { password = "wrong password" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await other.PostAsJsonAsync("/api/auth/login", new { password = "4821", name = "Other browser" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await other.GetAsync("/api/state")).StatusCode);
    }

    [Fact]
    public async Task A_paired_phone_cannot_add_devices_by_minting_qr_codes_or_changing_the_password()
    {
        var phone = await PairedPhoneAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await phone.PostAsync("/api/auth/pairing", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await phone.PutAsJsonAsync("/api/auth/password", new { password = "4821" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await phone.DeleteAsync("/api/auth/password")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _host.Client().PostAsJsonAsync("/api/auth/login", new { password = "4821" })).StatusCode); // nothing was set
        Assert.Equal(HttpStatusCode.OK, (await phone.GetAsync("/api/auth/devices")).StatusCode); // the rest still works
    }

    [Fact]
    public async Task Login_is_refused_when_no_password_is_set()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _host.Client().PostAsJsonAsync("/api/auth/login", new { password = "4821" })).StatusCode);
    }

    [Fact]
    public async Task Repeated_wrong_passwords_lock_the_client_out_even_for_the_right_one()
    {
        await _host.Client(TestHost.Loopback).PutAsJsonAsync("/api/auth/password", new { password = "4821" });
        var attacker = _host.Client("192.168.1.99");
        for (var i = 0; i < 6; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await attacker.PostAsJsonAsync("/api/auth/login", new { password = "guess " + i })).StatusCode);

        var locked = await attacker.PostAsJsonAsync("/api/auth/login", new { password = "4821" });
        Assert.Equal(HttpStatusCode.TooManyRequests, locked.StatusCode);
        Assert.True(locked.Headers.Contains("Retry-After"));

        // The lock is per address: the owner on another device is unaffected.
        Assert.Equal(HttpStatusCode.OK, (await _host.Client("192.168.1.60").PostAsJsonAsync("/api/auth/login", new { password = "4821" })).StatusCode);
    }

    [Fact]
    public async Task Malformed_bodies_are_rejected_without_crashing()
    {
        var lan = _host.Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await lan.PostAsJsonAsync("/api/auth/pair", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await lan.PostAsJsonAsync("/api/auth/login", new { password = new string('x', 5000) })).StatusCode);
        var junk = await lan.PostAsync("/api/auth/pair", new StringContent("{ nope", System.Text.Encoding.UTF8, "application/json"));
        Assert.True((int)junk.StatusCode is 400 or 401);
    }

    [Fact]
    public async Task Pair_and_login_pages_are_served_without_a_session()
    {
        var lan = _host.Client();
        Assert.Equal(HttpStatusCode.OK, (await lan.GetAsync("/pair.html")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await lan.GetAsync("/login.html")).StatusCode);
    }
}
