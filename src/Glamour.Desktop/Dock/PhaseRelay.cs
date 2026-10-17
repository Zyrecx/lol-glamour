using Glamour.Core;
using Microsoft.Extensions.Hosting;

namespace Glamour.Desktop.Dock;

/// <summary>
/// Tells the dock the moment the game phase changes, so marks and tags go when champ select ends (the game starting) instead of on the dock's next
/// one-second tick (user: win rate tags stayed about a second into the game). Lives with the host: a host restart makes a new one.
/// </summary>
internal sealed class PhaseRelay(EventHub events, Action changed) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var sub = events.Subscribe();
        try
        {
            await foreach (var e in sub.ReadAllAsync(ct))
                if (e.Type == "phase") changed();
        }
        catch (OperationCanceledException) { /* host stopping */ }
    }
}
