<script lang="ts">
  // Riot's recommended rune pages for my champion and lane. Applying one puts it in a temporary page; the user's own pages are never touched.
  import { api, errorText } from '../../lib/api'
  import { asset } from '../../lib/assets'
  import { champions } from '../../lib/champions.svelte'
  import { live } from '../../lib/live.svelte'
  import type { Board, RunePage } from '../../lib/types'
  import { laneName } from '../util'

  let { board }: { board: Board } = $props()
  const me = $derived(board.myTeam.find((p) => p.isMe)!)
  const champ = $derived(champions.byId(me.championId))
  const position = $derived(me.lane === 'SUPPORT' ? 'UTILITY' : me.lane)
  let pages = $state<RunePage[]>([])
  let pagesLane = $state('')
  let pagesFor = ''
  let page = $state(0)
  let applied = $state<number | null>(null)
  let busy = $state(false)
  let err = $state('')
  let note = $state('')

  $effect(() => {
    const key = `${me.championId}/${position}`
    if (me.championId === 0 || key === pagesFor) return
    pagesFor = key
    pages = []; applied = null; err = ''
    api(`/champselect/runes?championId=${me.championId}&position=${position}`)
      .then(async (r) => {
        if (pagesFor !== key) return
        if (!r.ok) { err = await errorText(r); return }
        const j = await r.json()
        pages = j.pages; pagesLane = j.position; page = 0
      })
      .catch(() => (err = 'Cannot reach the PC.'))
  })
  async function applyRunes() {
    const p = pages[page]
    if (!p) return
    busy = true; err = ''; note = ''
    err = (await live.act('/champselect/runes/apply', 'POST', { championId: me.championId, index: p.index, position })) ?? ''
    if (!err) { applied = p.index; note = live.settings?.dryRun ? 'Dry run is on: nothing was sent.' : '' }
    busy = false
  }
</script>

{#if me.championId === 0}
  <p class="muted">Hover or lock a champion first.</p>
{:else}
  <span class="label">Riot recommended{champ ? ` · ${champ.name}` : ''}{pagesLane ? ` ${laneName(pagesLane).toLowerCase()}` : ''}</span>
  {#if pages.length === 0 && !err}<p class="muted">Loading…</p>{/if}
  {#each pages as p, i (p.index)}
    <button type="button" class="page" class:on={page === i} aria-pressed={page === i} onclick={() => (page = i)}>
      <span class="ph">
        <img class="key" src={asset(p.keystoneIcon)} alt="" />
        <span class="col"><b>{p.keystone}</b><span class="muted">{p.descriptor}</span></span>
      </span>
      <span class="perks">{#each p.perks as r, j (j)}<img src={asset(r.icon)} alt={r.name} title={r.name} loading="lazy" />{/each}</span>
    </button>
  {/each}
  {#if pages.length}
    <button type="button" class="btn" class:btn-primary={applied !== pages[page]?.index} class:btn-done={applied === pages[page]?.index} disabled={busy} onclick={applyRunes}>
      {applied === pages[page]?.index ? 'Applied' : `Apply ${pages[page]?.keystone ?? ''}`}
    </button>
    <span class="muted center">Your own rune pages are not changed.</span>
  {/if}
{/if}
{#if note}<p class="muted">{note}</p>{/if}
{#if err}<p class="err" role="alert">{err}</p>{/if}

<style>
  .page { display: flex; flex-direction: column; gap: 8px; padding: 10px 12px; border-radius: 12px; border: 1px solid var(--g-line); background: var(--g-bg); color: var(--g-fg); text-align: left; width: 100%; min-height: 0; }
  .page.on { border-color: var(--g-teal); box-shadow: 0 0 0 1px var(--g-teal) inset; }
  .ph { display: flex; align-items: center; gap: 12px; }
  .key { width: 50px; height: 50px; }
  .col { display: flex; flex-direction: column; gap: 4px; min-width: 0; }
  .perks { display: flex; flex-wrap: wrap; gap: 6px; }
  .perks img { width: 28px; height: 28px; border-radius: 50%; background: var(--g-panel); border: 1px solid rgba(200,170,110,.3); }
  .center { text-align: center; }
</style>
