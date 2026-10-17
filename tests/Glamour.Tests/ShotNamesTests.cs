using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Glamour.Host;

namespace Glamour.Tests;

public class ShotNamesTests
{
    [Fact]
    public void I_am_always_the_same_made_up_name_and_the_others_keep_theirs()
    {
        var names = new ShotNames();
        var party = JsonNode.Parse("""
            {"parties":[{"ally":true,"players":[
              {"puuid":"a","championId":518,"name":"Hornbeam#EUW","isMe":true},
              {"puuid":"b","championId":222,"name":"Pinecone Lad#7731","isMe":false}]}]}
            """);
        var shown = names.Rewrite(party)!.ToJsonString();
        Assert.Contains("\"Tidewren#EUW\"", shown);
        Assert.DoesNotContain("Hornbeam", shown);
        Assert.DoesNotContain("Pinecone", shown);
        Assert.DoesNotContain("7731", shown); // a tag that is not a region is made up too
        Assert.Equal(names.Fake("Pinecone Lad#7731"), names.Fake("Pinecone Lad#7731"));
        Assert.Equal(names.Fake("Pinecone Lad"), names.Fake("Pinecone Lad#7731").Split('#')[0]);
    }

    [Fact]
    public void Champion_queue_and_skin_names_stay()
    {
        var names = new ShotNames();
        var answer = JsonNode.Parse("""{"champions":[{"id":103,"name":"Ahri"}],"queue":{"id":420,"name":"Ranked Solo/Duo"},"skin":{"name":"Star Guardian Ahri"}}""");
        Assert.Equal(answer!.ToJsonString(), names.Rewrite(answer.DeepClone())!.ToJsonString());
    }

    [Fact]
    public void A_learned_name_goes_from_free_text_and_riot_ids_from_anywhere()
    {
        var names = new ShotNames();
        names.Rewrite(JsonNode.Parse("""{"friends":[{"puuid":"c","gameName":"Saltlick","tagLine":"EUNE"}]}"""));
        var settings = JsonNode.Parse("""{"autoHonorFriends":["Saltlick#EUNE","Otherone#1234"],"note":"Swap asked Saltlick for mid"}""");
        var shown = names.Rewrite(settings)!.ToJsonString();
        Assert.DoesNotContain("Saltlick", shown);
        Assert.DoesNotContain("Otherone", shown);
        Assert.Contains("#EUNE", shown);
        Assert.Contains("Swap asked " + names.Fake("Saltlick") + " for mid", shown);
    }

    [Fact]
    public void The_signed_in_account_is_me()
    {
        var names = new ShotNames();
        var profiles = JsonNode.Parse("""[{"id":"1","name":"Second","riotId":"Second#EUW","state":"ready"},{"id":"2","name":"Hornbeam","riotId":"Hornbeam#EUW","state":"active"}]""");
        var shown = names.Rewrite(profiles)!.ToJsonString();
        Assert.Contains("\"Tidewren#EUW\"", shown);
        Assert.Contains("\"name\":\"Tidewren\"", shown);
        Assert.DoesNotContain("Second", shown);
    }

    [Fact]
    public void A_made_up_name_is_about_as_long_as_the_real_one()
    {
        var names = new ShotNames();
        Assert.InRange(names.Fake("Abc").Length, 3, 4);
        Assert.InRange(names.Fake("Longername12").Length, 10, 12);
    }

    [Fact]
    public void Screen_words_find_known_names_with_one_ocr_slip_and_their_tag()
    {
        var names = new ShotNames();
        names.Learn("Marigolds#020");
        names.Learn("quiet tide guy#EUNE");
        var line = "Marigo1ds #020 joined the lobby".Split(' ');
        var hit = Assert.Single(names.Hits(line));
        Assert.Equal((0, 2, names.Fake("Marigolds") + " #" + names.Tag("020")), hit);
        Assert.Equal((1, 1, "(" + names.Fake("Marigolds") + ")"), Assert.Single(names.Hits("Krug (Marigolds)".Split(' '))));
        Assert.Equal((0, 1, names.Fake("Marigolds") + "#EUNE"), Assert.Single(names.Hits(["Marigolds#EUNE"])));
        var spaced = names.Hits("quiet tide guy (Gromp)".Split(' '));
        Assert.Equal((0, 3, names.Fake("quiet tide guy")), Assert.Single(spaced));
        Assert.Empty(names.Hits("Krug joined the lobby".Split(' ')));
    }

    [Fact]
    public async Task With_the_setting_on_the_host_answers_with_made_up_names()
    {
        using var host = new TestHost(settings: new() { ["Glamour:ShotNames"] = "true" });
        var c = host.Client(TestHost.Loopback);
        (await c.PatchAsJsonAsync("/api/settings", new { autoHonorFriends = new[] { "Hornbeam#4410" } })).EnsureSuccessStatusCode();
        var text = await c.GetStringAsync("/api/settings");
        Assert.DoesNotContain("Hornbeam", text);
        Assert.DoesNotContain("4410", text);
        Assert.Contains("autoHonorFriends", text);
    }
}
