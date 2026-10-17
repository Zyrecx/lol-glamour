using System.Text.Json.Nodes;
using Glamour.Lcu;
using Microsoft.Extensions.Logging;

namespace Glamour.Core;

/// <summary>
/// Opt-in. In PreEndOfGame, gives a COOL honor to each listed friend who is on the ballot (list order, up to the votes I have), then submits once.
/// Two votes in one submit were proven live (INSIGHTS "Post-game"); HEART and SHOTCALLER are not (HEART is sent as COOL).
/// A submit closes the client's honor screen (LIVE), so with `HonorStay` the votes are only staged and the screen stays for the user's other votes,
/// unless they used up every vote. Optionally skips the honor screen when no vote was cast. Never votes when the user already voted, once per gameId.
/// </summary>
public sealed class AutoHonorFeature(ILcuClient client, IActionGateway gateway, GlamourSettings settings, ILogger<AutoHonorFeature> log,
    Func<TimeSpan, CancellationToken, Task>? delay = null, AutomationGate? gate = null) : IFeature
{
    private const int Attempts = 10;
    private static readonly TimeSpan RetryEvery = TimeSpan.FromMilliseconds(500);
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;
    private string _phase = "None";
    private long _doneGameId;

    /// <summary>The run started by the last phase change (tests await it).</summary>
    public Task Idle { get; private set; } = Task.CompletedTask;

    public Task OnPhaseAsync(string phase, CancellationToken ct)
    {
        _phase = phase;
        if (phase == "PreEndOfGame" && (VotingOn || settings.SkipHonorScreen) && Active)
            Idle = Task.Run(() => RunAsync(ct), CancellationToken.None);
        return Task.CompletedTask;
    }

    private bool Active => gate?.Active ?? true;

    private bool VotingOn => settings.AutoHonorEnabled && settings.AutoHonorFriends.Count > 0;

    public Task OnChampSelectAsync(ChampSelectSession session, CancellationToken ct) => Task.CompletedTask;

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            // Skip-only needs no ballot: the screen is skippable the moment the phase flips.
            if (!VotingOn) { await SkipAsync(); return; }
            // The ballot can still be empty (gameId 0) when the phase flips; it fills within a few seconds.
            for (var i = 0; i < Attempts && _phase == "PreEndOfGame" && !ct.IsCancellationRequested; i++)
            {
                if (i > 0) await _delay(RetryEvery, ct);
                if (await client.GetAsync("/lol-honor-v2/v1/ballot", ct) is not { Status: 200, Body: { } ballot }) continue;
                var gameId = ballot["gameId"]?.GetValue<long>() ?? 0;
                if (gameId == 0 || ballot["eligibleAllies"] is not JsonArray { Count: > 0 } allies) continue;
                if (gameId == _doneGameId) return;
                if (!Active) return; // switched off or paused while the ballot filled
                _doneGameId = gameId;
                if (!await VoteAsync(ballot, allies, ct)) await SkipAsync();
                return;
            }
            if (_phase == "PreEndOfGame") await SkipAsync(); // the ballot never filled: nothing was voted
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogError(ex, "Auto-honor failed");
        }
    }

    private async Task SkipAsync()
    {
        if (!settings.SkipHonorScreen || _phase != "PreEndOfGame" || !Active) return;
        var r = await gateway.SkipHonorAsync();
        log.LogInformation("Skip honor screen -> {Status}", r.Status);
    }

    /// <summary>True when the ballot is settled (a vote exists or failed); false when nobody was voted for, so the caller may skip.</summary>
    private async Task<bool> VoteAsync(JsonNode ballot, JsonArray allies, CancellationToken ct)
    {
        if (ballot["honoredPlayers"] is JsonArray { Count: > 0 })
        {
            log.LogInformation("Auto-honor: the ballot already has a vote, leaving it alone");
            return true;
        }
        var wanted = settings.AutoHonorFriends.Select(f => f?.Trim() ?? "").Where(f => f.Length > 0).ToList();
        var friends = await client.GetAsync("/lol-chat/v1/friends", ct);
        if (friends is not { Status: 200, Body: JsonArray list }) return false;
        var onBallot = allies.Select(a => a?["puuid"]?.GetValue<string>()).Where(p => p is not null).ToHashSet();
        // One COOL per listed friend who is on the ballot, in list order, up to the votes I have (two votes in one submit were proven live, INSIGHTS "Post-game").
        var votes = Math.Max(1, ballot["votePool"]?["votes"]?.GetValue<int>() ?? 1);
        var voted = new List<string>();
        foreach (var id in wanted)
        {
            if (voted.Count >= votes) break;
            var puuid = list.Select(f => (name: $"{f?["gameName"]}#{f?["gameTag"]}", puuid: f?["puuid"]?.GetValue<string>()))
                .FirstOrDefault(f => string.Equals(f.name, id, StringComparison.OrdinalIgnoreCase)).puuid;
            if (puuid is null || !onBallot.Contains(puuid)) continue;
            var vote = await gateway.HonorAsync(puuid, "COOL", ct);
            if (!vote.IsSuccess) { log.LogWarning("Auto-honor vote for {Id} failed with {Status}", id, vote.Status); break; }
            voted.Add(id);
        }
        if (voted.Count > 0 && settings.HonorStay && voted.Count < votes)
        {
            log.LogInformation("Auto-honor: COOL to {Ids} staged, the screen stays for {Left} more vote(s)", string.Join(", ", voted), votes - voted.Count);
            return true;
        }
        if (voted.Count > 0)
        {
            var submit = await gateway.SubmitBallotAsync(ct);
            log.LogInformation("Auto-honor: COOL to {Ids}, submit -> {Status}", string.Join(", ", voted), submit.Status);
            return true;
        }
        log.LogInformation("Auto-honor: none of the listed friends is on the ballot");
        return false;
    }
}
