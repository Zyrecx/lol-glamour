import { BEST_COUNTER, type Board, type LaneKey, type LaneSetup, type Settings, type Timing } from '../lib/types'

const LANES: LaneKey[] = ['TOP', 'JUNGLE', 'MIDDLE', 'BOTTOM', 'SUPPORT']

/** A lobby position as a settings lane: the lobby says UTILITY where the settings say SUPPORT; FILL and UNSELECTED are no lane. */
export function laneKeyOf(position: string | null | undefined): LaneKey | null {
  const p = position === 'UTILITY' ? 'SUPPORT' : position
  return LANES.includes(p as LaneKey) ? (p as LaneKey) : null
}

export interface GamePlan {
  /** The lane whose setup applies, when it has one of its own; null when the default for all lanes applies. */
  lane: LaneKey | null
  setup: LaneSetup
}

/** The ban and pick setup that applies in `lane`: its own one, else the default for all lanes. */
export function planOf(s: Settings, lane: LaneKey | null): GamePlan {
  const own = lane ? s.lanes?.[lane] : undefined
  if (lane && own) return { lane, setup: own }
  return { lane: null, setup: { banChampionId: s.autoBanChampionId, banTiming: s.banTiming, lockChampionId: s.autoLockChampionId, lockTiming: s.lockTiming, hoverChampionId: s.preHoverChampionId } }
}

/** The settings change that sets part of the plan's setup: the lane's own entry, or the default fields. */
export function setupChange(s: Settings, plan: GamePlan, c: Partial<LaneSetup>): Partial<Settings> {
  if (plan.lane) return { lanes: { ...s.lanes, [plan.lane]: { ...plan.setup, ...c } } }
  return {
    ...('banChampionId' in c && { autoBanChampionId: c.banChampionId }), ...(c.banTiming && { banTiming: c.banTiming }),
    ...('lockChampionId' in c && { autoLockChampionId: c.lockChampionId }), ...(c.lockTiming && { lockTiming: c.lockTiming }),
    ...('hoverChampionId' in c && { preHoverChampionId: c.hoverChampionId }),
  }
}

/** The listed friends who are in the party, in list order (the order auto-honor votes in); Riot IDs compared like the host does. */
export function honorInParty(friends: string[], party: string[]): string[] {
  const here = new Set(party.map((n) => n.trim().toLowerCase()).filter(Boolean))
  return friends.map((f) => f.trim()).filter((f) => f && here.has(f.toLowerCase()))
}

/** One thing automation does in this champ select, and how far it got: pending, done as planned, or the user chose something else. */
export interface DraftStep { kind: 'ban' | 'lock' | 'hover'; championId: number; timing: Timing | null; state: 'pending' | 'done' | 'yours'; otherId?: number }

/** What auto ban, lock and pre-select do in this champ select, read from the board and the setup for my lane. Empty outside draft or with nothing set. */
export function draftSteps(s: Settings, board: Board): DraftStep[] {
  if (board.mode !== 'draft') return []
  const me = board.myTeam.find((p) => p.isMe)
  const { setup } = planOf(s, laneKeyOf(me?.lane))
  const steps: DraftStep[] = []
  if (setup.banChampionId != null) {
    const mine = board.banList?.find((b) => b.cellId === board.myCellId)
    const id = setup.banChampionId
    steps.push({ kind: 'ban', championId: id, timing: setup.banTiming, ...(!mine ? { state: 'pending' } : mine.championId === id ? { state: 'done' } : { state: 'yours', otherId: mine.championId }) })
  }
  const pick = (id: number) => (!me?.locked ? 'pending' : me.championId === id || id === BEST_COUNTER ? 'done' : 'yours')
  if (setup.lockChampionId != null) {
    // "Best counter" names no champion up front: once locked it reads as done with what was locked (by Glamour or by me, it was a counter pick either way).
    const id = setup.lockChampionId === BEST_COUNTER && me?.locked ? me.championId : setup.lockChampionId
    const state = pick(setup.lockChampionId)
    steps.push({ kind: 'lock', championId: id, timing: setup.lockTiming, state, ...(state === 'yours' && { otherId: me!.championId }) })
  } else if (setup.hoverChampionId != null) {
    const id = setup.hoverChampionId
    const state = !me || me.championId <= 0 ? 'pending' : me.championId === id ? 'done' : 'yours'
    steps.push({ kind: 'hover', championId: id, timing: null, state, ...(state === 'yours' && { otherId: me!.championId }) })
  }
  return steps
}

/** Ban, lock and pre-select run in draft-style champ selects only; ARAM and Mayhem (bench queues) have none. Unknown queue: assume they run. */
export const champsRunIn = (gameMode: string | undefined) => !gameMode || gameMode === 'CLASSIC'
