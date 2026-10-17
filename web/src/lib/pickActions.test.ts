import { describe, expect, it } from 'vitest'
import { canLock, pickSlot, takenMap } from './pickActions'
import type { Board } from './types'

const board = (over: Partial<Board> = {}) => ({
  stage: 'BAN_PICK', myTeam: [], theirTeam: [], myBans: [], theirBans: [], myPickActionId: 0, ...over,
}) as unknown as Board

describe('pick actions', () => {
  it('uses my live turn, else my own unfinished pick, else nothing', () => {
    expect(pickSlot({ actionId: 7, type: 'pick', stage: 'BAN_PICK' }, board())).toMatchObject({ actionId: 7 })
    expect(pickSlot(null, board({ myPickActionId: 4 }))).toEqual({ actionId: 4, type: 'pick' })
    expect(pickSlot(null, board())).toBeNull()
  })
  it('locks only on a live pick turn in BAN_PICK', () => {
    const turn = { actionId: 7, type: 'pick', stage: 'BAN_PICK' }
    expect(canLock(board(), turn)).toBe(true)
    expect(canLock(board({ stage: 'PLANNING' }), turn)).toBe(false)
    expect(canLock(board(), null)).toBe(false)
    expect(canLock(board(), { ...turn, type: 'ban' })).toBe(false)
  })
  it('marks banned and locked champions', () => {
    const m = takenMap(board({ myBans: [1], theirBans: [0], theirTeam: [{ championId: 2, locked: true }, { championId: 3, locked: false }] as never }))
    expect([...m]).toEqual([[1, 'Banned'], [2, 'Picked']])
  })
})
