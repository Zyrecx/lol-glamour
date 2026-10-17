<script lang="ts">
  // Ask one teammate for a lane swap, pick-order swap or champion trade. Each option previews the result and says why it is closed.
  // Only sent on a tap; they answer in their own client (hard rule: never touch other players unasked).
  import { champions } from '../../lib/champions.svelte'
  import { live } from '../../lib/live.svelte'
  import type { Board, BoardPlayer } from '../../lib/types'
  import Lane from '../parts/Lane.svelte'
  import Sheet from '../parts/Sheet.svelte'
  import { laneName, ordinal } from '../util'

  let { board, target, nameOf, onclose }: { board: Board; target: BoardPlayer; nameOf: (p: BoardPlayer) => string; onclose: () => void } = $props()
  const me = $derived(board.myTeam.find((p) => p.isMe)!)
  const offer = (kind: string) => board.swaps?.find((w) => w.kind === kind && w.cellId === target.cellId && w.state === 'AVAILABLE')
  const theirChamp = $derived(champions.byId(target.championId)?.name ?? 'their champion')
  const myChamp = $derived(champions.byId(me.championId)?.name ?? 'yours')
  let busy = $state(false)
  let err = $state('')

  // Once everyone has locked in (FINALIZATION) only trades are possible; before that trades do not exist. Show only what can apply.
  const final = $derived(board.stage === 'FINALIZATION')
  const options = $derived([
    {
      kind: 'position', title: 'Swap lanes', offer: offer('position'),
      closed: target.lane && target.lane === me.lane ? 'You already share a lane.' : !target.lane ? 'This mode has no lanes.' : 'The client offers no lane swap with this player right now.',
    },
    {
      kind: 'pick-order', title: 'Swap pick order', offer: offer('pick-order'),
      closed: me.locked || target.locked ? 'Closed: one of you has already picked.' : 'The client offers no pick-order swap with this player right now.',
    },
    {
      kind: 'trade', title: 'Trade champions', offer: offer('trade'),
      closed: board.stage !== 'FINALIZATION' ? 'Opens once everyone has locked in.' : 'The client offers no trade with this player.',
    },
  ].filter((o) => (o.kind === 'trade') === final || (o.offer && !final)))

  async function ask(kind: string, id: number) {
    busy = true
    err = (await live.act(`/swap/${kind}/${id}/request`)) ?? ''
    busy = false
    if (!err) onclose()
  }
</script>

<Sheet label="Ask for a swap" {onclose}>
  <div class="who">
    {#if champions.byId(target.championId)}<img src={champions.byId(target.championId)!.icon} alt="" />{:else}<span class="blank"></span>{/if}
    <div>
      <span class="display" style="font-size: 20px">{nameOf(target)}</span>
      <span class="sub"><Lane lane={target.lane} size={15} />{laneName(target.lane) || 'No lane'}{target.pickTurn ? ` · picks ${ordinal(target.pickTurn)}` : ''}</span>
    </div>
  </div>
  {#each options as o (o.kind)}
    <button type="button" class="opt" disabled={busy || !o.offer} onclick={() => o.offer && ask(o.kind, o.offer.id)}>
      <b>{o.title}</b>
      <span class="prev">
        {#if o.kind === 'position'}
          <span>You'd play</span><span class="to"><Lane lane={target.lane} size={18} />{laneName(target.lane)}</span><span>· {nameOf(target)} plays {laneName(me.lane)}</span>
        {:else if o.kind === 'pick-order'}
          <span>You'd pick</span><b class="to">{target.pickTurn ? ordinal(target.pickTurn) : '?'}</b><span>instead of {me.pickTurn ? ordinal(me.pickTurn) : '?'}</span>
        {:else}
          <span>You'd play</span><b class="to">{theirChamp}</b><span>· they get {myChamp}</span>
        {/if}
      </span>
      {#if !o.offer}<span class="why">{o.closed}</span>{/if}
    </button>
  {/each}
  <span class="muted">Only sent when you tap. They answer in their client.</span>
  {#if err}<p class="err" role="alert">{err}</p>{/if}
</Sheet>

<style>
  .who { display: flex; align-items: center; gap: 12px; }
  .who img, .blank { width: 48px; height: 48px; border-radius: 8px; border: 1px solid var(--g-gold); background: var(--g-bg); flex: none; }
  .who > div { display: flex; flex-direction: column; gap: 2px; }
  .sub { font-size: 13px; color: var(--g-muted); display: flex; align-items: center; gap: 5px; }
  .sub :global(svg) { color: var(--g-gold); }
  .opt { display: flex; flex-direction: column; align-items: flex-start; gap: 6px; padding: 12px; border-radius: 12px; border: 1px solid rgba(200,170,110,.35); background: var(--g-bg); color: var(--g-fg); text-align: left; width: 100%; min-height: 0; }
  .opt:hover:not(:disabled) { border-color: var(--g-teal); filter: none; }
  .opt:disabled { opacity: .5; border-color: rgba(166,159,143,.25); }
  .opt b { font-size: 15px; }
  .prev { display: flex; align-items: center; flex-wrap: wrap; gap: 6px; font-size: 13.5px; color: var(--g-fg2); }
  .to { color: var(--g-teal); display: inline-flex; align-items: center; gap: 4px; }
  .why { font-size: 12px; color: var(--g-muted); }
</style>
