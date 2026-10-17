<script lang="ts">
  // First-run tour (SPEC "Publishing"): a few screens the first time the window opens on this PC, until finished or skipped (host.json `tourDone`).
  // The second screen asks what Glamour should do; each feature that needs setting up gets its own screen after it. `?tour=1` shows it again.
  import { onMount } from 'svelte'
  import { act, getJson } from '../../lib/api'
  import { live } from '../../lib/live.svelte'
  import type { LaneKey } from '../../lib/types'
  import BanLockGrid from './BanLockGrid.svelte'
  import ChampionPool from '../../lib/components/ChampionPool.svelte'
  import Segmented from '../../lib/components/Segmented.svelte'
  import Toggle from '../../lib/components/Toggle.svelte'
  import Mark from '../../shell/parts/Mark.svelte'

  const forced = new URLSearchParams(location.search).get('tour') === '1'
  let closed = $state(false)
  let step = $state(0)
  let err = $state('')
  let desktop = $state<{ available: boolean; autostart: boolean; panelAutoOpen: boolean } | null>(null)
  const app = $derived(live.app)
  const s = $derived(live.settings)
  const open = $derived(!closed && !!app && (forced || !app.tourDone))

  // Features with a screen of their own; Auto accept and the panel are only a switch, set on the choice screen.
  type Setup = 'champs' | 'pool' | 'mayhem' | 'phone'
  let wants = $state<Record<Setup, boolean>>({ champs: false, pool: false, mayhem: false, phone: false })
  type Page = 'welcome' | 'choose' | Setup | 'basics' | 'done'
  // Phone last: its button leaves the tour for the Phone page.
  const pages = $derived<Page[]>([
    'welcome', 'choose', ...(['champs', 'pool', 'mayhem'] as const).filter((f) => wants[f]), 'basics',
    ...(wants.phone ? ['phone' as const] : []), ...(app?.installFolder ? ['done' as const] : []),
  ])
  const page = $derived(pages[Math.min(step, pages.length - 1)])

  onMount(loadDesktop)
  async function loadDesktop() {
    try { desktop = await getJson('/desktop') } catch { desktop = null }
  }

  async function setAutostart(enabled: boolean) {
    err = (await act('/desktop/autostart', 'PUT', { enabled })) ?? ''
    await loadDesktop()
  }

  async function setAutoOpen(enabled: boolean) {
    err = (await act('/desktop/panel-auto-open', 'PUT', { enabled })) ?? ''
    await loadDesktop()
  }

  async function setTelemetry(enabled: boolean) {
    err = (await act('/app/telemetry', 'PUT', { enabled })) ?? ''
    await live.refreshApp()
  }

  async function set(patch: Parameters<typeof live.patchInEffect>[0]) {
    err = (await live.patchInEffect(patch)) ?? ''
  }

  // The champion pool is per lane; the tour starts on mid.
  const lanes: { value: LaneKey; label: string; lane: string }[] = [
    { value: 'TOP', label: 'Top', lane: 'TOP' }, { value: 'JUNGLE', label: 'Jgl', lane: 'JUNGLE' }, { value: 'MIDDLE', label: 'Mid', lane: 'MIDDLE' },
    { value: 'BOTTOM', label: 'Bot', lane: 'BOTTOM' }, { value: 'SUPPORT', label: 'Sup', lane: 'SUPPORT' },
  ]
  let lane = $state<LaneKey>('MIDDLE')
  const pool = $derived(s?.championPools?.[lane] ?? [])
  function setPool(ids: number[]) {
    if (!s) return
    const pools = { ...s.championPools }
    if (ids.length) pools[lane] = ids
    else delete pools[lane]
    set({ championPools: pools })
  }

  function go(by: number) { err = ''; step = Math.max(0, Math.min(pages.length - 1, step + by)) }

  /** Done or skipped: never again on this PC. `to` opens a page after it. */
  async function finish(to?: string) {
    closed = true
    await act('/app/tour-done', 'POST')
    await live.refreshApp()
    if (to) location.hash = to
  }
</script>

{#if open && app}
  <div class="veil" role="dialog" aria-modal="true" aria-label="Welcome to Glamour">
    <div class="box">
      <div class="dots" aria-hidden="true">{#each pages as p, i (p)}<span class:on={i === step}></span>{/each}</div>

      {#if page === 'welcome'}
        <Mark size={56} />
        <h2 class="display">Welcome to Glamour</h2>
        <p>Glamour works beside League: it accepts matches, bans and locks your champions, and shows counters and parties in champion select.</p>
        <p class="muted">A panel docks next to the League client. Glamour itself sits in the tray.</p>
      {:else if page === 'choose'}
        <h2 class="display">What should Glamour do?</h2>
        <div class="rows">
          <Toggle label="Accept matches" checked={s?.autoAccept === 'Always'} onchange={(v) => set({ autoAccept: v ? 'Always' : 'Off' })} />
          <Toggle label="Ban and lock my champions" checked={wants.champs} onchange={(v) => (wants.champs = v)} />
          <Toggle label="My champions among the counters" hint="Your champion pool per lane." checked={wants.pool} onchange={(v) => (wants.pool = v)} />
          {#if desktop?.available}
            <Toggle label="Open the panel in champ select" hint="Off: it stays a thin bar until you open it." checked={desktop.panelAutoOpen} onchange={setAutoOpen} />
          {/if}
          <Toggle label="ARAM Mayhem help" checked={wants.mayhem} onchange={(v) => (wants.mayhem = v)} />
          <Toggle label="Use my phone" checked={wants.phone} onchange={(v) => (wants.phone = v)} />
        </div>
        <p class="muted">All of it can be changed later.</p>
      {:else if page === 'champs'}
        <h2 class="display">Ban and lock</h2>
        <div class="rows grid"><BanLockGrid onerror={(m) => (err = m)} /></div>
        <p class="muted">Faded lanes follow All lanes.</p>
      {:else if page === 'pool'}
        <h2 class="display">Your champions</h2>
        <div class="lanepick"><Segmented label="Lane" options={lanes} value={lane} onchange={(v) => (lane = v)} /></div>
        <div class="rows pool"><ChampionPool ids={pool} onchange={setPool} /></div>
        <p class="muted">Always shown among the counter picks.</p>
      {:else if page === 'mayhem'}
        <h2 class="display">ARAM Mayhem</h2>
        <div class="rows">
          <Toggle label="Augment tags in game" checked={!!s?.augmentTips} onchange={(v) => set({ augmentTips: v })} />
          <Toggle label="Win rates from arammayhem.com" checked={!!s?.mayhemWinRates} onchange={(v) => set({ mayhemWinRates: v })} />
        </div>
        <p class="muted">Off: bench tiers from u.gg, no augment tags.</p>
      {:else if page === 'basics'}
        <h2 class="display">Help improve Glamour</h2>
        <div class="rows">
          <Toggle label="Help improve Glamour" checked={app.telemetry} onchange={setTelemetry} />
        </div>
        <p class="muted">Sends errors and counts of which features are used. No names, accounts or ids.</p>
        {#if desktop?.available}
          <div class="rows">
            <Toggle label="Start with Windows" checked={desktop.autostart} onchange={setAutostart} />
          </div>
        {/if}
      {:else if page === 'phone'}
        <h2 class="display">Your phone, too</h2>
        <p>Accept a match, follow champion select and get alerts from your phone on the same Wi-Fi.</p>
        <button type="button" class="quiet" onclick={() => finish('#/phone')}>Pair a phone</button>
      {:else}
        <h2 class="display">All set</h2>
        <p class="muted folder">Installed to {app.installFolder}</p>
        <button type="button" class="quiet" onclick={() => act('/app/open-folder', 'POST')}>Open folder</button>
      {/if}

      {#if err}<p class="warn">{err}</p>{/if}
      <div class="nav">
        {#if step === 0}
          <button type="button" class="quiet" onclick={() => finish()}>Skip</button>
        {:else}
          <button type="button" class="quiet" onclick={() => go(-1)}>Back</button>
        {/if}
        {#if step < pages.length - 1}
          <button type="button" onclick={() => go(1)}>{page === 'phone' ? 'Not now' : 'Next'}</button>
        {:else}
          <button type="button" onclick={() => finish()}>Done</button>
        {/if}
      </div>
    </div>
  </div>
{/if}

<style>
  /* Over everything but the champion picker, which its screens open. */
  .veil { position: fixed; inset: 0; z-index: calc(var(--z-picker, 60) - 1); display: grid; place-items: center; background: rgba(3, 6, 12, .82); backdrop-filter: blur(4px); }
  .box {
    width: min(500px, 88vw); min-height: 360px; display: flex; flex-direction: column; align-items: center; gap: 14px; text-align: center; padding: 26px 30px 22px;
    background: #0B121E; border: 1px solid var(--g-gold); border-radius: 14px;
  }
  h2 { font-size: 20px; letter-spacing: .06em; }
  p { font-size: 15px; line-height: 1.5; }
  .muted { color: var(--g-muted); font-size: 14px; }
  .rows { width: 100%; text-align: left; padding: 4px 14px; border: 1px solid var(--g-line-soft); border-radius: 10px; }
  .rows.grid { padding: 10px 12px; }
  /* Square lane buttons with room between them. */
  .lanepick :global(.seg) { gap: 8px; padding: 6px; }
  .lanepick :global(button.withicon) { flex: none; width: 58px; aspect-ratio: 1; gap: 4px; }
  .rows.pool { padding: 12px 14px; }
  .folder { overflow-wrap: anywhere; }
  .dots { display: flex; gap: 6px; }
  .dots span { width: 7px; height: 7px; border-radius: 50%; background: var(--g-line); }
  .dots span.on { background: var(--g-teal); box-shadow: 0 0 6px var(--g-teal); }
  .nav { margin-top: auto; width: 100%; display: flex; justify-content: space-between; padding-top: 8px; }
</style>
