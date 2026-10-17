import { getJson } from './api'

/** Names for the common queues, used until (or if never) the client's list loads, so no screen says "Queue 420". */
const KNOWN: Record<number, string> = {
  400: 'Normal Draft', 420: 'Ranked Solo/Duo', 430: 'Normal Blind', 440: 'Ranked Flex', 450: 'ARAM', 490: 'Quickplay', 700: 'Clash', 1700: 'Arena', 2400: 'ARAM Mayhem',
}

/** Queue names for display ("Ranked Flex", "Normal Draft", ...), loaded once per connection from the client's own queue list. */
class QueueNames {
  names = $state<Record<number, string>>({})
  modes = $state<Record<number, string>>({})
  #tried = false

  async ensure() {
    if (this.#tried) return
    this.#tried = true
    try {
      const all = await getJson<{ id: number; name?: string; description?: string; gameMode?: string }[]>('/queues')
      this.modes = Object.fromEntries(all.filter((q) => q.gameMode).map((q) => [q.id, q.gameMode!]))
      this.names = Object.fromEntries(all.filter((q) => q.name || q.description).map((q) => [q.id, (q.name || q.description)!]))
    } catch { this.#tried = false } // the client may not be up yet; try again on the next ask
  }

  /** The queue's game mode (CLASSIC, ARAM, ...), empty when unknown. */
  modeOf(id: number): string {
    return this.modes[id] ?? ''
  }

  label(id: number): string {
    return this.names[id] ?? KNOWN[id] ?? (id ? `Queue ${id}` : '')
  }
}

export const queueNames = new QueueNames()
