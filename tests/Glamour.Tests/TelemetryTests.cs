using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Glamour.Core;
using Glamour.Host.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Glamour.Tests;

/// <summary>Nothing here reaches the network: every request lands in <see cref="Capture"/>. Made-up players only.</summary>
public class TelemetryTests : IDisposable
{
    private sealed class Capture : HttpMessageHandler
    {
        public List<(Uri Url, string Body, Dictionary<string, string> Headers)> Sent { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            lock (Sent) Sent.Add((request.RequestUri!, body, request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value))));
            return new HttpResponseMessage(HttpStatusCode.OK);
        }

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken ct) => SendAsync(request, ct).GetAwaiter().GetResult();
    }

    private readonly Capture _net = new();
    private readonly Telemetry _t;

    public TelemetryTests()
    {
        _t = new Telemetry("desktop", _net) { IsOn = () => true };
    }

    public void Dispose() => _t.Dispose();

    private List<(Uri Url, string Body, Dictionary<string, string> Headers)> Sent()
    {
        _t.Flush(TimeSpan.FromSeconds(5));
        lock (_net.Sent) return [.. _net.Sent];
    }

    /// <summary>The event inside a Sentry envelope (its third line).</summary>
    private static JsonObject Event(string envelope) => JsonNode.Parse(envelope.Split('\n')[2])!.AsObject();

    private static Exception Thrown(string message)
    {
        try { throw new InvalidOperationException(message); }
        catch (InvalidOperationException e) { return e; }
    }

    [Fact]
    public void An_error_names_no_player_user_or_pc_and_carries_its_frames()
    {
        // A name learned from this session's events goes even without its tag, as do the saved accounts' names.
        _t.Saw("""Glamour.Events: board {"myTeam":[{"cellId":1,"name":"Made Up Name#EUW","championId":0}]}""");
        _t.KnownNames = () => ["Saved Smurf#NA1"];
        var path = $@"C:\Users\{Environment.UserName}\AppData\Local\Glamour\x.json";
        _t.Error("Glamour.Core.Engine", "Handling {Uri} failed", $"Handling {path} failed for Made Up Name", Thrown($"Made Up Name and Saved Smurf not in lobby on {Environment.MachineName}"));

        var (url, body, headers) = Assert.Single(Sent());
        Assert.Equal("o4512245239578624.ingest.de.sentry.io", url.Host);
        Assert.Contains("sentry_key=", headers["X-Sentry-Auth"]);
        foreach (var gone in new[] { "Made Up", "Saved Smurf", Environment.UserName, Environment.MachineName })
            Assert.DoesNotContain(gone, body, StringComparison.OrdinalIgnoreCase);
        var e = Event(body);
        Assert.Null(e["user"]);
        Assert.Null(e["server_name"]);
        Assert.Equal("dev", e["environment"]!.GetValue<string>());
        Assert.Equal("Handling {Uri} failed", e["logentry"]!["message"]!.GetValue<string>());
        var ex = e["exception"]!["values"]!.AsArray().Single()!;
        Assert.Equal("System.InvalidOperationException", ex["type"]!.GetValue<string>());
        var frame = ex["stacktrace"]!["frames"]!.AsArray().Last()!;
        Assert.Equal(nameof(Thrown), frame["function"]!.GetValue<string>());
        Assert.True(frame["in_app"]!.GetValue<bool>());
    }

    [Fact]
    public void Nothing_goes_when_switched_off()
    {
        _t.IsOn = () => false;
        _t.Error("Glamour.X", "Failed", "Failed", Thrown("x"));
        _t.Track("game", new Dictionary<string, object> { ["queue"] = "420" });
        Assert.Empty(Sent());
    }

    [Fact]
    public void The_same_error_goes_once_per_run()
    {
        for (var i = 0; i < 3; i++) _t.Error("Glamour.X", "Failed {N}", $"Failed {i}", Thrown("x"));
        _t.Error("Glamour.X", "Other failure", "Other failure", null);
        Assert.Equal(2, Sent().Count);
    }

    [Fact]
    public void Errors_and_web_script_errors_go_but_warnings_stay_in_the_log()
    {
        var log = new TelemetryLog(_t);
        log.CreateLogger("Glamour.Host.LolalyticsMatchupSource").LogWarning(new HttpRequestException("down"), "Could not fetch matchups");
        log.CreateLogger("Glamour.Core.SettingsStore").LogError(Thrown("disk"), "Could not save settings to {Path}", @"C:\x");
        log.CreateLogger("Glamour.Web").LogWarning("{Who} on {Page}: {Kind}: {Message}{Stack}", "device 'Made Up Phone'", "/?view=phone", "error", "x is undefined", "");

        var sent = Sent().Select(s => Event(s.Body)).ToList();
        Assert.Equal(2, sent.Count);
        Assert.Contains(sent, e => e["logentry"]!["message"]!.GetValue<string>() == "Could not save settings to {Path}");
        var web = Assert.Single(sent, e => e["logger"]!.GetValue<string>() == "Glamour.Web");
        Assert.Equal("error: x is undefined", web["logentry"]!["formatted"]!.GetValue<string>());
        Assert.DoesNotContain("Made Up Phone", web.ToJsonString());
    }

    [Fact]
    public async Task A_game_sends_one_usage_event_with_its_counts_and_switches()
    {
        var settings = new GlamourSettings { AutoAccept = AutoAcceptMode.Always, AutoLockChampionId = LaneSetup.BestCounter };
        var tally = new UsageTally(_t, settings, new ServiceCollection().BuildServiceProvider());
        var lcu = new FakeLcu();
        var gateway = new ActionGateway(lcu, settings, NullLogger<ActionGateway>.Instance, tally);
        await gateway.AcceptReadyCheckAsync();
        await gateway.CompleteAsync(1, 7, "pick");
        await Task.Run(async () => { UsageOrigin.Set("phone"); await gateway.HoverAsync(2, 7); });
        lcu.WriteStatus["/lol-champ-select/v1/session/actions/3"] = 400;
        await gateway.CompleteAsync(3, 7, "ban"); // refused: not counted

        await tally.OnPhaseAsync("InProgress", default);
        Assert.Empty(Sent());
        await tally.OnPhaseAsync("EndOfGame", default);
        await tally.OnPhaseAsync("Lobby", default);
        await tally.OnPhaseAsync("Lobby", default);

        var (url, body, headers) = Assert.Single(Sent());
        Assert.Equal("https://eu.aptabase.com/api/v0/event", url.ToString());
        Assert.Equal("A-EU-3112221579", headers["App-Key"]);
        var e = JsonNode.Parse(body)!;
        Assert.Equal("game", e["eventName"]!.GetValue<string>());
        Assert.True(e["systemProps"]!["isDebug"]!.GetValue<bool>());
        var props = e["props"]!;
        Assert.Equal(1, props["auto_accept"]!.GetValue<int>());
        Assert.Equal(1, props["auto_lock"]!.GetValue<int>());
        Assert.Equal(1, props["phone_hover"]!.GetValue<int>());
        Assert.Null(props["auto_ban"]);
        Assert.Equal("on", props["set_best_counter"]!.GetValue<string>());
        Assert.Equal("off", props["set_ban"]!.GetValue<string>());
        Assert.Equal("0", props["queue"]!.GetValue<string>());
    }

    [Fact]
    public async Task Tests_and_the_demo_host_have_no_telemetry()
    {
        using var host = new TestHost();
        Assert.Null(host.Services.GetService<Telemetry>());
        Assert.Null(host.Services.GetService<IUsage>());
        var info = await host.Client(TestHost.Loopback).GetFromJsonAsync<JsonElement>("/api/app");
        Assert.True(info.GetProperty("telemetry").GetBoolean()); // on by default, shown on the tour
        Assert.False(info.GetProperty("tourDone").GetBoolean());
        Assert.False(info.GetProperty("updates").GetProperty("available").GetBoolean());
    }

    [Fact]
    public async Task Only_the_pc_switches_telemetry_or_ends_the_tour()
    {
        using var host = new TestHost(lan: true);
        Assert.False((await host.Client(TestHost.Lan).PutAsJsonAsync("/api/app/telemetry", new { enabled = false })).IsSuccessStatusCode);
        var pc = host.Client(TestHost.Loopback);
        var r = await (await pc.PutAsJsonAsync("/api/app/telemetry", new { enabled = false })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(r.GetProperty("telemetry").GetBoolean());
        r = await (await pc.PostAsync("/api/app/tour-done", null)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(r.GetProperty("tourDone").GetBoolean());
    }
}
