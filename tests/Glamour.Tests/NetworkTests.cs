using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Glamour.Host.Network;

namespace Glamour.Tests;

public class NetworkTests : IDisposable
{
    private readonly TestHost _host = new();

    public void Dispose() => _host.Dispose();

    [Theory]
    [InlineData("5170", 5170, true)]
    [InlineData("80,5170", 5170, true)]
    [InlineData("5000-6000", 5170, true)]
    [InlineData("*", 5170, true)]
    [InlineData(null, 5170, true)]
    [InlineData("5171", 5170, false)]
    [InlineData("80,443", 5170, false)]
    [InlineData("6000-7000", 5170, false)]
    [InlineData("junk", 5170, false)]
    public void Port_lists(string? ports, int port, bool expected) =>
        Assert.Equal(expected, WindowsFirewall.PortsCover(ports, port));

    [Fact]
    public async Task Network_info_needs_a_login_from_the_lan()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _host.Client().GetAsync("/api/network")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _host.Client(TestHost.Loopback).GetAsync("/api/network")).StatusCode);
    }

    [Fact]
    public async Task Network_info_reports_lan_off_by_default()
    {
        var info = await _host.Client(TestHost.Loopback).GetFromJsonAsync<JsonElement>("/api/network");
        Assert.False(info.GetProperty("listeningOnLan").GetBoolean());
        Assert.Equal("lan-off", info.GetProperty("problem").GetString());
    }

    [Fact]
    public async Task Pc_can_ask_for_the_firewall_rule_and_the_status_then_clears()
    {
        var pc = _host.Client(TestHost.Loopback);
        var r = await pc.PostAsync("/api/network/firewall", null);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal([5170, 5171], _host.Firewall.Added); // both phone ports, even before a certificate exists
        Assert.True((await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("firewall").GetProperty("allowed").GetBoolean());
    }

    [Fact]
    public async Task Declining_the_admin_prompt_is_reported_not_hidden()
    {
        _host.Firewall.UserAccepts = false;
        var r = await _host.Client(TestHost.Loopback).PostAsync("/api/network/firewall", null);
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.False((await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("added").GetBoolean());
    }

    [Fact]
    public async Task A_paired_phone_cannot_trigger_the_admin_prompt_on_the_pc()
    {
        var qr = await _host.Client(TestHost.Loopback).PostAsync("/api/auth/pairing", null);
        var url = (await qr.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("url").GetString()!;
        var phone = _host.Client();
        await phone.PostAsJsonAsync("/api/auth/pair", new { secret = url[(url.IndexOf('#') + 1)..] });

        Assert.Equal(HttpStatusCode.Forbidden, (await phone.PostAsync("/api/network/firewall", null)).StatusCode);
        Assert.Empty(_host.Firewall.Added);
    }
}
