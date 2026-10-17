import type { Settings } from './types'

/** Device settings and the global switch: the same for every account, never overridden. The host sends the list (`settingsMeta.sharedKeys` in the snapshot). */
let shared: string[] = []
export const setSharedKeys = (keys: string[]) => { shared = keys }
export const isShared = (key: string) => shared.includes(key)

/** The settings an account runs on: the shared defaults with its overrides laid over them (whole top-level fields, null included). */
export function viewOf(defaults: Settings | null, overrides: Partial<Settings> | null | undefined): Settings | null {
  if (!defaults) return null
  const own = Object.fromEntries(Object.entries(overrides ?? {}).filter(([k]) => !isShared(k)))
  return { ...defaults, ...own }
}

/** Splits a change into what goes to the shared defaults and what is an override for the account being edited. */
export function splitChange(change: Partial<Settings>): { shared: Partial<Settings>; own: Partial<Settings> } {
  const entries = Object.entries(change)
  return {
    shared: Object.fromEntries(entries.filter(([k]) => isShared(k))) as Partial<Settings>,
    own: Object.fromEntries(entries.filter(([k]) => !isShared(k))) as Partial<Settings>,
  }
}

/**
 * Splits a change to the settings in effect: a field the signed-in account overrides goes to its overrides, everything else
 * (shared keys included) to the shared defaults. With no saved account signed in, all of it goes to the defaults.
 */
export function splitInEffect(change: Partial<Settings>, overrides: Partial<Settings> | null | undefined): { shared: Partial<Settings>; own: Partial<Settings> } {
  const mine = new Set(overriddenKeys(overrides))
  const entries = Object.entries(change)
  return {
    shared: Object.fromEntries(entries.filter(([k]) => !mine.has(k))) as Partial<Settings>,
    own: Object.fromEntries(entries.filter(([k]) => mine.has(k))) as Partial<Settings>,
  }
}

/** Plain names for the override list. A key missing here is shown as is. */
export const SETTING_LABELS: Record<string, string> = {
  autoAccept: 'Auto accept', autoBanChampionId: 'Auto ban', banTiming: 'Ban timing', skipBanIfAllyHovered: 'Skip ban if an ally hovers it', matchupLoadout: 'Matchup loadout', mayhemWinRates: 'Mayhem win rates', augmentTips: 'Augment tags', loadingParties: 'Loading-screen premades',
  autoLockChampionId: 'Auto lock', lockTiming: 'Lock timing', preHoverChampionId: 'Pre-hover', lastSecondMarginSeconds: 'Last-second margin',
  laneSwapAccept: 'Accept lane swaps', laneSwapAsk: 'Ask for my lane', pickSlotAccept: 'Accept pick-order swaps', pickSlotAsk: 'Ask for my pick slot', pickSlot: 'Pick slot',
  autoHonorEnabled: 'Auto honor', autoHonorFriends: 'Honor friends', skipHonorScreen: 'Skip honor screen', honorStay: 'Stay after honoring', skipScreens: 'Skipped screens',
  rankOverrideEnabled: 'Rank override', rankTier: 'Rank tier', rankDivision: 'Rank division', rankQueue: 'Rank queue',
  statusMessageOverride: 'Status message', lanes: 'Per-lane ban and lock', championPools: 'Champion pool',
  autoAcceptDelaySeconds: 'Accept delay', acceptTiming: 'Accept timing', actionDelaySeconds: 'Ban and lock delay',
}

export const overriddenKeys = (overrides: Partial<Settings> | null | undefined): string[] => Object.keys(overrides ?? {}).filter((k) => !isShared(k))
