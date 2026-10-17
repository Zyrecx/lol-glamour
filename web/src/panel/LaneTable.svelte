<script lang="ts">
  // The panel's Auto tab: every lane setup in one table, a row per lane (All, then each lane) with ban, lock and pre-select tiles that open the
  // champion picker and a timing chip under ban and lock. A lane without its own setup shows All's dimmed; editing it gives the lane its own
  // (a copy of All with the change), the x on the row goes back to All. Edits go through live.patch, so they follow Auto's "For" scope.
  import { champions } from '../lib/champions.svelte'
  import { live } from '../lib/live.svelte'
  import ChampionPicker from '../lib/components/ChampionPicker.svelte'
  import Toggle from '../lib/components/Toggle.svelte'
  import LaneIcon from '../shell/parts/Lane.svelte'
  import type { LaneKey, LaneSetup, Settings, Timing } from '../lib/types'

  const s = $derived(live.view)
  const ROWS: { key: 'ALL' | LaneKey; label: string; icon: 'FILL' | LaneKey }[] = [
    { key: 'ALL', label: 'All', icon: 'FILL' }, { key: 'TOP', label: 'Top', icon: 'TOP' }, { key: 'JUNGLE', label: 'Jgl', icon: 'JUNGLE' },
    { key: 'MIDDLE', label: 'Mid', icon: 'MIDDLE' }, { key: 'BOTTOM', label: 'Bot', icon: 'BOTTOM' }, { key: 'SUPPORT', label: 'Sup', icon: 'SUPPORT' },
  ]
  type Field = 'banChampionId' | 'lockChampionId' | 'hoverChampionId'
  const COLS: { field: Field; timing: 'banTiming' | 'lockTiming' | null; kind: string; title: string }[] = [
    { field: 'banChampionId', timing: 'banTiming', kind: 'ban', title: 'Ban' },
    { field: 'lockChampionId', timing: 'lockTiming', kind: 'lock', title: 'Lock' },
    { field: 'hoverChampionId', timing: null, kind: 'hover', title: 'Pre-select' },
  ]

  const all = (x: Settings): LaneSetup => ({ banChampionId: x.autoBanChampionId, banTiming: x.banTiming, lockChampionId: x.autoLockChampionId, lockTiming: x.lockTiming, hoverChampionId: x.preHoverChampionId })
  const setupOf = (x: Settings, key: 'ALL' | LaneKey) => (key === 'ALL' ? all(x) : x.lanes?.[key] ?? all(x))
  const ownOf = (x: Settings, key: 'ALL' | LaneKey) => key === 'ALL' || !!x.lanes?.[key]

  let msg = $state('')
  async function save(change: Partial<Settings>) { msg = (await live.patch(change)) ?? '' }
  function change(key: 'ALL' | LaneKey, c: Partial<LaneSetup>) {
    if (!s) return
    if (key !== 'ALL') { save({ lanes: { ...s.lanes, [key]: { ...setupOf(s, key), ...c } } }); return }
    save({
      ...('banChampionId' in c && { autoBanChampionId: c.banChampionId }), ...(c.banTiming && { banTiming: c.banTiming }),
      ...('lockChampionId' in c && { autoLockChampionId: c.lockChampionId }), ...(c.lockTiming && { lockTiming: c.lockTiming }),
      ...('hoverChampionId' in c && { preHoverChampionId: c.hoverChampionId }),
    })
  }
  function reset(key: LaneKey) {
    if (!s) return
    const lanes = { ...s.lanes }
    delete lanes[key]
    save({ lanes })
  }
  const flip = (t: Timing): Timing => (t === 'Instant' ? 'LastSecond' : 'Instant')
  const CHIP: Record<Timing, string> = { Instant: 'Turn', LastSecond: 'Last' }
  $effect(() => { champions.ensure() })
</script>

{#if s}
  <section class="lanes">
    <h2 class="band">Ban and lock<span class="aside">per lane</span></h2>
    <div class="grid" role="table" aria-label="Ban and lock per lane">
      <div class="head" role="row">
        <span role="columnheader"></span>
        {#each COLS as col (col.field)}<span role="columnheader">{col.title}</span>{/each}
        <span role="columnheader"></span>
      </div>
      {#each ROWS as row (row.key)}
        {@const setup = setupOf(s, row.key)}
        {@const own = ownOf(s, row.key)}
        <div class="row" class:inherit={!own} role="row">
          <span class="who" role="rowheader" title={own ? '' : 'Same as All'}><LaneIcon lane={row.icon} size={18} />{row.label}</span>
          {#each COLS as col (col.field)}
            {@const id = setup[col.field]}
            {@const c = champions.byId(id)}
            <span class={`cell ${col.kind}`} role="cell">
              <ChampionPicker bestCounter={col.kind === 'lock'} label={`${col.title} · ${row.label === 'All' ? 'all lanes' : row.label}`} value={id} onpick={(v) => change(row.key, { [col.field]: v })}>
                {#snippet trigger(show)}
                  <button type="button" class="tile" onclick={show} title={`${col.title}: ${c?.name ?? 'not set'}`} aria-label={`${col.title} for ${row.label}: ${c?.name ?? 'not set'}`}>
                    {#if c}<img src={c.icon} alt="" width="30" height="30" />{:else}<span class="slot" aria-hidden="true">+</span>{/if}
                  </button>
                {/snippet}
              </ChampionPicker>
              {#if col.timing && id != null}
                {@const t = setup[col.timing]}
                <button type="button" class="chip" title={t === 'Instant' ? 'On my turn (tap for last second)' : 'Last second (tap for on my turn)'}
                  onclick={() => change(row.key, { [col.timing!]: flip(t) })}>{CHIP[t]}</button>
              {/if}
            </span>
          {/each}
          <span class="end" role="cell">
            {#if row.key !== 'ALL' && own}
              <button type="button" class="reset" title="Back to All" aria-label={`${row.label}: back to All`} onclick={() => reset(row.key as LaneKey)}>
                <svg width="10" height="10" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="3" stroke-linecap="round" aria-hidden="true"><path d="M6 6l12 12M18 6L6 18" /></svg>
              </button>
            {/if}
          </span>
        </div>
      {/each}
    </div>
    <div class="opts">
      <Toggle label="Skip a ban a teammate hovers" checked={s.skipBanIfAllyHovered} onchange={(v) => save({ skipBanIfAllyHovered: v })} />
    </div>
    {#if msg}<p class="err" role="alert">{msg}</p>{/if}
  </section>
{/if}

<style>
  .lanes { display: flex; flex-direction: column; }
  .band { margin: 0; display: flex; align-items: center; gap: 8px; min-height: 30px; padding: 0 12px; background: var(--l-band); border-bottom: 1px solid var(--l-frame);
    font: 700 11px var(--g-body); letter-spacing: .1em; text-transform: uppercase; color: var(--l-grey1); }
  .band .aside { margin-left: auto; color: var(--l-grey2); letter-spacing: .04em; text-transform: none; font-weight: 400; }
  .grid { display: flex; flex-direction: column; padding: 2px 0 4px; }
  .head, .row { display: grid; grid-template-columns: 52px repeat(3, 1fr) 22px; align-items: center; padding: 0 8px 0 12px; }
  .head span { font-size: 10px; letter-spacing: .02em; white-space: nowrap; text-transform: uppercase; color: var(--l-grey2); text-align: center; padding: 4px 0 2px; }
  .row { min-height: 50px; border-top: 1px solid rgba(30, 40, 45, .6); }
  .who { display: flex; align-items: center; gap: 5px; font-size: 12px; color: var(--l-gold1); }
  .inherit .who { color: var(--l-grey2); }
  .cell { display: flex; flex-direction: column; align-items: center; gap: 2px; }
  .tile { width: 32px; height: 32px; min-height: 0 !important; padding: 0 !important; border: 0 !important; background: none !important; cursor: pointer; line-height: 0; }
  .tile img, .slot { width: 32px; height: 32px; box-sizing: border-box; border: 1px solid var(--l-gold4); display: block; }
  .slot { display: grid; place-items: center; border-style: dashed; border-color: var(--l-grey3); color: var(--l-grey2); font-size: 15px; line-height: 1; }
  .tile:hover .slot { border-color: var(--l-gold4); color: var(--l-gold2); }
  .ban img { border-color: #6B2A2C; }
  .hover img { border-style: dashed; }
  .inherit .tile img { opacity: .4; filter: grayscale(.6); }
  .inherit .tile:hover img { opacity: .8; }
  .chip { min-height: 0 !important; height: 14px; padding: 0 5px !important; font-size: 9px !important; letter-spacing: .06em; line-height: 12px; }
  .ban .chip { border-color: #5A2A2A !important; color: #F0B4B4 !important; }
  .lock .chip { border-color: #0A5A60 !important; color: var(--l-blue-ink) !important; }
  .inherit .chip { opacity: .5; }
  .end { display: grid; place-items: center; }
  .reset { width: 18px; height: 18px; min-height: 0 !important; padding: 0 !important; display: grid; place-items: center; border: 1px solid var(--l-grey3) !important; background: none !important; color: var(--l-grey1) !important; cursor: pointer; }
  .reset:hover { color: var(--l-gold1) !important; border-color: var(--l-gold4) !important; }
  .opts { padding: 0 12px; border-top: 1px solid var(--l-frame); }
  .err { margin: 6px 12px; font-size: 12px; color: #E86A6C; }
</style>
