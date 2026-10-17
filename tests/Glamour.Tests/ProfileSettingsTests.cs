using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Glamour.Core;

namespace Glamour.Tests;

public class SettingsOverlayTests
{
    private static JsonObject O(string json) => JsonNode.Parse(json)!.AsObject();

    [Fact]
    public void Overrides_replace_whole_fields_and_the_rest_follows_the_defaults()
    {
        var defaults = new GlamourSettings { AutoBanChampionId = 238, BanTiming = ActionTiming.LastSecond, AutoHonorFriends = ["a#1"] };
        var e = SettingsOverlay.Apply(defaults, O("""{"autoAccept":"Always","autoHonorFriends":["b#2","c#3"]}"""));
        Assert.Equal(AutoAcceptMode.Always, e.AutoAccept);
        Assert.Equal(["b#2", "c#3"], e.AutoHonorFriends);
        Assert.Equal(238, e.AutoBanChampionId);
        Assert.Equal(ActionTiming.LastSecond, e.BanTiming);
        Assert.Equal(["a#1"], defaults.AutoHonorFriends); // the defaults object is never touched
    }

    [Fact]
    public void A_null_override_is_a_real_value_not_inherit()
    {
        var e = SettingsOverlay.Apply(new GlamourSettings { AutoBanChampionId = 238 }, O("""{"autoBanChampionId":null}"""));
        Assert.Null(e.AutoBanChampionId);
    }

    [Fact]
    public void Shared_and_unknown_keys_in_the_overrides_are_ignored()
    {
        var defaults = new GlamourSettings { DryRun = false, NtfyServer = "https://ntfy.sh" };
        var e = SettingsOverlay.Apply(defaults, O("""{"dryRun":true,"ntfyServer":"https://evil.test","nope":1}"""));
        Assert.False(e.DryRun);
        Assert.Equal("https://ntfy.sh", e.NtfyServer);
    }

    [Fact]
    public void Keys_match_in_any_casing()
    {
        Assert.Equal("autoAccept", SettingsOverlay.Canonical("AutoAccept"));
        Assert.Null(SettingsOverlay.Canonical("nope"));
        Assert.Equal(AutoAcceptMode.Phone, SettingsOverlay.Apply(new GlamourSettings(), O("""{"AutoAccept":"Phone"}""")).AutoAccept);
    }
}

public class ProfileSettingsEndpointTests : IDisposable
{
    private readonly TestHost _host = new();
    private readonly HttpClient _pc;
    private string _a = "", _b = "";
    public ProfileSettingsEndpointTests() => _pc = _host.Client(TestHost.Loopback);
    public void Dispose() => _host.Dispose();

    private static string Session(string id) => $"psl:\n    authorization:\n        riot-client:\n            refresh_token: t-{id}\n            refresh_tokens_session_id: {id}\n";

    /// <summary>Two saved accounts, B signed in and active.</summary>
    private async Task TwoProfilesAsync()
    {
        File.WriteAllText(_host.RiotSessionFile, Session("s1"));
        _a = (await (await _pc.PostAsJsonAsync("/api/profiles", new { name = "A" })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("activeId").GetString()!;
        Assert.Equal(HttpStatusCode.OK, (await _pc.PostAsync("/api/profiles/add/begin?confirm=true", null)).StatusCode);
        File.WriteAllText(_host.RiotSessionFile, Session("s2"));
        _b = (await (await _pc.PostAsJsonAsync("/api/profiles", new { name = "B" })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("activeId").GetString()!;
    }

    private Task<HttpResponseMessage> Override(string id, object body) => _pc.PatchAsJsonAsync($"/api/profiles/{id}/settings", body);
    private Task<HttpResponseMessage> Switch(string id) => _pc.PostAsync($"/api/profiles/{id}/switch?confirm=true", null);
    private Task<JsonElement> EffectiveAsync() => _pc.GetFromJsonAsync<JsonElement>("/api/settings");
    private static string Code(JsonElement e) => e.GetProperty("code").GetString()!;

    [Fact]
    public async Task Switching_accounts_swaps_the_effective_settings_and_keeps_the_defaults_file_clean()
    {
        await TwoProfilesAsync();
        await _pc.PatchAsJsonAsync("/api/settings", new { autoBanChampionId = 238, autoAccept = "Always" });
        Assert.Equal(HttpStatusCode.OK, (await Override(_a, new { set = new { autoBanChampionId = (int?)null, autoAccept = "Phone" } })).StatusCode);

        var onB = await EffectiveAsync();
        Assert.Equal(238, onB.GetProperty("autoBanChampionId").GetInt32()); // B has no overrides
        Assert.Equal("Always", onB.GetProperty("autoAccept").GetString());

        await Switch(_a);
        var onA = await EffectiveAsync();
        Assert.Equal(JsonValueKind.Null, onA.GetProperty("autoBanChampionId").ValueKind); // null override = auto-ban off for A, not "inherit"
        Assert.Equal("Phone", onA.GetProperty("autoAccept").GetString());

        var file = File.ReadAllText(Path.Combine(Path.GetDirectoryName(_host.RiotSessionFile)!, "..", "settings.json"));
        Assert.Contains("\"AutoAccept\": \"Always\"", file); // only the shared defaults are saved here
        Assert.Contains("238", file);

        await Switch(_b);
        Assert.Equal("Always", (await EffectiveAsync()).GetProperty("autoAccept").GetString());
    }

    [Fact]
    public async Task Shared_defaults_changes_reach_an_account_except_for_fields_it_overrides()
    {
        await TwoProfilesAsync();
        await Override(_b, new { set = new { autoAccept = "Phone" } });
        var r = await (await _pc.PatchAsJsonAsync("/api/settings", new { autoAccept = "Always", lastSecondMarginSeconds = 5 })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Phone", r.GetProperty("autoAccept").GetString()); // the answer is the effective view
        Assert.Equal(5, r.GetProperty("lastSecondMarginSeconds").GetDouble());
    }

    [Fact]
    public async Task Clearing_an_override_follows_the_default_again()
    {
        await TwoProfilesAsync();
        await Override(_b, new { set = new { autoAccept = "Phone" } });
        Assert.Equal("Phone", (await EffectiveAsync()).GetProperty("autoAccept").GetString());
        var r = await Override(_b, new { clear = new[] { "autoAccept" } });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        Assert.Equal("Off", (await EffectiveAsync()).GetProperty("autoAccept").GetString());
        var p = (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("profiles").EnumerateArray().Single(x => x.GetProperty("id").GetString() == _b);
        Assert.Equal(JsonValueKind.Null, p.GetProperty("overrides").ValueKind); // nothing left to show
    }

    [Theory]
    [InlineData("""{"set":{"dryRun":true}}""", "shared-setting")]
    [InlineData("""{"set":{"ntfyTopic":"x"}}""", "shared-setting")]
    [InlineData("""{"set":{"nope":1}}""", "bad-setting")]
    [InlineData("""{"set":{"autoAccept":"Bogus"}}""", "bad-setting")]
    [InlineData("""{"set":{"rankOverrideEnabled":true,"rankTier":"WOOD"}}""", "bad-setting")]
    [InlineData("""{"set":{"lastSecondMarginSeconds":99}}""", "bad-setting")]
    public async Task Bad_overrides_are_rejected_and_change_nothing(string body, string code)
    {
        await TwoProfilesAsync();
        var r = await _pc.PatchAsync($"/api/profiles/{_b}/settings", new StringContent(body, System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Equal(code, Code(await r.Content.ReadFromJsonAsync<JsonElement>()));
        var p = (await _pc.GetFromJsonAsync<JsonElement>("/api/profiles")).GetProperty("profiles").EnumerateArray().Single(x => x.GetProperty("id").GetString() == _b);
        Assert.Equal(JsonValueKind.Null, p.GetProperty("overrides").ValueKind);
    }

    [Fact]
    public async Task A_failed_client_launch_still_puts_the_new_accounts_settings_in_effect()
    {
        await TwoProfilesAsync();
        await Override(_a, new { set = new { autoAccept = "Phone" } });
        _host.RiotClient.StartFails = true;
        var r = await Switch(_a);
        Assert.Equal(HttpStatusCode.InternalServerError, r.StatusCode);
        Assert.Equal("launch-failed", Code(await r.Content.ReadFromJsonAsync<JsonElement>()));
        Assert.Equal(_a, (await _pc.GetFromJsonAsync<JsonElement>("/api/profiles")).GetProperty("activeId").GetString());
        Assert.Equal("Phone", (await EffectiveAsync()).GetProperty("autoAccept").GetString()); // not B's settings
    }

    [Fact]
    public async Task An_unknown_profile_is_a_404()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await Override("nope", new { set = new { autoAccept = "Phone" } })).StatusCode);
    }

    [Fact]
    public async Task Removing_the_active_account_drops_its_overrides_and_falls_back_to_the_defaults()
    {
        await TwoProfilesAsync();
        await Override(_b, new { set = new { autoAccept = "Phone" } });
        await _pc.DeleteAsync($"/api/profiles/{_b}");
        Assert.Equal("Off", (await EffectiveAsync()).GetProperty("autoAccept").GetString());
        var list = await _pc.GetFromJsonAsync<JsonElement>("/api/profiles");
        Assert.DoesNotContain("Phone", list.GetRawText());
    }

    [Fact]
    public async Task While_an_add_is_open_the_defaults_apply()
    {
        await TwoProfilesAsync();
        await Override(_b, new { set = new { autoAccept = "Phone" } });
        await _pc.PostAsync("/api/profiles/add/begin?confirm=true", null); // the client is signed out until the new account is saved
        Assert.Equal("Off", (await EffectiveAsync()).GetProperty("autoAccept").GetString());
        await _pc.PostAsync("/api/profiles/add/cancel", null);
        Assert.Equal("Phone", (await EffectiveAsync()).GetProperty("autoAccept").GetString());
    }

    [Fact]
    public async Task Signing_in_again_to_refresh_a_profile_keeps_its_overrides()
    {
        await TwoProfilesAsync();
        await Override(_b, new { set = new { autoAccept = "Phone" } });
        File.WriteAllText(_host.RiotSessionFile, Session("s2")); // same account, new token generation
        Assert.Equal(HttpStatusCode.OK, (await _pc.PostAsJsonAsync("/api/profiles", new { name = "B", id = _b })).StatusCode);
        Assert.Equal("Phone", (await EffectiveAsync()).GetProperty("autoAccept").GetString());
    }
}
