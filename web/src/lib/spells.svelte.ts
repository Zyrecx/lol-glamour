import { getJson } from './api'
import type { Spell } from './types'

/** Summoner spell names and icons (Data Dragon, via the host). Loaded once, on first use. */
class Spells {
  list = $state<Spell[]>([])
  error = $state('')
  #loading = false

  async ensure() {
    if (this.list.length > 0 || this.#loading) return
    this.#loading = true
    try {
      this.list = (await getJson<{ spells: Spell[] }>('/spells')).spells
      this.error = ''
    } catch (e) {
      this.error = e instanceof Error ? e.message : 'Could not load summoner spells.'
    } finally {
      this.#loading = false
    }
  }

  byId(id: number): Spell | undefined {
    return this.list.find((s) => s.id === id)
  }
}

export const spells = new Spells()

/** Spells that can be taken in a game mode. Modes the list does not know (a new mode) get every spell rather than none. */
export function spellsForMode(all: Spell[], gameMode: string): Spell[] {
  const fit = all.filter((s) => s.modes.includes(gameMode))
  return fit.length >= 4 ? fit : all
}
