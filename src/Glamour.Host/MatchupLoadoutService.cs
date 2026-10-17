using System.Text.Json.Nodes;
using Glamour.Core;
using Glamour.Lcu;

namespace Glamour.Host;

public sealed record LoadoutApply(int? Enemy = null);

/// <summary>What happened to each part: "done", "kept" (the user changed it after Glamour, so it was left alone), "none" (no advice) or "failed".</summary>
public sealed record LoadoutResult(string ItemSet, string Runes, string Spells);

/// <summary>
/// The matchup loadout (SPEC "Matchup loadout"): item set, rune page and summoner spells for my champion against my lane opponent, from
/// <see cref="IMatchupBuildSource"/>. Applied from the panel (Apply) or, with `MatchupLoadout` on, by itself once my pick is locked and the opponent is
/// guessed (<see cref="OnChampSelectAsync"/>), after
/// <see cref="ApplyDelay"/> shown as a countdown with Cancel (<see cref="CancelAuto"/>). The rune page is the same single temporary page the recommended runes use (<see cref="RunePageTracker"/>),
/// the item set Glamour's one set (<see cref="ItemSets"/>). Automatic writes leave spells or runes alone once the user changed them after Glamour,
/// and stop for the rest of the champ select once the user applied a loadout by hand.
/// </summary>
public sealed class MatchupLoadoutService(ILcuClient client, IActionGateway gateway, GlamourSettings settings, IMatchupSource matchups, IMatchupBuildSource builds,
    RunePageTracker runePages, ChampionCatalog champions, AutomationGate gate, TimeProvider time, ILogger<MatchupLoadoutService> log) : IFeature
{
    /// <summary>How long an automatic apply waits after it is ready, so the panel can count it down and Cancel it.</summary>
    public TimeSpan ApplyDelay { get; set; } = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan EndMargin = TimeSpan.FromSeconds(3); // an apply never waits into the last seconds of champ select

    private readonly Lock _lock = new();
    private CancellationTokenSource _draft = new();
    private (int Champion, int Enemy)? _applied, _working;
    private (int Spell1, int Spell2)? _wroteSpells;
    private int? _wroteRunePage;
    private string? _lastKey; // my champion and the enemy picks the last automatic run saw: FINALIZATION sends many events that change neither
    private readonly HashSet<(int Champion, int Enemy, string Lane)> _warmed = [];
    private bool _byHand, _cancelled;
    private CancellationTokenSource? _wait;

    public (int Champion, int Enemy)? Applied { get { lock (_lock) return _applied; } }

    /// <summary>The user cancelled the automatic loadout for this champ select (Apply by hand still works).</summary>
    public bool Cancelled { get { lock (_lock) return _cancelled; } }

    /// <summary>
    /// Cancel on the panel: no automatic loadout for the rest of this champ select, a waiting one included. False when the automatic loadout is off,
    /// already cancelled or already applied by hand.
    /// </summary>
    public bool CancelAuto()
    {
        int enemy;
        lock (_lock)
        {
            if (!settings.MatchupLoadout || _cancelled || _byHand) return false;
            _cancelled = true;
            _wait?.Cancel();
            enemy = _applied?.Enemy ?? 0;
        }
        log.LogInformation("Matchup loadout cancelled for this champ select");
        gate.SetLoadout(new LoadoutStatus("cancelled", enemy));
        return true;
    }

    /// <summary>The loadout for my hovered or locked champion against <paramref name="enemyId"/>, else the guessed opponent. Null when either is not known yet.</summary>
    public async Task<LoadoutPlan?> PlanAsync(ChampSelectBoard? board, int? enemyId, CancellationToken ct)
    {
        if (board is not { Mode: ChampSelectMode.Draft } || board.MyTeam.FirstOrDefault(p => p.IsMe) is not { ChampionId: > 0, Lane.Length: > 0 } me) return null;
        var enemy = enemyId is { } chosen && board.TheirTeam.Any(p => p.ChampionId == chosen) ? chosen : (await CounterPick.ViewAsync(board, settings, matchups, ct)).EnemyId;
        if (enemy <= 0) return null;
        var pair = builds.GetAsync(me.ChampionId, enemy, me.Lane, ct);
        var general = builds.GetAsync(me.ChampionId, 0, me.Lane, ct);
        await Task.WhenAll(pair, general);
        return LoadoutPlanner.Plan(pair.Result, general.Result, me.Lane, me.Spell1Id, me.Spell2Id) is { } plan ? plan with { EnemyId = enemy } : null;
    }

    /// <summary>Applies every part of the plan (Apply in the panel): the user asked, so nothing is kept back, and automation stays out for this champ select.</summary>
    public async Task<LoadoutResult> ApplyByHandAsync(LoadoutPlan plan, CancellationToken ct)
    {
        lock (_lock) { _byHand = true; _wait?.Cancel(); }
        var result = await ApplyAsync(plan, keepUserChanges: false, ct);
        if (settings.MatchupLoadout) gate.SetLoadout(new LoadoutStatus("done", plan.EnemyId));
        return result;
    }

    private async Task<LoadoutResult> ApplyAsync(LoadoutPlan plan, bool keepUserChanges, CancellationToken ct)
    {
        var names = (await champions.GetAsync(ct))?.Champions;
        string Name(int id) => names?.FirstOrDefault(c => c.Id == id)?.Name ?? $"#{id}";
        var itemSet = await ItemSetAsync(plan, $"{Runes.TempPagePrefix}: {Name(plan.ChampionId)} vs {Name(plan.EnemyId)}", ct);
        var runes = await RunesAsync(plan, $"{Runes.TempPagePrefix} vs {Name(plan.EnemyId)}", keepUserChanges, ct);
        var spells = await SpellsAsync(plan, keepUserChanges, ct);
        lock (_lock) _applied = (plan.ChampionId, plan.EnemyId);
        log.LogInformation("Matchup loadout {Champ} vs {Enemy}: item set {Items}, runes {Runes}, spells {Spells}", plan.ChampionId, plan.EnemyId, itemSet, runes, spells);
        return new LoadoutResult(itemSet, runes, spells);
    }

    private async Task<string> ItemSetAsync(LoadoutPlan plan, string title, CancellationToken ct)
    {
        if (plan.Items.Core.Count == 0 && plan.Items.Starter.Count == 0) return "none";
        if (await client.GetAsync("/lol-summoner/v1/current-summoner", ct) is not { Status: 200, Body: { } me } || me["summonerId"]?.GetValue<long>() is not { } id) return "failed";
        // Read right before the write: PUT replaces the whole list, so it must carry the user's sets as they are now.
        if (await client.GetAsync($"/lol-item-sets/v1/item-sets/{id}/sets", ct) is not { Status: 200, Body: JsonObject current }) return "failed";
        var put = await gateway.PutItemSetsAsync(id, ItemSets.With(current, ItemSets.Build(title, plan.ChampionId, plan.Items)), ct);
        return put.IsSuccess ? "done" : "failed";
    }

    private async Task<string> RunesAsync(LoadoutPlan plan, string name, bool keepUserChanges, CancellationToken ct)
    {
        if (plan.Runes is not { } page) return "none";
        if (keepUserChanges)
        {
            // Kept: another page chosen after the loadout's, or a recommended page the user applied through Glamour still current.
            int? wrote;
            lock (_lock) wrote = _wroteRunePage;
            var current = await client.GetAsync("/lol-perks/v1/currentpage", ct) is { Status: 200, Body: { } now } ? now["id"]?.GetValue<int>() : null;
            if (wrote is { } w ? current != w : runePages.LastCreated is { } chosen && current == chosen) return "kept";
        }
        await LoadoutEndpoints.RemoveOwnPreviousPageAsync(client, gateway, runePages, ct);
        var created = await gateway.CreateTempRunePageAsync(new
        {
            name,
            primaryStyleId = page.PrimaryStyleId,
            subStyleId = page.SubStyleId,
            selectedPerkIds = page.PerkIds,
            current = true,
            isTemporary = true, // no recommendation fields needed (INSIGHTS "Runes")
        }, ct);
        if (created.IsSuccess && created.Body?["id"] is { } made && created.Body["isTemporary"]?.GetValue<bool>() != true)
        {
            // Without Riot's recommendation fields the client might make a lasting page (and use up a page slot): take it straight back.
            log.LogWarning("Rune page {Id} came back not temporary; deleting it", made.GetValue<int>());
            await gateway.DeleteRunePageAsync(made.GetValue<int>(), ct);
            return "failed";
        }
        if (created.IsSuccess && runePages.Created(created.Body))
            lock (_lock) _wroteRunePage = runePages.LastCreated;
        return created.IsSuccess ? "done" : "failed";
    }

    private async Task<string> SpellsAsync(LoadoutPlan plan, bool keepUserChanges, CancellationToken ct)
    {
        if (plan.Spell1 == 0) return "none";
        var now = await MySpellsAsync(ct);
        (int, int)? wrote;
        lock (_lock) wrote = _wroteSpells;
        if (keepUserChanges && wrote is { } w && now is { } n && n != w) return "kept";
        if (now == (plan.Spell1, plan.Spell2)) { lock (_lock) _wroteSpells = now; return "done"; }
        var r = await gateway.SetSelectionAsync(plan.Spell1, plan.Spell2, null, ct);
        // A 204 is not proof (an unowned skin is ignored the same way): read the selection back.
        var after = r.IsSuccess ? await MySpellsAsync(ct) : null;
        if (after != (plan.Spell1, plan.Spell2)) return "failed";
        lock (_lock) _wroteSpells = after;
        return "done";
    }

    private async Task<(int, int)?> MySpellsAsync(CancellationToken ct) =>
        await client.GetAsync("/lol-champ-select/v1/session", ct) is { Status: 200, Body: { } s } && ChampSelectSession.FromJson(s)?.Board?.MyTeam.FirstOrDefault(p => p.IsMe) is { } me
            ? (me.Spell1Id, me.Spell2Id) : null;

    public Task OnPhaseAsync(string phase, CancellationToken ct)
    {
        if (phase == "ChampSelect") return Task.CompletedTask;
        lock (_lock)
        {
            _draft.Cancel();
            _draft = new CancellationTokenSource();
            (_applied, _working, _wroteSpells, _wroteRunePage, _byHand, _cancelled, _lastKey) = (null, null, null, null, false, false, null);
            _warmed.Clear();
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Fetches the builds before anyone asks (an OP.GG call takes 5-8 s, live): the general one as soon as I hover or pick a champion, and the pair
    /// against each enemy pick, so the panel's card and the automatic apply find them cached. Each once per champ select; failures are the source's.
    /// </summary>
    private void Warm(ChampSelectBoard? board)
    {
        if (board is not { Mode: ChampSelectMode.Draft } || board.MyTeam.FirstOrDefault(p => p.IsMe) is not { ChampionId: > 0, Lane.Length: > 0 } me) return;
        CancellationToken draft;
        List<(int, int, string)> fresh;
        lock (_lock)
        {
            fresh = [.. new[] { 0 }.Concat(board.TheirTeam.Select(p => p.ChampionId).Where(id => id > 0))
                .Select(enemy => (me.ChampionId, enemy, me.Lane)).Where(_warmed.Add)];
            draft = _draft.Token;
        }
        foreach (var (champion, enemy, lane) in fresh) _ = builds.GetAsync(champion, enemy, lane, draft);
    }

    /// <summary>
    /// With `MatchupLoadout` on: once my pick is locked and the opponent guessed, applies the loadout, again only when the guess changes. Runs in the
    /// background (a fresh pair takes OP.GG 5-8 s), waits <see cref="ApplyDelay"/> (never into the last
    /// <see cref="EndMargin"/> of champ select) and never runs after champ select. Stop and the pause (`AutomationGate.DraftActive`) cover it.
    /// </summary>
    public Task OnChampSelectAsync(ChampSelectSession session, CancellationToken ct)
    {
        Warm(session.Board);
        if (!settings.MatchupLoadout || !gate.DraftActive || session.Board is not { Mode: ChampSelectMode.Draft } board
            || board.MyTeam.FirstOrDefault(p => p.IsMe) is not { Locked: true, ChampionId: > 0 } me) return Task.CompletedTask;
        CancellationToken draft;
        lock (_lock)
        {
            var key = $"{me.ChampionId}:{string.Join(',', board.TheirTeam.Select(p => p.ChampionId))}";
            if (_byHand || _cancelled || _working is not null || key == _lastKey) return Task.CompletedTask;
            _lastKey = key;
            _working = (me.ChampionId, 0);
            draft = _draft.Token;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                var plan = await PlanAsync(board, null, draft);
                if (plan is null || !Fresh(plan)) return;
                var delay = ApplyDelay;
                // FINALIZATION's timer runs to the game: leave the apply room before it ends. Other phases' timers end only the phase.
                if (session.Timer.Phase == "FINALIZATION" && session.TimeLeft(time.GetUtcNow()) - EndMargin is var room && room < delay) delay = room;
                if (delay > TimeSpan.Zero)
                {
                    CancellationTokenSource wait;
                    lock (_lock) wait = _wait = CancellationTokenSource.CreateLinkedTokenSource(draft);
                    gate.SetLoadout(new LoadoutStatus("waiting", plan.EnemyId, time.GetUtcNow() + delay));
                    try { await Task.Delay(delay, time, wait.Token); }
                    catch (OperationCanceledException) { return; } // cancelled, applied by hand or champ select over: each sets its own status
                    finally { lock (_lock) if (_wait == wait) _wait = null; wait.Dispose(); }
                    if (!Fresh(plan)) { gate.SetLoadout(null); return; }
                }
                await ApplyAsync(plan, keepUserChanges: true, draft);
                gate.SetLoadout(new LoadoutStatus("done", plan.EnemyId));
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { log.LogWarning(ex, "Matchup loadout failed"); }
            finally { lock (_lock) _working = null; }
        }, CancellationToken.None);
        return Task.CompletedTask;
    }

    private bool Fresh(LoadoutPlan plan)
    {
        lock (_lock) return !_byHand && !_cancelled && _applied != (plan.ChampionId, plan.EnemyId) && gate.DraftActive && settings.MatchupLoadout;
    }
}
