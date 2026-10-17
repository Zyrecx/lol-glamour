import type { Board, Dodge, GameParties, HonorBallot, Invitation, LobbyInfo, Swap, Turn } from './types'

/** What is happening in the client right now, derived from the SSE snapshot and later events. Pure, so it is unit-tested. */
export interface NowState {
  connected: boolean
  /** How far the client is while not connected: nothing, Riot Client only, League starting. See `clientStatus`. */
  clientStage: ClientStage
  phase: string
  queue: string
  lobby: LobbyInfo
  invitations: Invitation[]
  ready: string | null
  /** My answer to the ready check (Accepted, Declined, None), whoever gave it: the client, the phone or automation. */
  readyResponse: string
  /** Local `Date.now()` when the ready check opened, and how long it lasts; null when none. The host sends elapsed times, never its clock. */
  readySince: number | null
  readyWindowMs: number
  /** Local `Date.now()` when the search started; null when not searching. */
  queueSince: number | null
  /** The client's estimate in seconds (0 = none); shown as m:ss like the client, it is often far off (INSIGHTS). */
  queueEstimate: number
  /** The post-game ballot; null when there is none (the honor screen closes itself). */
  honor: HonorBallot | null
  /** Automation paused for the next game (the global switch is the `automationEnabled` setting). */
  paused: boolean
  /** Auto ban, lock and pre-select stopped for the champ select running now (ends with it). */
  draftStopped: boolean
  /** Local `Date.now()` when a delayed auto-accept fires; null when none is waiting. */
  acceptAt: number | null
  /** The ban or lock automation is waiting to send, with the local `Date.now()` it fires at; null when none waits. */
  draftAt: { type: 'ban' | 'pick'; championId: number; at: number } | null
  /** Action types ("ban", "pick") the user skipped in this champ select. */
  draftSkipped: string[]
  /** Swaps automation sent or accepted in this champ select ("Asked Mid for their lane"). */
  swapNotes: string[]
  /** The automatic matchup loadout in this champ select: waiting (applied at local `Date.now()` `at`, with Cancel), done or cancelled; null before any. */
  loadout: { state: 'waiting' | 'done' | 'cancelled'; enemyId: number; at: number | null } | null
  /** My champion, skin and lane from the last champ select, kept after the board is cleared so the in-game screen can show them. */
  myPick: { championId: number; skinId: number; lane: string; queueId: number } | null
  /** Local `Date.now()` at in-game time 0:00, from the game's own clock; null when no game runs. */
  gameStart: number | null
  gameMode: string
  stage: string
  turn: Turn | null
  board: Board | null
  swaps: Swap[]
  dodge: Dodge | null
  outcome: string
  /** Who queued with whom in the running game; null outside a game (the host reads it from GameStart on). */
  parties: GameParties | null
}

/** Every event type the UI listens for on `/api/events`. An event the host publishes but this list lacks never arrives (now.test.ts checks it against the host source). */
export const EVENTS = [
  'snapshot', 'connection', 'settings', 'defaults', 'profiles', 'phase', 'lobby', 'invitations', 'queue', 'ready-check', 'ready-check-result', 'champselect-start',
  'cs-stage', 'board', 'myturn', 'myturn-end', 'swap', 'dodge', 'champselect-end', 'honor', 'automation', 'game', 'parties', 'app', 'profile-icon',
]

export type ClientStage = 'none' | 'riot-client' | 'league-starting' | 'connected'

/** One line for the connection pill: where the client is, not just whether League runs. */
export function clientStatus(s: Pick<NowState, 'connected' | 'clientStage'>): { text: string; on: boolean } {
  if (s.connected) return { text: 'League connected', on: true }
  switch (s.clientStage) {
    case 'league-starting': return { text: 'League client starting', on: false }
    case 'riot-client': return { text: 'Riot Client open, League not started', on: false }
    default: return { text: 'Riot Client not running', on: false }
  }
}

export const SWAP_NAMES: Record<string, string> = { position: 'lane swap', 'pick-order': 'pick order swap', trade: 'champion trade' }


export const noLobby: LobbyInfo = { canStart: true, queueId: 0, showPositions: false, first: '', second: '', members: 0 }

export const initialNow = (): NowState => ({
  connected: false, clientStage: 'none', phase: 'None', queue: 'None', lobby: noLobby, invitations: [], ready: null, readyResponse: 'None',
  readySince: null, readyWindowMs: 0, queueSince: null, queueEstimate: 0, honor: null, paused: false, draftStopped: false, acceptAt: null, draftAt: null, draftSkipped: [], swapNotes: [], loadout: null, myPick: null, gameStart: null, gameMode: '', stage: '', turn: null, board: null, swaps: [], dodge: null, outcome: '', parties: null,
})
// eslint-disable-next-line @typescript-eslint/no-explicit-any
const partiesOf = (p: any): GameParties | null => (p && p.gameId ? p : null)

/** A start time on this device's clock from an elapsed time the host measured; null when nothing is running. */
const since = (elapsedMs: number | undefined, running: boolean, at: number) => (running ? at - (elapsedMs ?? 0) : null)

// eslint-disable-next-line @typescript-eslint/no-explicit-any
const queueOf = (q: any, at: number) => ({ queue: q.state, queueSince: since(q.elapsedMs, q.state === 'Searching', at), queueEstimate: q.estimatedQueueTime ?? 0 })
// eslint-disable-next-line @typescript-eslint/no-explicit-any
const readyOf = (r: any, at: number) => ({
  ready: r.state, readyResponse: r.playerResponse ?? 'None', readySince: since(r.elapsedMs, r.state === 'InProgress', at), readyWindowMs: r.windowMs ?? 0,
})
// eslint-disable-next-line @typescript-eslint/no-explicit-any
const automationOf = (a: any, at: number) => ({
  paused: !!a?.paused, draftStopped: !!a?.draftStopped, acceptAt: a?.acceptPending ? at + a.acceptPending.timeLeftMs : null,
  draftAt: a?.draftPending ? { type: a.draftPending.type, championId: a.draftPending.championId, at: at + a.draftPending.timeLeftMs } : null,
  draftSkipped: Array.isArray(a?.draftSkipped) ? a.draftSkipped : [],
  swapNotes: Array.isArray(a?.swapNotes) ? a.swapNotes : [],
  loadout: a?.loadout ? { state: a.loadout.state, enemyId: a.loadout.enemyId, at: a.loadout.timeLeftMs == null ? null : at + a.loadout.timeLeftMs } : null,
})
// eslint-disable-next-line @typescript-eslint/no-explicit-any
const gameOf = (g: any, at: number) => ({ gameStart: since(g?.gameTimeMs, !!g?.inGame, at), gameMode: g?.gameMode ?? '' })
const pickOf = (b: Board | null | undefined) => {
  const me = b?.myTeam.find((p) => p.isMe)
  return b && me && me.championId > 0 ? { championId: me.championId, skinId: me.skinId, lane: me.lane, queueId: b.queueId } : null
}
// eslint-disable-next-line @typescript-eslint/no-explicit-any
const honorOf = (h: any): HonorBallot | null => (h && h.gameId ? h : null)

/** `at` is this device's clock when the event arrived (tests pass a fixed one). */
// eslint-disable-next-line @typescript-eslint/no-explicit-any
export function applyEvent(s: NowState, type: string, d: any, at: number = Date.now()): NowState {
  switch (type) {
    case 'snapshot': {
      const x = d.state
      return {
        ...s, ...automationOf(d.automation, at), ...gameOf(d.game, at), connected: d.connected, clientStage: d.stage ?? (d.connected ? 'connected' : 'none'), phase: d.phase,
        ...queueOf(x.queue, at), lobby: x.lobby ?? noLobby, invitations: x.invitations ?? [], ...readyOf(x.readyCheck, at), honor: honorOf(x.honor), stage: x.champSelect.stage,
        turn: x.champSelect.turn, board: x.champSelect.board ?? null, myPick: pickOf(x.champSelect.board) ?? s.myPick, swaps: x.champSelect.swaps,
        dodge: x.dodge.state === 'Invalid' ? null : x.dodge, parties: partiesOf(x.parties),
      }
    }
    case 'parties': return { ...s, parties: partiesOf(d) }
    case 'connection': return { ...s, connected: d.connected, clientStage: d.stage ?? (d.connected ? 'connected' : 'none') }
    case 'phase': return d.phase === 'ChampSelect' ? { ...s, phase: d.phase, outcome: '', dodge: null } : { ...s, phase: d.phase }
    case 'invitations': return { ...s, invitations: d }
    case 'lobby': return { ...s, lobby: d }
    case 'queue': return { ...s, ...queueOf(d, at) }
    case 'ready-check': return { ...s, ...readyOf(d, at) }
    case 'honor': return { ...s, honor: honorOf(d) }
    case 'automation': return { ...s, ...automationOf(d, at) }
    case 'game': return { ...s, ...gameOf(d, at) }
    case 'champselect-start': return { ...s, outcome: '', swaps: [], board: null, myPick: null }
    case 'cs-stage': return { ...s, stage: d.stage }
    case 'board': return { ...s, board: d, myPick: pickOf(d) ?? s.myPick }
    case 'myturn': return { ...s, turn: d }
    case 'myturn-end': return { ...s, turn: null }
    // Only live requests are kept; any other state (accepted, timed out, gone) removes the entry and its buttons.
    case 'swap': {
      const rest = s.swaps.filter((x) => !(x.kind === d.kind && x.id === d.id))
      return { ...s, swaps: d.state === 'RECEIVED' || d.state === 'SENT' ? rest.concat(d) : rest }
    }
    case 'dodge': return { ...s, dodge: d }
    case 'champselect-end': return { ...s, outcome: d.outcome, turn: null, board: null, swaps: [] }
    default: return s
  }
}
