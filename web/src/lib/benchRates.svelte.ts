import { getJson } from './api'
import { live } from './live.svelte'
import { benchMode, demoRates, type ModeRates } from './benchAdvice'
import type { Board } from './types'

// One list per mode for everyone (the panel and the phone sheet ask at once); kept for half an hour, the host caches it for a day.
const cache = new Map<string, { at: number; rates: Promise<ModeRates | null> }>()
const KEEP_MS = 30 * 60_000

function load(mode: string, key: string): Promise<ModeRates | null> {
  const hit = cache.get(key)
  if (hit && Date.now() - hit.at < KEEP_MS) return hit.rates
  const rates = getJson<ModeRates>(`/winrates/${mode}`).catch(() => { cache.delete(key); return null })
  cache.set(key, { at: Date.now(), rates })
  return rates
}

/** The bench numbers for the live board: asked once the board is an ARAM or Mayhem one, null before that, while loading or when the host has none. */
export function benchRates(board: () => Board | null, demo: boolean) {
  let rates = $state<ModeRates | null>(null)
  let loaded = $state(false)
  const mode = $derived(benchMode(board()))
  // The Mayhem source follows a setting, so a change of it asks again.
  const key = $derived(mode === 'mayhem' ? `${mode}|${live.settings?.mayhemWinRates ?? ''}` : mode)
  $effect(() => {
    const m = mode
    const k = key ?? ''
    rates = null
    loaded = false
    if (!m) return
    if (demo) { const b = board(); if (b) rates = demoRates(b, new URLSearchParams(location.search).has('tiers')); loaded = true; return }
    let stale = false
    load(m, k).then((r) => { if (!stale) { rates = r; loaded = true } })
    return () => { stale = true }
  })
  return {
    get mode() { return mode },
    get rates() { return rates },
    get loaded() { return loaded },
  }
}
