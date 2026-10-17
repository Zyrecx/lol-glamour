import { describe, expect, it } from 'vitest'
import { overriddenKeys, setSharedKeys, splitChange, splitInEffect, viewOf } from './scope'
import type { Settings } from './types'

setSharedKeys(['ntfyEnabled', 'ntfyServer', 'ntfyTopic', 'dryRun', 'automationEnabled']) // what the host sends in settingsMeta

const defaults = { autoAccept: 'Always', autoBanChampionId: 238, dryRun: false, ntfyEnabled: true } as Settings

describe('viewOf', () => {
  it('lays overrides over the defaults and keeps the rest', () => {
    const v = viewOf(defaults, { autoAccept: 'Phone' })!
    expect(v.autoAccept).toBe('Phone')
    expect(v.autoBanChampionId).toBe(238)
  })
  it('treats null as a real override (auto-ban off), not as inherit', () => {
    expect(viewOf(defaults, { autoBanChampionId: null })!.autoBanChampionId).toBeNull()
  })
  it('ignores shared keys and tolerates no overrides', () => {
    expect(viewOf(defaults, { dryRun: true })!.dryRun).toBe(false)
    expect(viewOf(defaults, null)).toEqual(defaults)
    expect(viewOf(null, { autoAccept: 'Off' })).toBeNull()
  })
})

describe('splitChange', () => {
  it('sends device settings to the defaults and the rest to the account', () => {
    expect(splitChange({ dryRun: true, autoAccept: 'Off' })).toEqual({ shared: { dryRun: true }, own: { autoAccept: 'Off' } })
  })
})

describe('splitInEffect', () => {
  it('sends what the signed-in account overrides to it, the rest and shared keys to the defaults', () => {
    const own = { autoBanChampionId: null, dryRun: true } as Partial<Settings>
    expect(splitInEffect({ autoBanChampionId: 11, autoAccept: 'Off', dryRun: true }, own))
      .toEqual({ shared: { autoAccept: 'Off', dryRun: true }, own: { autoBanChampionId: 11 } })
  })
  it('sends everything to the defaults with no account or no overrides', () => {
    expect(splitInEffect({ autoAccept: 'Off' }, null)).toEqual({ shared: { autoAccept: 'Off' }, own: {} })
  })
})

describe('overriddenKeys', () => {
  it('lists the account fields only', () => {
    expect(overriddenKeys({ autoAccept: 'Off', autoBanChampionId: null })).toEqual(['autoAccept', 'autoBanChampionId'])
    expect(overriddenKeys(null)).toEqual([])
  })
})
