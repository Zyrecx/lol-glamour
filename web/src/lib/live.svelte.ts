import { act, api, errorText, getJson } from './api'
import { applyEvent, EVENTS, initialNow } from './now'
import { demoFromUrl, demoNow } from '../shell/demo'
import { setSharedKeys, splitChange, splitInEffect, viewOf } from './scope'
import type { AppInfo, AuthStatus, Profile, ProfilesSnapshot, Settings, SettingsMeta } from './types'

/** The one live view of the host: derived client state plus the settings and accounts, all fed by the SSE stream. */
class Live {
  now = $state(initialNow())
  /** The settings in effect: the shared defaults with the active account's overrides. What the host runs on. */
  settings = $state<Settings | null>(null)
  /** The shared defaults alone. */
  defaults = $state<Settings | null>(null)
  profiles = $state<ProfilesSnapshot | null>(null)
  /** Settings rules from the host (shared keys, number ranges). */
  meta = $state<SettingsMeta | null>(null)
  /** Whose settings the Automation page edits: 'all' (the shared defaults) or a profile id. */
  scope = $state('all')
  linkLost = $state(false)
  /** Version, "Help improve Glamour", the first-run tour and updates (`GET /api/app`, then `app` events). */
  app = $state<AppInfo | null>(null)
  /** The signed-in account's profile icon as the client last announced it (`profile-icon` events); null until it changes. */
  profileIcon = $state<{ id: number; blank: boolean } | null>(null)
  #started = false
  /** `?demo=<screen>` fakes the client state for a design review (shell/demo.ts). */
  #demo = demoFromUrl(location.search)

  #es: EventSource | null = null
  #retries = 0
  #retry: ReturnType<typeof setTimeout> | undefined

  start() {
    if (this.#started) return
    this.#started = true
    this.#open()
    // A phone wakes up with the stream gone: reconnect at once instead of waiting out the backoff.
    document.addEventListener('visibilitychange', () => {
      if (document.visibilityState === 'visible' && this.#es?.readyState === EventSource.CLOSED) this.#reopen(0)
    })
  }

  #open() {
    const es = new EventSource('/api/events')
    this.#es = es
    es.onopen = () => { this.linkLost = false; this.#retries = 0 }
    es.onerror = () => {
      this.linkLost = true
      // On a dropped connection EventSource retries by itself. It gives up for good on a non-200 answer (a 401 once this device
      // was revoked, an error while the host restarts): then check the sign-in and open a new one.
      if (es.readyState === EventSource.CLOSED) this.#reopen()
    }
    for (const type of EVENTS) {
      es.addEventListener(type, (e) => {
        let data
        try { data = JSON.parse((e as MessageEvent).data).data } catch { return }
        if (type === 'snapshot') {
          this.settings = data.settings; this.defaults = data.defaults; this.profiles = data.profiles
          this.meta = data.settingsMeta ?? null
          if (this.meta) setSharedKeys(this.meta.sharedKeys)
          this.refreshApp()
        }
        else if (type === 'app') { this.app = data; return }
        else if (type === 'profile-icon') { this.profileIcon = data; return }
        else if (type === 'settings') this.settings = data
        else if (type === 'defaults') this.defaults = data
        else if (type === 'profiles') this.profiles = data
        const at = Date.now()
        this.now = applyEvent(this.now, type, data, at)
        if (this.#demo) this.now = demoNow(this.#demo, this.now, at)
      })
    }
  }

  /** Opens a new stream after `delay` (1 s doubling to 30 s), or goes to the sign-in page when this browser is no longer paired. */
  #reopen(delay = Math.min(30_000, 1000 * 2 ** this.#retries++)) {
    clearTimeout(this.#retry)
    this.#retry = setTimeout(async () => {
      try {
        if (!(await getJson<AuthStatus>('/auth/status')).authenticated) { location.replace('/login.html'); return }
      } catch { this.#reopen(); return } // the host is not answering yet
      this.#es?.close()
      this.#open()
    }, delay)
  }

  async refreshApp() {
    try { this.app = await getJson<AppInfo>('/app') } catch { /* an older host, or not reachable: the sections stay hidden */ }
  }

  /** The profile being edited, or null for the shared defaults (also when that profile was removed meanwhile). */
  get scopeProfile(): Profile | null {
    return this.scope === 'all' ? null : (this.profiles?.profiles.find((p) => p.id === this.scope) ?? null)
  }

  /** The settings the editing sections show: the shared defaults, or the chosen account's (defaults plus its overrides). */
  get view(): Settings | null {
    const p = this.scopeProfile
    return p ? viewOf(this.defaults, p.overrides) : (this.defaults ?? this.settings)
  }

  /**
   * Changes some settings of the scope being edited. Only the changed fields go to the host, which merges them, so another open
   * device's changes are never overwritten. In an account's scope its own fields become overrides; device settings (ntfy, dry run)
   * always go to the shared defaults. Returns an error message, or null.
   */
  async patch(change: Partial<Settings>): Promise<string | null> {
    const p = this.scopeProfile
    const { shared, own } = p ? splitChange(change) : { shared: change, own: {} }
    const err = Object.keys(shared).length ? await this.#patchDefaults(shared) : null
    return err ?? (p && Object.keys(own).length ? await this.#patchOverrides(p.id, { set: own }) : null)
  }

  /** Changes the settings in effect now, whatever the Automation page edits: an override of the signed-in account stays its own. */
  async patchInEffect(change: Partial<Settings>): Promise<string | null> {
    const p = this.profiles?.profiles.find((x) => x.id === this.profiles?.activeId) ?? null
    const { shared, own } = splitInEffect(change, p?.overrides)
    const err = Object.keys(shared).length ? await this.#patchDefaults(shared) : null
    if (err || !p || !Object.keys(own).length) return err
    const before = this.settings
    if (before) this.settings = { ...before, ...own } // optimistic; the host's settings event confirms
    const e = await this.#patchOverrides(p.id, { set: own })
    if (e) this.settings = before
    return e
  }

  /** Makes these fields of the chosen account follow the shared defaults again. */
  resetOverrides(keys: string[]): Promise<string | null> {
    const p = this.scopeProfile
    return p ? this.#patchOverrides(p.id, { clear: keys }) : Promise.resolve(null)
  }

  async #patchDefaults(change: Partial<Settings>): Promise<string | null> {
    const before = { settings: this.settings, defaults: this.defaults }
    if (before.settings) this.settings = { ...before.settings, ...change } // optimistic; the host's events confirm
    if (before.defaults) this.defaults = { ...before.defaults, ...change }
    try {
      const r = await api('/settings', 'PATCH', change)
      if (!r.ok) { this.settings = before.settings; this.defaults = before.defaults; return await errorText(r) }
      this.settings = await r.json() // the effective settings: an account that overrides the field keeps its own value
      return null
    } catch (e) {
      this.settings = before.settings
      this.defaults = before.defaults
      return e instanceof Error && e.message === 'unauthorized' ? null : 'Cannot reach the PC.'
    }
  }

  async #patchOverrides(id: string, body: { set?: Partial<Settings>; clear?: string[] }): Promise<string | null> {
    const before = this.profiles
    if (before && body.set) this.profiles = { ...before, profiles: before.profiles.map((p) => (p.id === id ? { ...p, overrides: { ...p.overrides, ...body.set } } : p)) }
    try {
      const r = await api(`/profiles/${encodeURIComponent(id)}/settings`, 'PATCH', body)
      if (!r.ok) { this.profiles = before; return await errorText(r) }
      this.profiles = await r.json()
      return null
    } catch (e) {
      this.profiles = before
      return e instanceof Error && e.message === 'unauthorized' ? null : 'Cannot reach the PC.'
    }
  }

  act = act
}

export const live = new Live()
