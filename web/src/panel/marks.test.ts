import { describe, expect, it } from 'vitest'
import { bandPath, cardTags, marks, onMarksMessage, slotOf } from './marks.svelte'

describe('marks message', () => {
  it('turns the marks on with the portrait rows and off again', () => {
    expect(onMarksMessage('marks:134.5,80')).toBe(true)
    expect({ ...marks }).toEqual({ on: true, y0: 134.5, step: 80 })
    expect(onMarksMessage('marks:off')).toBe(true)
    expect(marks.on).toBe(false)
  })

  it('leaves other messages to others', () => {
    expect(onMarksMessage('face:2')).toBe(false)
    expect(onMarksMessage(42)).toBe(false)
  })
})

describe('cardTags', () => {
  const team = [{ cellId: 7, championId: 64 }, { cellId: 5, championId: 238 }, { cellId: 6, championId: 0 }, { cellId: 8, championId: 777 }, { cellId: 9, championId: 0 }]
  it('puts my win rate on the listed enemies by card order', () => {
    const mine = { picked: true, lines: [{ championId: 238, winRate: 47.64, fewGames: false }, { championId: 64, winRate: 55, fewGames: true }] }
    expect(cardTags(team, mine)).toBe('tags:47.6,,55.0~,,')
  })
  it('sends empty tags before any enemy pick or without a hover', () => {
    expect(cardTags(team, { picked: false, lines: [{ championId: 238, winRate: 40, fewGames: false }] })).toBe('tags:,,,,')
    expect(cardTags(team, null)).toBe('tags:,,,,')
  })
})

describe('bandPath', () => {
  const list = { left: 40, top: 110, bottom: 400 }

  it('is a straight line when the ring is level with the list', () => {
    expect(bandPath(214.5, list)).toEqual({ link: 'M 0 214.5 H 40', entry: 214.5 })
  })

  it('bends into the list when the ring is above or below it', () => {
    expect(bandPath(60, list).entry).toBe(124)
    expect(bandPath(455, list)).toEqual({ link: 'M 0 455 C 20 455 20 386 40 386', entry: 386 })
  })

  it('enters the middle of a list too short for the inset', () => {
    expect(bandPath(0, { left: 40, top: 100, bottom: 110 }).entry).toBe(105)
  })
})

describe('slotOf', () => {
  const team = [{ cellId: 7, championId: 99 }, { cellId: 5, championId: 4 }, { cellId: 6, championId: 0 }]

  it('counts by cell, as the client lists them', () => {
    expect(slotOf(team, 4)).toBe(0)
    expect(slotOf(team, 99)).toBe(2)
  })

  it('is -1 for no champion or one not on their team', () => {
    expect(slotOf(team, 0)).toBe(-1)
    expect(slotOf(team, 12)).toBe(-1)
  })
})
