using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Glamour.Core;

namespace Glamour.Tests;

public class NotificationRulesTests
{
    private static PhoneNotification? For(string type, object? data, AutoAcceptMode mode = AutoAcceptMode.Phone) =>
        NotificationRules.For(new GlamourEvent(type, data), new GlamourSettings { AutoAccept = mode });

    [Fact]
    public void MatchFound_PingsOnlyWhenPhoneMustAccept()
    {
        var found = new { state = "InProgress", playerResponse = "None" };
        Assert.Equal("Match found", For("ready-check", found)?.Title);
        Assert.Null(For("ready-check", found, AutoAcceptMode.Always));
        Assert.Null(For("ready-check", new { state = "InProgress", playerResponse = "Accepted" }));
        Assert.Null(For("ready-check", new { state = "EveryoneReady", playerResponse = "Accepted" }));
    }

    [Fact]
    public void ChampSelectEvents_MapToNotifications()
    {
        Assert.Equal("Your turn to ban", For("myturn", new { type = "ban" })?.Title);
        Assert.NotNull(For("swap", new { kind = "position", state = "RECEIVED" }));
        Assert.Null(For("swap", new { kind = "position", state = "SENT" }));
        Assert.NotNull(For("champselect-start", new { queueId = 400 }));
        Assert.NotNull(For("cs-stage", new { stage = "FINALIZATION" }));
        Assert.Null(For("cs-stage", new { stage = "BAN_PICK" }));
        Assert.NotNull(For("ready-check-result", new { result = "failed" }));
        Assert.Null(For("ready-check-result", new { result = "passed" }));
    }

    [Fact]
    public void MyTurn_UsesTheLanesSetup()
    {
        var settings = new GlamourSettings { AutoBanChampionId = 238, Lanes = { ["SUPPORT"] = new LaneSetup() } };
        Assert.Null(NotificationRules.For(new GlamourEvent("myturn", new { type = "ban", lane = "TOP" }), settings));
        Assert.NotNull(NotificationRules.For(new GlamourEvent("myturn", new { type = "ban", lane = "SUPPORT" }), settings));
    }

    [Fact]
    public void MyTurn_SkippedWhenAutoActionIsSet()
    {
        var e = new GlamourEvent("myturn", new { type = "ban" });
        Assert.Null(NotificationRules.For(e, new GlamourSettings { AutoBanChampionId = 238 }));
        Assert.NotNull(NotificationRules.For(e, new GlamourSettings { AutoLockChampionId = 1 }));
    }

    [Fact]
    public void Dodge_IgnoresMyOwn()
    {
        Assert.NotNull(For("dodge", new { byMe = false }));
        Assert.Null(For("dodge", new { byMe = true }));
        Assert.Null(For("phase", new { phase = "Lobby" }));
    }

    [Theory]
    [InlineData("https://ntfy.sh", true)]
    [InlineData("http://ntfy.sh", false)]
    [InlineData("https://user:pw@ntfy.sh", false)]
    [InlineData("ntfy.sh", false)]
    public void ServerMustBeHttps(string server, bool ok) => Assert.Equal(ok, NotificationRules.IsValidServer(server));
}

public class NotificationHostTests : IDisposable
{
    private readonly TestHost _host = new();
    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task Enabling_GeneratesSecretTopic_AndKeepsIt_AndRejectsHttpServer()
    {
        var pc = _host.Client(TestHost.Loopback);
        var first = await (await pc.PutAsJsonAsync("/api/settings", new { ntfyEnabled = true })).Content.ReadFromJsonAsync<JsonObject>();
        var topic = first!["ntfyTopic"]!.GetValue<string>();
        Assert.StartsWith("glamour-", topic);
        var second = await (await pc.PutAsJsonAsync("/api/settings", new { ntfyEnabled = true, ntfyTopic = topic })).Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal(topic, second!["ntfyTopic"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.BadRequest, (await pc.PutAsJsonAsync("/api/settings", new { ntfyEnabled = true, ntfyServer = "http://evil.lan" })).StatusCode);
    }

    [Fact]
    public async Task TestEndpoint_RefusesWhenOff()
    {
        var pc = _host.Client(TestHost.Loopback);
        await pc.PutAsJsonAsync("/api/settings", new { ntfyEnabled = false });
        Assert.Equal(HttpStatusCode.BadRequest, (await pc.PostAsync("/api/notify/test", null)).StatusCode);
    }
}

public class MutedNotificationTests
{
    [Theory]
    [InlineData("match", "ready-check", "{\"state\":\"InProgress\",\"playerResponse\":\"None\"}")]
    [InlineData("champ-select", "champselect-start", "{}")]
    [InlineData("swap", "swap", "{\"kind\":\"position\",\"state\":\"RECEIVED\"}")]
    [InlineData("dodge", "dodge", "{\"byMe\":false}")]
    public void A_muted_kind_sends_nothing_and_the_others_still_do(string kind, string type, string json)
    {
        var data = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(json);
        var muted = new GlamourSettings { MutedNotifications = [kind] };
        Assert.Null(NotificationRules.For(new GlamourEvent(type, data), muted));
        Assert.NotNull(NotificationRules.For(new GlamourEvent(type, data), new GlamourSettings()));
        Assert.NotNull(NotificationRules.For(new GlamourEvent("cs-stage", new { stage = "FINALIZATION" }), new GlamourSettings { MutedNotifications = ["swap", "match"] }));
    }

    [Fact]
    public void Every_notifying_event_type_belongs_to_a_kind()
    {
        foreach (var t in new[] { "ready-check", "ready-check-result", "champselect-start", "cs-stage", "myturn", "swap", "dodge" })
            Assert.Contains(NotificationKinds.Of(t), NotificationKinds.All);
    }
}

public class NotificationActionTests
{
    [Fact]
    public void Match_found_carries_an_accept_button_only_when_the_phone_must_accept()
    {
        var found = new { state = "InProgress", playerResponse = "None" };
        Assert.Equal(NotificationActions.Accept, NotificationRules.For(new GlamourEvent("ready-check", found), new GlamourSettings { AutoAccept = AutoAcceptMode.Phone })?.Action);
        var delayed = NotificationRules.For(new GlamourEvent("ready-check", found), new GlamourSettings { AutoAccept = AutoAcceptMode.Always, AutoAcceptDelaySeconds = 3 });
        Assert.Equal(NotificationActions.CancelAutoAccept, delayed?.Action);
        Assert.Null(NotificationRules.For(new GlamourEvent("swap", new { kind = "position", state = "RECEIVED" }), new GlamourSettings())?.Action);
    }
}
