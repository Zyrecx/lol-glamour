import { describe, expect, it } from 'vitest'
import type { Board, BoardPlayer, PlayableQueue, Settings } from '../lib/types'
import { art, automationChips, choosePosition, clock, headOf, laneIcon, movePin, ordinal, partySummary, shortcuts, statusOf } from './util'

const p = (cellId: number, over: Partial<BoardPlayer> = {}): BoardPlayer => ({
  cellId, championId: 0, locked: false, lane: '', spell1Id: 4, spell2Id: 14, skinId: 0, name: `P${cellId}`, isMe: false, pickTurn: 0, ...over,
})
const board = (over: Partial<Board> = {}): Board => ({
  stage: 'BAN_PICK', myCellId: 1, myTeam: [p(0, { pickTurn: 1 }), p(1, { isMe: true, pickTurn: 3 })], theirTeam: [p(5, { pickTurn: 2 })],
  myBans: [], theirBans: [], acting: [], next: [], pickTurnNow: 0, timeLeftMs: 20000, queueId: 400, mode: 'draft', bench: [], rerolls: 0,
  allowRerolling: false, swaps: [], ...over,
} as Board)

describe('shell helpers', () => {
  it('formats clocks and ordinals', () => {
    expect(clock(7_900)).toBe('0:07')
    expect(clock(754_000)).toBe('12:34')
    expect(clock(3_723_000)).toBe('1:02:03')
    expect(clock(-5)).toBe('0:00')
    expect([1, 2, 3, 4, 11, 12, 13, 21].map(ordinal)).toEqual(['1st', '2nd', '3rd', '4th', '11th', '12th', '13th', '21st'])
  })

  it('maps both lane vocabularies to one icon set', () => {
    expect(laneIcon('UTILITY')).toBe('SUPPORT')
    expect(laneIcon('SUPPORT')).toBe('SUPPORT')
    expect(laneIcon('UNSELECTED')).toBeNull()
  })

  it('fills art templates with the skin number', () => {
    const c = { id: 518, name: 'Neeko', icon: '', splash: 'https://x/splash/Neeko_{num}.jpg', portrait: null }
    expect(art(c, 518001, 'splash')).toBe('https://x/splash/Neeko_1.jpg')
    expect(art(c, 0, 'splash')).toBe('https://x/splash/Neeko_0.jpg')
    expect(art(c, 0, 'portrait')).toBe('')
  })

  it('summarises automation, per lane when asked', () => {
    const s = {
      autoAccept: 'Always', autoAcceptDelaySeconds: 2, autoBanChampionId: 238, autoLockChampionId: 518, lockTiming: 'LastSecond', preHoverChampionId: null,
      lanes: { SUPPORT: { banChampionId: 1, lockChampionId: null, hoverChampionId: 99, banTiming: 'Instant', lockTiming: 'Instant' } },
      autoHonorEnabled: true, autoHonorFriends: ['A#1', 'B#2'], skipScreens: ['missions-celebration'], skipHonorScreen: true,
    } as unknown as Settings
    const name = (id: number) => `#${id}`
    expect(automationChips(s, name)).toEqual(['Auto-accept · 2s', 'Ban #238', 'Lock #518 · last second', 'Per-lane setups', 'Honor 2 friends', 'Skip 2 screens'])
    expect(automationChips(s, name, 'SUPPORT')).toEqual(['Auto-accept · 2s', 'Ban #1', 'Hover #99', 'Honor 2 friends', 'Skip 2 screens'])
  })

  it('picks shortcuts from pins that are playable, else the first ones', () => {
    const q = (id: number) => ({ id, name: `Q${id}` }) as PlayableQueue
    const all = [q(420), q(400), q(450), q(2400)]
    expect(shortcuts(all, [2400, 999, 400]).map((x) => x.id)).toEqual([2400, 400])
    expect(shortcuts(all, []).map((x) => x.id)).toEqual([420, 400, 450])
    expect(shortcuts(all, [2400, 450, 400, 420]).map((x) => x.id)).toEqual([2400, 450, 400, 420]) // pins are not capped
  })

  it('moves a pin one place and stops at the ends', () => {
    expect(movePin([1, 2, 3], 2, -1)).toEqual([2, 1, 3])
    expect(movePin([1, 2, 3], 2, 1)).toEqual([1, 3, 2])
    expect(movePin([1, 2, 3], 1, -1)).toEqual([1, 2, 3])
    expect(movePin([1, 2, 3], 3, 1)).toEqual([1, 2, 3])
    expect(movePin([1, 2, 3], 9, 1)).toEqual([1, 2, 3])
  })

  it('shows no chip for the old Phone accept mode (it did the same as Off)', () => {
    const s = { autoAccept: 'Phone', autoAutoAcceptDelaySeconds: 0, lanes: {}, autoHonorEnabled: false, autoHonorFriends: [], skipScreens: [], skipHonorScreen: false } as unknown as Settings
    expect(automationChips(s, (id) => `#${id}`)).toEqual([])
  })

  it('marks who picks now and next, and how far my pick is', () => {
    const b = board({ acting: [{ cellId: 5, type: 'pick' }], next: [{ cellId: 1, type: 'pick' }], pickTurnNow: 2 })
    expect(statusOf(b, 5)).toBe('PICKING')
    expect(statusOf(b, 1)).toBe('NEXT')
    expect(statusOf(b, 0)).toBe('')
    expect(headOf(b, null, (x) => x.name)).toEqual({ label: 'An enemy is picking', sub: 'You pick in 1 turn.', mine: false })
    expect(headOf(b, 'pick', (x) => x.name).mine).toBe(true)
    expect(headOf(board({ acting: [{ cellId: 0, type: 'pick' }], pickTurnNow: 1 }), null, (x) => x.name).label).toBe('P0 is picking')
  })

  it('sums up parties in one line, mine first as the host sends them', () => {
    const pl = (isMe = false) => ({ puuid: '', championId: 0, name: '', isMe })
    expect(partySummary({ gameId: 1, parties: [] })).toBe('Everyone solo')
    expect(partySummary({ gameId: 1, parties: [{ ally: true, players: [pl(true), pl()] }, { ally: true, players: [pl(), pl()] }, { ally: false, players: [pl(), pl(), pl()] }] }))
      .toBe('Your duo · Ally duo · Enemy 3-stack')
  })

  it('names the mode instead of a picker in ARAM and Mayhem, where nobody picks', () => {
    expect(headOf(board({ mode: 'aram', acting: [] }), null, (x) => x.name).label).toBe('ARAM')
    expect(headOf(board({ mode: 'mayhem', acting: [] }), null, (x) => x.name).label).toBe('Mayhem')
  })

  it('swaps lobby lanes instead of clashing', () => {
    expect(choosePosition('UTILITY', 'MIDDLE', 'first', 'MIDDLE')).toEqual({ first: 'MIDDLE', second: 'UTILITY' })
    expect(choosePosition('UTILITY', 'MIDDLE', 'second', 'UTILITY')).toEqual({ first: 'MIDDLE', second: 'UTILITY' })
    expect(choosePosition('UTILITY', 'MIDDLE', 'first', 'TOP')).toEqual({ first: 'TOP', second: 'MIDDLE' })
    expect(choosePosition('UTILITY', 'MIDDLE', 'first', 'FILL')).toEqual({ first: 'FILL', second: 'FILL' })
    expect(choosePosition('FILL', 'FILL', 'first', 'TOP')).toEqual({ first: 'TOP', second: 'UNSELECTED' })
    expect(choosePosition('UNSELECTED', 'UNSELECTED', 'second', 'JUNGLE')).toEqual({ first: 'UNSELECTED', second: 'JUNGLE' })
  })
})
