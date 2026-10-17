using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Glamour.Core;
using Glamour.Host.Profiles;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Glamour.Tests;

public sealed class FakeRiotClient : IRiotClient
{
    public List<string> RunningNames { get; set; } = [];
    public List<string> Calls { get; } = [];
    public bool StartFails { get; set; }
    /// <summary>What the client's local API says is signed in; null means it does not answer.</summary>
    public RiotIdentity? Identity { get; set; }
    public Task<RiotIdentity?> IdentityAsync(CancellationToken ct) => Task.FromResult(Identity);

    public IReadOnlyList<string> Running() => RunningNames;
    public Task StopAsync(CancellationToken ct) { Calls.Add("stop"); RunningNames = []; return Task.CompletedTask; }
    public void Start(IReadOnlyList<string> products) { if (StartFails) throw new FileNotFoundException("no client"); Calls.Add(products.Count == 0 ? "start" : "start " + string.Join("+", products)); RunningNames = ["RiotClientServices"]; }
}

public class ProfileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "glamour-profiles-" + Guid.NewGuid().ToString("N"));
    private readonly FakeRiotClient _client = new();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero));
    private readonly ProfileStore _store;
    private readonly ProfileService _svc;
    private readonly EventHub _events = new();
    private string _phase = "None";
    private string Live => Path.Combine(_dir, "riot", "RiotGamesPrivateSettings.yaml");

    public ProfileTests()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Live)!);
        _store = new ProfileStore(Path.Combine(_dir, "profiles"), NullLogger<ProfileStore>.Instance);
        _svc = new ProfileService(_store, _client, () => _phase, Live, _time, _events);
    }

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    private static string Session(string sessionId, string token) =>
        $"psl:\n    authorization:\n        riot-client:\n            refresh_token: {token}\n            refresh_tokens_session_id: {sessionId}\n";

    private void SignIn(string sessionId, string token) => File.WriteAllText(Live, Session(sessionId, token));
    private string LiveText => File.ReadAllText(Live);
    private string Saved(string id) => File.ReadAllText(_store.SessionPath(id));

    private async Task<string> SaveAsync(string name, string sessionId, string token)
    {
        SignIn(sessionId, token);
        await _svc.SaveCurrentAsync(name, null);
        return _store.Data.Profiles.Single(p => p.Name == name).Id;
    }

    private static async Task<ProfileException> Fails(Task<object> t) => await Assert.ThrowsAsync<ProfileException>(() => t);

    [Fact]
    public async Task Saving_the_signed_in_account_makes_it_the_active_profile()
    {
        var id = await SaveAsync("Main", "s1", "t1");
        Assert.Equal(id, _store.Data.ActiveId);
        Assert.Contains("t1", Saved(id));
        Assert.Equal("Main", _store.Data.Profiles.Single().Name);
    }

    [Fact]
    public async Task A_blank_name_defaults_to_the_riot_id_and_keeps_the_region()
    {
        SignIn("s1", "t1");
        _client.Identity = new RiotIdentity("Tidewren#LARK", "EUN1");
        await _svc.SaveCurrentAsync("  ", null);
        var p = _store.Data.Profiles.Single();
        Assert.Equal("Tidewren#LARK", p.Name);
        Assert.Equal("Tidewren#LARK", p.RiotId);
        Assert.Equal("EUN1", p.Region);
    }

    [Fact]
    public async Task A_typed_name_wins_but_the_riot_id_is_still_recorded()
    {
        SignIn("s1", "t1");
        _client.Identity = new RiotIdentity("Tidewren#LARK", "EUN1");
        await _svc.SaveCurrentAsync("Main", null);
        Assert.Equal(("Main", "Tidewren#LARK"), (_store.Data.Profiles.Single().Name, _store.Data.Profiles.Single().RiotId));
    }

    [Fact]
    public async Task A_blank_name_without_a_riot_id_is_refused()
    {
        SignIn("s1", "t1");
        Assert.Equal("bad-name", (await Fails(_svc.SaveCurrentAsync("", null))).Code);
    }

    [Fact]
    public async Task A_profile_saved_without_a_riot_id_gets_it_once_the_client_answers()
    {
        var id = await SaveAsync("Main", "s1", "t1");
        Assert.Null(_store.Data.Profiles.Single().RiotId);
        await _svc.RefreshIdentityAsync(default); // client does not answer yet: nothing changes
        Assert.Null(_store.Data.Profiles.Single().RiotId);
        _client.Identity = new RiotIdentity("Tidewren#LARK", "EUN1");
        await _svc.RefreshIdentityAsync(default);
        Assert.Equal(("Main", "Tidewren#LARK", "EUN1"), (_store.Data.Profiles.Single().Name, _store.Data.Profiles.Single().RiotId, _store.Data.Profiles.Single().Region));
    }

    [Fact]
    public async Task The_riot_id_is_never_copied_onto_a_profile_from_another_saved_account()
    {
        await SaveAsync("A", "s1", "t1");
        _client.Identity = new RiotIdentity("A#1", "EUN1");
        await _svc.RefreshIdentityAsync(default);
        await _svc.BeginAddAsync(true, default);
        SignIn("s2", "t2");
        _client.Identity = null; // the client did not answer when B was saved
        await _svc.SaveCurrentAsync("B", null);
        _client.Identity = new RiotIdentity("A#1", "EUN1"); // a stale answer that belongs to A
        await _svc.RefreshIdentityAsync(default);
        Assert.Null(_store.Data.Profiles.Single(p => p.Name == "B").RiotId);
    }

    [Fact]
    public async Task Saving_without_a_signed_in_account_fails()
    {
        Assert.Equal("not-signed-in", (await Fails(_svc.SaveCurrentAsync("Main", null))).Code);
        File.WriteAllText(Live, "psl:\n    riot-login:\n        persist: null\n"); // a logged-out file has no refresh token
        Assert.Equal("not-signed-in", (await Fails(_svc.SaveCurrentAsync("Main", null))).Code);
    }

    [Fact]
    public async Task The_same_account_cannot_be_saved_twice_even_after_its_token_rotated()
    {
        await SaveAsync("Main", "s1", "t1");
        SignIn("s1", "t2");
        Assert.Equal("already-saved", (await Fails(_svc.SaveCurrentAsync("Again", null))).Code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("a-name-that-is-much-longer-than-forty-characters")]
    public async Task Names_must_be_one_to_forty_characters(string name)
    {
        SignIn("s1", "t1");
        Assert.Equal("bad-name", (await Fails(_svc.SaveCurrentAsync(name, null))).Code);
    }

    [Fact]
    public async Task Names_are_unique_ignoring_case()
    {
        await SaveAsync("Main", "s1", "t1");
        SignIn("s2", "t2");
        Assert.Equal("name-taken", (await Fails(_svc.SaveCurrentAsync("main", null))).Code);
    }

    [Fact]
    public async Task Adding_an_account_captures_the_current_one_and_moves_its_session_aside()
    {
        var main = await SaveAsync("Main", "s1", "t1");
        SignIn("s1", "t1-rotated"); // the client refreshed the token while it ran

        _client.RunningNames = ["RiotClientServices"];
        await _svc.BeginAddAsync(confirm: true, default);

        Assert.False(File.Exists(Live));
        Assert.Contains("t1-rotated", Saved(main)); // re-captured, not the stale copy
        Assert.True(_store.Data.Adding);
        Assert.Equal(["stop", "start"], _client.Calls);

        SignIn("s2", "t2"); // the user signs in to the second account
        await _svc.SaveCurrentAsync("Alt", null);
        var alt = _store.Data.Profiles.Single(p => p.Name == "Alt").Id;
        Assert.Equal(alt, _store.Data.ActiveId);
        Assert.False(_store.Data.Adding);
        Assert.False(File.Exists(_store.AsidePath));
        Assert.Contains("t1-rotated", Saved(main));
        Assert.Contains("t2", Saved(alt));
    }

    [Fact]
    public async Task Adding_while_the_current_account_is_unsaved_is_refused_so_nothing_is_lost()
    {
        SignIn("s1", "t1");
        Assert.Equal("save-first", (await Fails(_svc.BeginAddAsync(true, default))).Code);
        Assert.Contains("t1", LiveText);
        Assert.Empty(_client.Calls);
    }

    [Fact]
    public async Task Cancelling_an_add_puts_the_previous_session_back()
    {
        await SaveAsync("Main", "s1", "t1");
        await _svc.BeginAddAsync(true, default);
        await _svc.CancelAddAsync(default);
        Assert.Contains("t1", LiveText);
        Assert.False(_store.Data.Adding);
        Assert.False(File.Exists(_store.AsidePath));
    }

    [Fact]
    public async Task Switching_recaptures_the_old_account_and_restores_the_target()
    {
        var main = await SaveAsync("Main", "s1", "t1");
        await _svc.BeginAddAsync(true, default);
        SignIn("s2", "t2");
        await _svc.SaveCurrentAsync("Alt", null);
        var alt = _store.Data.ActiveId!;
        SignIn("s2", "t2-rotated");
        _client.Calls.Clear();
        _client.RunningNames = ["RiotClientServices"];

        await _svc.SwitchAsync(main, confirm: true, default);

        Assert.Contains("t1", LiveText);
        Assert.Contains("t2-rotated", Saved(alt));
        Assert.Equal(main, _store.Data.ActiveId);
        Assert.Equal(["stop", "start"], _client.Calls);

        SignIn("s1", "t1-rotated");
        await _svc.SwitchAsync(alt, confirm: true, default);
        Assert.Contains("t2-rotated", LiveText);
        Assert.Contains("t1-rotated", Saved(main));
    }

    [Fact]
    public async Task Switching_reopens_the_games_that_were_open()
    {
        var main = await SaveAsync("Main", "s1", "t1");
        await _svc.BeginAddAsync(true, default);
        SignIn("s2", "t2");
        await _svc.SaveCurrentAsync("Alt", null);
        var alt = _store.Data.ActiveId!;

        _client.Calls.Clear();
        _client.RunningNames = ["RiotClientServices", "LeagueClient", "LeagueClientUx"];
        await _svc.SwitchAsync(main, confirm: true, default);
        Assert.Equal(["stop", "start league_of_legends"], _client.Calls);

        _client.Calls.Clear();
        _client.RunningNames = ["RiotClientServices"];
        await _svc.SwitchAsync(alt, confirm: true, default);
        Assert.Equal(["stop", "start"], _client.Calls);

        _client.Calls.Clear();
        _client.RunningNames = ["RiotClientServices", "LeagueClient", "VALORANT", "VALORANT-Win64-Shipping"];
        await _svc.SwitchAsync(main, confirm: true, default);
        Assert.Equal(["stop", "start league_of_legends+valorant"], _client.Calls);
    }

    [Fact]
    public async Task Switching_to_the_active_profile_does_nothing()
    {
        var main = await SaveAsync("Main", "s1", "t1");
        _client.Calls.Clear();
        await _svc.SwitchAsync(main, confirm: false, default);
        Assert.Empty(_client.Calls);
    }

    [Theory]
    [InlineData("Matchmaking")]
    [InlineData("ReadyCheck")]
    [InlineData("ChampSelect")]
    [InlineData("InProgress")]
    [InlineData("Reconnect")]
    public async Task Nothing_closes_the_client_in_a_queue_or_game(string phase)
    {
        var main = await SaveAsync("Main", "s1", "t1");
        await _svc.BeginAddAsync(true, default);
        SignIn("s2", "t2");
        await _svc.SaveCurrentAsync("Alt", null);
        _phase = phase;
        _client.Calls.Clear();

        Assert.Equal("busy", (await Fails(_svc.SwitchAsync(main, true, default))).Code);
        Assert.Equal("busy", (await Fails(_svc.BeginAddAsync(true, default))).Code);
        Assert.False(_svc.CanSwitch);
        Assert.Empty(_client.Calls);
        Assert.Contains("t2", LiveText);
    }

    [Fact]
    public async Task A_running_client_needs_confirmation_and_the_error_lists_what_closes()
    {
        var main = await SaveAsync("Main", "s1", "t1");
        await _svc.BeginAddAsync(true, default);
        SignIn("s2", "t2");
        await _svc.SaveCurrentAsync("Alt", null);
        _client.RunningNames = ["RiotClientServices", "VALORANT-Win64-Shipping"];
        _client.Calls.Clear();

        var ex = await Fails(_svc.SwitchAsync(main, confirm: false, default));
        Assert.Equal("confirm", ex.Code);
        Assert.Empty(_client.Calls);
        Assert.Equal("""{"willClose":["VALORANT"]}""", JsonSerializer.Serialize(ex.Extra)); // games by name, the Riot Client's own processes left out

    }

    [Fact]
    public async Task A_profile_without_a_saved_copy_needs_sign_in_and_is_never_restored()
    {
        var main = await SaveAsync("Main", "s1", "t1");
        await _svc.BeginAddAsync(true, default);
        SignIn("s2", "t2");
        await _svc.SaveCurrentAsync("Alt", null);
        File.Delete(_store.SessionPath(main)); // expired or deleted copy

        Assert.Contains("needs-sign-in", JsonSerializer.Serialize(_svc.Snapshot()));
        _client.Calls.Clear();
        Assert.Equal("needs-sign-in", (await Fails(_svc.SwitchAsync(main, true, default))).Code);
        Assert.Empty(_client.Calls);
        Assert.Contains("t2", LiveText);
    }

    [Fact]
    public async Task Re_signing_in_refreshes_that_profiles_copy()
    {
        var main = await SaveAsync("Main", "s1", "t1");
        await _svc.BeginAddAsync(true, default);
        SignIn("s2", "t2");
        await _svc.SaveCurrentAsync("Alt", null);
        File.Delete(_store.SessionPath(main));

        await _svc.BeginAddAsync(true, default);
        SignIn("s1b", "t1b"); // signed in again, new session
        await _svc.SaveCurrentAsync("Main", main);

        Assert.Equal(2, _store.Data.Profiles.Count);
        Assert.Equal(main, _store.Data.ActiveId);
        Assert.Contains("t1b", Saved(main));
    }

    [Fact]
    public async Task A_client_that_fails_to_start_reports_it_but_keeps_the_switch()
    {
        var main = await SaveAsync("Main", "s1", "t1");
        await _svc.BeginAddAsync(true, default);
        SignIn("s2", "t2");
        await _svc.SaveCurrentAsync("Alt", null);
        _client.StartFails = true;

        Assert.Equal("launch-failed", (await Fails(_svc.SwitchAsync(main, true, default))).Code);
        Assert.Contains("t1", LiveText);
        Assert.Equal(main, _store.Data.ActiveId);
    }

    [Fact]
    public async Task Removing_a_profile_deletes_only_the_saved_copy()
    {
        var main = await SaveAsync("Main", "s1", "t1");
        await _svc.RemoveAsync(main);
        Assert.Empty(_store.Data.Profiles);
        Assert.Null(_store.Data.ActiveId);
        Assert.False(File.Exists(_store.SessionPath(main)));
        Assert.Contains("t1", LiveText); // the Riot Client's own sign-in stays
    }

    [Fact]
    public async Task A_blank_rename_clears_the_nickname_back_to_the_riot_id()
    {
        SignIn("s1", "t1");
        _client.Identity = new RiotIdentity("Tidewren#LARK", "EUN1");
        await _svc.SaveCurrentAsync("Main", null);
        var id = _store.Data.Profiles.Single().Id;
        await _svc.RenameAsync(id, "  ");
        Assert.Equal("Tidewren#LARK", _store.Data.Profiles.Single().Name);
    }

    [Fact]
    public async Task A_blank_rename_without_a_riot_id_is_refused()
    {
        var a = await SaveAsync("A", "s1", "t1");
        Assert.Equal("bad-name", (await Fails(_svc.RenameAsync(a, ""))).Code);
    }

    [Fact]
    public async Task Rename_and_reorder()
    {
        var a = await SaveAsync("A", "s1", "t1");
        await _svc.BeginAddAsync(true, default);
        SignIn("s2", "t2");
        await _svc.SaveCurrentAsync("B", null);
        var b = _store.Data.ActiveId!;

        await _svc.RenameAsync(a, "First");
        await _svc.ReorderAsync([b, a]);
        Assert.Equal(["B", "First"], _store.Data.Profiles.Select(p => p.Name));
        Assert.Equal("bad-order", (await Fails(_svc.ReorderAsync([a]))).Code);
        Assert.Equal("name-taken", (await Fails(_svc.RenameAsync(a, "b"))).Code);
        Assert.Equal("not-found", (await Fails(_svc.RenameAsync("nope", "X"))).Code);
    }

    [Fact]
    public async Task The_profile_list_survives_a_restart()
    {
        var main = await SaveAsync("Main", "s1", "t1");
        var again = new ProfileStore(Path.Combine(_dir, "profiles"), NullLogger<ProfileStore>.Instance);
        again.Load();
        Assert.Equal(main, again.Data.ActiveId);
        Assert.Equal("Main", again.Data.Profiles.Single().Name);
    }

    [Fact]
    public async Task Changes_are_published_to_the_event_stream()
    {
        using var sub = _events.Subscribe();
        await SaveAsync("Main", "s1", "t1");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await foreach (var e in sub.ReadAllAsync(cts.Token)) { Assert.Equal("profiles", e.Type); break; }
    }

    [Fact]
    public void A_session_file_can_be_read_while_the_client_holds_it_open()
    {
        SignIn("s1", "t1");
        using var held = new FileStream(Live, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        Assert.True(ProfileStore.IsSignedIn(Live));
        Assert.Equal("s1", ProfileStore.SessionId(Live));
    }
}

public class ProfileEndpointTests : IDisposable
{
    private readonly TestHost _host = new();
    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task Profiles_need_a_login_from_the_lan()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _host.Client().GetAsync("/api/profiles")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _host.Client(TestHost.Loopback).GetAsync("/api/profiles")).StatusCode);
    }

    [Fact]
    public async Task Saving_without_a_signed_in_account_is_a_409_with_a_code()
    {
        var r = await _host.Client(TestHost.Loopback).PostAsJsonAsync("/api/profiles", new { name = "Main" });
        Assert.Equal(HttpStatusCode.Conflict, r.StatusCode);
        Assert.Equal("not-signed-in", (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Save_then_list_over_http()
    {
        File.WriteAllText(_host.RiotSessionFile, "psl:\n    authorization:\n        riot-client:\n            refresh_token: abc\n            refresh_tokens_session_id: s1\n");
        var http = _host.Client(TestHost.Loopback);
        Assert.Equal(HttpStatusCode.OK, (await http.PostAsJsonAsync("/api/profiles", new { name = "Main" })).StatusCode);
        var list = await http.GetFromJsonAsync<JsonElement>("/api/profiles");
        var p = list.GetProperty("profiles")[0];
        Assert.Equal("Main", p.GetProperty("name").GetString());
        Assert.Equal("active", p.GetProperty("state").GetString());
        Assert.DoesNotContain("abc", list.GetRawText()); // a token never leaves the host
    }
}
