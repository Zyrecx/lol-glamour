using System.Text.Json.Nodes;
using Glamour.Core;
using Glamour.Lcu;
using Microsoft.Extensions.Logging.Abstractions;

namespace Glamour.Tests;

/// <summary>Robustness of the core against odd input: malformed client messages, null-filled settings, switching automation off mid-run.</summary>
public class CoreAuditTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "glamour-test-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_dir, "settings.json");

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    [Theory]
    [InlineData("[8,\"OnJsonApiEvent\",{\"uri\":")] // cut off
    [InlineData("not json")]
    [InlineData("[8,\"OnJsonApiEvent\",{\"uri\":5,\"eventType\":\"Update\"}]")] // uri not a string
    [InlineData("[8,\"OnJsonApiEvent\",{\"uri\":\"/x\",\"eventType\":{}}]")]
    public void A_malformed_event_is_skipped_instead_of_ending_the_stream(string text) => Assert.Null(LcuEventStream.Parse(text));

    [Fact]
    public void Null_collections_in_the_settings_file_load_as_empty_and_the_rest_still_loads()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, """{"AutoLockChampionId":518,"Lanes":null,"MutedNotifications":null,"AutoHonorFriends":null,"SkipScreens":null,"PinnedQueueIds":null}""");
        var s = new GlamourSettings();
        new SettingsStore(s, FilePath, NullLogger<SettingsStore>.Instance).Load(); // threw a NullReferenceException at startup before
        Assert.Equal(518, s.AutoLockChampionId);
        Assert.Empty(s.Lanes);
        Assert.Empty(s.MutedNotifications);
        Assert.Empty(s.AutoHonorFriends);
        Assert.Empty(s.SkipScreens);
        Assert.Empty(s.PinnedQueueIds);
    }

    [Fact]
    public void A_null_lane_entry_falls_back_to_the_default_setup()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, """{"AutoBanChampionId":238,"Lanes":{"TOP":null,"MIDDLE":{"BanChampionId":7}}}""");
        var s = new GlamourSettings();
        new SettingsStore(s, FilePath, NullLogger<SettingsStore>.Instance).Load();
        Assert.Equal(238, s.ForLane("TOP").BanChampionId);
        Assert.Equal(7, s.ForLane("MIDDLE").BanChampionId);
    }

    [Fact]
    public void An_override_of_null_for_a_collection_reads_as_empty()
    {
        var merged = SettingsOverlay.Apply(new GlamourSettings { SkipScreens = [SkippableScreens.Missions] }, JsonNode.Parse("""{"lanes":null,"skipScreens":null}""")!.AsObject());
        Assert.Empty(merged.Lanes);
        Assert.Empty(merged.SkipScreens);
        Assert.Null(merged.ForLane("TOP").BanChampionId);
    }

    [Fact]
    public void An_unreadable_settings_file_is_kept_aside_before_the_next_save_overwrites_it()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, "{ not json");
        var s = new GlamourSettings();
        var store = new SettingsStore(s, FilePath, NullLogger<SettingsStore>.Instance);
        store.Load();
        store.Save();
        Assert.Equal("{ not json", File.ReadAllText(FilePath + ".corrupt"));
        Assert.NotNull(System.Text.Json.JsonDocument.Parse(File.ReadAllText(FilePath)));
        Assert.False(File.Exists(FilePath + ".tmp"));
    }

    private const string Juniperra = "6c1f0d2e-7a3b-5c49-9e81-2f4b6d8a0c11";

    private static JsonNode Ballot() => JsonNode.Parse("{\"gameId\":7,\"eligibleAllies\":[{\"puuid\":\"" + Juniperra + "\"}],\"honoredPlayers\":[],\"votePool\":{\"votes\":1}}")!;

    private static (FakeLcu Lcu, GlamourSettings Settings) HonorSetup()
    {
        var lcu = new FakeLcu();
        lcu.Gets["/lol-chat/v1/friends"] = JsonNode.Parse($$"""[{"gameName":"Juniperra","gameTag":"EUNE","puuid":"{{Juniperra}}"}]""");
        return (lcu, new GlamourSettings { AutoHonorEnabled = true, AutoHonorFriends = ["Juniperra#EUNE"], SkipHonorScreen = true });
    }

    [Fact]
    public async Task Auto_honor_switched_off_while_the_ballot_fills_neither_votes_nor_skips()
    {
        var (lcu, settings) = HonorSetup();
        lcu.Gets["/lol-honor-v2/v1/ballot"] = JsonNode.Parse("""{"gameId":0}""");
        var gate = new AutomationGate(settings, new EventHub(), TimeProvider.System);
        var gateway = new ActionGateway(lcu, settings, NullLogger<ActionGateway>.Instance);
        var feature = new AutoHonorFeature(lcu, gateway, settings, NullLogger<AutoHonorFeature>.Instance, (_, _) =>
        {
            settings.AutomationEnabled = false; // the user turns automation off during the wait
            lcu.Gets["/lol-honor-v2/v1/ballot"] = Ballot();
            return Task.CompletedTask;
        }, gate);
        await feature.OnPhaseAsync("PreEndOfGame", default);
        await feature.Idle;
        Assert.Empty(lcu.Writes);
    }

    [Fact]
    public async Task Auto_honor_skips_a_null_entry_in_the_friend_list()
    {
        var (lcu, settings) = HonorSetup();
        settings.SkipHonorScreen = false;
        settings.AutoHonorFriends = [null!, "Juniperra#EUNE"];
        lcu.Gets["/lol-honor-v2/v1/ballot"] = Ballot();
        var feature = new AutoHonorFeature(lcu, new ActionGateway(lcu, settings, NullLogger<ActionGateway>.Instance), settings, NullLogger<AutoHonorFeature>.Instance, (_, _) => Task.CompletedTask);
        await feature.OnPhaseAsync("PreEndOfGame", default);
        await feature.Idle;
        Assert.Equal(["/lol-honor/v1/honor", "/lol-honor/v1/ballot"], lcu.Writes.Select(w => w.Path));
    }
}
