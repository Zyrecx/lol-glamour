import type { Board } from './types'

/**
 * One champion's numbers in a mode: a win rate, or (u.gg's Mayhem fallback) only a `tier` letter. `games` is 0 when the source gives no counts;
 * `olderGames` of them come from `olderPatch` when the current patch is thin.
 */
export interface ModeRate { championId: number; winRate: number | null; games: number; olderGames: number; olderPatch: string | null; tier?: string | null }
/** `GET /api/winrates/{mode}`: every champion's numbers, `patch` the newest one counted, `source` the site. */
export interface ModeRates { mode: string; patch: string; source: string; champions: ModeRate[] }

/** A bench, offered or my own champion with its numbers, null when the source has none; `better` clearly beats mine. */
export interface BenchRow { championId: number; rate: ModeRate | null; better: boolean }

/** Points a champion must beat mine by to be marked: smaller gaps are noise next to a pick you already like. With tiers only: one tier up. */
export const BETTER_BY = 1
/** Tier letters best first (u.gg). */
export const TIERS = ['S+', 'S', 'A', 'B', 'C', 'D']

export type BenchMode = 'aram' | 'mayhem'

/** The numbers that fit this champ select: ARAM (450, ARAM Clash 720, ARAM customs 3200-3299) or Mayhem (2400). ARURF has a bench too, but neither's numbers. */
export function benchMode(b: Board | null): BenchMode | null {
  if (!b) return null
  if (b.mode === 'mayhem') return 'mayhem'
  if (b.mode !== 'aram') return null
  return b.queueId === 450 || b.queueId === 720 || (b.queueId >= 3200 && b.queueId < 3300) ? 'aram' : null
}

/** The mode's short name for labels. */
export const modeName = (m: BenchMode) => (m === 'aram' ? 'ARAM' : 'Mayhem')

/** A sort score: the win rate, or the tier's place on a win-rate-like scale (only ever compared within one source). */
function score(r: ModeRate | null): number {
  if (!r) return -1
  if (r.winRate != null) return r.winRate
  const i = r.tier ? TIERS.indexOf(r.tier) : -1
  return i < 0 ? -1 : (TIERS.length - i) * BETTER_BY
}

/** "52.4%" or "Tier S"; empty without numbers. */
export function rateText(r: ModeRate | null): string {
  if (!r) return ''
  if (r.winRate != null) return `${r.winRate.toFixed(1)}%`
  return r.tier ? `Tier ${r.tier}` : ''
}

/** My champion, the offered ones (Mayhem) and the bench, each list best first (champions without numbers last, in their order). */
export function benchRows(b: Board, rates: ModeRates | null): { mine: BenchRow | null; offered: BenchRow[]; bench: BenchRow[] } {
  const byId = new Map((rates?.champions ?? []).map((r) => [r.championId, r]))
  const myId = b.myTeam.find((p) => p.isMe)?.championId ?? 0
  const mineRate = byId.get(myId) ?? null
  const mineScore = score(mineRate)
  const row = (id: number): BenchRow => {
    const rate = byId.get(id) ?? null
    return { championId: id, rate, better: !!rate && mineScore >= 0 && score(rate) >= mineScore + BETTER_BY }
  }
  const sorted = (ids: number[]) => [...new Set(ids)].map(row)
    .map((r, i) => ({ r, i }))
    .sort((x, y) => score(y.r.rate) - score(x.r.rate) || x.i - y.i)
    .map((x) => x.r)
  return { mine: myId ? { championId: myId, rate: mineRate, better: false } : null, offered: sorted(b.offered ?? []), bench: sorted(b.bench) }
}

/**
 * The "bench:" message for the tray app, which tags the client's own bench slots: one item per slot in the board's bench order (the client fills its strip
 * in that order and a swap keeps the slot, LIVE), "52.4" or a tier letter, "^" first when better than mine, empty without numbers.
 */
export function benchTags(b: Board, rates: ModeRates | null): string {
  const rows = new Map(benchRows(b, rates).bench.map((r) => [r.championId, r]))
  const items = b.bench.map((id) => {
    const r = rows.get(id)
    const text = r?.rate?.winRate != null ? r.rate.winRate.toFixed(1) : r?.rate?.tier ?? ''
    return text && r?.better ? `^${text}` : text
  })
  return `bench:${items.join(',')}`
}

/** Made-up numbers for the demo boards (no host data behind them): win rates in ARAM, tiers in Mayhem when `tiers`. */
export function demoRates(b: Board, tiers = false): ModeRates {
  const ids = [...b.bench, ...(b.offered ?? []), ...b.myTeam.map((p) => p.championId)].filter((id) => id > 0)
  return { mode: b.mode, patch: '16.20', source: tiers ? 'u.gg' : 'demo', champions: ids.map((id) => tiers
    ? { championId: id, winRate: null, games: 0, olderGames: 0, olderPatch: null, tier: TIERS[(id * 7) % TIERS.length] }
    : { championId: id, winRate: 45 + ((id * 37) % 110) / 10, games: b.mode === 'mayhem' ? 0 : 4000 + ((id * 911) % 20000), olderGames: 0, olderPatch: null }) }
}
