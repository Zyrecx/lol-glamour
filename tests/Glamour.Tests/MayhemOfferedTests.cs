using System.Text.Json;
using System.Text.Json.Nodes;
using Glamour.Core;
using Microsoft.Extensions.Logging.Abstractions;

namespace Glamour.Tests;

/// <summary>Offer list shape from the live Mayhem run (tools/logs/lobbymayhem-mayhem-*.json): subset-champion-list returned [267,69] for the whole champ select.</summary>
public class MayhemOfferedTests
{
    private const string Offer = "/lol-lobby-team-builder/champ-select/v1/subset-champion-list";
    private readonly FakeLcu _lcu = new();
    private readonly Engine _engine;

    public MayhemOfferedTests() => _engine = new Engine(_lcu, [], new EventHub(), NullLogger<Engine>.Instance);

    private async Task SessionAsync(int queueId)
    {
        await _engine.HandleAsync(new Glamour.Lcu.LcuEvent("/lol-gameflow/v1/gameflow-phase", "Update", JsonValue.Create("ChampSelect")), default);
        await _engine.HandleAsync(new Glamour.Lcu.LcuEvent("/lol-champ-select/v1/session", "Update", JsonNode.Parse($$"""
            {"localPlayerCellId":0,"queueId":{{queueId}},"benchEnabled":true,"timer":{"phase":"BAN_PICK","adjustedTimeLeftInPhase":12000,"internalNowInEpochMs":1},
             "myTeam":[{"cellId":0,"championId":0,"team":1}],"theirTeam":[],"actions":[[]],"benchChampions":[]}
            """)), default);
    }

    private JsonElement Board() => JsonSerializer.SerializeToElement(_engine.Tracker.Snapshot(), new JsonSerializerOptions(JsonSerializerDefaults.Web)).GetProperty("champSelect").GetProperty("board");

    [Fact]
    public async Task Mayhem_board_carries_the_offered_champions()
    {
        _lcu.Gets[Offer] = JsonNode.Parse("[267,69]");
        await SessionAsync(2400);
        Assert.Equal([267, 69], Board().GetProperty("offered").EnumerateArray().Select(e => e.GetInt32()));
    }

    [Fact]
    public async Task Other_modes_never_ask_for_an_offer()
    {
        _lcu.Gets[Offer] = JsonNode.Parse("[267,69]");
        await SessionAsync(450);
        Assert.Equal(JsonValueKind.Null, Board().GetProperty("offered").ValueKind);
    }

    [Fact]
    public async Task A_missing_offer_leaves_it_empty_and_is_asked_again()
    {
        await SessionAsync(2400); // 404 before the client has the data
        Assert.Equal(JsonValueKind.Null, Board().GetProperty("offered").ValueKind);
        _lcu.Gets[Offer] = JsonNode.Parse("[1,2,3]");
        await SessionAsync(2400);
        Assert.Equal(3, Board().GetProperty("offered").GetArrayLength());
    }
}
