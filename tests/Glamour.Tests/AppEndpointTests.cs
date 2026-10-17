using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Glamour.Host;
using Microsoft.Extensions.DependencyInjection;

namespace Glamour.Tests;

public class AppEndpointTests
{
    private sealed class FakeUpdater : IUpdater
    {
        public string? InstallFolder => @"C:\Apps\GlamourApp";
        public UpdateStatus Status { get; set; } = new(true, "none");
        public event Action? Changed;
        public int Checks, Restarts, ModeChanges;
        public Task CheckAsync() { Checks++; Status = new(true, "ready", "0.2.0", "notes"); Changed?.Invoke(); return Task.CompletedTask; }
        public void RestartToUpdate() => Restarts++;
        public void ModeChanged() => ModeChanges++;
    }

    [Fact]
    public async Task Without_an_installed_copy_updates_are_not_available()
    {
        using var host = new TestHost();
        var pc = host.Client(TestHost.Loopback);
        Assert.Equal(HttpStatusCode.NotFound, (await pc.PostAsync("/api/app/updates/check", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await pc.PostAsync("/api/app/updates/restart", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await pc.PostAsync("/api/app/open-folder", null)).StatusCode);
    }

    [Fact]
    public async Task Check_now_mode_and_restart_reach_the_updater()
    {
        var updater = new FakeUpdater();
        using var host = new TestHost(configure: s => s.AddSingleton<IUpdater>(updater));
        var pc = host.Client(TestHost.Loopback);

        var info = await pc.GetFromJsonAsync<JsonElement>("/api/app");
        Assert.Equal("automatic", info.GetProperty("updates").GetProperty("mode").GetString());
        Assert.Equal(@"C:\Apps\GlamourApp", info.GetProperty("installFolder").GetString());

        Assert.Equal(HttpStatusCode.Conflict, (await pc.PostAsync("/api/app/updates/restart", null)).StatusCode); // nothing downloaded yet
        info = await (await pc.PostAsync("/api/app/updates/check", null)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ready", info.GetProperty("updates").GetProperty("state").GetString());
        Assert.Equal(HttpStatusCode.NoContent, (await pc.PostAsync("/api/app/updates/restart", null)).StatusCode);
        Assert.Equal(1, updater.Restarts);

        Assert.Equal(HttpStatusCode.BadRequest, (await pc.PutAsJsonAsync("/api/app/updates", new { mode = "sometimes" })).StatusCode);
        info = await (await pc.PutAsJsonAsync("/api/app/updates", new { mode = "notify" })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("notify", info.GetProperty("updates").GetProperty("mode").GetString());
        Assert.Equal(1, updater.ModeChanges);
    }
}
