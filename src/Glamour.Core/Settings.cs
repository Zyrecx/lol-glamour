namespace Glamour.Core;

public enum AutoAcceptMode { Off, Always, Phone }

/// <summary>Instant acts when the turn starts, after <see cref="GlamourSettings.ActionDelaySeconds"/>. LastSecond waits until the margin before the turn ends.</summary>
public enum ActionTiming { Instant, LastSecond }

/// <summary>Ban and lock choices for one lane. A lane with an entry in <see cref="GlamourSettings.Lanes"/> uses this instead of the default fields.</summary>
public sealed class LaneSetup
{
    public int? BanChampionId { get; set; }
    public ActionTiming BanTiming { get; set; } = ActionTiming.Instant;
    /// <summary>A champion id, or <see cref="BestCounter"/>.</summary>
    public int? LockChampionId { get; set; }
    public ActionTiming LockTiming { get; set; } = ActionTiming.Instant;

    /// <summary>As the lock champion: the best counter the panel shows at that moment (<see cref="BestCounterPick"/>), not a fixed champion.</summary>
    public const int BestCounter = -1;

    /// <summary>Champion to hover (never lock) before my pick turn.</summary>
    public int? HoverChampionId { get; set; }

    public LaneSetup Clone() => (LaneSetup)MemberwiseClone();
}

public static class LaneNames
{
    private static readonly string[] Positions = ["TOP", "JUNGLE", "MIDDLE", "BOTTOM", "UTILITY", "FILL"];

    /// <summary>A value the client accepts for a lane preference.</summary>
    public static bool IsPosition(string? value, bool allowUnselected) => Positions.Contains(value) || (allowUnselected && value == "UNSELECTED");

    public static readonly string[] All = ["TOP", "JUNGLE", "MIDDLE", "BOTTOM", "SUPPORT"];

    /// <summary>The client's `assignedPosition` (lowercase, `utility` = support) as one of <see cref="All"/>; null when the mode has no lanes.</summary>
    public static string? FromAssigned(string? position) => position?.ToLowerInvariant() switch
    {
        "top" => "TOP", "jungle" => "JUNGLE", "middle" => "MIDDLE", "bottom" => "BOTTOM", "utility" => "SUPPORT", _ => null,
    };
}

/// <summary>Live settings. Features read them on every event, so changes apply at once.</summary>
public sealed class GlamourSettings
{
    /// <summary>The global automation switch. Off: nothing is accepted, banned, locked, hovered, honored or skipped automatically. Rank and status overrides are not automation and stay as set.</summary>
    public bool AutomationEnabled { get; set; } = true;

    public AutoAcceptMode AutoAccept { get; set; } = AutoAcceptMode.Off;

    /// <summary>Always mode waits this long before accepting, so the phone can cancel it for this match. 0 accepts at once.</summary>
    public double AutoAcceptDelaySeconds { get; set; }

    /// <summary>Instant accepts after <see cref="AutoAcceptDelaySeconds"/>; LastSecond at the latest safe moment of the ready check (<see cref="AutoAcceptFeature.LastSecondDelay"/>).</summary>
    public ActionTiming AcceptTiming { get; set; } = ActionTiming.Instant;

    /// <summary>Champion to ban on my turn; null disables auto-ban.</summary>
    public int? AutoBanChampionId { get; set; }
    public ActionTiming BanTiming { get; set; } = ActionTiming.Instant;

    /// <summary>When true, the auto-ban is skipped if a teammate has that champion hovered or locked. Checked again right before a last-second ban.</summary>
    public bool SkipBanIfAllyHovered { get; set; } = true;

    /// <summary>Applies the matchup loadout (item set, runes, spells) by itself once my pick is locked (SPEC "Matchup loadout"); off = Apply in the panel.</summary>
    public bool MatchupLoadout { get; set; }

    /// <summary>
    /// Mayhem bench advice from arammayhem.com's win rates; off = u.gg's tiers only. Off by default, offered on the tour (SPEC "PC app direction" 5:
    /// the site keeps win rates out of its reusable data).
    /// </summary>
    public bool MayhemWinRates { get; set; }

    /// <summary>
    /// Mayhem augment tags in the game (desktop app only): reads the offered cards off the screen and tags each with aramkit's win-rate difference on my
    /// champion (SPEC "PC app direction" 5). Opt-in: it reads the screen during Mayhem games.
    /// </summary>
    public bool AugmentTips { get; set; }

    /// <summary>Premades marked on the game's loading screen (desktop app only): a coloured frame and a "Duo" / "3-stack" tag on each party's cards
    /// (SPEC "Parties on the loading screen"). Reads a few screen pixels while a game loads.</summary>
    public bool LoadingParties { get; set; } = true;

    /// <summary>Champion to lock on my turn, or <see cref="LaneSetup.BestCounter"/>; null disables auto-lock. Opt-in, never on by default.</summary>
    public int? AutoLockChampionId { get; set; }
    public ActionTiming LockTiming { get; set; } = ActionTiming.Instant;

    /// <summary>Champion to only HOVER before my pick turn (teammates see it, nothing is locked). Never replaces a hover the user made.</summary>
    public int? PreHoverChampionId { get; set; }

    /// <summary>Lane swaps (SPEC "Role and pick-order swaps"): accept a request that gives me a lane I ranked higher in the lobby. Others are left to me.</summary>
    public bool LaneSwapAccept { get; set; }
    /// <summary>Lane swaps: when I do not have my first lane, ask the ally who holds it (then the one holding my second), one request each.</summary>
    public bool LaneSwapAsk { get; set; }
    /// <summary>Pick-order swaps toward <see cref="PickSlot"/>: accept a request that moves me there or closer. Others are left to me.</summary>
    public bool PickSlotAccept { get; set; }
    /// <summary>Pick-order swaps: one request toward <see cref="PickSlot"/> per champ select, after the lane part.</summary>
    public bool PickSlotAsk { get; set; }
    public PickSlot PickSlot { get; set; } = PickSlot.Last;

    /// <summary>Instant timing hovers at once and bans or locks this many seconds later (0 = in the same moment). Re-checked before acting, so a change the user makes meanwhile wins.</summary>
    public double ActionDelaySeconds { get; set; } = 2;

    /// <summary>Seconds left in the turn at which LastSecond fires. A lock landing later than this is unverified.</summary>
    public double LastSecondMarginSeconds { get; set; } = 3;

    /// <summary>Phone notifications through ntfy (https://ntfy.sh or a self-hosted server). The topic is a secret, generated when first enabled.</summary>
    public bool NtfyEnabled { get; set; }
    public string NtfyServer { get; set; } = "https://ntfy.sh";
    public string? NtfyTopic { get; set; }

    /// <summary>Phone notifications through Web Push (needs HTTPS and an installed app). At most one of this and <see cref="NtfyEnabled"/> is on.</summary>
    public bool WebPushEnabled { get; set; }

    /// <summary>Notification kinds the user switched off (see <see cref="NotificationKinds"/>). Empty = every kind is sent. Applies to both channels.</summary>
    public List<string> MutedNotifications { get; set => field = value ?? []; } = [];

    /// <summary>Opt-in: after a game, give ONE COOL honor to the first listed friend on the ballot. Off by default.</summary>
    public bool AutoHonorEnabled { get; set; }

    /// <summary>Riot IDs (Name#Tag) in priority order. Resolved to puuids through the friend list and matched to the ballot by puuid, never by name.</summary>
    public List<string> AutoHonorFriends { get; set => field = value ?? []; } = [];

    /// <summary>Opt-in: skip the post-game honor screen unless a vote was cast (by the user or by auto-honor). Off by default.</summary>
    public bool SkipHonorScreen { get; set; }

    /// <summary>Auto-honor stages the friends' votes and leaves the screen open for the user's other votes; it submits only when no vote is left.</summary>
    public bool HonorStay { get; set; }

    /// <summary>Post-game screens to skip, from <see cref="SkippableScreens.All"/>. Empty by default; the honor screen has its own setting.</summary>
    public List<string> SkipScreens { get; set => field = value ?? []; } = [];

    /// <summary>Opt-in: keep my chat presence at this rank (what friends see in the hover card; the profile rank comes from the server and is unchanged). Re-applied whenever the client resets it.</summary>
    public bool RankOverrideEnabled { get; set; }
    public string RankTier { get; set; } = "CHALLENGER";
    public string RankDivision { get; set; } = "I";
    public string RankQueue { get; set; } = "RANKED_SOLO_5x5";

    /// <summary>Opt-in: keep my chat status message at this text; null leaves it alone.</summary>
    public string? StatusMessageOverride { get; set; }

    /// <summary>Optional per-lane ban/lock setups, keyed by <see cref="LaneNames.All"/>. A lane without an entry uses the default fields above.</summary>
    public Dictionary<string, LaneSetup> Lanes { get; set => field = value ?? []; } = [];

    /// <summary>The ban/lock setup to use for the lane I was assigned (null or an unconfigured lane: the defaults).</summary>
    public LaneSetup ForLane(string? lane) =>
        lane is not null && Lanes.TryGetValue(lane, out var own) && own is not null ? own
            : new LaneSetup { BanChampionId = AutoBanChampionId, BanTiming = BanTiming, LockChampionId = AutoLockChampionId, LockTiming = LockTiming, HoverChampionId = PreHoverChampionId };

    /// <summary>
    /// Champions the user plays, per lane (keys from <see cref="LaneNames.All"/>), always listed among the counter picks when the data knows them.
    /// The lane's lock and hover champions count too (<see cref="PoolFor"/>).
    /// </summary>
    public Dictionary<string, List<int>> ChampionPools { get; set => field = value ?? []; } = [];

    public const int MaxPoolSize = 12;

    /// <summary>My pool for a lane: its listed champions, then the lane's lock and hover champions.</summary>
    public IReadOnlyList<int> PoolFor(string lane)
    {
        var setup = ForLane(lane);
        return [.. (ChampionPools.TryGetValue(lane, out var list) && list is not null ? list : []).Append(setup.LockChampionId ?? 0).Append(setup.HoverChampionId ?? 0).Where(id => id > 0).Distinct()];
    }

    /// <summary>Queue ids shown as shortcuts on the phone's Play screen, in order. Empty: the first ones of the playable list.</summary>
    public List<int> PinnedQueueIds { get; set => field = value ?? []; } = [];

    // The collections above never hold null: a JSON null (a hand-edited settings.json, a PATCH) reads as empty, so no reader has to check.

    /// <summary>When true, writes are logged but not sent.</summary>
    public bool DryRun { get; set; }

    /// <summary>Copies every setting from <paramref name="other"/>. The one place a new setting must be added to be saved and updatable.</summary>
    public void CopyFrom(GlamourSettings other)
    {
        AutomationEnabled = other.AutomationEnabled;
        AutoAccept = other.AutoAccept;
        AutoAcceptDelaySeconds = other.AutoAcceptDelaySeconds;
        AcceptTiming = other.AcceptTiming;
        ActionDelaySeconds = other.ActionDelaySeconds;
        AutoBanChampionId = other.AutoBanChampionId;
        BanTiming = other.BanTiming;
        SkipBanIfAllyHovered = other.SkipBanIfAllyHovered;
        MatchupLoadout = other.MatchupLoadout;
        MayhemWinRates = other.MayhemWinRates;
        AugmentTips = other.AugmentTips;
        LoadingParties = other.LoadingParties;
        AutoLockChampionId = other.AutoLockChampionId;
        LockTiming = other.LockTiming;
        PreHoverChampionId = other.PreHoverChampionId;
        LaneSwapAccept = other.LaneSwapAccept;
        LaneSwapAsk = other.LaneSwapAsk;
        PickSlotAccept = other.PickSlotAccept;
        PickSlotAsk = other.PickSlotAsk;
        PickSlot = other.PickSlot;
        LastSecondMarginSeconds = other.LastSecondMarginSeconds;
        DryRun = other.DryRun;
        NtfyEnabled = other.NtfyEnabled;
        NtfyServer = other.NtfyServer;
        NtfyTopic = other.NtfyTopic;
        WebPushEnabled = other.WebPushEnabled;
        MutedNotifications = [.. other.MutedNotifications];
        AutoHonorEnabled = other.AutoHonorEnabled;
        AutoHonorFriends = [.. other.AutoHonorFriends];
        SkipHonorScreen = other.SkipHonorScreen;
        HonorStay = other.HonorStay;
        SkipScreens = [.. other.SkipScreens];
        RankOverrideEnabled = other.RankOverrideEnabled;
        RankTier = other.RankTier;
        RankDivision = other.RankDivision;
        RankQueue = other.RankQueue;
        StatusMessageOverride = other.StatusMessageOverride;
        Lanes = other.Lanes.Where(l => l.Value is not null).ToDictionary(l => l.Key, l => l.Value.Clone());
        PinnedQueueIds = [.. other.PinnedQueueIds];
        ChampionPools = other.ChampionPools.Where(p => p.Value is not null).ToDictionary(p => p.Key, p => p.Value.ToList());
    }
}
