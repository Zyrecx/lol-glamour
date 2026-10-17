<script lang="ts">
  // In a lobby: the mode, my two lanes (when the mode has a lane picker), the party, and Find match.
  import { live } from '../../lib/live.svelte'
  import { queueNames } from '../../lib/queues.svelte'
  import LobbyMembers from '../../lib/sections/LobbyMembers.svelte'
  import Chips from '../parts/Chips.svelte'
  import Lane from '../parts/Lane.svelte'
  import { playable } from '../playable.svelte'
  import { choosePosition, laneName, laneShort, LOBBY_LANES } from '../util'

  const lobby = $derived(live.now.lobby)
  const queue = $derived(playable.byId(lobby.queueId))
  const title = $derived(queue?.name ?? queueNames.label(lobby.queueId))
  let slot = $state<'first' | 'second'>('first')
  let busy = $state(false)
  let err = $state('')

  $effect(() => { playable.ensure(live.now.connected); queueNames.ensure() })

  const current = $derived(slot === 'first' ? lobby.first : lobby.second)
  const other = $derived(slot === 'first' ? lobby.second : lobby.first)
  const shown = (l: string) => (l && l !== 'UNSELECTED' ? l : '')

  async function pick(lane: string) {
    busy = true
    err = (await live.act('/lobby/positions', 'PUT', choosePosition(lobby.first, lobby.second, slot, lane))) ?? ''
    if (!err && slot === 'first' && lane !== 'FILL') slot = 'second'
    busy = false
  }
  async function run(path: string, method = 'POST') {
    busy = true
    err = (await live.act(path, method)) ?? ''
    busy = false
  }
</script>

<div class="lobby rise">
  <div class="head">
    <span class="label gold">{queue?.mapName || 'Lobby'}</span>
    <span class="display title">{title}</span>
  </div>

  {#if lobby.showPositions}
    <section class="panel stack10">
      <span class="label">Your positions · tap one to change</span>
      <div class="two">
        {#each [['first', 'Primary'], ['second', 'Secondary']] as [id, name] (id)}
          {@const l = id === 'first' ? lobby.first : lobby.second}
          <button type="button" class="slot" class:active={slot === id} aria-pressed={slot === id} onclick={() => (slot = id as 'first' | 'second')}
            disabled={id === 'second' && lobby.first === 'FILL'}>
            <span class="ico"><Lane lane={shown(l) || 'FILL'} size={30} /></span>
            <span><small>{name}</small>{shown(l) ? laneName(l) : 'Not set'}</span>
          </button>
        {/each}
      </div>
      <div class="six" role="group" aria-label={`Lane for ${slot === 'first' ? 'primary' : 'secondary'}`}>
        {#each LOBBY_LANES as l (l)}
          <button type="button" class="lpick" class:on={l === current} class:taken={l === other && l !== 'FILL'} aria-pressed={l === current}
            disabled={busy} onclick={() => pick(l)}>
            <Lane lane={l} size={24} />{laneShort(l)}
          </button>
        {/each}
      </div>
    </section>
  {/if}

  <LobbyMembers />
  <Chips lane={null} />

  {#if err}<p class="err" role="alert">{err}</p>{/if}
  <div class="dock-gap"></div>
</div>

<div class="dock">
  <div class="dock-row">
    <button type="button" class="btn btn-ghost btn-leave" disabled={busy} onclick={() => run('/lobby', 'DELETE')}>Leave</button>
    {#if lobby.canStart}
      <button type="button" class="btn btn-primary" disabled={busy} onclick={() => run('/queue')}>Find match</button>
    {:else}
      <button type="button" class="btn btn-wait" disabled>Waiting for the party leader</button>
    {/if}
  </div>
</div>

<style>
  .lobby { display: flex; flex-direction: column; gap: 12px; }
  .head { display: flex; flex-direction: column; gap: 4px; padding-top: 6px; }
  .gold { color: var(--g-gold); }
  .title { font-size: 28px; }
  .stack10 { display: flex; flex-direction: column; gap: 10px; }
  .two { display: grid; grid-template-columns: 1fr 1fr; gap: 8px; }
  .slot { display: flex; align-items: center; gap: 10px; min-height: 58px; padding: 8px 12px; border-radius: 12px; border: 1px solid rgba(200,170,110,.35); background: rgba(6,10,18,.6); color: var(--g-fg); font: 600 15px var(--g-body); text-align: left; }
  .slot:hover { border-color: var(--g-gold); filter: none; }
  .slot.active { border-color: var(--g-teal); box-shadow: 0 0 0 1px var(--g-teal) inset; }
  .slot small { display: block; font-size: 10.5px; letter-spacing: .14em; text-transform: uppercase; color: var(--g-muted); font-weight: 700; }
  .ico { color: var(--g-gold); display: flex; }
  .six { display: grid; grid-template-columns: repeat(6, minmax(0, 1fr)); gap: 6px; }
  .lpick { display: flex; flex-direction: column; align-items: center; justify-content: center; gap: 3px; min-height: 58px; padding: 0; border-radius: 10px; border: 1px solid rgba(200,170,110,.25); background: transparent; color: var(--g-muted); font: 700 10px var(--g-body); letter-spacing: .08em; }
  .lpick:hover { color: var(--g-fg); border-color: var(--g-gold); filter: none; }
  .lpick.on { color: var(--g-teal); border-color: var(--g-teal); background: rgba(45,226,196,.08); }
  .lpick.taken { opacity: .35; }
</style>
