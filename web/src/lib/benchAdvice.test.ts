import { describe, expect, it } from 'vitest'
import { benchMode, benchRows, benchTags, rateText, type ModeRates } from './benchAdvice'
import type { Board } from './types'

const board = (over: Partial<Board> = {}) => ({
  stage: 'BAN_PICK', mode: 'aram', queueId: 450, myTeam: [{ cellId: 0, championId: 1, isMe: true }], theirTeam: [], bench: [], ...over,
}) as unknown as Board

const rate = (championId: number, winRate: number) => ({ championId, winRate, games: 5000, olderGames: 0, olderPatch: null })
const rates: ModeRates = { mode: 'aram', patch: '16.20', source: 'lolalytics.com', champions: [rate(1, 50), rate(2, 50.9), rate(3, 53), rate(4, 47)] }

describe('bench advice', () => {
  it('uses ARAM numbers in ARAM queues and Mayhem numbers in Mayhem', () => {
    expect(benchMode(board())).toBe('aram')
    expect(benchMode(board({ queueId: 3210 }))).toBe('aram')
    expect(benchMode(board({ queueId: 900 }))).toBeNull() // ARURF has a bench too
    expect(benchMode(board({ mode: 'mayhem', queueId: 2400 }))).toBe('mayhem')
    expect(benchMode(board({ mode: 'draft', queueId: 420 }))).toBeNull()
    expect(benchMode(null)).toBeNull()
  })
  it('sorts the bench best first, unknown last, and marks only clear gains over mine', () => {
    const { mine, bench } = benchRows(board({ bench: [4, 99, 2, 3, 3] }), rates)
    expect(mine).toMatchObject({ championId: 1, rate: { winRate: 50 } })
    expect(bench.map((r) => r.championId)).toEqual([3, 2, 4, 99])
    expect(bench.map((r) => r.better)).toEqual([true, false, false, false]) // 50.9 is within a point of mine
  })
  it('ranks the offered pair too, and by tier when the source has only tiers', () => {
    const tier = (championId: number, t: string) => ({ championId, winRate: null, games: 0, olderGames: 0, olderPatch: null, tier: t })
    const tiers: ModeRates = { mode: 'mayhem', patch: '16.20', source: 'u.gg', champions: [tier(1, 'B'), tier(2, 'B'), tier(3, 'S'), tier(4, 'A'), tier(5, 'D')] }
    const { offered, bench } = benchRows(board({ mode: 'mayhem', offered: [5, 3], bench: [2, 4] }), tiers)
    expect(offered.map((r) => [r.championId, r.better])).toEqual([[3, true], [5, false]])
    expect(bench.map((r) => [r.championId, r.better])).toEqual([[4, true], [2, false]]) // one tier up counts, the same tier not
    expect(rateText(offered[0].rate)).toBe('Tier S')
    expect(rateText(rates.champions[1])).toBe('50.9%')
  })
  it('tags the client bench slots in the bench order, better ones marked', () => {
    expect(benchTags(board({ bench: [4, 99, 3] }), rates)).toBe('bench:47.0,,^53.0')
    expect(benchTags(board({ bench: [3] }), null)).toBe('bench:')
    expect(benchTags(board(), rates)).toBe('bench:')
  })
  it('marks nothing while my champion has no number', () => {
    const { mine, bench } = benchRows(board({ myTeam: [{ cellId: 0, championId: 0, isMe: true }] as Board['myTeam'], bench: [3] }), rates)
    expect(mine).toBeNull()
    expect(bench[0].better).toBe(false)
    expect(benchRows(board({ bench: [3] }), null).bench[0]).toMatchObject({ rate: null, better: false })
  })
})
