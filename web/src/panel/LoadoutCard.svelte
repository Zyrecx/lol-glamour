<script lang="ts">
  // Matchup loadout in the side panel (SPEC "Matchup loadout"): spells, keystone, item set and a tip against the enemy I face, with Apply.
  // With the automation switch on the host applies it on lock; the card then says Applied, with Reapply beside it.
  import { getJson, act } from '../lib/api'
  import { asset } from '../lib/assets'
  import { champions } from '../lib/champions.svelte'
  import { spells } from '../lib/spells.svelte'
  import type { Board } from '../lib/types'

  interface Plan {
    championId: number; enemyId: number; spell1: number; spell2: number; spellsVsEnemy: boolean; runesVsEnemy: boolean; itemsVsEnemy: boolean
    items: { starter: number[]; core: number[]; boots: number[] }; tip: string | null; laneAdvantage: string | null
  }
  interface Loadout { state: 'none' | 'waiting' | 'ready'; plan?: Plan; keystone?: { id: number; name: string; icon: string } | null; version?: string; applied?: boolean; auto?: boolean }

  let { board, enemyId }: { board: Board; enemyId: number } = $props()
  const me = $derived(board.myTeam.find((p) => p.isMe))
  const key = $derived(me && me.championId > 0 && enemyId > 0 ? `${me.championId}|${enemyId}|${me.locked}` : '')
  let view = $state<Loadout | null>(null)
  let busy = $state(false)
  let loading = $state(false) // a fresh matchup takes OP.GG 5-8 s: the band shows meanwhile
  let err = $state('')

  // Asked again when my champion, my lock or the enemy changes; while the host is applying it by itself, until it says Applied.
  $effect(() => {
    const k = key
    if (!k) { view = null; loading = false; return }
    let stale = false
    let timer = 0
    loading = true
    const load = (tries: number) => getJson<Loadout>(`/loadout?enemy=${enemyId}`).then((v) => {
      if (stale) return
      view = v
      loading = false
      if (v.auto && !v.applied && me?.locked && tries > 0) timer = window.setTimeout(() => load(tries - 1), 3000)
    }).catch(() => { if (!stale) { view = null; loading = false } })
    void spells.ensure()
    load(10)
    return () => { stale = true; clearTimeout(timer) }
  })

  async function apply() {
    busy = true
    err = (await act('/loadout/apply', 'POST', { enemy: enemyId })) ?? ''
    if (!err && view) view = { ...view, applied: true }
    busy = false
  }

  const itemIcon = (id: number) => (view?.version ? `https://ddragon.leagueoflegends.com/cdn/${view.version}/img/item/${id}.png` : '')
  const enemyName = $derived(champions.byId(enemyId)?.name ?? 'them')
</script>

{#if view?.state === 'ready' && view.plan && view.plan.enemyId === enemyId}
  {@const p = view.plan}
  <section class="block">
    <h2 class="band">Loadout vs {enemyName}
      <!-- Applied stays pressable: the client's own recommended runes replace the page in one click (user's habit), this puts it back. -->
      {#if view.applied}<span class="lane done">Applied</span>{/if}
      <button class="link" class:again={view.applied} disabled={busy} onclick={apply}>{view.applied ? 'Reapply' : 'Apply'}</button>
    </h2>
    <div class="kit pad">
      {#each [p.spell1, p.spell2].filter((id) => id > 0) as id (id)}
        {@const s = spells.byId(id)}
        {#if s}<img src={s.icon} alt={s.name} title={s.name} width="26" height="26" />{/if}
      {/each}
      {#if view.keystone}<img class="rune" src={asset(view.keystone.icon)} alt={view.keystone.name} title={view.keystone.name} width="26" height="26" />{/if}
      <span class="sep"></span>
      {#each [...p.items.starter.filter((id, i, a) => a.indexOf(id) === i), ...p.items.core.slice(0, 3)] as id (id)}
        <img src={itemIcon(id)} alt="" width="26" height="26" />
      {/each}
    </div>
    {#if !p.spellsVsEnemy && !p.runesVsEnemy && !p.itemsVsEnemy}<p class="muted pad note">Few games vs {enemyName}: general build.</p>{/if}
    {#if p.tip}<p class="tip pad">{p.tip}</p>{/if}
    {#if err}<p class="err pad">{err}</p>{/if}
  </section>
{:else}
  <!-- The panel keeps every section in place for the whole draft: the band stays, one line says what fills it. -->
  <section class="block">
    <h2 class="band">{key ? `Loadout vs ${enemyName}` : 'Loadout'}{#if loading}<span class="lane">Loading…</span>{/if}</h2>
    {#if !loading}<p class="muted pad">{key ? 'No build for this matchup.' : 'Fills once you face someone.'}</p>{/if}
  </section>
{/if}

<style>
  .block { display: flex; flex-direction: column; flex: none; }
  .band { margin: 0; display: flex; align-items: center; gap: 8px; min-height: 30px; padding: 0 12px; background: var(--l-band); border-bottom: 1px solid var(--l-frame); border-top: 1px solid var(--l-frame);
    font: 700 11px var(--g-body); letter-spacing: .1em; text-transform: uppercase; color: var(--l-grey1); }
  .band .lane { margin-left: auto; letter-spacing: .06em; }
  .band .done { color: var(--l-blue); }
  .band .done + .again { margin-left: 10px; color: var(--l-grey1) !important; }
  .link { margin-left: auto; min-height: 0 !important; padding: 0 !important; border: 0 !important; background: none !important; color: var(--l-blue) !important; font: 700 11px var(--g-body); letter-spacing: .06em; cursor: pointer; }
  .pad { padding: 8px 12px; margin: 0; }
  .kit { display: flex; flex-wrap: wrap; align-items: center; gap: 4px; }
  .kit img { width: 26px; height: 26px; display: block; box-sizing: border-box; border: 1px solid var(--l-gold4); background: var(--l-btn); }
  .kit .rune { border-radius: 50%; border-color: transparent; background: none; }
  .sep { width: 6px; }
  .note { padding-top: 0; }
  .muted { color: var(--l-grey1); font-size: 12px; }
  .tip { padding-top: 0; font-size: 12px; line-height: 1.4; color: var(--l-gold1); }
  .err { color: #E86A6C; font-size: 12px; }
</style>
