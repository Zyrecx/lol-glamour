/// <reference types="node" />
import { readdirSync, readFileSync } from 'node:fs'
import { join } from 'node:path'
import { expect, it } from 'vitest'
import { EVENTS } from './now'

// Every `Publish("name"` in the host's C# source is an event on /api/events.
function published(): Set<string> {
  const names = new Set<string>()
  for (const dir of ['Glamour.Core', 'Glamour.Host']) {
    const root = join(import.meta.dirname, '../../../src', dir)
    for (const f of readdirSync(root, { recursive: true }) as string[]) {
      if (!f.endsWith('.cs') || /(^|[\\/])(bin|obj)[\\/]/.test(f)) continue
      for (const m of readFileSync(join(root, f), 'utf8').matchAll(/\bPublish\("([a-z-]+)"/g)) names.add(m[1])
    }
  }
  return names
}

it('listens for every event the host publishes', () => {
  const host = published()
  expect(host.size).toBeGreaterThan(10) // the host source was found
  expect([...host].filter((e) => !EVENTS.includes(e))).toEqual([])
})
