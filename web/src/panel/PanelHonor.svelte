<script lang="ts">
  // The panel's Auto tab: auto-honor with its list edited in place (no typing) and the skip, through Auto's "For" scope (live.patch).
  // The phone and the PC window keep the text list (lib/sections/Honor.svelte).
  import { live } from '../lib/live.svelte'
  import Toggle from '../lib/components/Toggle.svelte'
  import HonorList from './HonorList.svelte'
  import type { Settings } from '../lib/types'

  const s = $derived(live.view)
  let msg = $state('')
  let busy = $state(false)
  async function save(change: Partial<Settings>) {
    busy = true
    msg = (await live.patch(change)) ?? ''
    busy = false
  }
</script>

{#if s}
  <section class="honor">
    <h2 class="band">Honor after a game</h2>
    <div class="body">
      <Toggle label="Auto-honor, first listed friends in the game" checked={s.autoHonorEnabled} onchange={(v) => save({ autoHonorEnabled: v })} />
      <div class="list"><HonorList list={s.autoHonorFriends} {busy} onsave={(next) => save({ autoHonorFriends: next })} /></div>
      {#if s.autoHonorEnabled}
        <Toggle label="Stay after honoring friends" checked={s.honorStay} onchange={(v) => save({ honorStay: v })} />
      {/if}
      <Toggle label="Skip the honor screen when no listed friend played" checked={s.skipHonorScreen} onchange={(v) => save({ skipHonorScreen: v })} />
      {#if msg}<p class="err" role="alert">{msg}</p>{/if}
    </div>
  </section>
{/if}

<style>
  .band { margin: 0; display: flex; align-items: center; min-height: 30px; padding: 0 12px; background: var(--l-band); border-top: 1px solid var(--l-frame); border-bottom: 1px solid var(--l-frame);
    font: 700 11px var(--g-body); letter-spacing: .1em; text-transform: uppercase; color: var(--l-grey1); }
  .body { padding: 0 12px 6px; }
  .list { padding: 2px 0 6px 4px; }
  .err { margin: 6px 0; font-size: 12px; color: #E86A6C; }
</style>
