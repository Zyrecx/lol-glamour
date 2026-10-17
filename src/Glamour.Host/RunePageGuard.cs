using System.Text.Json.Nodes;
using Glamour.Core;
using Glamour.Lcu;

namespace Glamour.Host;

/// <summary>
/// League rewrites the current temporary rune page once, about 6 s before champ select ends, with one of its own recommended pages (INSIGHTS "Runes"):
/// Glamour's page too, and the game then runs that. This puts Glamour's page back right after (live, bot customs: League does not rewrite again).
/// A change counts as League's only when it lands on Glamour's page (<see cref="RunePageTracker"/>) while that page is current, in
/// <see cref="Window"/> before FINALIZATION ends, and the client's rune window (<see cref="RuneWindow"/>) was not open in the last <see cref="Quiet"/>:
/// picking a recommended page by hand looks exactly like League's rewrite, but it happens in that window. Any other change is the user's and becomes
/// the page to keep, so a hand edit is what comes back. Another page made current is left alone, and so is everything after Stop or the pause.
/// </summary>
public sealed class RunePageGuard(RunePageTracker pages, IActionGateway gateway, RuneWindow runeWindow, AutomationGate gate, TimeProvider time, ILogger<RunePageGuard> log) : IFeature
{
    public const string CurrentPage = "/lol-perks/v1/currentpage";
    /// <summary>Time left in FINALIZATION when League's rewrite lands: 5.97-6.02 s in four bot customs, widened for timer drift.</summary>
    public static readonly (TimeSpan From, TimeSpan To) Window = (TimeSpan.FromSeconds(5.5), TimeSpan.FromSeconds(6.5));
    public static readonly TimeSpan Quiet = TimeSpan.FromSeconds(2);
    private const int MaxRestores = 2;

    private readonly Lock _lock = new();
    private ChampSelectTimer? _timer;
    private (int Id, PageRunes Runes)? _keep;
    private (PageRunes Runes, DateTimeOffset At)? _undone; // the rewrite last put back: the client sends its event twice (LIVE), the repeat must not count again
    private static readonly TimeSpan Repeat = TimeSpan.FromSeconds(1);
    private int _restores;

    private sealed record PageRunes(string Name, int Primary, int Sub, string Perks)
    {
        public static PageRunes? From(JsonNode? p) =>
            p?["primaryStyleId"] is { } primary && p["subStyleId"] is { } sub && p["selectedPerkIds"] is JsonArray { Count: > 0 } perks
                ? new(p["name"]?.GetValue<string>() ?? "", primary.GetValue<int>(), sub.GetValue<int>(), string.Join(',', perks.Select(x => x?.GetValue<int>())))
                : null;

        public object Body() => new
        {
            name = Name, primaryStyleId = Primary, subStyleId = Sub, selectedPerkIds = Perks.Split(',').Select(int.Parse).ToArray(), current = true, isTemporary = true,
        };
    }

    public Task OnPhaseAsync(string phase, CancellationToken ct)
    {
        if (phase != "ChampSelect") lock (_lock) (_timer, _keep, _undone, _restores) = (null, null, null, 0);
        return Task.CompletedTask;
    }

    public Task OnChampSelectAsync(ChampSelectSession session, CancellationToken ct)
    {
        lock (_lock) _timer = session.Timer;
        return Task.CompletedTask;
    }

    public async Task OnEventAsync(LcuEvent e, CancellationToken ct)
    {
        if (e.Uri != CurrentPage || e.EventType == "Delete" || e.Data is not JsonObject page || page["id"]?.GetValue<int>() is not { } id) return;
        if (pages.LastCreated != id || page["isTemporary"]?.GetValue<bool>() != true || PageRunes.From(page) is not { } now) return;
        PageRunes keep;
        lock (_lock)
        {
            // Glamour's page as the create answered it, not as the first event shows it: that event can already be League's rewrite.
            if (_keep is not { } k || k.Id != id)
            {
                var made = pages.Page?["id"]?.GetValue<int>() == id ? PageRunes.From(pages.Page) : null;
                k = (_keep = (id, made ?? now)).Value;
            }
            if (k.Runes == now || _undone is { } u && u.Runes == now && time.GetUtcNow() - u.At < Repeat) return; // unchanged, Glamour's own restore coming back, or a repeat of the rewrite it undid
            var left = _timer is { Phase: "FINALIZATION" } t ? new ChampSelectSession(0, t, []).TimeLeft(time.GetUtcNow()) : (TimeSpan?)null;
            var window = runeWindow.OpenWithin(Quiet, time.GetUtcNow());
            var league = left >= Window.From && left <= Window.To && window != true;
            if (!league || _restores >= MaxRestores || !gate.DraftActive)
            {
                _keep = (id, now);
                var who = !league ? "by the user" : !gate.DraftActive ? "in League's slot, automation stopped" : "in League's slot again, restores used up";
                log.LogInformation("Rune page {Id} changed {Who} ({Name}, {Left} left, rune window {Window}): kept", id, who, now.Name, Left(left), window?.ToString() ?? "unknown");
                return;
            }
            _restores++;
            _undone = (now, time.GetUtcNow());
            keep = k.Runes;
            log.LogInformation("Rune page {Id} rewritten by League to {Name} ({Left} left, rune window {Window}): putting {Kept} back", id, now.Name, Left(left), window?.ToString() ?? "unknown", keep.Name);
        }
        var r = await gateway.UpdateRunePageAsync(id, keep.Body(), ct);
        if (!r.IsSuccess) log.LogWarning("Rune page {Id} could not be put back: {Status}", id, r.Status);
    }

    private static string Left(TimeSpan? left) => left is { } l ? $"{l.TotalSeconds:0.00} s" : "no FINALIZATION timer";
}
