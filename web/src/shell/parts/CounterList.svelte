<script lang="ts">
  // The counter pick view as a list (SPEC #3): who I face (tap to choose), the picks against them, my champion against the enemy team.
  // With `onpick` a pick row is a button (the phone hovers it); without, read only (PC Home).
  import { champions } from '../../lib/champions.svelte'
  import { counterRows, counterSorts, gamesLabel, opponentChance, percent, waitingNote, type CounterPickView, type CounterSort } from '../../lib/counterPick'
  import DamageBar from '../../lib/components/DamageBar.svelte'
  import { laneName } from '../util'

  let { view, setForced, usable, onpick }: {
    view: CounterPickView; setForced: (id: number | null) => void; usable?: (id: number) => boolean; onpick?: (id: number) => void
  } = $props()
  const enemy = $derived(champions.byId(view.enemyId))
  const mine = $derived(view.mine && (view.mine.picked || view.mine.lines.length) ? view.mine : null)
  // Against my lane opponent or their whole team; the team list alone until my opponent is known.
  let sortBy = $state<CounterSort>('lane')
  const sorts = $derived(counterSorts(view))
  const list = $derived(view.state !== 'locked' ? counterRows(view, sortBy) : null)
</script>

<div class="list">
{#if view.enemies?.length}
  <div class="hd"><span class="label">Who you face</span>{#if view.forced}<button type="button" class="link" onclick={() => setForced(null)}>Auto</button>{/if}</div>
  <div class="enemies">
    {#each view.enemies as id (id)}
      {@const c = champions.byId(id)}
      <button type="button" class="enemy" class:on={id === view.enemyId} aria-pressed={id === view.enemyId} aria-label={c?.name ?? `#${id}`} onclick={() => setForced(id)}>
        {#if c}<img src={c.icon} alt="" />{:else}<span class="ph"></span>{/if}
      </button>
    {/each}
  </div>
{/if}

{#if list}
  <div class="hd"><span class="label">Picks</span>
    <span class="sorts" role="group" aria-label="Sort picks">
      {#if sorts.includes('lane')}<button type="button" class:on={list.sort === 'lane'} aria-pressed={list.sort === 'lane'} onclick={() => (sortBy = 'lane')}>vs {enemy?.name ?? 'lane'}{#if opponentChance(view) != null} {opponentChance(view)}%{/if}</button>{/if}
      {#if sorts.includes('team')}<button type="button" class:on={list.sort === 'team'} aria-pressed={list.sort === 'team'} onclick={() => (sortBy = 'team')}>vs team</button>{/if}
    </span>
  </div>
  {#if list.sort === 'lane' && view.dataLane}<p class="muted">{enemy?.name ?? 'They'} rarely plays {laneName(view.lane).toLowerCase()}: counters from {laneName(view.dataLane).toLowerCase()}.</p>
  {:else if view.state === 'nodata'}<p class="muted">No lane data for {enemy?.name ?? 'that champion'}.</p>
  {:else if view.state === 'waiting'}<p class="muted">{waitingNote(view, laneName(view.lane).toLowerCase(), (id) => champions.byId(id)?.name ?? `#${id}`)}</p>{/if}
  {#if view.duo?.best.length}<p class="duo-line">Good with {champions.byId(view.duo.partnerId)?.name ?? 'your duo'}: {view.duo.best.map((id) => champions.byId(id)?.name ?? `#${id}`).join(', ')}</p>{/if}
  <ul class="rows">
    {#each list.rows as p (p.championId)}
      {@const c = champions.byId(p.championId)}
      <li>
        <svelte:element this={onpick ? 'button' : 'div'} role={onpick ? undefined : 'group'} type={onpick ? 'button' : undefined} class="row" class:act={!!onpick}
          disabled={onpick ? !usable?.(p.championId) : undefined} onclick={() => onpick?.(p.championId)} aria-label={onpick ? `Hover ${c?.name ?? ''}` : c?.name}>
          {#if c}<img src={c.icon} alt="" />{:else}<span class="ph"></span>{/if}
          <span class="who">
            <span class="nm">{c?.name ?? `#${p.championId}`}{#if p.inPool}<small>pool</small>{/if}{#if p.duo}<small class="duo">duo</small>{/if}{#if p.auto}<small>auto</small>{/if}</span>
            <span class="games" title={p.sub.title}>{p.sub.text}</span>
          </span>
          <span class="nums">
            <span class="wr num" class:good={p.main >= 50}>{percent(p.main)}</span>
            {#if p.alt != null}<span class="alt">{p.altLabel} {p.alt.toFixed(1)}</span>{/if}
          </span>
        </svelte:element>
      </li>
    {:else}
      <li class="muted">No data for this matchup.</li>
    {/each}
  </ul>
{:else if view.state === 'nodata'}
  <p class="muted">No data for {enemy?.name ?? 'that champion'}.</p>
{:else if view.state === 'waiting'}
  <p class="muted">{waitingNote(view, laneName(view.lane).toLowerCase(), (id) => champions.byId(id)?.name ?? `#${id}`)}</p>
{/if}

{#if view.damage}
  <div class="hd"><span class="label">Their damage</span></div>
  <div class="dmg"><DamageBar damage={view.damage} /></div>
{/if}

{#if mine}
  {@const mc = champions.byId(mine.championId)}
  <div class="hd"><span class="label">{mc?.name ?? 'Your pick'} {mine.picked ? 'vs enemy team' : 'is weak vs'}</span><span class="lane">{laneName(view.lane)}</span></div>
  <ul class="rows">
    {#each mine.lines as l (l.championId)}
      {@const c = champions.byId(l.championId)}
      {@const g = gamesLabel(l)}
      <li class="line" class:opp={mine.picked && l.championId === view.enemyId}>
        {#if c}<img src={c.icon} alt="" />{:else}<span class="ph"></span>{/if}
        <span class="who"><span class="nm">{c?.name ?? `#${l.championId}`}</span><span class="games" title={g.title}>{g.text}</span></span>
        <span class="wr num" class:good={l.winRate >= 50}>{percent(l.winRate)}</span>
      </li>
    {:else}
      <li class="muted">No games against them in {laneName(view.lane).toLowerCase()}.</li>
    {/each}
  </ul>
{/if}
</div>

<style>
  .list { display: flex; flex-direction: column; gap: 10px; }
  .hd { display: flex; align-items: center; gap: 8px; margin-top: 4px; }
  .label { font: 700 11px var(--g-body); letter-spacing: .08em; text-transform: uppercase; color: var(--g-gold); }
  .lane { margin-left: auto; font-size: 12px; color: var(--g-muted); }
  .duo-line { margin: 0 0 6px; font-size: 13px; color: var(--g-gold); }
  .link { margin-left: auto; background: none; border: none; padding: 0; min-height: 0; color: var(--g-teal); font: 600 13px var(--g-body); }
  .enemies { display: flex; gap: 8px; flex-wrap: wrap; }
  .enemy { padding: 0; min-height: 0; border-radius: 8px; border: 2px solid transparent; background: none; }
  .enemy.on { border-color: var(--g-teal); }
  .enemy img, .enemy .ph { width: 44px; height: 44px; border-radius: 6px; display: block; }
  .rows { list-style: none; margin: 0; padding: 0; display: flex; flex-direction: column; gap: 6px; }
  .row, .line { display: flex; align-items: center; gap: 10px; width: 100%; padding: 8px 10px; border-radius: 10px; border: 1px solid rgba(200,170,110,.25); background: var(--g-bg); color: var(--g-fg); text-align: left; min-height: 0; font: inherit; }
  .row.act:hover:not(:disabled) { border-color: var(--g-teal); filter: none; }
  .row:disabled { opacity: .55; }
  .line.opp { border-color: rgba(200,170,110,.6); }
  .row img, .line img, .ph { width: 36px; height: 36px; border-radius: 6px; flex: none; background: rgba(255,255,255,.06); }
  .who { display: flex; flex-direction: column; min-width: 0; flex: 1; }
  .nm { font-weight: 600; font-size: 15px; }
  .nm small { margin-left: 6px; font-size: 10px; letter-spacing: .06em; text-transform: uppercase; color: var(--g-teal); }
  .nm small.duo { color: var(--g-gold); }
  .games { font-size: 12px; color: var(--g-muted); }
  .wr { font-weight: 700; font-size: 15px; color: var(--g-fg2); }
  .wr.good { color: var(--g-teal); }
  .sorts { margin-left: auto; display: flex; gap: 4px; }
  .sorts button { min-height: 28px; padding: 0 10px; border-radius: 14px; border: 1px solid rgba(200,170,110,.3); background: none; color: var(--g-muted); font: 600 12px var(--g-body); }
  .sorts button.on { border-color: var(--g-teal); color: var(--g-fg); }
  .nums { display: flex; flex-direction: column; align-items: flex-end; }
  .alt { font-size: 11px; color: var(--g-muted); white-space: nowrap; }
  .dmg { color: var(--g-fg2); }
  .muted { color: var(--g-muted); font-size: 13px; margin: 0; }
</style>
