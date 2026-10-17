using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Glamour.Host;
using Microsoft.Extensions.DependencyInjection;

namespace Glamour.Tests;

public class DesktopEndpointTests : IDisposable
{
    private readonly TestHost _host = new();
    public void Dispose() => _host.Dispose();

    private static StringContent Enabled(bool on) => new($$"""{"enabled":{{(on ? "true" : "false")}}}""", Encoding.UTF8, "application/json");

    [Fact]
    public async Task Not_available_without_the_desktop_app()
    {
        var info = await _host.Client(TestHost.Loopback).GetFromJsonAsync<JsonElement>("/api/desktop");
        Assert.False(info.GetProperty("available").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound, (await _host.Client(TestHost.Loopback).PutAsync("/api/desktop/autostart", Enabled(true))).StatusCode);
    }

    private sealed class FakeShell : IDesktopShell { public bool Autostart { get; set; } public Glamour.Host.Panel.PanelSide? PanelSide { get; set; } public bool PanelAutoOpen { get; set; } = true; public int Restarts; public Task RestartHostAsync() { Restarts++; return Task.CompletedTask; } }

    [Fact]
    public async Task Panel_side_is_auto_right_or_left_and_set_from_the_pc()
    {
        var shell = new FakeShell();
        using var host = new TestHost(configure: s => s.AddSingleton<IDesktopShell>(shell));
        var pc = host.Client(TestHost.Loopback);
        Assert.Equal("auto", (await pc.GetFromJsonAsync<JsonElement>("/api/desktop")).GetProperty("panelSide").GetString());

        var r = await pc.PutAsJsonAsync("/api/desktop/panel-side", new { side = "left" });
        Assert.Equal("left", (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("panelSide").GetString());
        Assert.Equal(Glamour.Host.Panel.PanelSide.Left, shell.PanelSide);

        Assert.Equal(HttpStatusCode.BadRequest, (await pc.PutAsJsonAsync("/api/desktop/panel-side", new { side = "1" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await pc.PutAsJsonAsync("/api/desktop/panel-side", new { side = "top" })).StatusCode);
        Assert.Equal(Glamour.Host.Panel.PanelSide.Left, shell.PanelSide);

        await pc.PutAsJsonAsync("/api/desktop/panel-side", new { side = "auto" });
        Assert.Null(shell.PanelSide);
    }

    [Fact]
    public async Task Panel_auto_open_is_set_from_the_pc()
    {
        var shell = new FakeShell();
        using var host = new TestHost(configure: s => s.AddSingleton<IDesktopShell>(shell));
        var pc = host.Client(TestHost.Loopback);

        Assert.True((await pc.GetFromJsonAsync<JsonElement>("/api/desktop")).GetProperty("panelAutoOpen").GetBoolean());
        var r = await pc.PutAsJsonAsync("/api/desktop/panel-auto-open", new { enabled = false });
        Assert.False((await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("panelAutoOpen").GetBoolean());
        Assert.False(shell.PanelAutoOpen);
    }

    [Fact]
    public async Task Autostart_is_set_from_the_pc_and_refused_from_a_paired_phone()
    {
        var shell = new FakeShell();
        using var host = new TestHost(configure: s => s.AddSingleton<IDesktopShell>(shell));
        var pc = host.Client(TestHost.Loopback);

        var pairing = await (await pc.PostAsync("/api/auth/pairing", null)).Content.ReadFromJsonAsync<JsonElement>();
        var url = pairing.GetProperty("url").GetString()!;
        var phone = host.Client();
        Assert.Equal(HttpStatusCode.OK, (await phone.PostAsJsonAsync("/api/auth/pair", new { secret = url[(url.IndexOf('#') + 1)..], name = "Phone" })).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Client().PutAsync("/api/desktop/autostart", Enabled(true))).StatusCode); // not signed in
        Assert.Equal(HttpStatusCode.Forbidden, (await phone.PutAsync("/api/desktop/autostart", Enabled(true))).StatusCode); // signed in, but a registry write is the PC's
        Assert.False(shell.Autostart);

        Assert.Equal(HttpStatusCode.OK, (await pc.PutAsync("/api/desktop/autostart", Enabled(true))).StatusCode);
        Assert.True(shell.Autostart);
        Assert.True((await phone.GetFromJsonAsync<JsonElement>("/api/desktop")).GetProperty("autostart").GetBoolean());
    }

    [Fact]
    public async Task Phone_access_is_saved_from_the_pc_only_and_restarts_the_desktop_host()
    {
        var shell = new FakeShell();
        using var host = new TestHost(configure: s => s.AddSingleton<IDesktopShell>(shell));
        var pc = host.Client(TestHost.Loopback);

        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Client().PutAsync("/api/network/phone-access", Enabled(true))).StatusCode);
        Assert.False((await pc.GetFromJsonAsync<JsonElement>("/api/network")).GetProperty("phoneAccessWanted").GetBoolean());

        var r = await pc.PutAsync("/api/network/phone-access", Enabled(true));
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.True((await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("restarting").GetBoolean());
        var net = await pc.GetFromJsonAsync<JsonElement>("/api/network");
        Assert.True(net.GetProperty("phoneAccessWanted").GetBoolean());
        Assert.False(net.GetProperty("listeningOnLan").GetBoolean()); // it only listens after the restart
        for (var i = 0; i < 40 && shell.Restarts == 0; i++) await Task.Delay(50);
        Assert.Equal(1, shell.Restarts);
    }

    [Fact]
    public async Task Without_the_desktop_app_phone_access_asks_for_a_manual_restart()
    {
        var pc = _host.Client(TestHost.Loopback);
        var r = await pc.PutAsync("/api/network/phone-access", Enabled(true));
        Assert.False((await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("restarting").GetBoolean());
    }

    [Fact]
    public async Task A_phone_port_held_by_another_program_is_reported_not_fatal()
    {
        using var host = new TestHost(lan: true, configure: s => s.AddSingleton(new Glamour.Host.Network.PhoneAccess(requested: true) { Blocked = true }));
        var net = await host.Client(TestHost.Loopback).GetFromJsonAsync<JsonElement>("/api/network");
        Assert.False(net.GetProperty("listeningOnLan").GetBoolean());
        Assert.Equal("port-in-use", net.GetProperty("problem").GetString());
    }

    [Fact]
    public async Task Phone_urls_and_ports_never_use_the_port_of_the_pc_window()
    {
        using var host = new TestHost(lan: true); // no certificate yet: the old code fell back to the request's own port
        var pc = host.Client(TestHost.Loopback, port: 5180);

        var pairing = await (await pc.PostAsJsonAsync("/api/auth/pairing", new { https = false })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains(":5170/", pairing.GetProperty("url").GetString());
        Assert.DoesNotContain(":5180", pairing.GetProperty("url").GetString());
        Assert.Equal(5170, (await pc.GetFromJsonAsync<JsonElement>("/api/network")).GetProperty("port").GetInt32());
    }

    [Fact]
    public async Task A_start_up_argument_overrides_the_switch_and_the_switch_says_so()
    {
        using var host = new TestHost(lan: true); // the test host passes Glamour:AllowLan like a command line would
        var pc = host.Client(TestHost.Loopback);
        var net = await pc.GetFromJsonAsync<JsonElement>("/api/network");
        Assert.True(net.GetProperty("phoneAccessForced").GetBoolean());
        Assert.True(net.GetProperty("phoneAccessWanted").GetBoolean());
        Assert.Equal(HttpStatusCode.Conflict, (await pc.PutAsync("/api/network/phone-access", Enabled(false))).StatusCode);
    }
}
