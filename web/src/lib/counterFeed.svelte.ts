import { getJson } from './api'
import { counterKey, counterUrl, type CounterPickView } from './counterPick'
import type { Board } from './types'

/**
 * The counter pick view for the live board, shared by the side panel and the phone sheet. Asked again only when my lane, my lock, the picked enemies,
 * my teammates' champions or the opponent chosen by hand (`forced`) change. Call it while a component starts: the choice and the request end with that component (one champ select).
 * The demo has no host data behind it: made-up suggestions against the demo board.
 */
export function counterFeed(board: () => Board, demo: boolean) {
  let forced = $state<number | null>(null)
  let view = $state<CounterPickView | null>(null)
  const key = $derived(counterKey(board()) && `${counterKey(board())}|${forced ?? ''}`)
  $effect(() => {
    const k = key
    if (demo) { view = demoCounter(board(), forced); return }
    if (!k) { view = null; return }
    let stale = false
    getJson<CounterPickView>(counterUrl(forced)).then((v) => { if (!stale) view = v }).catch(() => { if (!stale) view = null })
    return () => { stale = true }
  })
  return {
    get view() { return view },
    get forced() { return forced },
    set forced(id: number | null) { forced = id },
  }
}

export function demoCounter(b: Board, enemy: number | null): CounterPickView {
  const enemies = b.theirTeam.map((p) => p.championId).filter((id) => id > 0)
  // The demo board's enemies by champion (Darius, Yasuo, Lux, Thresh), so my support lane meets their support, not their first pick.
  const lanes: Record<number, string> = { 122: 'TOP', 157: 'MIDDLE', 51: 'BOTTOM', 412: 'SUPPORT' }
  const enemyLanes = Object.fromEntries(enemies.map((id) => [id, lanes[id] ?? 'JUNGLE']))
  const chances = Object.fromEntries(enemies.map((id) => [id, enemyLanes[id] === 'SUPPORT' ? 86 : 3]))
  const opponent = enemy ?? enemies.find((id) => enemyLanes[id] === 'SUPPORT') ?? 0
  return { state: opponent ? 'ready' : 'waiting', lane: 'SUPPORT', enemyId: opponent, enemies, enemyLanes, chances, forced: enemy !== null, picks: [
    { championId: 25, winRate: 53.8, games: 2410, fewGames: false, inPool: true, team: 51.2 },
    { championId: 89, winRate: 52.9, games: 1873, fewGames: false, inPool: false, team: 52.6 },
    { championId: 267, winRate: 52.1, games: 3120, fewGames: false, inPool: false, team: 50.4 },
    { championId: 117, winRate: 51.4, games: 1502, fewGames: false, inPool: false, olderGames: 1120, olderPatch: '16.19' },
    { championId: 43, winRate: 49.2, games: 180, fewGames: true, inPool: true },
  ], teamPicks: [
    { championId: 89, winRate: 52.6, covered: enemies.length, games: 5120, inPool: false, lane: 52.9 },
    { championId: 111, winRate: 52.3, covered: enemies.length, games: 4210, inPool: false, lane: 50.8 },
    { championId: 25, winRate: 51.2, covered: Math.max(1, enemies.length - 1), games: 3300, inPool: true, lane: 53.8 },
    { championId: 497, winRate: 51.0, covered: enemies.length, games: 2980, inPool: false },
  ], autoLock: 89, duo: { partnerId: b.myTeam.find((p) => p.lane === 'BOTTOM' && p.championId > 0)?.championId ?? 222, best: [89, 412, 111] }, damage: { physical: 48200, magic: 30100, true: 6900, byChampion: Object.fromEntries(enemies.map((id, i) => [id, [
    { physical: 20159, magic: 834, true: 2514 }, { physical: 1200, magic: 14800, true: 900 }, { physical: 15214, magic: 2574, true: 1582 },
    { physical: 9800, magic: 400, true: 1200 }, { physical: 1800, magic: 11500, true: 700 }][i % 5]])) },
  allyDamage: { physical: 61500, magic: 12300, true: 4100, byChampion: Object.fromEntries(b.myTeam.filter((p) => p.championId > 0).map((p, i) => [p.championId, [
    { physical: 18200, magic: 1100, true: 1300 }, { physical: 14900, magic: 600, true: 900 }, { physical: 2400, magic: 9800, true: 500 },
    { physical: 21000, magic: 300, true: 1100 }, { physical: 5000, magic: 500, true: 300 }][i % 5]])) }, threats: [
    { championId: 89, winRate: 54.1, games: 2210, fewGames: false, inPool: false },
    { championId: 111, winRate: 53.2, games: 1830, fewGames: false, inPool: false },
    { championId: 497, winRate: 52.4, games: 3010, fewGames: false, inPool: false },
  ], mine: { championId: 412, picked: enemies.length > 0, lines: [
    { championId: opponent || 89, winRate: 48.6, games: 1290, fewGames: false },
    { championId: 53, winRate: 51.7, games: 640, fewGames: false },
  ] } }
}
