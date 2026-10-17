using Glamour.Core;

namespace Glamour.Host.Profiles;

public sealed class ProfileException(int status, string code, string message, object? extra = null) : Exception(message)
{
    public int Status => status;
    public string Code => code;
    public object? Extra => extra;
}

/// <summary>Switches the Riot account by swapping the client's session file while the client is closed. Never stores a password and
/// never presses the client's "Log out" (that revokes the session server-side). Mechanism and checks: INSIGHTS "Riot Client session files".</summary>
public sealed class ProfileService(ProfileStore store, IRiotClient client, Func<string> phase, string liveFile, TimeProvider time, EventHub events, Action? changed = null, IProfileCardSource? cards = null)
{
    // Closing the client in any other phase would drop a queue, a champ select or a running game.
    private static readonly HashSet<string> SafePhases = ["None", "Lobby", "EndOfGame"];
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>False in a queue, champion select or game, or while an add is open: a switch would be refused.</summary>
    public bool CanSwitch => SafePhases.Contains(phase()) && !store.Data.Adding;

    public object Snapshot()
    {
        var d = store.Data;
        return new
        {
            activeId = d.ActiveId,
            adding = d.Adding,
            signedIn = ProfileStore.IsSignedIn(liveFile),
            running = RunningOrNone(),
            profiles = d.Profiles.Select(p => new
            {
                p.Id, p.Name, p.CapturedAt, p.Overrides, p.RiotId, p.Region, p.Card, hasIcon = File.Exists(store.IconPath(p.Id)),
                state = !store.HasSession(p.Id) ? "needs-sign-in" : p.Id == d.ActiveId && !d.Adding ? "active" : "ready",
            }),
        };
    }

    /// <summary>Saves the account that is signed in now as a profile (or, with an id, refreshes that profile's copy: the re-sign-in path).
    /// A blank name defaults to the account's Riot ID (or, on a re-sign-in, the profile's current name).</summary>
    public Task<object> SaveCurrentAsync(string? name, string? id, CancellationToken ct = default) => Locked(async () =>
    {
        var existing = id is null ? null : Find(id) ?? throw NotFound();
        var ident = await client.IdentityAsync(ct);
        name = ValidName(string.IsNullOrWhiteSpace(name) ? existing?.Name ?? ident?.RiotId : name, id);
        if (!ProfileStore.IsSignedIn(liveFile)) throw new ProfileException(409, "not-signed-in", "No account is signed in to the Riot Client. Sign in with \"Stay signed in\" first.");
        var sid = ProfileStore.SessionId(liveFile);
        if (sid is not null && store.Data.Profiles.FirstOrDefault(p => p.Id != id && store.HasSession(p.Id) && ProfileStore.SessionId(store.SessionPath(p.Id)) == sid) is { } same)
            throw new ProfileException(409, "already-saved", $"This account is already saved as \"{same.Name}\".");
        var d = store.Data;
        id ??= Guid.NewGuid().ToString("N")[..8];
        ProfileStore.WriteAtomic(store.SessionPath(id), ProfileStore.ReadShared(liveFile));
        var i = d.Profiles.FindIndex(p => p.Id == id);
        var saved = new Profile(id, name, time.GetUtcNow(), i >= 0 ? d.Profiles[i].Overrides : null, ident?.RiotId ?? existing?.RiotId, ident?.Region ?? existing?.Region, existing?.Card); // a re-sign-in keeps the account's settings
        if (i >= 0) d.Profiles[i] = saved; else d.Profiles.Add(saved);
        d.ActiveId = id;
        d.Adding = false;
        DeleteIfExists(store.AsidePath); // the previous account was captured into its own profile before it was moved aside
        return Done();
    });

    /// <summary>Fills in the Riot ID and region of the signed-in account's profile when it has none yet (profiles saved before this existed).
    /// Does nothing without a running client, in an open add, or when there is nothing to fill.</summary>
    public Task<object> RefreshIdentityAsync(CancellationToken ct) => Locked(async () =>
    {
        if (!store.Data.Adding && Active() is { RiotId: null } && await CaptureIdentity(ct)) return Done();
        return Snapshot();
    });

    /// <summary>Records the Riot ID of the active profile while its client still runs (before a stop). True when the profile changed.</summary>
    private async Task<bool> CaptureIdentity(CancellationToken ct)
    {
        if (Active() is not { } active || await client.IdentityAsync(ct) is not { } ident) return false;
        if (store.Data.Profiles.Any(p => p.Id != active.Id && p.RiotId == ident.RiotId)) return false; // the live account is another saved one: do not mislabel
        var i = IndexOf(active.Id);
        store.Data.Profiles[i] = active with { RiotId = ident.RiotId, Region = ident.Region ?? active.Region };
        return true;
    }

    /// <summary>Reads the card of the signed-in account from the League client and keeps it on the active profile. True when the client answered
    /// (so the caller can retry soon when it did not). Does nothing in an open add, or when the client's account is not the active profile.</summary>
    public async Task<bool> RefreshCardAsync(CancellationToken ct)
    {
        var read = false;
        await Locked(async () =>
        {
            var r = await CaptureCard(ct);
            read = r != CardRead.NoRead;
            return r == CardRead.Changed ? Done() : Snapshot();
        });
        return read;
    }

    private enum CardRead { NoRead, Same, Changed }

    /// <summary>Best effort: a dying client or a failed read must never break the switch or add that calls this before closing the client.</summary>
    private async Task<CardRead> CaptureCard(CancellationToken ct)
    {
        if (cards is null || store.Data.Adding || Active() is not { } active) return CardRead.NoRead;
        ProfileCardRead? read;
        try { read = await cards.ReadAsync(time.GetUtcNow(), ct); }
        catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException) { return CardRead.NoRead; }
        if (read is null) return CardRead.NoRead;
        // Never put one account's card on another: the Riot ID must match, and an unnamed profile must not take the card of an account saved under another profile.
        if (active.RiotId is { } mine ? read.RiotId is { } theirs && !string.Equals(mine, theirs, StringComparison.OrdinalIgnoreCase)
            : read.RiotId is { } other && store.Data.Profiles.Any(p => p.Id != active.Id && string.Equals(p.RiotId, other, StringComparison.OrdinalIgnoreCase))) return CardRead.NoRead;
        var iconMissing = !File.Exists(store.IconPath(active.Id));
        if (read.Icon is { Length: > 0 } icon && (iconMissing || active.Card?.IconId != read.Card.IconId))
            ProfileStore.WriteAtomic(store.IconPath(active.Id), icon);
        if (active.Card is { } old && old with { At = read.Card.At } == read.Card && !iconMissing && read.Card.At - old.At < TimeSpan.FromHours(1)) return CardRead.Same; // unchanged: no rewrite or broadcast per poll
        store.Data.Profiles[IndexOf(active.Id)] = active with { Card = read.Card };
        return CardRead.Changed;
    }

    /// <summary>Starts adding another account: captures the current one, moves its session aside and opens the client at the login screen.</summary>
    public Task<object> BeginAddAsync(bool confirm, CancellationToken ct) => Locked(async () =>
    {
        Guard(confirm);
        var d = store.Data;
        if (d.Adding) throw new ProfileException(409, "adding", "An add is already open: sign in and save it, or cancel.");
        var live = ProfileStore.IsSignedIn(liveFile);
        if (live && Active() is null) throw new ProfileException(409, "save-first", "Save the current account as a profile first, or its sign-in would be lost.");
        if (live && Active() is { RiotId: null }) await CaptureIdentity(ct);
        await CaptureCard(ct); // last look at this account while its client still runs
        await client.StopAsync(ct);
        if (live)
        {
            ProfileStore.WriteAtomic(store.SessionPath(d.ActiveId!), ProfileStore.ReadShared(liveFile));
            Touch(d.ActiveId!);
            File.Move(liveFile, store.AsidePath, overwrite: true);
        }
        d.Adding = true;
        store.Save();
        return Launch();
    });

    public Task<object> CancelAddAsync(CancellationToken ct) => Locked(async () =>
    {
        var d = store.Data;
        if (!d.Adding) throw new ProfileException(409, "not-adding", "No add is open.");
        await client.StopAsync(ct);
        if (File.Exists(store.AsidePath)) File.Move(store.AsidePath, liveFile, overwrite: true);
        d.Adding = false;
        store.Save();
        return Launch();
    });

    public Task<object> SwitchAsync(string id, bool confirm, CancellationToken ct) => Locked(async () =>
    {
        var target = Find(id) ?? throw NotFound();
        var d = store.Data;
        if (d.Adding) throw new ProfileException(409, "adding", "Finish or cancel the open add first.");
        if (!store.HasSession(id)) throw new ProfileException(409, "needs-sign-in", $"\"{target.Name}\" has no saved sign-in. Sign in again to save it.");
        Guard(confirm);
        if (d.ActiveId == id) return Snapshot();
        var live = ProfileStore.IsSignedIn(liveFile);
        if (live && Active() is null) throw new ProfileException(409, "save-first", "Save the current account as a profile first, or its sign-in would be lost.");
        if (live && Active() is { RiotId: null }) await CaptureIdentity(ct);
        await CaptureCard(ct); // last look at this account while its client still runs
        var reopen = Products.Open(client.Running()); // the games that were open, not just the Riot Client: open them again on the new account
        await client.StopAsync(ct);
        if (live) { ProfileStore.WriteAtomic(store.SessionPath(d.ActiveId!), ProfileStore.ReadShared(liveFile)); Touch(d.ActiveId!); } // re-capture: the token rotated while it ran
        ProfileStore.WriteAtomic(liveFile, ProfileStore.ReadShared(store.SessionPath(id)));
        d.ActiveId = id;
        store.Save();
        return Launch(reopen);
    });

    public Task<object> RenameAsync(string id, string name) => Locked(() =>
    {
        var i = IndexOf(id);
        var cur = store.Data.Profiles[i];
        // The name is a nickname over the Riot ID: blank clears it, so the Riot ID itself is the name again.
        store.Data.Profiles[i] = cur with { Name = ValidName(string.IsNullOrWhiteSpace(name) ? cur.RiotId : name, id) };
        return Done();
    });

    /// <summary>Deletes only Glamour's saved copy. The Riot Client's own sign-in is untouched.</summary>
    public Task<object> RemoveAsync(string id) => Locked(() =>
    {
        store.Data.Profiles.RemoveAt(IndexOf(id));
        DeleteIfExists(store.SessionPath(id));
        DeleteIfExists(store.IconPath(id));
        if (store.Data.ActiveId == id) store.Data.ActiveId = null;
        return Done();
    });

    public Task<object> ReorderAsync(IReadOnlyList<string> ids) => Locked(() =>
    {
        var d = store.Data;
        if (ids.Count != d.Profiles.Count || ids.Distinct().Count() != ids.Count || ids.Any(i => Find(i) is null))
            throw new ProfileException(400, "bad-order", "The order must list every profile once.");
        d.Profiles = ids.Select(i => Find(i)!).ToList();
        return Done();
    });

    /// <summary>Changes one account's settings overrides inside the profile lock. <paramref name="change"/> gets the current overrides and returns the new ones
    /// (or throws a <see cref="ProfileException"/>); what is valid is decided by the caller (<c>SettingsService</c>).</summary>
    public Task<object> ChangeOverridesAsync(string id, Func<System.Text.Json.Nodes.JsonObject?, System.Text.Json.Nodes.JsonObject> change) => Locked(() =>
    {
        var i = IndexOf(id);
        var next = change(store.Data.Profiles[i].Overrides);
        store.Data.Profiles[i] = store.Data.Profiles[i] with { Overrides = next.Count == 0 ? null : next };
        return Done();
    });

    /// <summary>For display only: a switch itself asks <see cref="IRiotClient.Running"/> and stops when the process list cannot be read.</summary>
    private IReadOnlyList<string> RunningOrNone()
    {
        try { return client.Running(); }
        catch (IOException) { return []; }
    }

    private Profile? Find(string id) => store.Data.Profiles.FirstOrDefault(p => p.Id == id);
    private Profile? Active() => store.Data.ActiveId is { } a ? Find(a) : null;
    private int IndexOf(string id) => store.Data.Profiles.FindIndex(p => p.Id == id) is var i and >= 0 ? i : throw NotFound();
    private static ProfileException NotFound() => new(404, "not-found", "Unknown profile.");
    private static void DeleteIfExists(string path) { if (File.Exists(path)) File.Delete(path); }

    private void Touch(string id) { var i = store.Data.Profiles.FindIndex(p => p.Id == id); if (i >= 0) store.Data.Profiles[i] = store.Data.Profiles[i] with { CapturedAt = time.GetUtcNow() }; }

    private string ValidName(string? name, string? exceptId)
    {
        name = name?.Trim() ?? "";
        if (name.Length is 0 or > 40) throw new ProfileException(400, "bad-name", "A name is 1 to 40 characters.");
        if (store.Data.Profiles.Any(p => p.Id != exceptId && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))) throw new ProfileException(409, "name-taken", "Another profile has that name.");
        return name;
    }

    /// <summary>Never close the client in a queue, champ select or game; ask first when it is open at all.</summary>
    private void Guard(bool confirm)
    {
        if (!SafePhases.Contains(phase())) throw new ProfileException(409, "busy", "Not while in a queue, champion select or game. Finish it first.");
        if (client.Running() is { Count: > 0 } running && !confirm)
            throw new ProfileException(409, "confirm", "This closes the Riot Client and its games.", new { willClose = Products.OpenNames(running) });
    }

    private object Launch(IReadOnlyList<string>? products = null)
    {
        try { client.Start(products ?? []); }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or System.Text.Json.JsonException)
        {
            Done(); // the account did change: save it, switch the settings in effect and tell the UIs even though the client did not start
            throw new ProfileException(500, "launch-failed", $"The account was switched but the Riot Client did not start: {ex.Message}");
        }
        return Done();
    }

    private object Done()
    {
        store.Save();
        changed?.Invoke(); // the active account may have changed: settings in effect follow it
        var s = Snapshot();
        events.Publish("profiles", s);
        return s;
    }

    private async Task<object> Locked(Func<object> action) => await Locked(() => Task.FromResult(action()));

    private async Task<object> Locked(Func<Task<object>> action)
    {
        await _gate.WaitAsync();
        try { return await action(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new ProfileException(500, "io", ex.Message); }
        finally { _gate.Release(); }
    }
}
