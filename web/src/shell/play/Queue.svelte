<script lang="ts">
  // Searching: time in queue (counted here from the host's elapsed time), the estimate as m:ss like the client, and Cancel.
  import { live } from '../../lib/live.svelte'
  import { queueNames } from '../../lib/queues.svelte'
  import Chips from '../parts/Chips.svelte'
  import Ring from '../parts/Ring.svelte'
  import { playable } from '../playable.svelte'
  import { tick } from '../tick.svelte'
  import { clock, laneName } from '../util'

  const lobby = $derived(live.now.lobby)
  const queueId = $derived(lobby.queueId)
  const name = $derived(playable.byId(queueId)?.name ?? queueNames.label(queueId))
  const elapsed = $derived(live.now.queueSince ? tick.now - live.now.queueSince : 0)
  const lanes = $derived([lobby.first, lobby.second].filter((l) => l && l !== 'UNSELECTED').map(laneName).filter((v, i, a) => a.indexOf(v) === i))
  const estimate = $derived(live.now.queueEstimate > 0 ? `Estimated ${clock(live.now.queueEstimate * 1000)}` : '')
  let busy = $state(false)
  let err = $state('')

  $effect(() => tick.use())
  $effect(() => { playable.ensure(live.now.connected); queueNames.ensure() })

  async function cancel() {
    busy = true
    err = (await live.act('/queue', 'DELETE')) ?? ''
    busy = false
  }
</script>

<div class="queue rise">
  <Ring spin size={220} stroke={6}>
    <span class="label">Time in queue</span>
    <span class="num big">{clock(elapsed)}</span>
    {#if estimate}<span class="num est">{estimate}</span>{/if}
  </Ring>
  <div class="what">
    <span class="display" style="font-size: 24px">{name}</span>
    {#if lanes.length || lobby.members > 1}
      <span class="sub">{[lanes.join(' / '), lobby.members > 1 ? `party of ${lobby.members}` : ''].filter(Boolean).join(' · ')}</span>
    {/if}
  </div>
  <Chips center />
  {#if err}<p class="err" role="alert">{err}</p>{/if}
  <div class="dock-gap"></div>
</div>

<div class="dock">
  <button type="button" class="btn btn-ghost" disabled={busy} onclick={cancel}>Cancel queue</button>
</div>

<style>
  .queue { display: flex; flex-direction: column; align-items: center; gap: 22px; padding-top: 40px; }
  .big { font-size: 56px; font-weight: 700; line-height: 1.05; }
  .est { font-size: 15px; color: var(--g-muted); }
  .what { display: flex; flex-direction: column; align-items: center; gap: 6px; text-align: center; }
  .sub { font-size: 14px; color: var(--g-fg2); }
</style>
