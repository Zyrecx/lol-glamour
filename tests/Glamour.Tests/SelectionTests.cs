using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Glamour.Core;
using Glamour.Host;
using Microsoft.Extensions.Logging.Abstractions;

namespace Glamour.Tests;

public sealed class FakeSpellSource : ISpellSource
{
    public int Calls { get; private set; }
    public Task<SpellList> FetchAsync(CancellationToken ct)
    {
        Calls++;
        return Task.FromResult(new SpellList("16.1.1", [new SpellInfo(4, "Flash", "https://x/SummonerFlash.png", ["CLASSIC", "ARAM"]), new SpellInfo(14, "Ignite", "https://x/SummonerDot.png", ["CLASSIC"])]));
    }
}

public class SelectionTests : IDisposable
{
    private readonly TestHost _host = new();
    public void Dispose() => _host.Dispose();

    [Fact]
    public void Spell_file_parses_the_real_data_dragon_shape()
    {
        using var doc = JsonDocument.Parse("""{"SummonerFlash":{"id":"SummonerFlash","name":"Flash","key":"4","modes":["CLASSIC","ARAM"],"image":{"full":"SummonerFlash.png"}}}""");
        var list = DataDragonSpellSource.Parse("16.1.1", doc.RootElement);
        Assert.Equal(new SpellInfo(4, "Flash", "https://ddragon.leagueoflegends.com/cdn/16.1.1/img/spell/SummonerFlash.png", ["CLASSIC", "ARAM"]).Icon, list.Spells[0].Icon);
        Assert.Equal(["CLASSIC", "ARAM"], list.Spells[0].Modes);
    }

    [Fact]
    public async Task Spell_list_endpoint_fetches_once()
    {
        var pc = _host.Client(TestHost.Loopback);
        var json = await pc.GetFromJsonAsync<JsonElement>("/api/spells");
        Assert.Equal(2, json.GetProperty("spells").GetArrayLength());
        await pc.GetAsync("/api/spells");
        Assert.Equal(1, _host.Spells.Calls);
    }

    [Theory]
    [InlineData(4, 14, "Not in champion select.")] // the host has no client here, so the phase is None
    [InlineData(4, 4, "Choose two different spells.")]
    [InlineData(0, 4, "Choose two different spells.")]
    public async Task Spell_change_is_refused_outside_champ_select_or_with_bad_ids(int a, int b, string _)
    {
        var r = await _host.Client(TestHost.Loopback).PutAsJsonAsync("/api/champselect/spells", new { spell1Id = a, spell2Id = b });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task Selection_sends_only_the_fields_given_and_obeys_dry_run()
    {
        var lcu = new FakeLcu();
        var settings = new GlamourSettings();
        var gateway = new ActionGateway(lcu, settings, NullLogger<ActionGateway>.Instance);

        await gateway.SetSelectionAsync(4, 14, null);
        await gateway.SetSelectionAsync(null, null, 518016);
        Assert.Equal([("PATCH", "/lol-champ-select/v1/session/my-selection", """{"spell1Id":4,"spell2Id":14}"""),
                      ("PATCH", "/lol-champ-select/v1/session/my-selection", """{"selectedSkinId":518016}""")],
            lcu.Writes.Select(w => (w.Method.Method, w.Path, w.Body)));

        settings.DryRun = true;
        await gateway.SetSelectionAsync(4, 14, null);
        Assert.Equal(2, lcu.Writes.Count);
    }
}
