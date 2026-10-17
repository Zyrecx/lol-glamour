<script lang="ts">
  // The tour's ban and lock: one row per lane under an All lanes row. A lane with nothing of its own shows the All lanes champion, faded;
  // emptying a lane's slot makes it follow All lanes again, and a lane that matches All lanes everywhere loses its own setup.
  import { champions } from '../../lib/champions.svelte'
  import { live } from '../../lib/live.svelte'
  import type { LaneKey, LaneSetup, Settings } from '../../lib/types'
  import ChampionPicker from '../../lib/components/ChampionPicker.svelte'
  import LaneIcon from '../../shell/parts/Lane.svelte'

  let { onerror }: { onerror: (msg: string) => void } = $props()

  type Slot = 'banChampionId' | 'lockChampionId'
  const LANES: { key: LaneKey; name: string }[] = [
    { key: 'TOP', name: 'Top' }, { key: 'JUNGLE', name: 'Jungle' }, { key: 'MIDDLE', name: 'Mid' }, { key: 'BOTTOM', name: 'Bot' }, { key: 'SUPPORT', name: 'Support' },
  ]
  const s = $derived(live.settings)
  const base = (x: Settings): LaneSetup => ({ banChampionId: x.autoBanChampionId, banTiming: x.banTiming, lockChampionId: x.autoLockChampionId, lockTiming: x.lockTiming, hoverChampionId: x.preHoverChampionId })
  const same = (a: LaneSetup, b: LaneSetup) => a.banChampionId === b.banChampionId && a.banTiming === b.banTiming && a.lockChampionId === b.lockChampionId && a.lockTiming === b.lockTiming && a.hoverChampionId === b.hoverChampionId

  function cell(lane: LaneKey, slot: Slot) {
    const all = base(s!)[slot]
    const id = s!.lanes?.[lane]?.[slot] ?? (s!.lanes?.[lane] ? null : all)
    return { id, follows: id === all }
  }

  async function save(change: Partial<Settings>) { onerror((await live.patchInEffect(change)) ?? '') }

  // All lanes: lanes that showed the old champion follow to the new one.
  function setAll(slot: Slot, id: number | null) {
    if (!s) return
    const old = base(s)
    const now = { ...old, [slot]: id }
    const lanes: Settings['lanes'] = {}
    for (const [k, l] of Object.entries(s.lanes ?? {}) as [LaneKey, LaneSetup][]) {
      const moved = l[slot] === old[slot] ? { ...l, [slot]: id } : l
      if (!same(moved, now)) lanes[k] = moved
    }
    save({ ...(slot === 'banChampionId' ? { autoBanChampionId: id } : { autoLockChampionId: id }), lanes })
  }

  // A lane: none picked means follow All lanes.
  function setLane(lane: LaneKey, slot: Slot, id: number | null) {
    if (!s) return
    const all = base(s)
    const own = { ...(s.lanes?.[lane] ?? all), [slot]: id ?? all[slot] }
    const lanes = { ...s.lanes }
    if (same(own, all)) delete lanes[lane]
    else lanes[lane] = own
    save({ lanes })
  }
</script>

{#snippet slot(label: string, id: number | null, follows: boolean, lock: boolean, onpick: (id: number | null) => void)}
  {@const c = champions.byId(id)}
  <ChampionPicker {label} value={id} bestCounter={lock} {onpick}>
    {#snippet trigger(show)}
      <button type="button" class="slot" class:follows onclick={show} title={label}>
        {#if c}<img src={c.icon} alt="" />{:else}<span class="none"></span>{/if}
        <span class="name">{c ? c.name : 'None'}</span>
      </button>
    {/snippet}
  </ChampionPicker>
{/snippet}

{#if s}
  <div class="grid">
    <span></span><span class="head">Ban</span><span class="head">Lock</span>
    <span class="lane all"><LaneIcon lane="FILL" size={18} />All lanes</span>
    {@render slot('Ban · All lanes', s.autoBanChampionId, false, false, (id) => setAll('banChampionId', id))}
    {@render slot('Lock · All lanes', s.autoLockChampionId, false, true, (id) => setAll('lockChampionId', id))}
    {#each LANES as l (l.key)}
      {@const ban = cell(l.key, 'banChampionId')}
      {@const lock = cell(l.key, 'lockChampionId')}
      <span class="lane"><LaneIcon lane={l.key} size={18} />{l.name}</span>
      {@render slot(`Ban · ${l.name} (None: same as All lanes)`, ban.id, ban.follows, false, (id) => setLane(l.key, 'banChampionId', id))}
      {@render slot(`Lock · ${l.name} (None: same as All lanes)`, lock.id, lock.follows, true, (id) => setLane(l.key, 'lockChampionId', id))}
    {/each}
  </div>
{/if}

<style>
  .grid { width: 100%; display: grid; grid-template-columns: auto minmax(0, 1fr) minmax(0, 1fr); gap: 6px 8px; align-items: center; text-align: left; }
  .head { font-size: 11px; letter-spacing: .1em; text-transform: uppercase; color: var(--g-muted); padding-left: 4px; }
  .lane { display: flex; align-items: center; gap: 6px; font-size: 13px; color: var(--g-muted); padding-right: 4px; }
  .lane.all { color: inherit; }
  .slot { width: 100%; min-height: 34px; display: flex; align-items: center; gap: 7px; padding: 3px 8px 3px 3px; border-radius: 8px; border: 1px solid rgba(200,170,110,.35); background: transparent; color: inherit; font: inherit; font-size: 13px; text-align: left; }
  .slot img { width: 26px; height: 26px; border-radius: 5px; flex: none; }
  .slot .none { width: 26px; height: 26px; border-radius: 5px; flex: none; border: 1px dashed var(--g-line); }
  .slot .name { min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .slot.follows { opacity: .45; border-style: dashed; }
  .slot.follows:hover { opacity: .8; }
</style>
