using Glamour.Core;
using Microsoft.Extensions.Logging.Abstractions;

namespace Glamour.Tests;

public class SettingsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "glamour-test-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_dir, "settings.json");
    private SettingsStore Store(GlamourSettings s) => new(s, FilePath, NullLogger<SettingsStore>.Instance);

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    [Fact]
    public void Save_then_load_round_trips_every_property()
    {
        var saved = new GlamourSettings
        {
            AutoAccept = AutoAcceptMode.Phone, AutoBanChampionId = 238, BanTiming = ActionTiming.LastSecond,
            SkipBanIfAllyHovered = false, AutoLockChampionId = 518, LockTiming = ActionTiming.LastSecond,
            LastSecondMarginSeconds = 5, DryRun = true,
        };
        Store(saved).Save();

        var loaded = new GlamourSettings();
        Store(loaded).Load();

        foreach (var p in typeof(GlamourSettings).GetProperties())
            Assert.Equal(p.GetValue(saved), p.GetValue(loaded)); // fails if a new property is missed by CopyFrom or serialization
    }

    [Fact]
    public void CopyFrom_covers_every_property()
    {
        var source = new GlamourSettings
        {
            AutomationEnabled = false, AutoAcceptDelaySeconds = 3, AcceptTiming = ActionTiming.LastSecond, ActionDelaySeconds = 5, PinnedQueueIds = [420],
            AutoAccept = AutoAcceptMode.Always, AutoBanChampionId = 1, BanTiming = ActionTiming.LastSecond,
            SkipBanIfAllyHovered = false, MatchupLoadout = true, MayhemWinRates = true, AugmentTips = true, LoadingParties = false, AutoLockChampionId = 2, LockTiming = ActionTiming.LastSecond, PreHoverChampionId = 4,
            LaneSwapAccept = true, LaneSwapAsk = true, PickSlotAccept = true, PickSlotAsk = true, PickSlot = PickSlot.First,
            LastSecondMarginSeconds = 9, DryRun = true,
            NtfyEnabled = true, NtfyServer = "https://ntfy.example", NtfyTopic = "t", WebPushEnabled = true, MutedNotifications = ["swap"],
            AutoHonorEnabled = true, AutoHonorFriends = ["A#1"], SkipHonorScreen = true, HonorStay = true, SkipScreens = ["missions-celebration"],
            RankOverrideEnabled = true, RankTier = "GOLD", RankDivision = "II", RankQueue = "RANKED_FLEX_SR", StatusMessageOverride = "hi",
            Lanes = { ["TOP"] = new LaneSetup { BanChampionId = 3 } }, ChampionPools = { ["TOP"] = [86] },
        };
        var target = new GlamourSettings();
        target.CopyFrom(source);
        foreach (var p in typeof(GlamourSettings).GetProperties())
            Assert.NotEqual(p.GetValue(new GlamourSettings()), p.GetValue(target));
        Assert.Equal(["A#1"], target.AutoHonorFriends);
    }

    [Fact]
    public void A_lane_pool_is_its_list_then_the_lock_and_hover_champions()
    {
        var s = new GlamourSettings { AutoLockChampionId = 86, Lanes = { ["MIDDLE"] = new LaneSetup { LockChampionId = 238, HoverChampionId = 103 } }, ChampionPools = { ["MIDDLE"] = [103, 7] } };
        Assert.Equal([103, 7, 238], s.PoolFor("MIDDLE"));
        Assert.Equal([86], s.PoolFor("TOP")); // no list: the default lock champion
    }

    [Fact]
    public void Missing_file_keeps_defaults()
    {
        var s = new GlamourSettings();
        Store(s).Load();
        Assert.Null(s.AutoLockChampionId);
        Assert.Equal(AutoAcceptMode.Off, s.AutoAccept);
    }

    [Fact]
    public void Corrupt_file_keeps_defaults()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, "{ not json");
        var s = new GlamourSettings();
        Store(s).Load();
        Assert.Null(s.AutoLockChampionId);
    }
}
