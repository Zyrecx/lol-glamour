import { afterEach, beforeEach, expect, it, vi } from 'vitest'

// A stand-in for the browser's EventSource: the tests drive open, error and close by hand.
class FakeSource {
  static CLOSED = 2
  static all: FakeSource[] = []
  readyState = 0
  closed = false
  onopen: (() => void) | null = null
  onerror: (() => void) | null = null
  listeners = new Map<string, (e: { data: string }) => void>()
  constructor(public url: string) { FakeSource.all.push(this) }
  addEventListener(type: string, fn: (e: { data: string }) => void) { this.listeners.set(type, fn) }
  close() { this.closed = true; this.readyState = 2 }
  emit(type: string, data: unknown) { this.listeners.get(type)?.({ data: JSON.stringify({ type, data }) }) }
}

let replaced: string[]
let authenticated: boolean

beforeEach(() => {
  vi.useFakeTimers()
  vi.resetModules()
  FakeSource.all = []
  replaced = []
  authenticated = true
  vi.stubGlobal('EventSource', FakeSource)
  vi.stubGlobal('location', { search: '', replace: (u: string) => replaced.push(u) })
  vi.stubGlobal('document', { visibilityState: 'visible', addEventListener: () => {} })
  vi.stubGlobal('fetch', vi.fn(async () => new Response(JSON.stringify({ authenticated }), { status: 200 })))
})
afterEach(() => { vi.useRealTimers(); vi.unstubAllGlobals() })

async function started() {
  const { live } = await import('./live.svelte')
  live.start()
  return live
}

it('opens a new stream after the browser gives up on one', async () => {
  const live = await started()
  const first = FakeSource.all[0]
  first.readyState = FakeSource.CLOSED
  first.onerror!()
  expect(live.linkLost).toBe(true)
  await vi.advanceTimersByTimeAsync(1000)
  expect(FakeSource.all).toHaveLength(2)
  expect(first.closed).toBe(true)
  FakeSource.all[1].onopen!()
  expect(live.linkLost).toBe(false)
})

it('leaves retrying to the browser while the stream is only reconnecting', async () => {
  await started()
  FakeSource.all[0].onerror!() // readyState CONNECTING: EventSource retries by itself
  await vi.advanceTimersByTimeAsync(60_000)
  expect(FakeSource.all).toHaveLength(1)
})

it('goes to the sign-in page when this browser was signed out', async () => {
  await started()
  authenticated = false
  const first = FakeSource.all[0]
  first.readyState = FakeSource.CLOSED
  first.onerror!()
  await vi.advanceTimersByTimeAsync(1000)
  expect(replaced).toEqual(['/login.html'])
  expect(FakeSource.all).toHaveLength(1)
})

it('keeps going after a message that is not JSON', async () => {
  const live = await started()
  const es = FakeSource.all[0]
  es.listeners.get('phase')!({ data: 'not json' })
  es.emit('phase', { phase: 'Lobby' })
  expect(live.now.phase).toBe('Lobby')
})

it('takes the parties event', async () => {
  const live = await started()
  FakeSource.all[0].emit('parties', { gameId: 7, parties: [] })
  expect(live.now.parties?.gameId).toBe(7)
})
