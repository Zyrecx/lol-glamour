<script lang="ts">
  // In game (and the post-game phases before the honor screen): my champion, the game clock from the game itself, what runs after.
  import { champions } from '../../lib/champions.svelte'
  import { live } from '../../lib/live.svelte'
  import { queueNames } from '../../lib/queues.svelte'
  import Chips from '../parts/Chips.svelte'
  import Parties from '../parts/Parties.svelte'
  import { playable } from '../playable.svelte'
  import { tick } from '../tick.svelte'
  import { art, clock, laneName } from '../util'

  const pick = $derived(live.now.myPick)
  const champ = $derived(champions.byId(pick?.championId ?? null))
  const portrait = $derived(art(champ, pick?.skinId ?? 0, 'portrait'))
  const queue = $derived(pick ? (playable.byId(pick.queueId)?.name ?? queueNames.label(pick.queueId)) : '')
  const post = $derived(['WaitingForStats', 'PreEndOfGame', 'EndOfGame'].includes(live.now.phase))
  const parties = $derived(live.now.parties)
  const gameTime = $derived(live.now.gameStart ? tick.now - live.now.gameStart : 0)

  // After a crash the client waits on Reconnect (the client is back, so the host can send it).
  const lost = $derived(live.now.phase === 'Reconnect')
  let busy = $state(false)
  let err = $state('')
  async function reconnect() {
    busy = true
    err = (await live.act('/game/reconnect')) ?? ''
    busy = false
  }

  $effect(() => tick.use())
  $effect(() => { if (pick || parties) { champions.ensure(); queueNames.ensure() } })
</script>

<div class="game rise">
  {#if portrait}
    <div class="frame"><img src={portrait} alt={champ?.name ?? ''} /></div>
  {/if}
  <div class="what">
    <span class="display" style="font-size: 26px">{champ ? champ.name : post ? 'Game over' : 'In game'}{pick?.lane ? ` · ${laneName(pick.lane)}` : ''}</span>
    {#if queue}<span class="sub">{queue}</span>{/if}
    {#if live.now.gameStart}
      <span class="num clock" aria-label="Game time">{clock(gameTime)}</span>
    {:else if !post}
      <span class="muted">The game clock shows once the game has loaded.</span>
    {/if}
  </div>
  {#if lost}
    <button type="button" disabled={busy} onclick={reconnect}>Reconnect</button>
    {#if err}<span class="warn center">{err}</span>{/if}
  {/if}
  {#if parties}<Parties {parties} />{/if}
  <span class="muted center">{post ? 'Waiting for the post-game screens.' : 'Honor opens here when the game ends.'}</span>
  <Chips center />
</div>

<style>
  .game { display: flex; flex-direction: column; align-items: center; gap: 18px; padding-top: 10px; }
  .frame { padding: 4px; border: 1px solid var(--g-gold); border-radius: 6px; background: var(--g-bg); box-shadow: 0 0 40px rgba(45,226,196,.18); }
  .frame img { width: 196px; height: 356px; object-fit: cover; display: block; border-radius: 3px; }
  .what { display: flex; flex-direction: column; align-items: center; gap: 4px; text-align: center; }
  .sub { font-size: 14px; color: var(--g-fg2); }
  .clock { font-size: 40px; font-weight: 700; margin-top: 6px; }
  .center { text-align: center; max-width: 300px; }
</style>
