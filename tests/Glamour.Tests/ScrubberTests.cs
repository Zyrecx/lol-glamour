using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Glamour.Host.Logging;

namespace Glamour.Tests;

/// <summary>Made-up players and ids only: nothing here comes from a real log.</summary>
public class ScrubberTests : IDisposable
{
    private const string Puuid = "0a1b2c3d-1111-5222-8333-444455556666", Other = "9f8e7d6c-aaaa-5bbb-8ccc-ddddeeeeffff";
    private const string Pre = "2026-01-02 10:11:12.345 info ";

    private readonly Scrubber _s = new("Tester", "TESTBOX");
    private readonly TestHost _host = new();
    public void Dispose() => _host.Dispose();

    [Fact]
    public void A_player_keeps_one_number_for_puuid_summoner_id_and_riot_id_across_lines()
    {
        var a = _s.Line(Pre + $$"""Glamour.Events: lobby {"roster":[{"puuid":"{{Puuid}}","summonerId":1234567,"isMe":true},{"puuid":"{{Other}}","summonerId":7654321,"isMe":false}]}""");
        var b = _s.Line(Pre + $$"""Glamour.Events: honor {"gameId":5550001234,"candidates":[{"puuid":"{{Puuid}}","summonerId":1234567,"name":"Made Up Name#EUW","championId":1}]}""");
        var c = _s.Line(Pre + "Glamour.Core.AutoHonorFeature: Auto-honor: COOL to Made Up Name#EUW staged");

        Assert.Contains("\"puuid\":\"Player1\",\"summonerId\":\"Player1\"", a);
        Assert.Contains("\"puuid\":\"Player2\",\"summonerId\":\"Player2\"", a);
        Assert.Contains("\"name\":\"Player1\"", b);
        Assert.Contains("\"gameId\":\"Game1\"", b);
        Assert.Equal(Pre + "Glamour.Core.AutoHonorFeature: Auto-honor: COOL to Player1 staged", c);
        Assert.Contains("\"dodgeIds\":[\"Player1\",\"Player3\"]", _s.Line(Pre + """Glamour.Events: dodge {"dodgeIds":[1234567,2222222]}"""));
        foreach (var line in new[] { a, b, c })
        {
            Assert.DoesNotContain("Made Up", line);
            Assert.DoesNotContain("1234567", line);
            Assert.DoesNotContain(Puuid, line);
        }
    }

    [Fact]
    public void Game_ids_in_text_and_events_share_a_number()
    {
        var a = _s.Line(Pre + """Glamour.Events: parties {"gameId":4000000001,"parties":[]}""");
        var b = _s.Line(Pre + "Glamour.Desktop: Loading parties: done for game 4000000001, loading screen gone after 16.8 s");
        var c = _s.Line(Pre + "Glamour.Core.Engine: Parties: the gameflow session still holds game 4000000002, not this champ select's");
        Assert.Contains("\"gameId\":\"Game1\"", a);
        Assert.Contains("done for game Game1,", b);
        Assert.Contains("holds game Game2,", c);
        Assert.StartsWith(Pre, b); // the time stays
    }

    [Fact]
    public void Riot_ids_in_plain_text_go_and_ranks_and_short_names_stay()
    {
        var a = _s.Line(Pre + """Glamour.Events: board {"myTeam":[{"cellId":1,"name":"IV#TAG1","championId":0}]}""");
        var b = _s.Line(Pre + "Glamour.Core.PresenceOverrideFeature: Presence rank EMERALD IV -> CHALLENGER I, friend Someone#1234");
        Assert.Contains("\"name\":\"Player1\"", a);
        Assert.Equal(Pre + "Glamour.Core.PresenceOverrideFeature: Presence rank EMERALD IV -> CHALLENGER I, friend Player2", b);
    }

    [Fact]
    public void A_name_written_with_json_escapes_is_learned_and_goes_from_plain_text()
    {
        var a = _s.Line(Pre + """Glamour.Events: honor {"candidates":[{"puuid":"0a1b2c3d-1111-5222-8333-444455556666","name":"Mad\u0129 Up","ally":true}]}""");
        var b = _s.Line(Pre + "Glamour.Core.AutoHonorFeature: Auto-honor: COOL to Madĩ Up staged");
        Assert.DoesNotContain("Mad", a);
        Assert.Equal(Pre + "Glamour.Core.AutoHonorFeature: Auto-honor: COOL to Player1 staged", b);
    }

    [Fact]
    public void A_name_beside_no_player_field_is_not_a_player()
    {
        var a = _s.Line(Pre + """Glamour.Events: queue {"queue":{"name":"Ranked Solo","queueId":420}}""");
        Assert.Contains("\"name\":\"Ranked Solo\"", a);
    }

    [Fact]
    public void Level_and_record_go_rank_stays()
    {
        var a = _s.Line(Pre + """Glamour.Events: profiles {"profiles":[{"id":"ab12cd34","riotId":"Some One#EUW","card":{"iconId":10,"level":300,"solo":{"tier":"GOLD","division":"II","lp":21,"wins":57,"losses":51}}}]}""");
        Assert.Contains("\"riotId\":\"Player1\"", a);
        Assert.Contains("\"tier\":\"GOLD\",\"division\":\"II\",\"lp\":null,\"wins\":null,\"losses\":null", a);
        Assert.Contains("\"iconId\":null,\"level\":null", a);
    }

    [Fact]
    public void Summoner_id_in_an_item_set_path_goes()
    {
        var a = _s.Line(Pre + "Glamour.Core.ActionGateway: PUT /lol-item-sets/v1/item-sets/3000000000000001/sets -> 201");
        Assert.Equal(Pre + "Glamour.Core.ActionGateway: PUT /lol-item-sets/v1/item-sets/Player1/sets -> 201", a);
        Assert.Contains("/lol-perks/v1/pages/1826450780", _s.Line(Pre + "Glamour.Core.ActionGateway: DELETE /lol-perks/v1/pages/1826450780 -> 204")); // a page id is no one's
    }

    [Fact]
    public void Addresses_go_but_loopback_and_clock_stay()
    {
        var a = _s.Line(Pre + "Glamour.Requests: GET /api/state -> 403 for 192.168.1.50 (host 10.0.0.7:5170, origin none) via fe80::1c2b:3a4d:5e6f:7a8b and ::1 on 127.0.0.1:5170");
        Assert.Equal(Pre + "Glamour.Requests: GET /api/state -> 403 for x.x.x.x (host x.x.x.x:5170, origin none) via x::x and ::1 on 127.0.0.1:5170", a);
    }

    [Fact]
    public void Secrets_go()
    {
        var a = _s.Text("GET https://riot:Zq9xSecret@127.0.0.1:50123/lol-gameflow failed; open https://pc.local:5171/pair.html#S3cr3tPairing; ntfy https://ntfy.sh/my-private-topic; password=hunter22");
        Assert.Equal("GET https://riot:…@127.0.0.1:50123/lol-gameflow failed; open https://pc.local:5171/pair.html#… ntfy https://ntfy.sh/… password=…", a);
        var b = _s.Line(Pre + """Glamour.Events: settings {"pin":"1234","ntfyTopic":"x","topic":"secret-topic"}""");
        Assert.DoesNotContain("1234", b);
        Assert.DoesNotContain("secret-topic", b);
    }

    [Fact]
    public void User_machine_and_source_folder_go_from_paths()
    {
        var a = _s.Text(@"   at Glamour.Lcu.LcuHttpClient.SendAsync() in D:\Work\private-name\src\Glamour.Lcu\LcuHttpClient.cs:line 23");
        Assert.Equal(@"   at Glamour.Lcu.LcuHttpClient.SendAsync() in src\Glamour.Lcu\LcuHttpClient.cs:line 23", a);
        Assert.Equal(@"Content root path: C:\Users\User\AppData\Local\Glamour\app\", _s.Text(@"Content root path: C:\Users\Tester\AppData\Local\Glamour\app\"));
        Assert.Equal("https://PC.local:5171 on PC by User", _s.Text("https://testbox.local:5171 on TESTBOX by tester"));
    }

    [Fact]
    public void Numbers_start_again_in_a_new_scrubber()
    {
        var line = Pre + $$"""Glamour.Events: lobby {"roster":[{"puuid":"{{Other}}","isMe":false}]}""";
        _s.Line(Pre + $$"""Glamour.Events: lobby {"roster":[{"puuid":"{{Puuid}}","isMe":true}]}""");
        Assert.Contains("Player2", _s.Line(line));
        Assert.Contains("Player1", new Scrubber(null, null).Line(line));
    }

    [Fact]
    public async Task Send_a_log_zips_both_logs_scrubbed_on_the_pc_only()
    {
        Directory.CreateDirectory(_host.LogDir);
        await File.WriteAllTextAsync(Path.Combine(_host.LogDir, "glamour.1.log"), Pre + $$"""Glamour.Events: lobby {"roster":[{"puuid":"{{Puuid}}","isMe":true}]}""" + "\n");
        await File.WriteAllTextAsync(Path.Combine(_host.LogDir, "glamour.log"), Pre + $"Glamour.Core.Engine: {Puuid} left, friend Someone#1234\n");

        Assert.Equal(HttpStatusCode.Unauthorized, (await _host.Client().PostAsync("/api/logs/report", null)).StatusCode); // a phone, not signed in

        var r = await _host.Client(TestHost.Loopback).PostAsync("/api/logs/report", null);
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var body = await r.Content.ReadFromJsonAsync<JsonElement>();
        var path = body.GetProperty("path").GetString()!;
        Assert.StartsWith(Path.Combine(_host.LogDir, "reports"), path);
        Assert.StartsWith($"https://github.com/{LogReport.GitHubRepo}/issues/new?template=bug.yml&", body.GetProperty("issue").GetString());

        using var zip = ZipFile.OpenRead(path);
        Assert.Equal(["about.txt", "glamour.1.log", "glamour.log"], zip.Entries.Select(e => e.Name).ToArray());
        using var reader = new StreamReader(zip.GetEntry("glamour.log")!.Open());
        Assert.Equal(Pre + "Glamour.Core.Engine: Player1 left, friend Player2", reader.ReadLine()); // numbered from the older log on
    }

    [Fact]
    public void A_game_name_read_off_the_screen_goes_even_before_its_player_shows_in_an_event()
    {
        var dir = Path.Combine(_host.LogDir, "ocr");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "glamour.log"),
            Pre + "Glamour.Desktop: Augment tags: the button is up but the cards read as  / Fakename / \n" +
            Pre + """Glamour.Events: board {"myTeam":[{"cellId":1,"name":"Fakename#TAG1","isMe":true}]}""" + "\n");
        using var zip = ZipFile.OpenRead(LogReport.Build(dir, new Scrubber(null, null), new DateTime(2026, 1, 1)));
        var text = new StreamReader(zip.GetEntry("glamour.log")!.Open()).ReadToEnd();
        Assert.DoesNotContain("Fakename", text);
        Assert.Contains("read as  / Player1 /", text);
    }

    [Fact]
    public void Only_the_last_five_zips_are_kept()
    {
        var dir = Path.Combine(_host.LogDir, "keep");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "glamour.log"), "x\n");
        for (var i = 0; i < 7; i++) LogReport.Build(dir, new Scrubber(null, null), new DateTime(2026, 1, 1, 10, 0, i));
        var left = Directory.GetFiles(Path.Combine(dir, "reports")).Select(Path.GetFileName).Order().ToArray();
        Assert.Equal(5, left.Length);
        Assert.Equal("glamour-log-20260101-100002.zip", left[0]);
    }

    [Fact]
    public void The_issue_link_opens_the_problem_form_with_version_and_zip_filled_in()
    {
        var link = LogReport.IssueLink("owner/repo", "0.1.0+abc", "glamour-log-20260101-100000.zip");
        Assert.StartsWith("https://github.com/owner/repo/issues/new?template=bug.yml&", link);
        var query = System.Web.HttpUtility.ParseQueryString(new Uri(link).Query);
        Assert.Equal("0.1.0+abc", query["version"]);
        Assert.Contains("glamour-log-20260101-100000.zip", query["log"]);
        var form = File.ReadAllLines(Path.Combine(RepoRoot(), ".github", "ISSUE_TEMPLATE", "bug.yml")).Select(l => l.Trim());
        foreach (var field in query.AllKeys.Where(k => k != "template")) Assert.Contains($"id: {field}", form); // the form prefills fields by id
    }

    private static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string self = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(self)!, "..", ".."));
}
