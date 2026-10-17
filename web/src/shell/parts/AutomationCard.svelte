<script lang="ts">
  // The global automation switch and "pause next game", with what automation will do. Same card on Play and on the Automation tab.
  import { live } from '../../lib/live.svelte'
  import Chips from './Chips.svelte'
  let { chips = true }: { chips?: boolean } = $props()
  const on = $derived(live.settings?.automationEnabled ?? true)
  const paused = $derived(live.now.paused)
  const inGame = $derived(['GameStart', 'InProgress', 'Reconnect', 'WaitingForStats', 'PreEndOfGame', 'EndOfGame'].includes(live.now.phase))
  let err = $state('')
  let busy = $state(false)

  async function toggle() {
    busy = true
    err = (await live.patch({ automationEnabled: !on })) ?? ''
    busy = false
  }
  async function pause() {
    busy = true
    err = (await live.act('/automation/pause', 'PUT', { paused: !paused })) ?? ''
    busy = false
  }
  const note = $derived(!on ? 'Off everywhere until you turn it back on.'
    : paused ? (inGame ? 'Paused for this game, then back on.' : 'Paused for the next game only, then back on.')
    : inGame ? 'On. Pause skips the rest of this game.' : 'On. Pause skips just the next game.')
</script>

<section class="panel card-auto">
  <div class="row">
    <button type="button" class="switch" role="switch" aria-checked={on} disabled={busy} onclick={toggle}>
      <span class="track" class:on><span class="knob"></span></span>
      Automation
    </button>
    <button type="button" class="small-btn" disabled={busy || !on} onclick={pause}>{paused ? 'Resume' : inGame ? 'Pause this game' : 'Pause next game'}</button>
  </div>
  <span class="note">{note}</span>
  {#if chips}<Chips />{/if}
  {#if err}<p class="err" role="alert">{err}</p>{/if}
</section>

<style>
  .card-auto { display: flex; flex-direction: column; gap: 8px; }
  .row { display: flex; align-items: center; justify-content: space-between; gap: 8px; }
  .switch { display: flex; align-items: center; gap: 10px; background: transparent; border: 0; color: var(--g-fg); font: 700 15px var(--g-body); padding: 0; min-height: 44px; }
  .switch:hover { filter: none; }
  .track { width: 46px; height: 28px; border-radius: 999px; position: relative; background: rgba(166, 159, 143, .35); transition: background .2s; }
  .track.on { background: var(--g-teal); }
  .knob { position: absolute; top: 3px; left: 3px; width: 22px; height: 22px; border-radius: 50%; background: var(--g-bg); transition: left .2s; }
  .track.on .knob { left: 21px; }
  .note { font-size: 13px; color: var(--g-muted); }
</style>
