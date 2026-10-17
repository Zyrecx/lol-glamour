<script lang="ts">
  // Who queued together in the running game (SPEC "Parties in game"), mine first. Shared by the phone's in-game screen and the PC Home.
  import { champions } from '../../lib/champions.svelte'
  import type { GameParties } from '../../lib/types'

  let { parties }: { parties: GameParties } = $props()
  $effect(() => { champions.ensure() })
</script>

<section class="panel parties" aria-label="Parties">
  <span class="label">Parties</span>
  {#if parties.parties.length === 0}
    <span class="muted">Everyone queued solo.</span>
  {:else}
    {#each parties.parties as party, i (i)}
      <div class="party" class:ally={party.ally}>
        <span class="side">{party.players.some((p) => p.isMe) ? 'You' : party.ally ? 'Ally' : 'Enemy'} · {party.players.length === 2 ? 'duo' : `${party.players.length}-stack`}</span>
        <div class="who">
          {#each party.players as p (p.puuid)}
            {@const c = champions.byId(p.championId)}
            <span class="pp" class:me={p.isMe}>
              {#if c}<img src={c.icon} alt="" />{/if}
              <span class="nm">{p.isMe ? 'You' : (p.name.split('#')[0] || c?.name || 'Unknown')}</span>
            </span>
          {/each}
        </div>
      </div>
    {/each}
  {/if}
</section>

<style>
  .parties { width: 100%; box-sizing: border-box; display: flex; flex-direction: column; gap: 10px; }
  .party { display: flex; flex-direction: column; gap: 6px; padding-left: 10px; border-left: 3px solid var(--g-magenta); }
  .party.ally { border-left-color: var(--g-teal); }
  .side { font-size: 11px; letter-spacing: .12em; text-transform: uppercase; font-weight: 700; color: var(--g-muted); }
  .who { display: flex; flex-wrap: wrap; gap: 6px 12px; }
  .pp { display: flex; align-items: center; gap: 6px; font-size: 14px; min-width: 0; }
  .pp img { width: 30px; height: 30px; border-radius: 6px; border: 1px solid rgba(200,170,110,.45); }
  .pp.me img { border: 2px solid var(--g-teal); }
  .nm { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; max-width: 9rem; }
</style>
