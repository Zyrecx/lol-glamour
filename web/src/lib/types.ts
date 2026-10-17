export type AcceptMode = 'Off' | 'Always' | 'Phone'
export type Timing = 'Instant' | 'LastSecond'

export type LaneKey = 'TOP' | 'JUNGLE' | 'MIDDLE' | 'BOTTOM' | 'SUPPORT'
export interface LaneSetup { banChampionId: number | null; banTiming: Timing; lockChampionId: number | null; lockTiming: Timing; hoverChampionId: number | null }

/** Mirrors Glamour.Core GlamourSettings (camelCase over the wire). */
export interface Settings {
  /** The global automation switch (the same for every account). */
  automationEnabled: boolean
  autoAccept: AcceptMode
  /** Always mode waits this long so the phone can cancel; 0 = at once. */
  autoAcceptDelaySeconds: number
  /** Instant uses the delay above; LastSecond accepts at the latest safe moment (settingsMeta.maxAutoAcceptDelaySeconds into the ready check). */
  acceptTiming: Timing
  /** Instant ban/lock acts this long after the turn starts; 0 = at once. */
  actionDelaySeconds: number
  /** Play screen shortcuts, in order; empty = the first playable queues. */
  pinnedQueueIds: number[]
  autoBanChampionId: number | null
  banTiming: Timing
  skipBanIfAllyHovered: boolean
  /** Apply the matchup loadout (item set, runes, spells) on lock; off = Apply in the panel. */
  matchupLoadout: boolean
  /** Mayhem bench advice from arammayhem.com's win rates; off = u.gg tiers. */
  mayhemWinRates: boolean
  /** Mayhem augment tags in the game (desktop app): reads the offered cards off the screen. */
  augmentTips: boolean
  /** Premades marked on the game's loading screen (desktop app). */
  loadingParties: boolean
  autoLockChampionId: number | null
  lockTiming: Timing
  preHoverChampionId: number | null
  /** Lane and pick-order swaps (SPEC "Role and pick-order swaps"). */
  laneSwapAccept: boolean
  laneSwapAsk: boolean
  pickSlotAccept: boolean
  pickSlotAsk: boolean
  pickSlot: 'Last' | 'First' | 'NotFirst'
  lastSecondMarginSeconds: number
  ntfyEnabled: boolean
  ntfyServer: string
  ntfyTopic: string | null
  webPushEnabled: boolean
  mutedNotifications: string[]
  autoHonorEnabled: boolean
  autoHonorFriends: string[]
  skipHonorScreen: boolean
  /** Auto-honor leaves the honor screen open for my other votes (submits only when none is left). */
  honorStay: boolean
  skipScreens: string[]
  rankOverrideEnabled: boolean
  rankTier: string
  rankDivision: string
  rankQueue: string
  statusMessageOverride: string | null
  lanes: Partial<Record<LaneKey, LaneSetup>>
  /** Champions I play per lane, always listed among the counter picks (with the lane's lock and hover champions). */
  championPools: Partial<Record<LaneKey, number[]>>
  dryRun: boolean
}

/** Settings rules from the host, so the UI copies none: account-independent keys and number ranges. */
export interface SettingsMeta { sharedKeys: string[]; maxAutoAcceptDelaySeconds: number; maxActionDelaySeconds: number; maxLastSecondMarginSeconds: number }

export interface AuthStatus {
  authenticated: boolean
  loopback: boolean
  passwordSet: boolean
  device: { id: string; name: string } | null
}

export interface Device { id: string; name: string; lastSeen: string; current: boolean }

export interface Swap { kind: string; id: number; cellId: number; state: string }
export interface Turn { actionId: number; type: string; stage: string }
export interface BoardPlayer { cellId: number; championId: number; locked: boolean; lane: string; spell1Id: number; spell2Id: number; skinId: number; name: string; isMe: boolean
  /** 1-based pick turn (two players share one in draft); 0 = no pick. */
  pickTurn: number }
export interface BoardTurn { cellId: number; type: string }
/** A completed ban and the cell that made it (mine = my team's). */
export interface BoardBan { cellId: number; championId: number; mine: boolean }
/** The champ select screen: both teams, bans and who is acting. timeLeftMs was true when the event arrived. */
export interface Board {
  stage: string; myCellId: number; myTeam: BoardPlayer[]; theirTeam: BoardPlayer[]
  myBans: number[]; theirBans: number[]; acting: BoardTurn[]; timeLeftMs: number; queueId: number
  /** Who acts in the turn after the current one. */
  next: BoardTurn[]
  /** The pick turn being played; 0 before the picks start. */
  pickTurnNow: number
  /** My own unfinished pick action (live or not): hovering works on it before my turn. 0 when the mode has none. */
  myPickActionId?: number
  /** Completed bans in order, with who made each. */
  banList?: BoardBan[]
  /** draft, aram, mayhem or other (host decides from the queue and the bench). */
  mode: 'draft' | 'aram' | 'mayhem' | 'other'
  bench: number[]; rerolls: number; allowRerolling: boolean
  /** Mayhem: the champions dealt to me (2 or 3), from subset-champion-list. Null/absent elsewhere. */
  offered?: number[] | null
  /** Swap entries that can be requested (AVAILABLE) or are live (SENT, RECEIVED). */
  swaps: Swap[]
}
export interface Spell { id: number; name: string; icon: string; modes: string[] }
/** `lane`: the queue's position for them (ranked and flex, from champ select on), '' when none. */
export interface PartyPlayer { puuid: string; championId: number; name: string; isMe: boolean; lane?: string }
/** Who queued together in the running game (`parties` event); `parties` is empty when everyone is solo. */
export interface GameParties { gameId: number; parties: { ally: boolean; players: PartyPlayer[] }[] }
export interface Chroma { id: number; name: string; colors: string[]; preview: string }
export interface Skin { id: number; name: string; isBase: boolean; tile: string; chromas: Chroma[] }
export interface RuneInfo { id: number; name: string; icon: string }
export interface RunePage {
  index: number; recommendationId: string; keystone: string; keystoneIcon: string; descriptor: string
  primaryStyleId: number; secondaryStyleId: number; perks: RuneInfo[]; spellIds: number[]; position: string
}
export interface Dodge { state: string; dodgeIds?: number[]; byMe?: boolean }

export interface NetworkInfo {
  port: number
  listeningOnLan: boolean
  /** The saved choice; differs from `listeningOnLan` until the host restarts. */
  phoneAccessWanted: boolean
  /** A start-up argument (Glamour:AllowLan) decides; the switch cannot change it. */
  phoneAccessForced: boolean
  webUiEnabled: boolean
  requireHttps: boolean
  https: { port: number; fingerprint: string; installUrl: string | null } | null
  addresses: string[]
  problem: 'lan-off' | 'port-in-use' | 'network-public' | 'firewall-blocked' | null
}

export interface LobbyMember {
  puuid: string; summonerId: number; isMe: boolean; isLeader: boolean; isBot: boolean; ready: boolean; iconId: number; level: number; first: string; second: string
  /** Set by the demo only; the host sends no name (see summonerNames). */
  name?: string
}
export interface LobbyInfo { canStart: boolean; queueId: number; showPositions: boolean; first: string; second: string; members: number; roster?: LobbyMember[]; custom?: boolean }
export interface Invitation { invitationId: string; fromName: string; queueId: number; canAccept: boolean }
export interface OpenParty { friendName: string; puuid: string; partyId: string; queueId: number; members: number; maxPlayers: number }
export type Lane = 'TOP' | 'JUNGLE' | 'MIDDLE' | 'BOTTOM' | 'SUPPORT'
/** `splash` (wide) and `portrait` (tall) are URL templates: `{num}` is the skin number (skin id % 1000, 0 = base). Missing in an old cached list. */
/** As a lock champion: the best counter at that moment, chosen by the host (`LaneSetup.BestCounter`). */
export const BEST_COUNTER = -1
export interface Champion { id: number; name: string; icon: string; positions?: Lane[]; splash?: string | null; portrait?: string | null }
export interface Queue { id: number; name: string; category: string; gameMode: string }
/** A queue that can be started now (`GET /api/queues/playable`), with the client's display name. */
export interface PlayableQueue { id: number; name: string; mapId: number; mapName: string; gameMode: string; category: string; isRanked: boolean; showPositions: boolean }

export type ProfileState = 'active' | 'ready' | 'needs-sign-in'
export interface RankInfo { tier: string; division: string; lp: number; wins: number; losses: number }
/** What the League client said about the account the last time it was signed in (`at`). Rank is null when unranked. The icon image is `/api/profiles/{id}/icon` (see `hasIcon`). */
export interface ProfileCard { iconId: number; level: number; levelPercent: number; solo: RankInfo | null; flex: RankInfo | null; bannerType: string | null; crestType: string | null; flagTheme: string | null; flagLevel: number | null; bannerId: string | null; titleId: string | null; at: string }
/** One saved Riot account. `overrides` holds only the settings that differ from the shared defaults for this account. */
export interface Profile { id: string; name: string; capturedAt: string | null; overrides: Partial<Settings> | null; riotId: string | null; region: string | null; card: ProfileCard | null; hasIcon: boolean; state: ProfileState }
export interface ProfilesSnapshot { activeId: string | null; adding: boolean; signedIn: boolean; running: string[]; profiles: Profile[] }

export type HonorType = 'HEART' | 'COOL' | 'SHOTCALLER'
/** One player on the post-game ballot. `honor` is the type staged for them now, else null. */
export interface HonorCandidate { puuid: string; summonerId: number; name: string; championId: number; ally: boolean; isFriend: boolean; honor: HonorType | null }
/** `gameId` 0 means no ballot. `votes` is how many honors I may give, `staged` how many are on the ballot. */
export interface HonorBallot { gameId: number; votes: number; staged: number; candidates: HonorCandidate[] }

/** `GET /api/app` and the `app` event: this install of Glamour. */
export interface AppInfo {
  version: string
  channel: 'dev' | 'release'
  telemetry: boolean
  tourDone: boolean
  /** Null for a copy that was not installed (dev builds). */
  installFolder: string | null
  updates: {
    mode: 'automatic' | 'notify' | 'off'
    /** False outside an installed release. */
    available: boolean
    state: 'none' | 'checking' | 'downloading' | 'ready' | 'error' | 'off'
    version: string | null
    notes: string | null
    error: string | null
  }
}
