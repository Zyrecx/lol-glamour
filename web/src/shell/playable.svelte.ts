import { getJson } from '../lib/api'
import type { PlayableQueue } from '../lib/types'

/** The queues open right now, from the client through the host (`GET /api/queues/playable`). Loaded once per connection. */
class Playable {
  list = $state<PlayableQueue[]>([])
  /** Every queue the client lists (advanced), loaded on demand; some the client will refuse to start. */
  all = $state<PlayableQueue[]>([])
  error = $state('')
  #loaded = false
  #connected = false
  #retry: ReturnType<typeof setTimeout> | undefined

  async ensure(connected: boolean) {
    this.#connected = connected
    if (!connected) { this.#loaded = false; clearTimeout(this.#retry); return }
    if (this.#loaded) return
    this.#loaded = true
    try {
      this.list = await getJson<PlayableQueue[]>('/queues/playable')
      this.error = ''
    } catch (e) {
      this.#loaded = false
      this.error = e instanceof Error ? e.message : 'Could not load the modes.'
      // The first call can land while the client is still starting up; nothing else re-runs ensure while connected stays true.
      clearTimeout(this.#retry)
      this.#retry = setTimeout(() => this.ensure(this.#connected), 3000)
    }
  }

  async loadAll() {
    try {
      this.all = await getJson<PlayableQueue[]>('/queues/playable?all=true')
      this.error = ''
    } catch (e) {
      this.error = e instanceof Error ? e.message : 'Could not load the modes.'
    }
  }

  byId(id: number): PlayableQueue | undefined {
    return this.list.find((q) => q.id === id) ?? this.all.find((q) => q.id === id)
  }
}

export const playable = new Playable()
