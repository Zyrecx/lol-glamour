<script lang="ts">
  import { champions } from '../champions.svelte'
  import { live } from '../live.svelte'
  import Card from '../components/Card.svelte'
  import { benchRates } from '../benchRates.svelte'
  import { benchRows, modeName, rateText } from '../benchAdvice'
  import { demoFromUrl } from '../../shell/demo'

  // ARAM and Mayhem: everyone is dealt a champion and the rest sit on a shared bench. Tapping a bench champion takes it.
  const board = $derived(live.now.phase === 'ChampSelect' ? live.now.board : null)
  const show = $derived(!!board && (board.mode === 'aram' || board.mode === 'mayhem'))
  const me = $derived(board?.myTeam.find((p) => p.isMe))
  const mine = $derived(champions.byId(me?.championId ?? 0))
  const canAct = $derived(board?.stage === 'BAN_PICK' || board?.stage === 'FINALIZATION')

  $effect(() => { if (show) champions.ensure() })

  // ARAM and Mayhem: win rates (or tiers) on my champion, the offered pair and the bench, best first; better ones (a point or a tier over mine) are marked.
  const rates = benchRates(() => board, demoFromUrl(location.search) !== null)
  const ranked = $derived(board && rates.mode ? benchRows(board, rates.rates) : null)
  const benchIds = $derived(ranked ? ranked.bench.map((r) => r.championId) : board?.bench ?? [])
  const offeredIds = $derived(ranked ? ranked.offered.map((r) => r.championId) : board?.offered ?? [])
  const rateOf = (id: number) => ranked?.bench.find((r) => r.championId === id) ?? ranked?.offered.find((r) => r.championId === id)

  // The offered pair is picked with the normal pick action (its id is the live turn). Unverified in a real game (INSIGHTS open gaps).
  const turn = $derived(live.now.phase === 'ChampSelect' ? live.now.turn : null)
  const canPick = $derived(turn?.type === 'pick')

  let err = $state('')
  let busy = $state(false)
  async function take(id: number) {
    busy = true
    err = (await live.act(`/champselect/bench/${id}`)) ?? ''
    busy = false
  }
  async function pick(id: number) {
    if (!turn) return
    busy = true
    err = (await live.act(`/champselect/actions/${turn.actionId}/complete`, 'POST', { championId: id, type: 'pick' })) ?? ''
    busy = false
  }
  async function reroll() {
    busy = true
    err = (await live.act('/champselect/reroll')) ?? ''
    busy = false
  }
</script>

{#if board && show}
  <Card title="Your champion" hint={board.mode === 'mayhem' ? 'Mayhem: take one from the bench, or reroll.' : 'Take a champion from the bench, or reroll.'}>
    <div class="me">
      {#if mine}<img src={mine.icon} alt={mine.name} />{:else}<span class="blank" aria-hidden="true"></span>{/if}
      <strong>{mine ? mine.name : me?.championId ? `Champion #${me.championId}` : 'Waiting for your champion'}
        {#if ranked?.mine?.rate && rates.mode}<small class="wr">{rateText(ranked.mine.rate)} in {modeName(rates.mode)}</small>{/if}</strong>
      {#if board.allowRerolling}
        <button type="button" class="quiet" disabled={busy || board.rerolls <= 0 || !canAct} onclick={reroll}>Reroll ({board.rerolls})</button>
      {/if}
    </div>
    {#if board.offered?.length}
      <h3>Offered to you</h3>
      <div class="bench">
        {#each offeredIds as id (id)}
          {@const c = champions.byId(id)}
          {@const r = rateOf(id)}
          <button type="button" class="quiet tile" class:picked={me?.championId === id} class:better={r?.better} disabled={busy || !canPick} onclick={() => pick(id)} title={c ? c.name : `#${id}`}>
            {#if c}<img src={c.icon} alt={c.name} />{:else}<span class="blank" aria-hidden="true"></span>{/if}
            <span>{c ? c.name : `#${id}`}</span>
            {#if r?.rate}<span class="wr">{rateText(r.rate)}</span>{/if}
          </button>
        {/each}
      </div>
    {/if}
    <h3>Bench</h3>
    <div class="bench">
      {#each benchIds as id, i (i + ':' + id)}
        {@const c = champions.byId(id)}
        {@const r = rateOf(id)}
        <button type="button" class="quiet tile" class:better={r?.better} disabled={busy || !canAct} onclick={() => take(id)} title={c ? c.name : `#${id}`}>
          {#if c}<img src={c.icon} alt={c.name} />{:else}<span class="blank" aria-hidden="true"></span>{/if}
          <span>{c ? c.name : `#${id}`}</span>
          {#if r?.rate}<span class="wr">{rateText(r.rate)}</span>{/if}
        </button>
      {:else}
        <p class="muted">The bench is empty. Champions your teammates give up show up here.</p>
      {/each}
    </div>
    {#if err}<p class="err" role="alert">{err}</p>{/if}
  </Card>
{/if}

<style>
  .me { display: flex; align-items: center; gap: .75rem; }
  .me strong { flex: 1; }
  img, .blank { width: 2.6rem; height: 2.6rem; border-radius: .5rem; flex: none; }
  .blank { background: #0f0a16; }
  h3 { font-size: .85rem; color: var(--muted); font-weight: 600; margin-top: .25rem; }
  .bench { display: flex; flex-wrap: wrap; gap: .5rem; }
  .picked { outline: 2px solid var(--accent, currentColor); border-radius: .5rem; }
  .me strong { display: flex; flex-direction: column; }
  .wr { font-size: .72rem; color: var(--muted); font-weight: 600; font-variant-numeric: tabular-nums; }
  .better { outline: 1px solid var(--ok); border-radius: .5rem; }
  .better .wr { color: var(--ok); }
  .tile { display: grid; justify-items: center; gap: .2rem; padding: .4rem; min-width: 4.5rem; font-size: .72rem; }
</style>
