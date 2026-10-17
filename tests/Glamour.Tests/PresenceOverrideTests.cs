using System.Text.Json.Nodes;
using Glamour.Core;
using Glamour.Lcu;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Glamour.Tests;

/// <summary>Presence shape from tools/logs/presence-backup-20261002.json: every value under `lol` is a string.</summary>
public class PresenceOverrideTests
{
    private readonly FakeLcu _lcu = new();
    private readonly GlamourSettings _settings = new() { RankOverrideEnabled = true, RankTier = "CHALLENGER", RankDivision = "I" };
    private readonly FakeTimeProvider _time = new(DateTimeOffset.FromUnixTimeMilliseconds(1_000_000));
    private readonly PresenceOverrideFeature _feature;

    public PresenceOverrideTests()
    {
        var gateway = new ActionGateway(_lcu, _settings, NullLogger<ActionGateway>.Instance);
        _feature = new PresenceOverrideFeature(_lcu, gateway, _settings, _time, NullLogger<PresenceOverrideFeature>.Instance);
    }

    private static JsonNode Me(string tier = "EMERALD", string division = "IV", string queue = "RANKED_SOLO_5x5", string status = "") =>
        JsonNode.Parse($$$"""{"statusMessage":"{{{status}}}","lol":{"puuid":"p1","rankedLeagueTier":"{{{tier}}}","rankedLeagueDivision":"{{{division}}}","rankedLeagueQueue":"{{{queue}}}","rankedWins":"57","gameStatus":"outOfGame"}}""")!;

    private static LcuEvent Event(JsonNode me) => new("/lol-chat/v1/me", "Update", me);

    private JsonNode? LastLol() => JsonNode.Parse(_lcu.Writes.Last().Body!)?["lol"];

    [Fact]
    public async Task A_different_rank_is_replaced_in_the_whole_lol_object_and_nothing_else_is_lost()
    {
        await _feature.OnEventAsync(Event(Me()), default);
        var (method, path, _) = Assert.Single(_lcu.Writes);
        Assert.Equal((HttpMethod.Put, "/lol-chat/v1/me"), (method, path));
        var lol = LastLol()!;
        Assert.Equal(("CHALLENGER", "I", "57", "outOfGame"), (lol["rankedLeagueTier"]!.GetValue<string>(), lol["rankedLeagueDivision"]!.GetValue<string>(), lol["rankedWins"]!.GetValue<string>(), lol["gameStatus"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Our_own_echo_is_not_written_again_but_a_reset_by_the_client_is()
    {
        await _feature.OnEventAsync(Event(Me()), default);
        await _feature.OnEventAsync(Event(Me("CHALLENGER", "I")), default); // the echo of our write
        Assert.Single(_lcu.Writes);
        await _feature.OnEventAsync(Event(Me()), default); // the client reset it (after a game)
        Assert.Equal(2, _lcu.Writes.Count);
    }

    [Fact]
    public async Task A_client_that_keeps_reverting_is_not_fought_without_limit_and_the_window_reopens()
    {
        for (var i = 0; i < 20; i++) await _feature.OnEventAsync(Event(Me()), default);
        Assert.Equal(6, _lcu.Writes.Count);
        _time.Advance(TimeSpan.FromMinutes(2));
        await _feature.OnEventAsync(Event(Me()), default);
        Assert.Equal(7, _lcu.Writes.Count);
    }

    [Fact]
    public async Task Off_by_default_and_dry_run_write_nothing()
    {
        _settings.RankOverrideEnabled = false;
        await _feature.OnEventAsync(Event(Me()), default);
        _settings.RankOverrideEnabled = true;
        _settings.DryRun = true;
        await _feature.OnEventAsync(Event(Me()), default);
        Assert.Empty(_lcu.Writes);
    }

    [Fact]
    public async Task Turning_it_off_puts_the_real_rank_back_from_my_ranked_stats()
    {
        _lcu.Gets["/lol-ranked/v1/ranked-stats/p1"] = JsonNode.Parse("""{"queueMap":{"RANKED_SOLO_5x5":{"tier":"EMERALD","division":"IV","wins":58}}}""");
        await _feature.OnEventAsync(Event(Me()), default);
        _lcu.Gets["/lol-chat/v1/me"] = Me("CHALLENGER", "I");
        _settings.RankOverrideEnabled = false;
        await _feature.OnSettingsChangedAsync(default);
        var lol = LastLol()!;
        Assert.Equal(("EMERALD", "IV", "58"), (lol["rankedLeagueTier"]!.GetValue<string>(), lol["rankedLeagueDivision"]!.GetValue<string>(), lol["rankedWins"]!.GetValue<string>()));
        var count = _lcu.Writes.Count;
        await _feature.OnEventAsync(Event(Me("EMERALD", "IV")), default); // no longer overriding
        Assert.Equal(count, _lcu.Writes.Count);
    }

    [Fact]
    public async Task Status_message_is_set_and_the_original_comes_back_when_cleared()
    {
        _settings.RankOverrideEnabled = false;
        _settings.StatusMessageOverride = "gg";
        await _feature.OnEventAsync(Event(Me(status: "hello")), default);
        Assert.Contains("\"statusMessage\":\"gg\"", _lcu.Writes.Single().Body);
        _settings.StatusMessageOverride = null;
        _lcu.Gets["/lol-chat/v1/me"] = Me(status: "gg");
        await _feature.OnSettingsChangedAsync(default);
        Assert.Contains("\"statusMessage\":\"hello\"", _lcu.Writes.Last().Body);
    }

    [Theory]
    [InlineData("CHALLENGER", "I", "RANKED_SOLO_5x5", true)]
    [InlineData("MASTER", "NA", "RANKED_TFT", true)]
    [InlineData("GOD", "I", "RANKED_SOLO_5x5", false)]
    [InlineData("GOLD", "V", "RANKED_SOLO_5x5", false)]
    [InlineData("GOLD", "I", "NORMAL", false)]
    public void Only_values_the_client_knows_are_accepted(string tier, string division, string queue, bool ok) =>
        Assert.Equal(ok, RankValues.IsValid(new GlamourSettings { RankOverrideEnabled = true, RankTier = tier, RankDivision = division, RankQueue = queue }));
}
