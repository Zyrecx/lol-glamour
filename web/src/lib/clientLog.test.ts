import { afterEach, expect, test, vi } from 'vitest'
import { installClientLog } from './clientLog'

afterEach(() => vi.unstubAllGlobals())

test('script errors and unhandled rejections go to the PC log, the sign-in redirect does not, and a loop is capped', () => {
  const fetch = vi.fn(() => Promise.resolve(new Response(null, { status: 204 })))
  vi.stubGlobal('fetch', fetch)
  vi.stubGlobal('location', { pathname: '/', search: '?view=phone' })
  const target = new EventTarget() as unknown as Window
  installClientLog(target)

  target.dispatchEvent(Object.assign(new Event('error'), { message: 'boom', error: new Error('boom') }))
  target.dispatchEvent(Object.assign(new Event('unhandledrejection'), { reason: new Error('unauthorized') }))
  target.dispatchEvent(Object.assign(new Event('unhandledrejection'), { reason: 'nope' }))

  expect(fetch).toHaveBeenCalledTimes(2)
  const [url, init] = fetch.mock.calls[0] as unknown as [string, RequestInit]
  expect(url).toBe('/api/client-log')
  expect(JSON.parse(init.body as string)).toMatchObject({ kind: 'error', message: 'boom' })
  expect(JSON.parse((fetch.mock.calls[1] as unknown as [string, RequestInit])[1].body as string)).toMatchObject({ kind: 'unhandled promise', message: 'nope' })

  for (let i = 0; i < 30; i++) target.dispatchEvent(Object.assign(new Event('error'), { message: 'loop' }))
  expect(fetch).toHaveBeenCalledTimes(20)
})
