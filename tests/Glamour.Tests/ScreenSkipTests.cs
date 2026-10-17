using System.Text.Json.Nodes;
using Glamour.Core;
using Glamour.Lcu;
using Microsoft.Extensions.Logging.Abstractions;

namespace Glamour.Tests;

public class ScreenSkipTests
{
    private readonly FakeLcu _lcu = new();
    private readonly GlamourSettings _settings = new();
    private readonly ScreenSkipFeature _feature;

    public ScreenSkipTests()
    {
        var gateway = new ActionGateway(_lcu, _settings, NullLogger<ActionGateway>.Instance);
        _feature = new ScreenSkipFeature(_lcu, gateway, _settings, NullLogger<ScreenSkipFeature>.Instance, (_, _) => Task.CompletedTask);
    }

    private static LcuEvent Sequence(string name, string type = "Update") =>
        new("/lol-pre-end-of-game/v1/currentSequenceEvent", type, JsonNode.Parse($$"""{"name":"{{name}}","priority":2}"""));

    private string[] Paths => _lcu.Writes.Select(w => w.Path).ToArray();

    [Fact]
    public async Task Nothing_is_skipped_by_default()
    {
        await _feature.OnEventAsync(Sequence("missions-celebration"), default);
        Assert.Empty(_lcu.Writes);
    }

    [Fact]
    public async Task A_chosen_sequence_screen_is_completed_once_and_others_are_left()
    {
        _settings.SkipScreens = ["missions-celebration"];
        await _feature.OnEventAsync(Sequence("challenge-level-up-celebration"), default);
        await _feature.OnEventAsync(Sequence("missions-celebration"), default);
        await _feature.OnEventAsync(Sequence("missions-celebration"), default); // the same screen again
        await _feature.OnEventAsync(Sequence("missions-celebration", "Delete"), default);
        Assert.Equal(["/lol-pre-end-of-game/v1/complete/missions-celebration"], Paths);
    }

    [Fact]
    public async Task The_honor_screen_and_unknown_names_are_never_completed_here()
    {
        _settings.SkipScreens = ["honor-vote", "../x", "missions-celebration"];
        await _feature.OnEventAsync(Sequence("honor-vote"), default);
        await _feature.OnEventAsync(Sequence("../x"), default);
        Assert.Empty(_lcu.Writes);
    }

    [Fact]
    public async Task It_reads_the_current_screen_when_the_phase_starts_and_forgets_between_games()
    {
        _settings.SkipScreens = ["ranked-celebration"];
        _lcu.Gets["/lol-pre-end-of-game/v1/currentSequenceEvent"] = JsonNode.Parse("""{"name":"ranked-celebration","priority":4}""");
        await _feature.OnPhaseAsync("PreEndOfGame", default);
        await _feature.Idle;
        await _feature.OnPhaseAsync("Lobby", default);
        await _feature.OnPhaseAsync("PreEndOfGame", default);
        await _feature.Idle;
        Assert.Equal(2, Paths.Count(p => p.EndsWith("complete/ranked-celebration")));
    }

    [Fact]
    public async Task The_stats_screen_is_dismissed_in_EndOfGame_only_when_chosen()
    {
        await _feature.OnPhaseAsync("EndOfGame", default);
        await _feature.Idle;
        Assert.Empty(_lcu.Writes);
        _settings.SkipScreens = ["end-of-game-stats"];
        await _feature.OnPhaseAsync("EndOfGame", default);
        await _feature.Idle;
        Assert.Equal(["/lol-end-of-game/v1/state/dismiss-stats"], Paths);
    }

    [Fact]
    public async Task Dry_run_sends_nothing()
    {
        _settings.SkipScreens = ["missions-celebration"];
        _settings.DryRun = true;
        await _feature.OnEventAsync(Sequence("missions-celebration"), default);
        Assert.Empty(_lcu.Writes);
    }
}
