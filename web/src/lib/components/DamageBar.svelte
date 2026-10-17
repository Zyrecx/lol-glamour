<script lang="ts">
  // A team's damage by type as one bar (physical, magic, true): the enemies' to know what to build against, my team's to see what it lacks. Each champion's own split in the tooltip.
  import { champions } from '../champions.svelte'
  import { damageShares, type TeamDamage } from '../counterPick'

  let { damage, label = 'Enemy damage' }: { damage: TeamDamage; label?: string } = $props()
  const pc = $derived(damageShares(damage))
  const detail = $derived(Object.entries(damage.byChampion).map(([id, d]) => {
    const s = damageShares(d)
    return s ? `${champions.byId(Number(id))?.name ?? `#${id}`}: ${s.physical}% physical, ${s.magic}% magic, ${s.true}% true` : ''
  }).filter(Boolean).join('\n'))
</script>

{#if pc}
  <div class="dmg" title={detail}>
    <div class="bar" role="img" aria-label={`${label}:${pc.physical}% physical, ${pc.magic}% magic, ${pc.true}% true`}>
      <span class="phys" style:width={`${pc.physical}%`}></span><span class="magic" style:width={`${pc.magic}%`}></span><span class="true" style:width={`${pc.true}%`}></span>
    </div>
    <div class="legend">
      <span><i class="phys"></i>{pc.physical}% physical</span><span><i class="magic"></i>{pc.magic}% magic</span>{#if pc.true > 0}<span><i class="true"></i>{pc.true}% true</span>{/if}
    </div>
  </div>
{/if}

<style>
  .dmg { display: flex; flex-direction: column; gap: 5px; }
  .bar { display: flex; height: 8px; overflow: hidden; background: rgba(255, 255, 255, .06); }
  .bar span { display: block; height: 100%; }
  .legend { display: flex; flex-wrap: wrap; gap: 4px 12px; font-size: 11px; color: inherit; font-variant-numeric: tabular-nums; }
  .legend span { display: inline-flex; align-items: center; gap: 5px; white-space: nowrap; }
  .legend i { width: 8px; height: 8px; display: inline-block; }
  .phys { background: #D98A3D; }
  .magic { background: #5C8FE6; }
  .true { background: #E8E4D8; }
</style>
