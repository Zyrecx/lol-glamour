<script lang="ts">
  // A lane's champion pool: chips to remove, "+ Add" through the champion picker (Automation and the tour).
  import { champions } from '../champions.svelte'
  import ChampionPicker from './ChampionPicker.svelte'

  const MAX_POOL = 12
  let { ids, onchange }: { ids: number[]; onchange: (ids: number[]) => void } = $props()
</script>

<div class="pool">
  {#each ids as id (id)}
    {@const c = champions.byId(id)}
    <button type="button" class="pc" aria-label={`Remove ${c?.name ?? ''}`} title={`Remove ${c?.name ?? ''}`} onclick={() => onchange(ids.filter((x) => x !== id))}>
      {#if c}<img src={c.icon} alt="" />{/if}<span>{c?.name ?? `#${id}`}</span><b aria-hidden="true">×</b>
    </button>
  {/each}
  {#if ids.length < MAX_POOL}
    <ChampionPicker label="Add to pool" value={null} clearable={false} onpick={(id) => id != null && !ids.includes(id) && onchange([...ids, id])}>
      {#snippet trigger(show)}<button type="button" class="pc add" onclick={show}>+ Add</button>{/snippet}
    </ChampionPicker>
  {/if}
</div>

<style>
  .pool { display: flex; flex-wrap: wrap; gap: 6px; }
  .pc { display: inline-flex; align-items: center; gap: 6px; min-height: 32px; padding: 3px 10px 3px 3px; border-radius: 8px; border: 1px solid rgba(200,170,110,.35); background: transparent; color: inherit; font: inherit; font-size: 13px; }
  .pc img { width: 26px; height: 26px; border-radius: 5px; }
  .pc b { color: var(--g-muted, #888); font-weight: 400; }
  .pc.add { padding: 3px 10px; color: var(--g-teal, #2DE2C4); }
</style>
