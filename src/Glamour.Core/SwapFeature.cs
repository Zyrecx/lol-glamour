using System.Text.Json.Nodes;
using Glamour.Lcu;
using Microsoft.Extensions.Logging;

namespace Glamour.Core;

/// <summary>
/// Lane and pick-order swaps (SPEC "Role and pick-order swaps"): what to send comes from <see cref="SwapPlanner"/>; this sends it one at a time,
/// after the action delay and only if a fresh read of the session still asks for the same call. My lanes come from the lobby before the queue
/// (the champ select session has only the assigned ones). Stop, pause and the global switch stop it like the other draft automation.
/// </summary>
public sealed class SwapFeature(ILcuClient client, IActionGateway gateway, GlamourSettings settings, TimeProvider time, ILogger<SwapFeature> log,
    AutomationGate? gate = null) : IFeature
{
    private const string LobbyPath = "/lol-lobby/v2/lobby";
    private readonly object _lock = new();
    private SwapMemory _memory = new();
    private CancellationTokenSource? _draft; // this champ select's sends
    private bool _busy;
    private string? _first, _second;

    private bool On => settings.LaneSwapAccept || settings.LaneSwapAsk || settings.PickSlotAccept || settings.PickSlotAsk;
    private bool Active => gate?.DraftActive ?? true;

    public Task OnEventAsync(LcuEvent e, CancellationToken ct)
    {
        if (e.Uri == LobbyPath && e.Data is JsonObject lobby) Remember(lobby);
        return Task.CompletedTask;
    }

    public async Task OnResyncAsync(CancellationToken ct)
    {
        if ((await client.GetAsync(LobbyPath, ct)).Body is JsonObject lobby) Remember(lobby);
    }

    /// <summary>My lane wishes. The lobby is left (or empties) when the game starts; the last ones seen stay until the next lobby.</summary>
    private void Remember(JsonObject lobby)
    {
        if (lobby["localMember"] is not JsonObject me) return;
        lock (_lock)
        {
            _first = SwapPlanner.FromPreference(me["firstPositionPreference"]?.GetValue<string>());
            _second = SwapPlanner.FromPreference(me["secondPositionPreference"]?.GetValue<string>());
        }
    }

    public Task OnPhaseAsync(string phase, CancellationToken ct)
    {
        if (phase == "ChampSelect") return Task.CompletedTask;
        lock (_lock)
        {
            _draft?.Cancel();
            _draft?.Dispose();
            _draft = null;
            _memory = new SwapMemory();
            _busy = false;
        }
        return Task.CompletedTask;
    }

    public Task OnChampSelectAsync(ChampSelectSession session, CancellationToken ct)
    {
        if (!On || !Active || session.Board is not { } board) return Task.CompletedTask;
        SwapStep? step;
        CancellationToken token;
        lock (_lock)
        {
            if (_busy) return Task.CompletedTask;
            step = SwapPlanner.Next(board, session.Swaps ?? [], _first, _second, settings, _memory, session.SimultaneousPicks);
            if (step is null) return Task.CompletedTask;
            _busy = true;
            _draft ??= CancellationTokenSource.CreateLinkedTokenSource(ct);
            token = _draft.Token;
        }
        _ = SendLaterAsync(step, token);
        return Task.CompletedTask;
    }

    private async Task SendLaterAsync(SwapStep step, CancellationToken ct)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, settings.ActionDelaySeconds)), time, ct);
            if (!On || !Active) return;
            var fresh = ChampSelectSession.FromJson((await client.GetAsync("/lol-champ-select/v1/session", ct)).Body);
            SwapMemory memory;
            string? first, second;
            lock (_lock) (memory, first, second) = (_memory, _first, _second);
            var again = fresh?.Board is { } board ? SwapPlanner.Next(board, fresh.Swaps ?? [], first, second, settings, memory, fresh.SimultaneousPicks) : null;
            if (again != step)
            {
                log.LogInformation("Swap: the session changed while waiting, not sending {Verb} {Kind} {Id}", step.Verb, step.Kind, step.Id);
                return;
            }
            // Recorded before the call: a refused call is not retried on every update.
            lock (_lock)
            {
                if (step.Verb == "accept") memory.Answered.Add((step.Kind, step.Id));
                else if (step.Lane is { } lane) memory.AskedLanes.Add(lane);
                else memory.SlotAsked = true;
            }
            var r = await gateway.SwapAsync(step.Kind, step.Id, step.Verb, ct);
            log.LogInformation("Swap: {Note} (cell {Cell}, {Kind} {Id} {Verb}) -> {Status}", step.Note, step.CellId, step.Kind, step.Id, step.Verb, r.Status);
            if (r.Status < 400) gate?.AddSwapNote(step.Note);
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { log.LogError(e, "Swap {Verb} {Kind} {Id} failed", step.Verb, step.Kind, step.Id); }
        finally { lock (_lock) _busy = false; }
    }
}
