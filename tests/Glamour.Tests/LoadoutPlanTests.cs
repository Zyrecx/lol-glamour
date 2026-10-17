using System.Text.Json.Nodes;
using Glamour.Core;
using Glamour.Host;

namespace Glamour.Tests;

/// <summary>
/// Fixtures: real OP.GG answers (INSIGHTS "Matchup data"), Ahri vs Zed mid (pair guide, JSON), Neeko support and Olaf jungle (general builds, constructor
/// text; Olaf's with the later item slots and skill order). No network.
/// </summary>
public class LoadoutPlanTests
{
    private static MatchupBuild AhriVsZed() =>
        OpggBuildSource.FromData((JsonObject)JsonNode.Parse(File.ReadAllText(FixtureFiles.PathOf("opgg-guide-ahri-zed-mid.json")))!["data"]!, 103, 238, "MIDDLE");

    private static MatchupBuild NeekoGeneral() =>
        OpggBuildSource.FromData((JsonObject)OpggBuildSource.ParseText(File.ReadAllText(FixtureFiles.PathOf("opgg-general-neeko-support.txt")))!["data"]!, 518, 0, "SUPPORT");

    private static MatchupBuild OlafGeneral() =>
        OpggBuildSource.FromData((JsonObject)OpggBuildSource.ParseText(File.ReadAllText(FixtureFiles.PathOf("opgg-general-olaf-jungle.txt")))!["data"]!, 2, 0, "JUNGLE");

    [Fact]
    public void The_pair_guide_reads_spells_runes_items_and_the_tip()
    {
        var b = AhriVsZed();
        Assert.Equal([4, 14], b.Spells[0].Ids);
        Assert.Equal(1019, b.Spells[0].Games);
        Assert.Equal((8100, 8200), (b.Runes[0].PrimaryStyleId, b.Runes[0].SubStyleId));
        Assert.Equal([8112, 8139, 8140, 8106, 8210, 8226, 5005, 5008, 5001], b.Runes[0].PerkIds);
        Assert.Equal([1056, 2003, 2003], b.Starters[0].Ids);
        Assert.Equal([3020], b.Boots[0].Ids);
        Assert.Equal(3118, b.Items[0].Ids[0]); // Malignance, the most built
        Assert.StartsWith("Do not use Charm", b.Tip);
        Assert.Equal("Zed", b.LaneAdvantage);
    }

    [Fact]
    public void The_general_build_reads_from_the_constructor_text()
    {
        var b = NeekoGeneral();
        Assert.Equal([4, 7], b.Spells.Single().Ids);
        Assert.Equal([8229, 8226, 8210, 8237, 8106, 8126, 5008, 5008, 5001], b.Runes.Single().PerkIds);
        Assert.Equal((8200, 8100), (b.Runes[0].PrimaryStyleId, b.Runes[0].SubStyleId));
        Assert.Equal([3152, 3871, 3157], b.Items.Select(i => i.Ids[0]));
        Assert.Equal([3158], b.Boots.Single().Ids);
    }

    [Fact]
    public void The_constructor_text_parser_handles_strings_nesting_and_bad_input()
    {
        var o = OpggBuildSource.ParseText("class A: name,list\nclass B: x\n\nA(\"Kog'Maw \\\"x\\\"\",[B(1.5),B(-2)])")!;
        Assert.Equal("Kog'Maw \"x\"", o["name"]!.GetValue<string>());
        Assert.Equal(-2, o["list"]![1]!["x"]!.GetValue<double>());
        Assert.Throws<FormatException>(() => OpggBuildSource.ParseText("A(1,"));
    }

    [Theory]
    [InlineData("Dr. Mundo", "DR_MUNDO")]
    [InlineData("Kog'Maw", "KOGMAW")]
    [InlineData("Nunu & Willump", "NUNU_WILLUMP")]
    [InlineData("Jarvan IV", "JARVAN_IV")]
    [InlineData("K'Sante", "KSANTE")]
    [InlineData("Wukong", "WUKONG")]
    public void Champion_names_follow_opggs_form(string name, string expected) => Assert.Equal(expected, OpggBuildSource.Name(name));

    [Fact]
    public void A_well_played_pair_gives_the_whole_plan()
    {
        var plan = LoadoutPlanner.Plan(AhriVsZed(), null, "MIDDLE", spell1: 4, spell2: 12)!;
        Assert.Equal((4, 14), (plan.Spell1, plan.Spell2)); // Flash stays on D, Teleport becomes Ignite
        Assert.True(plan.SpellsVsEnemy && plan.RunesVsEnemy && plan.ItemsVsEnemy);
        Assert.Equal(8112, plan.Runes!.PerkIds[0]);
        Assert.Equal([1056, 2003, 2003], plan.Items.Starter);
        Assert.Equal([3020], plan.Items.Boots);
        Assert.Equal([3118, 4645, 3157], plan.Items.Core); // the most played path, in buying order
        var more = Assert.Single(plan.Items.Later!); // the guide has no later slots: its next most built items
        Assert.Equal("More items", more.Title);
        Assert.Equal(3, more.Ids.Count);
        Assert.DoesNotContain(more.Ids, id => plan.Items.Core.Contains(id) || id == 3020);
    }

    [Fact]
    public void The_general_build_gives_core_path_later_slots_and_skill_order()
    {
        var b = OlafGeneral();
        Assert.Equal([6631, 3073, 6333], b.Cores!.Single().Ids);
        Assert.Equal(3, b.Later!.Count);
        Assert.Equal(["Q", "E", "W"], b.SkillOrder);
        var plan = LoadoutPlanner.Plan(null, b, "JUNGLE", 6, 11)!;
        Assert.Equal([1102, 2003], plan.Items.Starter);
        Assert.Equal([6631, 3073, 6333], plan.Items.Core);
        Assert.Equal([3047], plan.Items.Boots);
        // Death's Dance is in the core, so the fourth item leaves it out; the sixth item's options have 2-4 games: no block.
        Assert.Equal(["Fourth item", "Fifth item"], plan.Items.Later!.Select(l => l.Title));
        Assert.Equal([3053, 3036], plan.Items.Later![0].Ids);
        Assert.Equal([3143, 2501, 3065], plan.Items.Later![1].Ids);
        var set = ItemSets.Build("Glamour: Olaf", 2, plan.Items);
        Assert.Equal(["Starter · max Q > E > W", "Core", "Boots", "Fourth item", "Fifth item"], set["blocks"]!.AsArray().Select(x => x!["type"]!.GetValue<string>()));
    }

    [Fact]
    public void Flash_keeps_its_slot_and_goes_on_d_when_in_neither()
    {
        Assert.Equal((14, 4), LoadoutPlanner.Place([4, 14], 12, 4)); // Flash on F stays on F
        Assert.Equal((4, 14), LoadoutPlanner.Place([4, 14], 4, 12));
        Assert.Equal((4, 14), LoadoutPlanner.Place([4, 14], 6, 7));  // no Flash yet: D
        Assert.Equal((4, 14), LoadoutPlanner.Place([14, 4], 14, 3)); // Ignite on D, but Flash goes in slot 1 when it had none
    }

    [Fact]
    public void A_thin_pair_falls_back_to_the_general_build_part_by_part()
    {
        var thin = new MatchupBuild(518, 143, "SUPPORT", [new([4, 14], 57, 30)], [new(8200, 8100, [8229, 8226, 8210, 8237, 8106, 8126, 5008, 5008, 5001], 27, 14)],
            [new([3865, 2003], 102, 50)], [new([3020], 40, 20)], [new([3152], 10, 5)], "tip", "Zyra");
        var plan = LoadoutPlanner.Plan(thin, NeekoGeneral(), "SUPPORT", 4, 7)!;
        Assert.Equal((4, 7), (plan.Spell1, plan.Spell2)); // pair 57 games: general Flash + Heal
        Assert.False(plan.SpellsVsEnemy || plan.RunesVsEnemy || plan.ItemsVsEnemy);
        Assert.Equal([3865, 2003], plan.Items.Starter); // 102 games: the pair's
        Assert.Equal([3152, 3871, 3157], plan.Items.Core);
        Assert.Equal("tip", plan.Tip); // the tip is about the pair whatever its games
        Assert.Equal(143, plan.EnemyId);
    }

    [Fact]
    public void The_jungle_takes_a_pair_with_smite_and_nothing_without_flash()
    {
        var b = new MatchupBuild(1, 2, "JUNGLE", [new([4, 14], 900, 450), new([11, 4], 800, 400)], [], [], [], []);
        var plan = LoadoutPlanner.Plan(b, null, "JUNGLE", 4, 11)!;
        Assert.Equal((4, 11), (plan.Spell1, plan.Spell2));
        Assert.Equal((0, 0), (LoadoutPlanner.Plan(b with { Spells = [new([6, 14], 900, 450)] }, null, "MIDDLE", 4, 14)!.Spell1, 0));
        Assert.Null(LoadoutPlanner.Plan(null, null, "MIDDLE", 4, 14));
    }

    [Fact]
    public void Our_item_set_replaces_only_itself_and_keeps_the_users_sets_whole()
    {
        var user = """{"associatedChampions":[],"blocks":[{"items":[{"count":1,"id":"1055"}],"type":"Start","someNewField":{"x":[1,2]}}],"title":"Mine","uid":"3a30","unknown":true}""";
        var current = (JsonObject)JsonNode.Parse($$"""{"accountId":5,"itemSets":[{{user}},{"title":"old","uid":"glamour-matchup"}],"timestamp":0,"extra":"keep"}""")!;
        var plan = new ItemSetPlan([1056, 2003, 2003], [3118], [3020]);
        var body = ItemSets.With(current, ItemSets.Build("Glamour: Ahri vs Zed", 103, plan));
        var sets = body["itemSets"]!.AsArray();
        Assert.Equal(2, sets.Count);
        Assert.Equal(user, sets[0]!.ToJsonString()); // byte for byte, fields Glamour does not know included
        Assert.Equal("keep", body["extra"]!.GetValue<string>());
        Assert.Equal("Glamour: Ahri vs Zed", sets[1]!["title"]!.GetValue<string>());
        var starter = sets[1]!["blocks"]![0]!["items"]!.AsArray();
        Assert.Equal("""[{"id":"1056","count":1},{"id":"2003","count":2}]""", starter.ToJsonString());
        Assert.Single(ItemSets.With(current, null)["itemSets"]!.AsArray());
        Assert.Equal(2, current["itemSets"]!.AsArray().Count); // the answer read is not changed
    }
}
