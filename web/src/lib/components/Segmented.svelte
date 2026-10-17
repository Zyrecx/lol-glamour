<script lang="ts" generics="T extends string">
  import LaneIcon from '../../shell/parts/Lane.svelte'
  // An option with `lane` shows that lane's icon above its label.
  let { label, options, value, onchange }: {
    label: string; options: { value: T; label: string; lane?: string }[]; value: T; onchange: (value: T) => void
  } = $props()
</script>

<div class="seg" role="radiogroup" aria-label={label}>
  {#each options as o (o.value)}
    <button type="button" role="radio" aria-checked={value === o.value} class:on={value === o.value} class:withicon={!!o.lane} onclick={() => onchange(o.value)}>{#if o.lane}<LaneIcon lane={o.lane} size={22} />{/if}<span>{o.label}</span></button>
  {/each}
</div>

<style>
  .seg { display: flex; background: #0f0a16; border: 1px solid var(--line); border-radius: var(--radius-sm); padding: .2rem; gap: .2rem; }
  button { flex: 1; min-height: 2.4rem; padding: .3rem .5rem; background: none; color: var(--muted); font-size: .92rem; }
  button.withicon { display: flex; flex-direction: column; align-items: center; justify-content: center; gap: .15rem; font-size: .75rem; padding: .35rem .1rem; min-width: 0; }
  button.withicon span { max-width: 100%; overflow: hidden; text-overflow: clip; white-space: nowrap; }
  button.on { background: var(--accent); color: var(--accent-fg); }
</style>
