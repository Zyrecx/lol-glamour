<script lang="ts">
  // Champ select on the PC Home, read only: both teams, who is picking, bans as a badge on the player who banned (as on the phone), the timer,
  // and the counter picks (choosing who I face is the only tap). Everything to act on stays in the League client.
  import { untrack } from 'svelte'
  import { champions } from '../../lib/champions.svelte'
  import { live } from '../../lib/live.svelte'
  import type { Board, BoardPlayer } from '../../lib/types'
  import Lane from '../../shell/parts/Lane.svelte'
  import Ring from '../../shell/parts/Ring.svelte'
  import { tick } from '../../shell/tick.svelte'
  import CounterList from '../../shell/parts/CounterList.svelte'
  import { counterFeed } from '../../lib/counterFeed.svelte'
  import { demoFromUrl } from '../../shell/demo'
  import { headOf, laneShort, partySummary, statusOf } from '../../shell/util'

  let { board }: { board: Board } = $props()
  const dealt = $derived(board.mode === 'aram' || board.mode === 'mayhem')
  const nameOf = (p: BoardPlayer) => p.name?.split('#')[0] || champions.byId(p.championId)?.name || `Ally ${p.cellId + 1}`
  const head = $derived(headOf(board, live.now.turn?.type ?? null, nameOf))
  const bansBy = $derived(new Map((board.banList ?? []).filter((b) => b.championId > 0).map((b) => [b.cellId, b.championId])))
  // Without banList (no actor known) the bans show as a row under each team.
  const looseBans = $derived(board.banList ? null : { mine: board.myBans, theirs: board.theirBans })

  const feed = counterFeed(() => board, demoFromUrl(location.search) !== null)
  const counter = $derived(feed.view && feed.view.state !== 'none' && (feed.view.state !== 'locked' || feed.view.mine) ? feed.view : null)

  let boardAt = $state(Date.now())
  $effect(() => { void board; boardAt = Date.now() })
  $effect(() => tick.use())
  $effect(() => { champions.ensure() })
  const left = $derived(Math.max(0, board.timeLeftMs - (tick.now - boardAt)))
  let phaseMs = $state(30000)
  const turnKey = $derived(`${board.stage}:${board.acting.map((t) => t.cellId).join(',')}`)
  $effect(() => { void turnKey; phaseMs = untrack(() => Math.max(board.timeLeftMs, 1000)) })
</script>

{#snippet team(players: BoardPlayer[], mine: boolean, bans: number[] | undefined)}
  <div class="team" class:mine>
    <span class="eyebrow">{mine ? 'Your team' : 'Enemy team'}</span>
    {#each players as p (p.cellId)}
      {@const c = champions.byId(p.championId)}
      {@const st = statusOf(board, p.cellId)}
      {@const ban = champions.byId(bansBy.get(p.cellId) ?? 0)}
      <div class="prow" class:self={p.isMe}>
        <span class="pic" class:hov={!p.locked && !!c} class:acting={st === 'PICKING'}>
          {#if c}<img src={c.icon} alt="" />{:else}<span class="unk">{#if mine && p.lane}<Lane lane={p.lane} size={22} />{:else}?{/if}</span>{/if}
          {#if ban}<img class="banb" src={ban.icon} alt={`Banned ${ban.name}`} title={`Banned ${ban.name}`} />{/if}
        </span>
        <span class="txt">
          <b>{p.isMe ? 'You' : mine ? nameOf(p) : (c?.name ?? 'Not picked')}</b>
          <small>
            {#if mine && p.lane}<Lane lane={p.lane} size={12} />{laneShort(p.lane)}{/if}
            {#if st === 'PICKING'}<span class="now">Picking</span>{:else if st === 'NEXT'}<span>Next</span>{:else if c && !p.locked}<span>Hovering</span>{/if}
          </small>
        </span>
      </div>
    {/each}
    {#if bans?.length}
      <div class="bans">{#each bans as b, i (i)}{@const c = champions.byId(b)}{#if c}<img src={c.icon} alt={`${c.name} banned`} title={`${c.name} banned`} />{/if}{/each}</div>
    {/if}
  </div>
{/snippet}

<section class="panel board">
  <div class="top">
    <div>
      {#if !dealt}<span class="label" class:mine={head.mine}>{head.label}</span>{/if}
      {#if head.sub}<span class="sub">{head.sub}</span>{/if}
    </div>
    <Ring size={52} stroke={4} fraction={left / phaseMs}><span class="num secs">{Math.ceil(left / 1000)}</span></Ring>
  </div>
  <div class="teams">
    {@render team(board.myTeam, true, looseBans?.mine)}
    {#if board.theirTeam.length && !dealt}{@render team(board.theirTeam, false, looseBans?.theirs)}{/if}
  </div>
  {#if counter && !dealt}<div class="counters"><CounterList view={counter} setForced={(id) => (feed.forced = id)} /></div>{/if}
  {#if live.now.parties}<p class="parties"><span class="eyebrow">Parties</span> {partySummary(live.now.parties)}</p>{/if}
</section>

<style>
  .board { display: flex; flex-direction: column; gap: 14px; }
  .top { display: flex; align-items: center; justify-content: space-between; gap: 12px; }
  .top > div { display: flex; flex-direction: column; gap: 2px; }
  .label { color: var(--g-gold); }
  .label.mine { color: var(--g-teal); }
  .sub { font-size: 13px; color: var(--g-fg2); }
  .secs { font-size: 17px; font-weight: 700; }
  .teams { display: grid; grid-template-columns: repeat(auto-fit, minmax(240px, 1fr)); gap: 18px; }
  .team { display: flex; flex-direction: column; gap: 8px; }
  .prow { display: flex; align-items: center; gap: 10px; min-width: 0; }
  .pic { position: relative; flex: none; width: 44px; height: 44px; }
  .pic img:first-child, .unk { width: 44px; height: 44px; border-radius: 8px; border: 1px solid rgba(200,170,110,.45); box-sizing: border-box; display: flex; align-items: center; justify-content: center; background: rgba(6,10,18,.6); }
  .unk { border-style: dashed; color: var(--g-faint); font-weight: 700; }
  .pic.hov img:first-child { opacity: .55; border-style: dashed; }
  .self .pic img:first-child, .self .pic .unk { border: 2px solid var(--g-teal); }
  .pic.acting img:first-child, .pic.acting .unk { opacity: 1; box-shadow: 0 0 0 2px var(--g-teal), 0 0 12px rgba(45,226,196,.7); }
  .banb { position: absolute; right: -6px; bottom: -5px; width: 22px; height: 22px; border-radius: 4px; border: 1px solid var(--g-magenta); filter: grayscale(1) brightness(.75); background: var(--g-bg); }
  .txt { display: flex; flex-direction: column; min-width: 0; }
  .txt b { font-size: 14px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .txt small { display: flex; align-items: center; gap: 6px; font-size: 12px; color: var(--g-muted); min-height: 16px; }
  .txt small :global(svg) { color: var(--g-gold); }
  .now { color: var(--g-teal); font-weight: 700; }
  .counters { padding-top: 12px; border-top: 1px solid rgba(200,170,110,.2); max-width: 34rem; }
  .parties { margin: 0; font-size: 14px; color: var(--g-fg2); }
  .bans { display: flex; gap: 4px; padding-top: 6px; border-top: 1px solid rgba(226,79,166,.35); }
  .bans img { width: 26px; height: 26px; border-radius: 4px; filter: grayscale(1) brightness(.7); border: 1px solid rgba(226,79,166,.5); }
</style>
