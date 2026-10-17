<script lang="ts">
  // Match found, over any tab. Closes by itself when the ready check ends, whoever answered (phone, client or automation).
  import { live } from '../../lib/live.svelte'
  import { queueNames } from '../../lib/queues.svelte'
  import Ring from '../parts/Ring.svelte'
  import { playable } from '../playable.svelte'
  import { tick } from '../tick.svelte'

  const queue = $derived(playable.byId(live.now.lobby.queueId)?.name ?? queueNames.label(live.now.lobby.queueId))
  const window = $derived(live.now.readyWindowMs || 13000)
  const left = $derived(live.now.readySince ? Math.max(0, window - (tick.now - live.now.readySince)) : window)
  const accepted = $derived(live.now.readyResponse === 'Accepted')
  const declined = $derived(live.now.readyResponse === 'Declined')
  const autoIn = $derived(live.now.acceptAt ? Math.max(0, Math.ceil((live.now.acceptAt - tick.now) / 1000)) : null)
  let busy = $state(false)
  let err = $state('')
  let by = $state('')

  $effect(() => tick.use())
  $effect(() => { queueNames.ensure() })
  // A buzz on Android when the match pops (iOS has no vibration API).
  $effect(() => { try { if (!accepted && !declined) navigator.vibrate?.([200, 100, 200]) } catch { /* not allowed */ } })

  async function run(path: string, label: string) {
    busy = true
    err = (await live.act(path)) ?? ''
    if (!err) by = label
    busy = false
  }
  const sub = $derived(accepted ? `${by || 'Accepted.'} Waiting for everyone else.`
    : declined ? 'Declined.'
    : autoIn !== null ? (autoIn > 0 ? `Auto-accepting in ${autoIn} s.` : 'Accepting…')
    : '')
</script>

<div class="take" role="dialog" aria-modal="true" aria-label="Match found">
  <div class="glow" aria-hidden="true"></div>
  <div class="body">
    {#if queue}<span class="label gold">{queue}</span>{/if}
    <span class="display title">{accepted ? 'Accepted' : 'Match found'}</span>

    <Ring size={220} fraction={accepted ? 1 : left / window} done={accepted}>
      {#if accepted}
        <svg width="64" height="64" viewBox="0 0 24 24" fill="none" stroke="#2DE2C4" stroke-width="2.4" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M5 12.5l4.5 4.5L19 7" /></svg>
      {:else}
        <span class="num secs">{Math.ceil(left / 1000)}</span>
        <span class="label">seconds</span>
      {/if}
    </Ring>

    <span class="sub" role="status">{sub}</span>

    <div class="actions">
      {#if !accepted && !declined}
        <button type="button" class="btn btn-primary" class:pulse={autoIn === null} disabled={busy} onclick={() => run('/ready-check/accept', 'Accepted from this phone.')}>
          {autoIn !== null ? 'Accept now' : 'Accept'}
        </button>
        {#if autoIn !== null}
          <button type="button" class="btn btn-ghost" disabled={busy} onclick={() => run('/ready-check/auto-accept/cancel', '')}>Don't auto-accept this one</button>
        {:else}
          <button type="button" class="btn btn-ghost" disabled={busy} onclick={() => run('/ready-check/decline', '')}>Decline</button>
        {/if}
      {/if}
      {#if err}<p class="err" role="alert">{err}</p>{/if}
    </div>
  </div>
</div>

<style>
  .take { position: fixed; inset: 0; z-index: 30; background: var(--g-bg); overflow-y: auto; }
  .glow { position: absolute; inset: 0; background: radial-gradient(circle at 50% 38%, rgba(45,226,196,.22) 0%, rgba(6,10,18,.9) 48%, rgba(6,10,18,.97) 100%); }
  .body { position: relative; min-height: 100%; max-width: 34rem; margin-inline: auto; display: flex; flex-direction: column; align-items: center; gap: 18px; padding: calc(56px + env(safe-area-inset-top)) 24px calc(24px + env(safe-area-inset-bottom)); }
  .gold { color: var(--g-gold); }
  .title { font-size: 34px; letter-spacing: .06em; }
  .secs { font-size: 72px; font-weight: 700; line-height: 1; }
  .sub { font-size: 16px; color: var(--g-fg2); text-align: center; min-height: 44px; max-width: 300px; }
  .actions { display: flex; flex-direction: column; gap: 10px; width: 100%; margin-top: auto; }
  .pulse { animation: g-pulse 1.6s ease-in-out infinite; }
</style>
