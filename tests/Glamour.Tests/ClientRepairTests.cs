using System.Net;
using System.Text.Json.Nodes;
using Glamour.Core;
using Glamour.Host;
using Glamour.Lcu;
using Microsoft.Extensions.DependencyInjection;

namespace Glamour.Tests;

public class ClientRepairTests : IDisposable
{
    private readonly TestHost _host = new();
    private readonly ScriptedLcu _lcu = new();
    private readonly HttpClient _pc;

    public ClientRepairTests()
    {
        _pc = _host.Client(TestHost.Loopback);
        _host.Services.GetRequiredService<SwappableLcuClient>().Current = _lcu;
    }

    public void Dispose() => _host.Dispose();

    private Task PhaseAsync(string phase) =>
        _host.Services.GetRequiredService<Engine>().HandleAsync(new LcuEvent("/lol-gameflow/v1/gameflow-phase", "Update", JsonValue.Create(phase)), default);

    private bool Sent(string path) => _lcu.Writes.Any(w => w.Method == HttpMethod.Post && w.Path == path);

    [Fact]
    public async Task Reconnect_is_sent_in_the_reconnect_phase()
    {
        await PhaseAsync("Reconnect");
        Assert.Equal(HttpStatusCode.NoContent, (await _pc.PostAsync("/api/game/reconnect", null)).StatusCode);
        Assert.True(Sent("/lol-gameflow/v1/reconnect"));
    }

    [Theory]
    [InlineData("None")]
    [InlineData("Lobby")]
    [InlineData("InProgress")]
    public async Task Reconnect_is_refused_outside_the_reconnect_phase(string phase)
    {
        await PhaseAsync(phase);
        Assert.Equal(HttpStatusCode.Conflict, (await _pc.PostAsync("/api/game/reconnect", null)).StatusCode);
        Assert.False(Sent("/lol-gameflow/v1/reconnect"));
    }

    [Theory]
    [InlineData("None")]
    [InlineData("Lobby")]
    [InlineData("ChampSelect")]
    [InlineData("Reconnect")]
    [InlineData("EndOfGame")]
    public async Task Client_ui_restart_is_sent_outside_a_game(string phase)
    {
        await PhaseAsync(phase);
        Assert.Equal(HttpStatusCode.NoContent, (await _pc.PostAsync("/api/client/restart-ux", null)).StatusCode);
        Assert.True(Sent("/riotclient/kill-and-restart-ux"));
    }

    [Theory]
    [InlineData("GameStart")]
    [InlineData("InProgress")]
    public async Task Client_ui_restart_is_refused_during_a_game(string phase)
    {
        await PhaseAsync(phase);
        Assert.Equal(HttpStatusCode.Conflict, (await _pc.PostAsync("/api/client/restart-ux", null)).StatusCode);
        Assert.False(Sent("/riotclient/kill-and-restart-ux"));
    }
}
