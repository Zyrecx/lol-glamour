<script lang="ts">
  import { bestCounter as best, champions } from '../champions.svelte'
  import LaneIcon from '../../shell/parts/Lane.svelte'
  import Icon from './Icon.svelte'
  import type { Snippet } from 'svelte'
  import type { Lane } from '../types'

  const LANES: { id: Lane; label: string }[] = [
    { id: 'TOP', label: 'Top' }, { id: 'JUNGLE', label: 'Jungle' }, { id: 'MIDDLE', label: 'Mid' },
    { id: 'BOTTOM', label: 'Bot' }, { id: 'SUPPORT', label: 'Support' },
  ]

  let { label, value, onpick, only, taken, clearable = true, trigger, bestCounter = false }: {
    label: string; value: number | null; onpick: (id: number | null) => void
    /** Offers "Best counter" first (a lock slot): the host picks the counter on the turn. */
    bestCounter?: boolean
    /** Draws its own opener instead of the field button; call `show` to open the picker. */
    trigger?: Snippet<[() => void]>
    /** When set, only these champion ids can be chosen (the client's pickable list). */
    only?: number[]
    /** Champions already banned or locked, with the reason shown on the tile; listed last, dimmed and not tappable. */
    taken?: Map<number, string>
    clearable?: boolean
  } = $props()

  let open = $state(false)
  let query = $state('')
  let search = $state<HTMLInputElement>()
  let lane = $state<Lane | null>(null)
  // Lanes come from an outside source; with none loaded the chips would filter everything away, so hide them.
  const hasLanes = $derived(champions.list.some((c) => c.positions?.length))
  const current = $derived(champions.byId(value))
  const shown = $derived(champions.list.filter((c) =>
    (!only || only.includes(c.id) || !!taken?.has(c.id)) && c.name.toLowerCase().includes(query.trim().toLowerCase()) && (lane === null || !!c.positions?.includes(lane)))
    .sort((a, b) => Number(!!taken?.has(a.id)) - Number(!!taken?.has(b.id))))

  // Choosing nothing is a tile like the champions, so the only button beside the search box is not mistaken for clearing the search.
  const showNone = $derived(clearable && lane === null && query.trim() === '')
  const showBest = $derived(bestCounter && lane === null && best.name.toLowerCase().includes(query.trim().toLowerCase()))
  function show() { open = true; query = ''; lane = null; champions.ensure() }
  // Focus the search box on a PC; on a phone it would pop the keyboard over the grid before you have seen it.
  $effect(() => { if (open && search && matchMedia('(pointer: fine)').matches) search.focus() })
  function pick(id: number | null) { open = false; onpick(id) }
  // Fixed overlays inside an animated tab are trapped under the tab bar (the fade-in animation makes a stacking context), so the picker
  // moves itself to the shell root while open.
  function portal(node: HTMLElement) {
    const root = node.closest('.glam') ?? document.body
    root.appendChild(node)
    return { destroy() { node.remove() } }
  }
</script>

<svelte:window onkeydown={(e) => { if (open && e.key === 'Escape') open = false }} />

{#if trigger}{@render trigger(show)}{:else}
<button type="button" class="field" onclick={show} aria-haspopup="dialog">
  {#if current}<img src={current.icon} alt="" />{:else}<span class="empty"></span>{/if}
  <span class="txt">
    <span class="muted">{label}</span>
    <span class="name">{current ? current.name : value != null ? champions.list.length ? `Champion #${value}` : '…' : 'Not set'}</span>
  </span>
</button>
{/if}

{#if open}
  <div class="layer" use:portal>
  <div class="scrim" role="presentation" onclick={() => (open = false)}></div>
  <div class="sheet" role="dialog" aria-modal="true" aria-label={`Choose ${label}`}>
    <div class="title">
      <span class="what">{label}</span>
      <button type="button" class="quiet icon" aria-label="Close" onclick={() => (open = false)}><Icon name="close" /></button>
    </div>
    <header>
      <input type="search" placeholder="Search champions" bind:this={search} bind:value={query} autocapitalize="off" autocomplete="off" />
    </header>
    {#if hasLanes}
      <div class="lanes" role="group" aria-label="Filter by lane">
        <button type="button" class="lane" class:on={lane === null} aria-pressed={lane === null} onclick={() => (lane = null)}><LaneIcon lane="FILL" size={24} /><span>All</span></button>
        {#each LANES as l (l.id)}
          <button type="button" class="lane" class:on={lane === l.id} aria-pressed={lane === l.id} onclick={() => (lane = lane === l.id ? null : l.id)}><LaneIcon lane={l.id} size={24} /><span>{l.label}</span></button>
        {/each}
      </div>
    {/if}
    <div class="grid">
      {#if champions.loading}<p class="muted">Loading…</p>{/if}
      {#if champions.error}<p class="err">{champions.error}</p>{/if}
      {#if !champions.loading && shown.length === 0 && champions.list.length > 0}<p class="muted">No champions match.</p>{/if}
      {#if showNone}
        <button type="button" class="champ none" class:sel={value == null} onclick={() => pick(null)}>
          <span class="inner"><span class="blank"></span><span>None</span></span>
        </button>
      {/if}
      {#if showBest}
        <button type="button" class="champ best" class:sel={value === best.id} onclick={() => pick(best.id)} title="Locks the top counter pick when your turn comes">
          <span class="inner"><img src={best.icon} alt="" /><span>{best.name}</span></span>
        </button>
      {/if}
      {#each shown as c (c.id)}
        {@const why = taken?.get(c.id)}
        <button type="button" class="champ" class:sel={c.id === value} class:gone={!!why} disabled={!!why} onclick={() => pick(c.id)}
          aria-label={why ? `${c.name}, ${why.toLowerCase()}` : undefined}>
          <span class="inner">
            <img src={c.icon} alt="" loading="lazy" />
            <span>{c.name}</span>
            {#if why}<span class="tag">{why}</span>{/if}
          </span>
        </button>
      {/each}
    </div>
  </div>
  </div>
{/if}

<style>
  .field { display: flex; align-items: center; gap: .75rem; width: 100%; text-align: left; background: var(--bg, #0f0a16); border: 1px solid var(--line); color: var(--fg); padding: .5rem .75rem; min-height: 3.5rem; }
  .txt { min-width: 0; }
  .name { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .field img, .empty { width: 2.4rem; height: 2.4rem; border-radius: .5rem; flex: none; }
  .empty { background: var(--card2); }
  .txt { display: grid; line-height: 1.2; }
  .name { font-weight: 600; }
  .scrim { position: fixed; inset: 0; background: #0009; z-index: var(--z-picker, 20); }
  .sheet {
    position: fixed; z-index: calc(var(--z-picker, 20) + 1); background: var(--surface); display: flex; flex-direction: column; gap: .75rem; padding: 1rem;
    inset: 0; overscroll-behavior: contain; padding-top: calc(env(safe-area-inset-top) + 1rem); padding-bottom: calc(env(safe-area-inset-bottom) + 1rem);
  }
  .title { display: flex; align-items: center; justify-content: space-between; gap: .5rem; }
  .what { font-weight: 700; font-size: 1.05rem; }
  header { display: flex; gap: .5rem; }
  header input { flex: 1; min-width: 0; }
  .icon { width: 2.75rem; padding: 0; display: grid; place-items: center; }
  .lanes { display: grid; grid-template-columns: repeat(6, minmax(0, 1fr)); gap: .35rem; flex: none; }
  .lane { display: flex; flex-direction: column; align-items: center; justify-content: center; gap: .15rem; min-height: 3.4rem; padding: .3rem 0; background: var(--card); font-size: .68rem; font-weight: 700; letter-spacing: .04em; color: var(--muted); }
  .lane.on { outline: 2px solid var(--accent2); outline-offset: -2px; color: var(--fg); }
  .lane:focus-visible, .champ:focus-visible { outline-offset: -2px; }
  .grid { overflow-y: auto; display: grid; grid-template-columns: repeat(auto-fill, minmax(4.6rem, 1fr)); gap: .5rem; align-content: start; grid-auto-rows: max-content; padding: 2px; flex: 1; min-height: 0; overscroll-behavior: contain; }
  .champ { display: block; padding: .4rem .2rem; background: var(--card); color: var(--fg); font-size: .72rem; line-height: 1.15; min-height: 0; text-align: center; }
  .inner { display: grid; justify-items: center; gap: .2rem; }
  .champ img { width: 3rem; height: 3rem; border-radius: .5rem; }
  .blank { width: 3rem; height: 3rem; border-radius: .5rem; border: 2px dashed var(--line); box-sizing: border-box; }
  .champ.none { color: var(--muted); }
  .champ.best { color: var(--accent2); font-weight: 700; }
  .champ.sel { outline: 2px solid var(--accent2); outline-offset: -2px; }
  .champ.gone { opacity: 1; cursor: not-allowed; background: transparent; color: var(--muted); }
  .champ.gone img { filter: grayscale(1) brightness(.45); }
  .tag { font-size: .6rem; font-weight: 700; letter-spacing: .08em; text-transform: uppercase; color: var(--accent2); }
  @media (min-width: 760px) {
    .sheet { inset: 5dvh auto auto 50%; transform: translateX(-50%); width: min(44rem, 92vw); height: 90dvh; border-radius: var(--radius); border: 1px solid var(--line); }
  }
</style>
