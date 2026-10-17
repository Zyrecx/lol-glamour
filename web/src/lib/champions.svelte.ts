import { getJson } from './api'
import { BEST_COUNTER, type Champion } from './types'

const KEY = 'glamour.champions'

// "Best counter" stands in a lock slot like a champion, so every tile, name and plan line shows it without a case of its own.
const TARGET = `<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 48 48'><rect width='48' height='48' fill='#1E2328'/><circle cx='24' cy='24' r='12' fill='none' stroke='#C8AA6E' stroke-width='3'/><circle cx='24' cy='24' r='4' fill='#C8AA6E'/><path d='M24 5v10M24 33v10M5 24h10M33 24h10' stroke='#C8AA6E' stroke-width='3'/></svg>`
export const bestCounter: Champion = { id: BEST_COUNTER, name: 'Best counter', icon: `data:image/svg+xml,${encodeURIComponent(TARGET)}` }

/** The last list that loaded, so names resolve on the first paint instead of showing an id until the request returns. */
function cached(): Champion[] {
  try {
    const v = JSON.parse(localStorage.getItem(KEY) ?? '[]')
    return Array.isArray(v) ? v : []
  } catch { return [] }
}

/** The panel's page lives as long as Glamour, so the list is asked again when this old (the host answers from memory and checks the patch itself). */
const MAX_AGE_MS = 30 * 60_000

/** Champion list for the ban/lock pickers and the names in chips. Seeded from the last session, refreshed when old or when an unknown id shows up, retried after a failure. */
class Champions {
  list = $state<Champion[]>(cached())
  error = $state('')
  loading = $state(false)
  #loadedAt = 0
  #missAt = 0
  // A plain field, not `loading`: ensure() is called from effects, and reading state here would make a failed load re-run the
  // effect and retry at once, forever.
  #busy = false

  async ensure() {
    if (this.#busy || (this.#loadedAt && Date.now() - this.#loadedAt < MAX_AGE_MS)) return
    this.#busy = true
    this.loading = true
    try {
      const r = await getJson<{ champions: Champion[] }>('/champions')
      this.list = r.champions
      this.#loadedAt = Date.now()
      this.error = ''
      try { localStorage.setItem(KEY, JSON.stringify(r.champions)) } catch { /* storage may be blocked */ }
    } catch (e) {
      this.error = e instanceof Error ? e.message : 'Could not load champions.'
    } finally {
      this.#busy = false
      this.loading = false
    }
  }

  byId(id: number | null): Champion | undefined {
    if (id == null) return undefined
    if (id === BEST_COUNTER) return bestCounter
    const c = this.list.find((c) => c.id === id)
    if (!c && id > 0) this.#missed()
    return c
  }

  /** A champion this list does not know (a new one, after a patch): ask again, at most once a minute. Out of the caller's render, since that may be a derived. */
  #missed() {
    if (this.#busy || Date.now() - this.#missAt < 60_000) return
    this.#missAt = Date.now()
    this.#loadedAt = 0
    queueMicrotask(() => void this.ensure())
  }
}

export const champions = new Champions()
