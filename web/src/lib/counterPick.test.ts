import { describe, expect, it } from 'vitest'
import { counterKey, counterRows, counterSorts, counterUrl, damageShares, enemyAtSlot, gamesLabel, opponentChance, percent, waitingNote, type CounterPickView } from './counterPick'
import type { Board } from './types'

const player = (o: object) => ({ cellId: 0, championId: 0, locked: false, lane: '', spell1Id: 0, spell2Id: 0, skinId: 0, name: '', isMe: false, ...o })
const board = (o: object = {}): Board => ({ stage: 'BAN_PICK', myCellId: 0, queueId: 400, mode: 'draft', myTeam: [player({ isMe: true, lane: 'MIDDLE' })], theirTeam: [player({ championId: 238 }), player({ championId: 0 }), player({ championId: 11 })], myBans: [], theirBans: [], acting: [], timeLeftMs: 1, ...o } as unknown as Board)

describe('counterKey', () => {
  it('is my lane and champion plus the picked enemy champions, sorted, plus what is taken', () => expect(counterKey(board({ myBans: [5] }))).toBe('MIDDLE:0:11,238:5:'))
  it("changes with a teammate's hover (my team's damage follows it)", () =>
    expect(counterKey(board({ myTeam: [player({ isMe: true, lane: 'MIDDLE' }), player({ championId: 5 })] }))).not.toBe(counterKey(board())))
  it('does not change with the timer', () => expect(counterKey(board({ timeLeftMs: 5 }))).toBe(counterKey(board())))
  it('changes with my hover and my lock (my own matchups follow them)', () => {
    const hovered = counterKey(board({ myTeam: [player({ isMe: true, lane: 'MIDDLE', championId: 103 })] }))
    expect(hovered).not.toBe(counterKey(board()))
    expect(counterKey(board({ myTeam: [player({ isMe: true, lane: 'MIDDLE', championId: 103, locked: true })] }))).not.toBe(hovered)
  })
  it('is empty with nothing to ask: no board, not draft, or no lane', () => {
    expect(counterKey(null)).toBe('')
    expect(counterKey(board({ mode: 'aram' }))).toBe('')
    expect(counterKey(board({ myTeam: [player({ isMe: true })] }))).toBe('')
  })
})

it('formats a win rate', () => expect(percent(58.436)).toBe('58.4%'))

describe('counterUrl', () => {
  it('asks for the host guess unless an opponent was picked', () => {
    expect(counterUrl(null)).toBe('/counter-pick')
    expect(counterUrl(238)).toBe('/counter-pick?enemy=238')
  })
})

describe('counterKey with bans and locks', () => {
  it('changes when a ban or a lock-in happens', () => {
    const base = counterKey(board())
    expect(counterKey(board({ theirBans: [11] }))).not.toBe(base)
    expect(counterKey(board({ myTeam: [player({ isMe: true, lane: 'MIDDLE' }), player({ championId: 5, locked: true })] }))).not.toBe(base)
  })
})

describe('enemyAtSlot', () => {
  const b = board({ theirTeam: [player({ cellId: 7, championId: 11 }), player({ cellId: 5, championId: 238 }), player({ cellId: 6, championId: 0 })] })
  it('counts the client roster top to bottom by cell', () => {
    expect(enemyAtSlot(b, 0)).toBe(238)
    expect(enemyAtSlot(b, 2)).toBe(11)
  })
  it('is null for a player with no champion yet or a card past the team', () => {
    expect(enemyAtSlot(b, 1)).toBeNull()
    expect(enemyAtSlot(b, 4)).toBeNull()
  })
})

describe('gamesLabel', () => {
  it('names the previous patch when it lent games', () => {
    expect(gamesLabel({ games: 1000, fewGames: false })).toEqual({ text: '1,000 games', title: '' })
    expect(gamesLabel({ games: 1000, fewGames: false, olderGames: 800, olderPatch: '16.19' })).toEqual({ text: '1,000 games · incl. 16.19', title: '800 of 1,000 games from patch 16.19' })
    expect(gamesLabel({ games: 120, fewGames: true, olderGames: 120, olderPatch: '16.19' })).toEqual({ text: 'few games · 16.19', title: 'All 120 games from patch 16.19' })
  })
})

describe('lane and team lists', () => {
  const base: CounterPickView = { state: 'ready', lane: 'MIDDLE', enemyId: 238, enemies: [238, 64], forced: false, autoLock: 2,
    picks: [{ championId: 1, winRate: 55.5, games: 2000, fewGames: false, inPool: false, team: 52.1 }, { championId: 2, winRate: 53, games: 1200, fewGames: false, inPool: true }],
    teamPicks: [{ championId: 3, winRate: 54, covered: 2, games: 4000, inPool: false, lane: 51 }, { championId: 4, winRate: 52, covered: 1, games: 120, inPool: false }] }

  it('offers lane only with an opponent, team with any enemy', () => {
    expect(counterSorts(base)).toEqual(['lane', 'team'])
    expect(counterSorts({ ...base, state: 'waiting', picks: null })).toEqual(['team'])
    expect(counterSorts({ ...base, teamPicks: null })).toEqual(['lane'])
    expect(counterSorts(null)).toEqual([])
  })

  it('sorts by the chosen number and shows the other beside it', () => {
    const lane = counterRows(base, 'lane')!
    expect(lane.rows.map((r) => [r.championId, r.main, r.alt, r.auto])).toEqual([[1, 55.5, 52.1, false], [2, 53, null, true]])
    const team = counterRows(base, 'team')!
    expect(team.rows[0]).toMatchObject({ championId: 3, main: 54, alt: 51, altLabel: 'lane' })
    expect(team.rows[0].sub.text).toBe('4,000 games · vs 2 of 2')
    expect(team.rows[1].sub.text).toBe('few games · vs 1 of 2')
    expect(counterRows({ ...base, state: 'waiting', picks: null }, 'lane')!.sort).toBe('team') // no opponent yet: the team list
  })

  it('marks the rows that do best with my bot lane partner, in both lists', () => {
    const duo = { ...base, duo: { partnerId: 222, best: [2, 3] } }
    expect(counterRows(duo, 'lane')!.rows.map((r) => r.duo)).toEqual([false, true])
    expect(counterRows(duo, 'team')!.rows.map((r) => r.duo)).toEqual([true, false])
    expect(counterRows(base, 'lane')!.rows.every((r) => !r.duo)).toBe(true)
  })

  it('turns damage into percents that add up to 100', () => {
    expect(damageShares({ physical: 25644, magic: 1017, true: 2725 })).toEqual({ physical: 88, magic: 3, true: 9 })
    expect(damageShares({ physical: 0, magic: 0, true: 0 })).toBeNull()
    expect(damageShares(null)).toBeNull()
  })
})

describe('how sure the lane guess is', () => {
  const view: CounterPickView = { state: 'waiting', lane: 'MIDDLE', enemyId: 0, enemies: [950, 64, 11], forced: false, picks: null, chances: { '950': 28, '64': 9, '11': 41 } }
  const name = (id: number) => `#${id}`

  it('names who it might be while unsure, likeliest first, and nobody under 15%', () =>
    expect(waitingNote(view, 'mid', name)).toBe('Not sure who plays mid: #11 41%, #950 28%.'))
  it('says nobody fits yet without candidates', () => expect(waitingNote({ ...view, chances: { '64': 9 } }, 'mid', name)).toBe('No mid laner picked yet.'))
  it('shows the chance of a guessed opponent under 90%, never of one picked by hand', () => {
    const ready = { ...view, state: 'ready' as const, enemyId: 11, chances: { '11': 72 } }
    expect(opponentChance(ready)).toBe(72)
    expect(opponentChance({ ...ready, chances: { '11': 95 } })).toBeNull()
    expect(opponentChance({ ...ready, forced: true })).toBeNull()
  })
})
