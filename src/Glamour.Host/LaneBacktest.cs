using System.Globalization;
using System.Text.Json;
using Glamour.Core;

namespace Glamour.Host;

/// <summary>
/// `--lane-backtest &lt;lane-truth.json&gt;` (from `tools/lane-truth.mjs`): plays every team as the enemy, in every pick order and for every lane of mine, and prints how often
/// the opponent the lane guess names is right. One row per threshold candidate, the rule as built ("Glamour": <see cref="CounterPick.SureChance"/>, the likeliest once all five are picked), the old rule (the most likely split, 30% share, 5% from four picks) last.
/// </summary>
public static class LaneBacktest
{
    private sealed record Player(int Id, string Lane);

    public static int Run(string file)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        var shares = doc.RootElement.GetProperty("shares").EnumerateObject().ToDictionary(p => int.Parse(p.Name, CultureInfo.InvariantCulture),
            p => p.Value.ValueKind == JsonValueKind.Object ? (IReadOnlyDictionary<string, double>)p.Value.EnumerateObject().ToDictionary(l => l.Name, l => l.Value.GetDouble()) : null);
        var teams = doc.RootElement.GetProperty("teams").EnumerateArray()
            .Select(t => t.GetProperty("players").EnumerateArray().Select(p => new Player(p.GetProperty("id").GetInt32(), p.GetProperty("lane").GetString()!)).ToList()).ToList();

        // (picked so far, chance of the top candidate, is it right, old rule's name, is that right) per pick-order prefix and lane of mine.
        var cases = new List<(int Picked, double Chance, bool Right, bool OldNamed, bool OldRight, bool Present)>();
        foreach (var team in teams)
            foreach (var order in Orders(team))
                for (var k = 1; k <= 5; k++)
                {
                    var picked = order.Take(k).ToList();
                    List<(int, IReadOnlyDictionary<string, double>?)> input = [.. picked.Select(p => (p.Id, shares.GetValueOrDefault(p.Id)))];
                    var assigned = LaneAssigner.Assign(input);
                    foreach (var lane in LaneNames.All)
                    {
                        var chances = LaneAssigner.Chances(input, lane);
                        var best = chances.OrderByDescending(c => c.Value).First();
                        var truth = picked.FirstOrDefault(p => p.Lane == lane)?.Id ?? 0;
                        var least = k >= 4 ? 5 : 30;
                        var old = picked.FirstOrDefault(p => assigned[p.Id] == lane && shares.GetValueOrDefault(p.Id) is { } s && s.GetValueOrDefault(lane) >= least)?.Id ?? 0;
                        cases.Add((k, best.Value, best.Key == truth, old > 0, old > 0 && old == truth, truth > 0));
                    }
                }

        Console.WriteLine($"{teams.Count} teams, {cases.Count} cases (pick order x picks so far x my lane); 'named' = an opponent is named, 'right' = of those, the true laner");
        Console.WriteLine($"{"rule",-12}{"picked",8}{"named",8}{"right",8}{"missed",8}");
        foreach (var k in new int?[] { null, 1, 2, 3, 4, 5 })
        {
            var set = cases.Where(c => k is null || c.Picked == k).ToList();
            string Row(string rule, Func<(int Picked, double Chance, bool Right, bool OldNamed, bool OldRight, bool Present), bool> named,
                Func<(int Picked, double Chance, bool Right, bool OldNamed, bool OldRight, bool Present), bool> right)
            {
                var n = set.Where(named).ToList();
                // missed: my laner is picked but nobody is named
                var missed = set.Count(c => c.Present && !named(c)) / (double)Math.Max(1, set.Count(c => c.Present));
                return $"{rule,-12}{k?.ToString(CultureInfo.InvariantCulture) ?? "all",8}{Pct(n.Count, set.Count),8}{Pct(n.Count(right), n.Count),8}{missed * 100,7:0}%";
            }
            foreach (var t in new[] { 0.4, 0.5, 0.6, 0.7, 0.8 })
                Console.WriteLine(Row($"chance {t * 100:0}%", c => c.Chance >= t, c => c.Right));
            Console.WriteLine(Row("Glamour", c => c.Chance >= CounterPick.SureChance || c.Picked == 5, c => c.Right));
            Console.WriteLine(Row("old rule", c => c.OldNamed, c => c.OldRight));
            Console.WriteLine();
        }

        Console.WriteLine("Calibration (all cases): chance of the top candidate vs how often it is right");
        foreach (var g in cases.GroupBy(c => Math.Min(9, (int)(c.Chance * 10))).OrderBy(g => g.Key))
            Console.WriteLine($"  {g.Key * 10,3}-{g.Key * 10 + 10,3}%  {g.Count(),6} cases  right {Pct(g.Count(c => c.Right), g.Count()),5}");
        return 0;
    }

    private static string Pct(int part, int whole) => whole == 0 ? "-" : $"{part * 100.0 / whole:0}%";

    private static IEnumerable<List<Player>> Orders(List<Player> team)
    {
        if (team.Count <= 1) { yield return team; yield break; }
        for (var i = 0; i < team.Count; i++)
            foreach (var rest in Orders([.. team.Where((_, j) => j != i)]))
                yield return [team[i], .. rest];
    }
}
