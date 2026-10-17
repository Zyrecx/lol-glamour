import type { Board, BoardPlayer, Champion, GameParties, PlayableQueue, Settings } from '../lib/types'

/** Our own lane icon ids. The client says UTILITY in lobbies and SUPPORT on the board; both map to SUPPORT. */
export type LaneIcon = 'TOP' | 'JUNGLE' | 'MIDDLE' | 'BOTTOM' | 'SUPPORT' | 'FILL'

const LANE_NAMES: Record<LaneIcon, [string, string]> = {
  TOP: ['Top', 'TOP'], JUNGLE: ['Jungle', 'JGL'], MIDDLE: ['Mid', 'MID'], BOTTOM: ['Bottom', 'BOT'], SUPPORT: ['Support', 'SUP'], FILL: ['Fill', 'FILL'],
}

export function laneIcon(lane: string | null | undefined): LaneIcon | null {
  const l = lane === 'UTILITY' ? 'SUPPORT' : lane
  return l && l in LANE_NAMES ? (l as LaneIcon) : null
}
export const laneName = (lane: string | null | undefined) => { const l = laneIcon(lane); return l ? LANE_NAMES[l][0] : '' }
export const laneShort = (lane: string | null | undefined) => { const l = laneIcon(lane); return l ? LANE_NAMES[l][1] : '' }

/** The lobby lane choices in the client's words (UTILITY = support). */
export const LOBBY_LANES = ['TOP', 'JUNGLE', 'MIDDLE', 'BOTTOM', 'UTILITY', 'FILL'] as const

/** 0:07, 12:34, 1:02:03. Negative counts as 0. */
export function clock(ms: number): string {
  const s = Math.max(0, Math.floor(ms / 1000))
  const h = Math.floor(s / 3600), m = Math.floor((s % 3600) / 60), sec = String(s % 60).padStart(2, '0')
  return h ? `${h}:${String(m).padStart(2, '0')}:${sec}` : `${m}:${sec}`
}

export const ordinal = (n: number) => n + (n % 100 >= 11 && n % 100 <= 13 ? 'th' : ['th', 'st', 'nd', 'rd'][n % 10] ?? 'th')

/** Champion art from the host's templates (`{num}` = skin number); empty when the list has none. */
export function art(c: Champion | undefined, skinId: number, kind: 'splash' | 'portrait'): string {
  const t = c?.[kind]
  return t ? t.replace('{num}', String(skinId > 0 ? skinId % 1000 : 0)) : ''
}

/** One thing automation will do: a champion (ban, lock, hover) shown with its icon, or a plain line. */
export interface AutomationItem { kind: 'ban' | 'lock' | 'hover' | 'text'; text: string; championId?: number; sub?: string }

/** What automation will do. `lane` picks that lane's own setup when it has one (the board's lane words). */
export function automationItems(s: Settings | null, nameOf: (id: number) => string, lane?: string | null): AutomationItem[] {
  if (!s) return []
  const items: AutomationItem[] = []
  const text = (t: string) => items.push({ kind: 'text', text: t })
  if (s.autoAccept === 'Always') text(s.acceptTiming === 'LastSecond' ? 'Auto-accept · last second' : s.autoAcceptDelaySeconds > 0 ? `Auto-accept · ${s.autoAcceptDelaySeconds}s` : 'Auto-accept')
  const own = lane ? s.lanes?.[lane as keyof Settings['lanes']] : undefined
  const ban = own ? own.banChampionId : s.autoBanChampionId
  const lock = own ? own.lockChampionId : s.autoLockChampionId
  const lockTiming = own ? own.lockTiming : s.lockTiming
  const hover = own ? own.hoverChampionId : s.preHoverChampionId
  if (ban) items.push({ kind: 'ban', text: nameOf(ban), championId: ban })
  if (lock) items.push({ kind: 'lock', text: nameOf(lock), championId: lock, sub: lockTiming === 'LastSecond' ? 'last second' : undefined })
  else if (hover) items.push({ kind: 'hover', text: nameOf(hover), championId: hover })
  if (!lane && Object.keys(s.lanes ?? {}).length) text('Per-lane setups')
  if (s.autoHonorEnabled && s.autoHonorFriends.length) text(`Honor ${s.autoHonorFriends.length === 1 ? '1 friend' : `${s.autoHonorFriends.length} friends`}`)
  const skips = s.skipScreens.length + (s.skipHonorScreen ? 1 : 0)
  if (skips) text(`Skip ${skips} screen${skips === 1 ? '' : 's'}`)
  return items
}

const VERB = { ban: 'Ban', lock: 'Lock', hover: 'Hover' } as const

/** The same as one-line strings ("Ban Nasus", "Lock Neeko · last second"). */
export function automationChips(s: Settings | null, nameOf: (id: number) => string, lane?: string | null): string[] {
  return automationItems(s, nameOf, lane).map((i) => (i.kind === 'text' ? i.text : `${VERB[i.kind]} ${i.text}${i.sub ? ` · ${i.sub}` : ''}`))
}

/** The Play shortcuts: every pinned queue that is playable now, in pin order; none pinned = the first three. */
export function shortcuts(all: PlayableQueue[], pinned: number[], count = 3): PlayableQueue[] {
  const byId = new Map(all.map((q) => [q.id, q]))
  const mine = pinned.map((id) => byId.get(id)).filter((q): q is PlayableQueue => !!q)
  return mine.length ? mine : all.slice(0, count)
}

/** `ids` with `id` moved one place up (-1) or down (+1); unchanged at the ends or when absent. */
export function movePin(ids: number[], id: number, by: -1 | 1): number[] {
  const i = ids.indexOf(id), j = i + by
  if (i < 0 || j < 0 || j >= ids.length) return ids
  const next = [...ids]
  ;[next[i], next[j]] = [next[j], next[i]]
  return next
}

export type CellStatus = 'PICKING' | 'NEXT' | ''

export function statusOf(board: Board, cellId: number): CellStatus {
  if (board.acting.some((t) => t.cellId === cellId && t.type === 'pick')) return 'PICKING'
  if ((board.next ?? []).some((t) => t.cellId === cellId && t.type === 'pick')) return 'NEXT'
  return ''
}

export interface ChampSelectHead { label: string; sub: string; mine: boolean }

/** The champ select header: whose turn it is and how far away mine is. */
export function headOf(board: Board, turnType: string | null, nameOf: (p: BoardPlayer) => string): ChampSelectHead {
  const me = board.myTeam.find((p) => p.isMe)
  if (board.stage === 'FINALIZATION') return { label: 'Finalization', sub: 'Trades are open until the game starts.', mine: false }
  if (board.mode === 'aram' || board.mode === 'mayhem') return { label: board.mode === 'aram' ? 'ARAM' : 'Mayhem', sub: '', mine: false }
  if (board.stage === 'PLANNING') return { label: 'Planning', sub: 'Hover what you want to play.', mine: false }
  if (turnType) return { label: `Your turn · ${turnType}`, sub: turnType === 'ban' ? 'Ban before the timer runs out.' : 'Lock in before the timer runs out.', mine: true }
  if (me?.locked) return { label: 'Locked in', sub: 'Waiting for the others.', mine: false }
  const bans = board.acting.some((t) => t.type === 'ban')
  if (bans) return { label: 'Ban phase', sub: me?.pickTurn ? `You pick ${ordinal(me.pickTurn)} turn.` : '', mine: false }
  const picker = [...board.myTeam, ...board.theirTeam].find((p) => board.acting.some((t) => t.cellId === p.cellId))
  const who = picker ? (board.myTeam.includes(picker) ? nameOf(picker) : 'An enemy') : 'Someone'
  const left = me?.pickTurn && board.pickTurnNow ? me.pickTurn - board.pickTurnNow : 0
  return { label: `${who} is picking`, sub: left > 0 ? `You pick in ${left} turn${left === 1 ? '' : 's'}.` : '', mine: false }
}

/**
 * Lobby lane preferences after choosing `lane` for one slot. Choosing the other slot's lane swaps the two, so the client never sees a clash;
 * Fill as primary fills both (the client hides the secondary then).
 */
export function choosePosition(first: string, second: string, slot: 'first' | 'second', lane: string): { first: string; second: string } {
  const empty = (l: string) => !l || l === 'UNSELECTED' || l === 'FILL'
  if (slot === 'first') {
    if (lane === 'FILL') return { first: 'FILL', second: 'FILL' }
    return { first: lane, second: lane === second ? (empty(first) ? 'UNSELECTED' : first) : first === 'FILL' ? 'UNSELECTED' : second }
  }
  if (lane === first) return { first: empty(second) ? 'UNSELECTED' : second, second: lane }
  return { first, second: lane }
}

/** Parties in one line for champ select, where names are hidden: "Your duo · Enemy 3-stack", or "Everyone solo". */
export function partySummary(p: GameParties): string {
  if (!p.parties.length) return 'Everyone solo'
  return p.parties.map((x) => {
    const kind = x.players.length === 2 ? 'duo' : `${x.players.length}-stack`
    return x.players.some((pl) => pl.isMe) ? `Your ${kind}` : `${x.ally ? 'Ally' : 'Enemy'} ${kind}`
  }).join(' · ')
}
