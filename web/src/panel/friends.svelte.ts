import { getJson } from '../lib/api'

export interface Friend { riotId: string; puuid: string; online: boolean }

/** The League friend list (`GET /api/friends`), loaded on demand and again at most once a minute. */
class Friends {
  list = $state<Friend[]>([])
  loaded = $state(false)
  error = $state('')
  #at = 0

  async load() {
    if (Date.now() - this.#at < 60_000) return
    this.#at = Date.now()
    try {
      this.list = await getJson<Friend[]>('/friends')
      this.error = ''
    } catch {
      this.error = 'Friends need the League client.'
      this.#at = 0
    }
    this.loaded = true
  }

  /** True when this Riot ID is a League friend (auto-honor only reaches friends). */
  has(riotId: string) {
    const k = riotId.trim().toLowerCase()
    return this.list.some((f) => f.riotId.toLowerCase() === k)
  }
}

export const friends = new Friends()
