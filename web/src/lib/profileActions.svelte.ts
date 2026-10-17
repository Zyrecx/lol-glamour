import { api } from './api'
import { live } from './live.svelte'
import { label } from './profileLabel'
import type { Profile } from './types'

type Result = { ok: boolean; code?: string; error: string; willClose?: string[] }

/** What the Accounts screens (phone list, PC page) do with profiles: every call, its progress text, errors and the close-the-client confirm. */
export class ProfileActions {
  busy = $state('')
  err = $state('')
  /** The host asked first: this closes the Riot Client and these games. */
  confirmClose = $state<{ run: () => Promise<unknown>; willClose: string[] } | null>(null)
  saveAs = $state('new')
  removing = $state<string | null>(null)

  get snap() { return live.profiles }
  get list() { return this.snap?.profiles ?? [] }
  /** The signed-in account is not saved yet: nothing could switch back to it. */
  get unsaved() { const s = this.snap; return !!s && s.signedIn && !s.adding && !this.list.some((p) => p.id === s.activeId) }
  get needSignIn() { return this.list.filter((p) => p.state === 'needs-sign-in') }

  async #call(path: string, method = 'POST', body?: unknown): Promise<Result> {
    try {
      const r = await api('/profiles' + path, method, body)
      const j = await r.json().catch(() => null)
      if (r.ok) { live.profiles = j; return { ok: true, error: '' } }
      return { ok: false, code: j?.code, error: j?.error ?? `Request failed (${r.status})`, willClose: j?.detail?.willClose }
    } catch (e) {
      return { ok: false, error: e instanceof Error && e.message === 'unauthorized' ? '' : 'Cannot reach the PC.' }
    }
  }

  /** Runs a call that may close the client: shows progress, asks first when the host says so. */
  async #guarded(label: string, path: (confirm: boolean) => string, method = 'POST', body?: unknown, confirm = false) {
    this.busy = label
    this.err = ''
    this.confirmClose = null
    const r = await this.#call(path(confirm), method, body)
    this.busy = ''
    if (r.code === 'confirm') this.confirmClose = { willClose: r.willClose ?? [], run: () => this.#guarded(label, path, method, body, true) }
    else if (!r.ok) this.err = r.error
    return r.ok
  }

  async #simple(fn: () => Promise<Result>) {
    this.busy = 'Saving…'
    this.err = ''
    const r = await fn()
    this.busy = ''
    if (!r.ok) this.err = r.error
    return r.ok
  }

  static #q = (c: boolean) => (c ? '?confirm=true' : '')
  switchTo = (p: Profile) => this.#guarded(`Switching to ${label(p)}…`, (c) => `/${p.id}/switch${ProfileActions.#q(c)}`)
  beginAdd = (saveAs = 'new') => { this.saveAs = saveAs; return this.#guarded('Closing the Riot Client and opening the sign-in screen…', (c) => `/add/begin${ProfileActions.#q(c)}`) }
  cancelAdd = () => this.#guarded('Restoring the previous sign-in…', () => '/add/cancel')

  /** Saves the signed-in account: `id` null = a new profile. */
  async save(id: string | null) {
    await this.#simple(() => this.#call('', 'POST', { name: '', id }))
  }
  async remove(id: string) {
    if (await this.#simple(() => this.#call(`/${id}`, 'DELETE'))) this.removing = null
  }
  async move(i: number, by: -1 | 1) {
    const ids = this.list.map((p) => p.id)
    ;[ids[i], ids[i + by]] = [ids[i + by], ids[i]]
    await this.#simple(() => this.#call('/order', 'PUT', { ids }))
  }
  async refresh() {
    this.busy = 'Checking…'
    try { const r = await api('/profiles'); if (r.ok) live.profiles = await r.json() } catch { this.err = 'Cannot reach the PC.' }
    this.busy = ''
  }

  /** While the client sits at its sign-in screen, notice the sign-in without a button press. Call from an $effect; returns the cleanup. */
  watchSignIn() {
    if (!this.snap?.adding || this.snap.signedIn) return
    const t = setInterval(async () => {
      try { const r = await api('/profiles'); if (r.ok) live.profiles = await r.json() } catch { /* next tick */ }
    }, 2000)
    return () => clearInterval(t)
  }
}
