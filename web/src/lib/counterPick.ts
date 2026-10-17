import type { Board } from './types'

/** `olderGames` of the `games` are from the previous patch `olderPatch` (a thin matchup on a new patch counts those too). */
export interface CounterSuggestion { championId: number; winRate: number; games: number; fewGames: boolean; inPool: boolean; olderGames?: number; olderPatch?: string | null; team?: number | null }
/** A pick against the whole enemy team: `winRate` is its team score (its win rate against each picked enemy, thin pairs pulled toward 50, averaged), `covered` of
 * the enemies have games against it, `lane` its win rate against my lane opponent when known. */
export interface TeamSuggestion { championId: number; winRate: number; covered: number; games: number; inPool: boolean; lane?: number | null }
/** Damage to champions per game, by type. */
export interface DamageSplit { physical: number; magic: number; true: number }
/** The picked enemies' damage added up, and each one's own (keyed by champion id). */
export interface TeamDamage extends DamageSplit { byChampion: Record<string, DamageSplit> }
/** My champion's win rate against one opponent in my lane. */
export interface MatchupLine { championId: number; winRate: number; games: number; fewGames: boolean; olderGames?: number; olderPatch?: string | null }

/** The games label of a matchup row, and its tooltip when some games are from the previous patch. */
export function gamesLabel(r: { games: number; fewGames: boolean; olderGames?: number; olderPatch?: string | null }): { text: string; title: string } {
  const base = r.fewGames ? 'few games' : `${r.games.toLocaleString()} games`
  if (!r.olderGames || !r.olderPatch) return { text: base, title: '' }
  const text = `${base} · ${r.olderGames >= r.games ? '' : 'incl. '}${r.olderPatch}`
  const title = r.olderGames >= r.games ? `All ${r.games.toLocaleString()} games from patch ${r.olderPatch}` : `${r.olderGames.toLocaleString()} of ${r.games.toLocaleString()} games from patch ${r.olderPatch}`
  return { text, title }
}
/** My hovered or locked champion: `picked` lines are the picked enemies in my lane (my opponent first), otherwise its hardest common matchups. */
export interface MyMatchups { championId: number; picked: boolean; lines: MatchupLine[] }
/**
 * `GET /api/counter-pick[?enemy=id]`: "none" shows nothing, "locked" no suggestions (my pick is in), "waiting" means no enemy is likely in my lane yet,
 * "nodata" that the enemy picked by hand has no table, "ready" has the opponent and the suggestions. `enemies` are the picked enemy champions (to choose who
 * I face), `forced` that `enemy` was used, `dataLane` the lane the suggestions come from when it is not mine, `mine` my own champion's matchups,
 * `threats` who beats my champion most in my lane (their win rate against it; bans and locked champions left out).
 */
export interface CounterPickView {
  state: 'none' | 'locked' | 'waiting' | 'nodata' | 'ready'; lane: string | null; enemyId: number; picks: CounterSuggestion[] | null; enemies: number[] | null
  forced: boolean; dataLane?: string | null; mine?: MyMatchups | null
  /** The most likely lane of each picked enemy champion (a guess from its lane shares), keyed by champion id. */
  enemyLanes?: Record<string, string> | null
  threats?: CounterSuggestion[] | null
  /** Picks against all picked enemies (also while waiting for my lane opponent). */
  teamPicks?: TeamSuggestion[] | null
  damage?: TeamDamage | null
  /** My team's damage: every ally's hovered or locked champion, me included. */
  allyDamage?: TeamDamage | null
  /** The champion the lane's "Best counter" auto-lock would lock now. */
  autoLock?: number | null
  /** Bot lane: my partner's champion and the picks that do best with it, best first (before my lock). */
  duo?: DuoView | null
  /** How likely each picked enemy plays my lane (percent), keyed by champion id. */
  chances?: Record<string, number> | null
}

export interface DuoView { partnerId: number; best: number[] }

/** Which list the counter picks show: against my lane opponent, or against their whole team. */
export type CounterSort = 'lane' | 'team'
/** One row of the counter list, whichever way it is sorted: `main` is the number it is sorted by, `alt` the other one when known. */
export interface CounterRow { championId: number; main: number; alt: number | null; altLabel: string; sub: { text: string; title: string }; inPool: boolean; auto: boolean; duo: boolean }

/** The lists this view has: lane once my opponent is known, team once any enemy is picked. */
export function counterSorts(v: CounterPickView | null): CounterSort[] {
  if (!v) return []
  return [...(v.state === 'ready' && v.picks ? ['lane' as const] : []), ...(v.teamPicks?.length ? ['team' as const] : [])]
}

/** The rows for `want`, or for the list the view has when it lacks that one. */
export function counterRows(v: CounterPickView, want: CounterSort): { sort: CounterSort; rows: CounterRow[] } | null {
  const sorts = counterSorts(v)
  const sort = sorts.includes(want) ? want : sorts[0]
  if (!sort) return null
  const auto = (id: number) => v.autoLock === id
  const duo = (id: number) => !!v.duo?.best.includes(id)
  if (sort === 'lane') return { sort, rows: (v.picks ?? []).map((p) => ({ championId: p.championId, main: p.winRate, alt: p.team ?? null, altLabel: 'team', sub: gamesLabel(p), inPool: p.inPool, auto: auto(p.championId), duo: duo(p.championId) })) }
  const enemies = v.enemies?.length ?? 0
  return { sort, rows: (v.teamPicks ?? []).map((p) => ({
    championId: p.championId, main: p.winRate, alt: p.lane ?? null, altLabel: 'lane', inPool: p.inPool, auto: auto(p.championId), duo: duo(p.championId),
    sub: { text: p.games < 300 ? `few games · vs ${p.covered} of ${enemies}` : `${p.games.toLocaleString()} games · vs ${p.covered} of ${enemies}`, title: `Games against ${p.covered} of the ${enemies} picked enemies` },
  })) }
}

/** The team's damage as whole percents (adding up to 100) for the bar, or null with nothing known. */
export function damageShares(d: DamageSplit | null | undefined): DamageSplit | null {
  const total = d ? d.physical + d.magic + d.true : 0
  if (!d || total <= 0) return null
  const magic = Math.round((d.magic / total) * 100), tru = Math.round((d.true / total) * 100)
  return { physical: 100 - magic - tru, magic, true: tru }
}

/**
 * What the host's answer depends on: my lane, my champion and whether it is locked, which enemy champions are picked, and which champions are banned or locked.
 * The panel asks again only when this changes, so timer ticks do not cause requests. Empty when there is nothing to ask.
 */
export function counterKey(board: Board | null): string {
  if (!board || board.mode !== 'draft') return ''
  const me = board.myTeam.find((p) => p.isMe)
  if (!me || !me.lane) return ''
  const enemies = board.theirTeam.map((p) => p.championId).filter((id) => id > 0).sort((a, b) => a - b)
  // Bans and locked-in champions also change the list (they are never suggested).
  const taken = [...board.myBans, ...board.theirBans, ...(board.banList ?? []).map((b) => b.championId), ...[...board.myTeam, ...board.theirTeam].filter((p) => p.locked).map((p) => p.championId)]
    .filter((id) => id > 0).sort((a, b) => a - b)
  // Teammates' hovers change my team's damage.
  const allies = board.myTeam.filter((p) => !p.isMe).map((p) => p.championId).filter((id) => id > 0).sort((a, b) => a - b)
  return `${me.lane}:${me.championId}${me.locked ? 'L' : ''}:${enemies.join(',')}:${[...new Set(taken)].join(',')}:${allies.join(',')}`
}

/** The request for the current board and the opponent picked by hand (null: the host's guess). */
export const counterUrl = (forced: number | null) => (forced ? `/counter-pick?enemy=${forced}` : '/counter-pick')

/** The picked enemies that may play my lane (from 15%), likeliest first: who it might be while no opponent is named. */
export const maybeOpponents = (view: CounterPickView) =>
  Object.entries(view.chances ?? {}).map(([id, chance]) => ({ championId: Number(id), chance })).filter((c) => c.chance >= 15).sort((a, b) => b.chance - a.chance)

/** The line while no opponent is named: who it might be, or that nobody fits yet. `lane` in words ("mid"). */
export const waitingNote = (view: CounterPickView, lane: string, name: (id: number) => string) => {
  const maybe = maybeOpponents(view)
  return maybe.length ? `Not sure who plays ${lane}: ${maybe.map((m) => `${name(m.championId)} ${m.chance}%`).join(', ')}.` : `No ${lane} laner picked yet.`
}

/** How sure the guessed opponent is (percent), when worth saying: not picked by hand and under 90%. */
export const opponentChance = (view: CounterPickView) => {
  const c = view.forced ? null : view.chances?.[view.enemyId]
  return c != null && c < 90 ? c : null
}

/** "58.4%" for a win rate in percent. */
export const percent = (v: number) => `${v.toFixed(1)}%`

/**
 * The enemy champion on the Nth card of the client's own roster (a click there, sent by the tray app as "face:N"), or null when that player has no
 * champion yet. The client lists each team by cell, top to bottom (the order is [UNVERIFIED], SPEC "Click layer on the client's roster").
 */
export function enemyAtSlot(board: Board, index: number): number | null {
  const id = [...board.theirTeam].sort((a, b) => a.cellId - b.cellId)[index]?.championId ?? 0
  return id > 0 ? id : null
}
