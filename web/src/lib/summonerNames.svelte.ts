import { getJson } from './api'

/** Riot IDs of lobby members by summonerId: the lobby payload has no readable name, so each one is asked for once and kept. */
class SummonerNames {
  byId = $state<Record<number, string>>({})
  #asked = new Set<number>()

  ensure(ids: number[]) {
    for (const id of ids) {
      if (!id || this.#asked.has(id)) continue
      this.#asked.add(id)
      getJson<{ name: string }>(`/summoner/${id}`).then((r) => (this.byId[id] = r.name)).catch(() => this.#asked.delete(id))
    }
  }
}

export const summonerNames = new SummonerNames()
