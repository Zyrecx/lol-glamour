import { describe, expect, it } from 'vitest'
import { champsRunIn, draftSteps, honorInParty, laneKeyOf, planOf, setupChange } from './plan'
import type { Board, Settings } from '../lib/types'

const s = {
  autoBanChampionId: 75, banTiming: 'Instant', autoLockChampionId: 518, lockTiming: 'LastSecond', preHoverChampionId: null,
  lanes: { SUPPORT: { banChampionId: 1, banTiming: 'LastSecond', lockChampionId: null, lockTiming: 'Instant', hoverChampionId: 99 } },
} as unknown as Settings

describe('laneKeyOf', () => {
  it('maps the lobby position to a settings lane', () => {
    expect(laneKeyOf('UTILITY')).toBe('SUPPORT')
    expect(laneKeyOf('MIDDLE')).toBe('MIDDLE')
    expect(laneKeyOf('FILL')).toBeNull()
    expect(laneKeyOf('UNSELECTED')).toBeNull()
    expect(laneKeyOf('')).toBeNull()
  })
})

describe('planOf', () => {
  it('uses the lane setup when the lane has one', () => {
    expect(planOf(s, 'SUPPORT')).toEqual({ lane: 'SUPPORT', setup: s.lanes.SUPPORT })
  })
  it('falls back to the default for a lane without one, and for no lane', () => {
    const def = { lane: null, setup: { banChampionId: 75, banTiming: 'Instant', lockChampionId: 518, lockTiming: 'LastSecond', hoverChampionId: null } }
    expect(planOf(s, 'TOP')).toEqual(def)
    expect(planOf(s, null)).toEqual(def)
  })
})

describe('setupChange', () => {
  it('edits the default fields', () => {
    expect(setupChange(s, planOf(s, null), { banChampionId: null, lockTiming: 'Instant' })).toEqual({ autoBanChampionId: null, lockTiming: 'Instant' })
  })
  it('edits the lane entry as a whole, keeping the other lanes', () => {
    const lanes = { ...s.lanes, TOP: { banChampionId: 2, banTiming: 'Instant', lockChampionId: null, lockTiming: 'Instant', hoverChampionId: null } } as Settings['lanes']
    const t = { ...s, lanes } as Settings
    const c = setupChange(t, planOf(t, 'SUPPORT'), { banChampionId: 3 })
    expect(c.lanes?.SUPPORT?.banChampionId).toBe(3)
    expect(c.lanes?.SUPPORT?.hoverChampionId).toBe(99)
    expect(c.lanes?.TOP?.banChampionId).toBe(2)
  })
})

describe('honorInParty', () => {
  it('keeps list order and ignores case and spaces', () => {
    expect(honorInParty(['Best#EUW', ' other#euw ', 'Away#NA'], ['OTHER#EUW', 'me#EUW', 'best#euw'])).toEqual(['Best#EUW', 'other#euw'])
    expect(honorInParty(['Best#EUW'], [])).toEqual([])
  })
})

describe('draftSteps', () => {
  const me = { cellId: 2, championId: 0, locked: false, lane: '', isMe: true }
  const board = (p: object, extra: object = {}) => ({ mode: 'draft', myCellId: 2, myTeam: [{ ...me, ...p }], banList: [], ...extra }) as unknown as Board
  it('lists the default ban and lock as pending with their timing', () => {
    expect(draftSteps(s, board({}))).toEqual([
      { kind: 'ban', championId: 75, timing: 'Instant', state: 'pending' },
      { kind: 'lock', championId: 518, timing: 'LastSecond', state: 'pending' },
    ])
  })
  it('uses my lane setup: pre-select when it has no lock', () => {
    expect(draftSteps(s, board({ lane: 'SUPPORT' })).map((x) => x.kind)).toEqual(['ban', 'hover'])
    expect(draftSteps(s, board({ lane: 'SUPPORT', championId: 99 }))[1].state).toBe('done')
    expect(draftSteps(s, board({ lane: 'SUPPORT', championId: 12 }))[1]).toMatchObject({ state: 'yours', otherId: 12 })
  })
  it('marks my ban and lock done, or the user\'s own when they differ', () => {
    const done = draftSteps(s, board({ championId: 518, locked: true }, { banList: [{ cellId: 2, championId: 75, mine: true }] }))
    expect(done.map((x) => x.state)).toEqual(['done', 'done'])
    const other = draftSteps(s, board({ championId: 7, locked: true }, { banList: [{ cellId: 2, championId: 8, mine: true }, { cellId: 3, championId: 75, mine: true }] }))
    expect(other).toMatchObject([{ state: 'yours', otherId: 8 }, { state: 'yours', otherId: 7 }])
  })
  it('names no champion for "Best counter" until the lock, then what was locked', () => {
    const best = { ...s, autoLockChampionId: -1 }
    expect(draftSteps(best, board({}))[1]).toEqual({ kind: 'lock', championId: -1, timing: 'LastSecond', state: 'pending' })
    expect(draftSteps(best, board({ championId: 7, locked: true }))[1]).toEqual({ kind: 'lock', championId: 7, timing: 'LastSecond', state: 'done' })
  })
  it('is empty outside draft', () => {
    expect(draftSteps(s, board({}, { mode: 'aram' }))).toEqual([])
  })
})

describe('champsRunIn', () => {
  it('runs in classic queues and when the queue is not known', () => {
    expect(champsRunIn('CLASSIC')).toBe(true)
    expect(champsRunIn(undefined)).toBe(true)
    expect(champsRunIn('ARAM')).toBe(false)
    expect(champsRunIn('KIWI')).toBe(false)
  })
})
