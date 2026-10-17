using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Glamour.Host;
using Glamour.Lcu;
using Microsoft.Extensions.DependencyInjection;

namespace Glamour.Tests;

public class ProfileIconTests : IDisposable
{
    private readonly TestHost _host = new();
    private readonly ScriptedLcu _lcu = new();
    private readonly HttpClient _pc;

    public ProfileIconTests()
    {
        _pc = _host.Client(TestHost.Loopback);
        _host.Services.GetRequiredService<SwappableLcuClient>().Current = _lcu;
    }

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task Reads_the_current_icon()
    {
        _lcu.Gets["/lol-summoner/v1/current-summoner"] = JsonNode.Parse("""{"profileIconId":907}""");
        var j = await _pc.GetFromJsonAsync<JsonNode>("/api/profile-icon");
        Assert.Equal(907, j!["id"]!.GetValue<int>());
        Assert.False(j["blank"]!.GetValue<bool>());
    }

    [Fact]
    public async Task No_client_is_503()
    {
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await _pc.GetAsync("/api/profile-icon")).StatusCode);
    }

    [Fact]
    public async Task Clearing_sets_icon_29()
    {
        Assert.Equal(HttpStatusCode.NoContent, (await _pc.DeleteAsync("/api/profile-icon")).StatusCode);
        var w = Assert.Single(_lcu.Writes);
        Assert.Equal(HttpMethod.Put, w.Method);
        Assert.Equal("/lol-summoner/v1/current-summoner/icon", w.Path);
        Assert.Equal(29, JsonNode.Parse(w.Body!)!["profileIconId"]!.GetValue<int>());
    }

    [Fact]
    public async Task A_client_icon_change_is_published_once()
    {
        var hub = new Glamour.Core.EventHub();
        using var sub = hub.Subscribe();
        var watch = new ProfileIconWatch(hub);
        foreach (var icon in new[] { 907, 907, 29 })
            await watch.OnEventAsync(new LcuEvent("/lol-summoner/v1/current-summoner", "Update", JsonNode.Parse($$"""{"profileIconId":{{icon}}}""")), default);
        await watch.OnEventAsync(new LcuEvent("/lol-summoner/v1/current-summoner/icon", "Update", JsonNode.Parse("""{"profileIconId":5}""")), default);
        var got = new List<int>();
        using var cts = new CancellationTokenSource(200);
        try { await foreach (var ev in sub.ReadAllAsync(cts.Token)) got.Add(System.Text.Json.JsonSerializer.SerializeToNode(ev.Data)!["id"]!.GetValue<int>()); }
        catch (OperationCanceledException) { }
        Assert.Equal([907, 29], got);
    }
}
