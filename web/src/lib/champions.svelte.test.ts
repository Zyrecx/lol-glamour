import { afterEach, describe, expect, it, vi } from 'vitest'
import { flushSync } from 'svelte'
import { champions } from './champions.svelte'

const tick = () => new Promise((r) => setTimeout(r, 0))

describe('champions.ensure', () => {
  afterEach(() => { vi.unstubAllGlobals(); vi.useRealTimers() })

  const list = (...ids: number[]) => vi.fn(async () => new Response(JSON.stringify({ champions: ids.map((id) => ({ id, name: `C${id}`, icon: '' })) })))

  it('does not retry in a loop when called from an effect and the host fails', async () => {
    const fetch = vi.fn(async () => new Response(JSON.stringify({ error: 'Champion list unavailable' }), { status: 503 }))
    vi.stubGlobal('fetch', fetch)
    const stop = $effect.root(() => { $effect(() => { champions.ensure() }) })
    for (let i = 0; i < 10; i++) { flushSync(); await tick() }
    stop()
    expect(fetch).toHaveBeenCalledTimes(1)
    expect(champions.error).toContain('unavailable')
  })

  it('asks again only once the list is half an hour old (the panel page lives as long as Glamour)', async () => {
    vi.useFakeTimers({ toFake: ['Date'] })
    const fetch = list(1)
    vi.stubGlobal('fetch', fetch)
    await champions.ensure()
    vi.advanceTimersByTime(29 * 60_000)
    await champions.ensure()
    expect(fetch).toHaveBeenCalledTimes(1)
    vi.advanceTimersByTime(2 * 60_000)
    await champions.ensure()
    expect(fetch).toHaveBeenCalledTimes(2)
  })

  it('asks again for a champion it does not know (new after a patch), at most once a minute', async () => {
    vi.useFakeTimers({ toFake: ['Date'] })
    vi.stubGlobal('fetch', list(1))
    await champions.ensure()
    const fetch = list(1, 999)
    vi.stubGlobal('fetch', fetch)
    vi.advanceTimersByTime(2 * 60_000)
    expect(champions.byId(999)).toBeUndefined()
    await tick(); await tick()
    expect(fetch).toHaveBeenCalledTimes(1)
    expect(champions.byId(999)?.name).toBe('C999')
    champions.byId(123); champions.byId(124) // still unknown: no second request within the minute
    await tick()
    expect(fetch).toHaveBeenCalledTimes(1)
  })
})
