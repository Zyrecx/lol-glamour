using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Glamour.Tests;

public sealed class ExposureTests : IDisposable
{
    private readonly TestHost _plain = new();
    private readonly TestHost _https = new(https: true);
    private readonly TestHost _lan = new(lan: true);

    public void Dispose() { _plain.Dispose(); _https.Dispose(); _lan.Dispose(); }

    private static async Task<HttpClient> PairAsync(TestHost host, bool tls)
    {
        var pc = host.Client(TestHost.Loopback);
        var json = await (await pc.PostAsync("/api/auth/pairing", null)).Content.ReadFromJsonAsync<JsonElement>();
        var url = json.GetProperty("url").GetString()!;
        var phone = host.Client(tls: tls);
        Assert.Equal(HttpStatusCode.OK, (await phone.PostAsJsonAsync("/api/auth/pair", new { secret = url[(url.IndexOf('#') + 1)..], name = "Phone" })).StatusCode);
        return phone;
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("203.0.113.9")]
    [InlineData("172.32.0.1")]
    [InlineData("2001:db8::1")]
    public async Task Public_addresses_are_refused_even_to_the_login_page(string ip)
    {
        var c = _plain.Client(ip);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync("/api/auth/status")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await c.GetAsync("/")).StatusCode);
    }

    [Theory]
    [InlineData("192.168.43.7")]
    [InlineData("10.1.2.3")]
    [InlineData("172.20.0.5")]
    [InlineData("100.100.1.1")]
    [InlineData("fe80::1")]
    public async Task Private_addresses_are_let_through(string ip)
    {
        Assert.Equal(HttpStatusCode.OK, (await _plain.Client(ip).GetAsync("/api/auth/status")).StatusCode);
    }

    [Fact]
    public async Task Pages_are_never_served_from_a_cache()
    {
        // A cached index.html kept loading the previous build's chunks (still in wwwroot) in the panel.
        var pc = _plain.Client(TestHost.Loopback);
        foreach (var path in new[] { "/", "/pair.html" })
        {
            var res = await pc.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            Assert.True(res.Headers.CacheControl?.NoCache, path);
        }
    }

    [Fact]
    public async Task Phones_get_the_pages_by_default_and_the_pc_can_switch_them_off()
    {
        var phone = await PairAsync(_plain, tls: false);
        Assert.Equal(HttpStatusCode.OK, (await phone.GetAsync("/")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _plain.Client().GetAsync("/pair.html")).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await phone.PutAsJsonAsync("/api/network/web-ui", new { enabled = false })).StatusCode); // PC only
        Assert.Equal(HttpStatusCode.NoContent, (await _plain.Client(TestHost.Loopback).PutAsJsonAsync("/api/network/web-ui", new { enabled = false })).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await phone.GetAsync("/")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _plain.Client().GetAsync("/pair.html")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await phone.GetAsync("/api/state")).StatusCode); // the API (for another client) keeps working
        Assert.Equal(HttpStatusCode.OK, (await _plain.Client().GetAsync("/api/auth/status")).StatusCode);
        Assert.NotEqual(HttpStatusCode.NotFound, (await _plain.Client(TestHost.Loopback).GetAsync("/")).StatusCode); // the PC can always turn it back on

        var net = await _plain.Client(TestHost.Loopback).GetFromJsonAsync<JsonElement>("/api/network");
        Assert.False(net.GetProperty("webUiEnabled").GetBoolean());
    }

    [Fact]
    public void Web_ui_choice_survives_a_restart()
    {
        var path = Path.Combine(Path.GetTempPath(), "glamour-host-" + Guid.NewGuid().ToString("N"), "host.json");
        try
        {
            var log = Microsoft.Extensions.Logging.Abstractions.NullLogger<Glamour.Host.Network.HostSettings>.Instance;
            Assert.True(new Glamour.Host.Network.HostSettings(path, log).WebUiEnabled);
            new Glamour.Host.Network.HostSettings(path, log).WebUiEnabled = false;
            Assert.False(new Glamour.Host.Network.HostSettings(path, log).WebUiEnabled);
            File.WriteAllText(path, "{ not json");
            Assert.True(new Glamour.Host.Network.HostSettings(path, log).WebUiEnabled); // unreadable file: defaults
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }

    [Fact]
    public async Task With_https_on_plain_http_from_the_lan_only_serves_the_certificate_pages()
    {
        Assert.Equal(HttpStatusCode.NoContent, (await _https.Client(TestHost.Loopback).PutAsJsonAsync("/api/network/require-https", new { enabled = true })).StatusCode);
        var lan = _https.Client(follow: false);
        var crt = await lan.GetAsync("/ca.crt");
        Assert.Equal(HttpStatusCode.OK, crt.StatusCode);
        Assert.Equal("application/x-x509-ca-cert", crt.Content.Headers.ContentType?.MediaType);
        var ca = _https.Services.GetRequiredService<Glamour.Host.Network.LocalCaHolder>().Current!;
        Assert.Equal(ca.CaDer, await crt.Content.ReadAsByteArrayAsync());

        var install = await lan.GetAsync("/install");
        Assert.Equal(HttpStatusCode.OK, install.StatusCode);
        var html = await install.Content.ReadAsStringAsync();
        Assert.Contains(ca.Fingerprint, html);
        Assert.Contains($"':{ca.HttpsPort}'", html); // the page probes and then continues on the https port

        Assert.Equal(HttpStatusCode.Forbidden, (await lan.GetAsync("/api/auth/status")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await lan.PostAsJsonAsync("/api/auth/login", new { password = "1234" })).StatusCode); // the PIN never travels in clear
        var page = await lan.GetAsync("/pair.html");
        Assert.Equal(HttpStatusCode.TemporaryRedirect, page.StatusCode);
        Assert.Contains($":{ca.HttpsPort}/pair.html", page.Headers.Location?.ToString());
    }

    [Fact]
    public async Task With_https_on_the_pc_and_tls_clients_work_normally()
    {
        Assert.Equal(HttpStatusCode.OK, (await _https.Client(TestHost.Loopback).GetAsync("/api/state")).StatusCode);
        var phone = await PairAsync(_https, tls: true);
        Assert.Equal(HttpStatusCode.OK, (await phone.GetAsync("/api/state")).StatusCode);
    }

    [Fact]
    public async Task Pairing_qr_points_at_https_and_carries_the_install_page()
    {
        var json = await (await _https.Client(TestHost.Loopback).PostAsJsonAsync("/api/auth/pairing", new { https = true })).Content.ReadFromJsonAsync<JsonElement>();
        var ca = _https.Services.GetRequiredService<Glamour.Host.Network.LocalCaHolder>().Current!;
        Assert.StartsWith("http://", json.GetProperty("url").GetString()); // the setup page, reachable before the certificate is trusted
        Assert.Contains($":{ca.HttpPort}/install#", json.GetProperty("url").GetString());
        Assert.Equal(ca.Fingerprint, json.GetProperty("fingerprint").GetString());
    }

    [Fact]
    public async Task Plain_http_stays_open_unless_the_pc_requires_https()
    {
        var phone = await PairAsync(_https, tls: false);
        Assert.Equal(HttpStatusCode.OK, (await phone.GetAsync("/api/state")).StatusCode); // choosing "simple" for a phone keeps working
        Assert.Equal(HttpStatusCode.OK, (await _https.Client().GetAsync("/api/auth/status")).StatusCode);
    }

    [Fact]
    public async Task Requiring_https_needs_the_certificate_to_exist_and_is_pc_only()
    {
        var pc = _lan.Client(TestHost.Loopback);
        Assert.Equal(HttpStatusCode.Conflict, (await pc.PutAsJsonAsync("/api/network/require-https", new { enabled = true })).StatusCode);
        await pc.PostAsJsonAsync("/api/auth/pairing", new { https = true });
        var phone = await PairAsync(_lan, tls: false);
        Assert.Equal(HttpStatusCode.Forbidden, (await phone.PutAsJsonAsync("/api/network/require-https", new { enabled = true })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await pc.PutAsJsonAsync("/api/network/require-https", new { enabled = true })).StatusCode);
    }

    [Fact]
    public async Task The_certificate_is_made_only_when_install_as_app_is_chosen()
    {
        var holder = _lan.Services.GetRequiredService<Glamour.Host.Network.LocalCaHolder>();
        var pc = _lan.Client(TestHost.Loopback);
        Assert.Null(holder.Current);
        Assert.Equal(HttpStatusCode.NotFound, (await _lan.Client().GetAsync("/ca.crt")).StatusCode);

        var simple = await (await pc.PostAsJsonAsync("/api/auth/pairing", new { https = false })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("/pair.html#", simple.GetProperty("url").GetString());
        Assert.Equal(JsonValueKind.Null, simple.GetProperty("fingerprint").ValueKind);
        Assert.Null(holder.Current);

        var app = await (await pc.PostAsJsonAsync("/api/auth/pairing", new { https = true })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("/install#", app.GetProperty("url").GetString());
        Assert.NotNull(holder.Current);
        Assert.Equal(holder.Current!.Fingerprint, app.GetProperty("fingerprint").GetString());
        Assert.Equal(HttpStatusCode.OK, (await _lan.Client().GetAsync("/ca.crt")).StatusCode);
    }

    [Fact]
    public async Task Install_as_app_is_refused_while_phone_access_is_off()
    {
        var r = await _plain.Client(TestHost.Loopback).PostAsJsonAsync("/api/auth/pairing", new { https = true });
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
    }

    [Fact]
    public async Task Without_https_there_are_no_certificate_pages()
    {
        Assert.NotEqual(HttpStatusCode.OK, (await _plain.Client().GetAsync("/ca.crt")).StatusCode);
        var net = await _plain.Client(TestHost.Loopback).GetFromJsonAsync<JsonElement>("/api/network");
        Assert.Equal(JsonValueKind.Null, net.GetProperty("https").ValueKind);
    }

    [Fact]
    public async Task Firewall_fix_opens_both_ports_when_https_is_on()
    {
        var r = await _https.Client(TestHost.Loopback).PostAsync("/api/network/firewall", null);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var ca = _https.Services.GetRequiredService<Glamour.Host.Network.LocalCaHolder>().Current!;
        Assert.Equal([ca.HttpPort, ca.HttpsPort], _https.Firewall.Added);
    }
}
