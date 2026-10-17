<script lang="ts">
  // The three challenge tokens on the profile of the signed-in account. The same challenge may fill several slots. Used by both apps.
  import { act, getJson } from '../api'
  import { asset } from '../assets'
  import { live } from '../live.svelte'

  type Owned = { id: number; name: string; level: string; description: string; icon: string; category: string }
  const SLOTS = 3

  let owned = $state<Owned[]>([])
  let saved = $state<number[]>([])
  let draft = $state<(number | null)[]>([null, null, null])
  let picking = $state<number | null>(null) // the slot being filled
  let fillAll = $state(false)
  let search = $state('')
  let busy = $state(false)
  let loaded = $state(false)
  let err = $state('')
  let note = $state('')

  const byId = $derived(new Map(owned.map((o) => [o.id, o])))
  const chosen = $derived(draft.filter((d): d is number => d !== null))
  const dirty = $derived(JSON.stringify(chosen) !== JSON.stringify(saved))
  const shown = $derived.by(() => {
    const q = search.trim().toLowerCase()
    return q ? owned.filter((o) => o.name.toLowerCase().includes(q) || o.category.toLowerCase().includes(q)) : owned
  })

  async function load() {
    err = ''
    try {
      const j = await getJson<{ equipped: number[]; owned: Owned[] }>('/challenges/tokens')
      owned = j.owned
      saved = j.equipped
      draft = [...j.equipped, null, null, null].slice(0, SLOTS)
      loaded = true
    } catch (e) {
      loaded = false
      err = e instanceof Error && e.message !== 'unauthorized' ? e.message : ''
    }
  }
  // Load whenever the League client comes (back): the list is the account's own, and a switch may have changed the account.
  let wasConnected = false
  $effect(() => {
    const on = live.now.connected
    if (on && !wasConnected) load()
    else if (!on) { loaded = false; picking = null }
    wasConnected = on
  })

  function pick(id: number) {
    if (picking === null) return
    if (fillAll) draft = Array(SLOTS).fill(id)
    else draft[picking] = id
    note = ''
    picking = null
    search = ''
  }

  function clearSlot(i: number) {
    draft[i] = null
    // keep filled slots first: the client stores a list, not positions
    draft = [...draft.filter((d) => d !== null), ...draft.filter((d) => d === null)]
  }

  async function apply(ids: number[]) {
    busy = true; err = ''; note = ''
    const e = await act('/challenges/tokens', 'PUT', { challengeIds: ids })
    busy = false
    if (e) { err = e; return }
    await load()
    note = ids.length ? 'Saved. It shows on your profile.' : 'Cleared.'
  }
</script>

<section class="panel tokens">
  <h2 class="display">Profile tokens</h2>
  <p class="muted">The three challenges on your profile.</p>

  {#if err}<p class="err" role="alert">{err}</p>{/if}

  {#if !loaded && !err}
    <p class="muted">{live.now.connected ? 'Loading…' : 'Start League to edit tokens.'}</p>
  {:else if loaded}
    <div class="slots">
      {#each draft as id, i (i)}
        {@const o = id === null ? null : byId.get(id)}
        <div class="slot" class:on={picking === i}>
          <button type="button" class="face" aria-label={o ? `Slot ${i + 1}: ${o.name}. Change` : `Slot ${i + 1} empty. Choose a token`} onclick={() => { picking = picking === i ? null : i }}>
            {#if o}<img src={asset(o.icon)} alt="" /><b>{o.name}</b><small>{o.level.toLowerCase()}</small>{:else}<span class="plus" aria-hidden="true">+</span><small>Empty</small>{/if}
          </button>
          {#if id !== null}<button type="button" class="quiet x" aria-label={`Empty slot ${i + 1}`} onclick={() => clearSlot(i)}>Remove</button>{/if}
        </div>
      {/each}
    </div>

    {#if picking !== null}
      <div class="picker">
        <div class="bar">
          <input type="search" placeholder="Search your challenges" bind:value={search} aria-label="Search challenges" />
          <label class="all"><input type="checkbox" bind:checked={fillAll} /> Use for all 3 slots</label>
        </div>
        <ul>
          {#each shown as o (o.id)}
            <li><button type="button" onclick={() => pick(o.id)}><img src={asset(o.icon)} alt="" loading="lazy" /><span><b>{o.name}</b><small>{o.level.toLowerCase()} · {o.category.toLowerCase()}</small></span></button></li>
          {:else}
            <li class="muted">Nothing matches.</li>
          {/each}
        </ul>
      </div>
    {/if}

    <div class="actions">
      <button type="button" disabled={busy || !dirty} onclick={() => apply(chosen)}>Apply</button>
      <button type="button" class="quiet" disabled={busy || !dirty} onclick={() => { draft = [...saved, null, null, null].slice(0, SLOTS); picking = null }}>Undo</button>
      <button type="button" class="danger" disabled={busy || saved.length === 0} onclick={() => apply([])}>Clear all</button>
    </div>
    {#if note}<p class="muted" role="status">{note}</p>{/if}
  {/if}
</section>

<style>
  .tokens { display: flex; flex-direction: column; gap: 10px; }
  h2 { font-size: 16px; margin: 0; }
  p { margin: 0; }
  .slots { display: grid; grid-template-columns: repeat(3, 1fr); gap: 8px; }
  .slot { display: flex; flex-direction: column; gap: 4px; min-width: 0; }
  .face {
    display: flex; flex-direction: column; align-items: center; justify-content: center; gap: 4px; min-height: 120px; padding: 8px !important; text-align: center;
    background: transparent !important; color: var(--g-fg) !important; border: 1px dashed rgba(200, 170, 110, .5) !important; border-radius: var(--g-radius) !important;
  }
  .slot.on .face, .face:hover:not(:disabled) { border-color: var(--g-gold) !important; background: rgba(200, 170, 110, .06) !important; filter: none; }
  .face img { width: 56px; height: 56px; object-fit: contain; }
  .face b { font-size: 12px; line-height: 1.2; overflow-wrap: anywhere; }
  .face small, li small { color: var(--g-muted); font-weight: 400; letter-spacing: 0; text-transform: capitalize; }
  .plus { font-size: 30px; line-height: 1; color: var(--g-teal); }
  .x { font-size: 12px; }
  .picker { display: flex; flex-direction: column; gap: 8px; border: 1px solid var(--g-line); border-radius: var(--g-radius); padding: 8px; }
  .bar { display: flex; flex-wrap: wrap; gap: 8px; align-items: center; }
  .bar input[type='search'] { flex: 1; min-width: 10rem; }
  .all { display: flex; gap: 6px; align-items: center; font-size: 13px; }
  ul { list-style: none; margin: 0; padding: 0; max-height: 320px; overflow-y: auto; display: grid; gap: 4px; }
  li button {
    display: flex; gap: 10px; align-items: center; width: 100%; text-align: left; padding: 6px !important;
    background: transparent !important; color: var(--g-fg) !important; border: 1px solid transparent !important; border-radius: 8px !important;
  }
  li button:hover { border-color: var(--g-line) !important; background: rgba(200, 170, 110, .06) !important; filter: none; }
  li img { width: 36px; height: 36px; object-fit: contain; flex: none; }
  li span { display: flex; flex-direction: column; min-width: 0; }
  .actions { display: flex; flex-wrap: wrap; gap: 8px; }
  .err { color: var(--g-magenta); }
</style>
