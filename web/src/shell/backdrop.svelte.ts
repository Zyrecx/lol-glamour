import { getJson } from '../lib/api'

const KEY = 'glamour.backdrop'

/** The skin set as the profile background in the League client (`GET /api/me/backdrop`), remembered so the page has it with the client closed. */
class Backdrop {
  skinNum = $state(0)
  championId = $state(0)
  #connected = false
  #loaded = false

  constructor() {
    try {
      const saved = JSON.parse(localStorage.getItem(KEY) ?? 'null')
      if (saved?.championId > 0) { this.championId = saved.championId; this.skinNum = saved.skinNum ?? 0 }
    } catch { /* private window or blocked storage: start empty */ }
  }

  async ensure(connected: boolean) {
    if (!connected) { this.#loaded = false; return }
    if (this.#loaded) return
    this.#loaded = true
    try {
      const r = await getJson<{ championId: number; skinNum: number }>('/me/backdrop')
      if (r.championId > 0) {
        this.championId = r.championId
        this.skinNum = r.skinNum
        try { localStorage.setItem(KEY, JSON.stringify({ championId: r.championId, skinNum: r.skinNum })) } catch { /* storage is a convenience only */ }
      }
    } catch { this.#loaded = false }
  }
}

export const backdrop = new Backdrop()
