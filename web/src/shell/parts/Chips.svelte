<script lang="ts">
  // What automation will do. Champions (auto ban, lock, hover) show as their icon framed in the action's colour; the rest as plain chips.
  // Dimmed while automation is off or paused.
  import { champions } from '../../lib/champions.svelte'
  import { live } from '../../lib/live.svelte'
  import { automationItems } from '../util'
  let { lane = null, center = false }: { lane?: string | null; center?: boolean } = $props()
  const active = $derived(!!live.settings?.automationEnabled && !live.now.paused)
  const items = $derived(automationItems(live.settings, (id) => champions.byId(id)?.name ?? (champions.list.length ? `#${id}` : '…'), lane))
  $effect(() => { if (items.length) champions.ensure() })
  const LABEL = { ban: 'Auto ban', lock: 'Auto lock', hover: 'Pre-hover' } as const
</script>

{#if items.length}
  <div class="chips" class:center class:off={!active}>
    {#each items as it (it.kind + it.text)}
      {#if it.kind === 'text'}
        <span class="chip" class:off={!active}>{it.text}</span>
      {:else}
        {@const c = champions.byId(it.championId ?? null)}
        <span class={`champ ${it.kind}`} aria-label={`${LABEL[it.kind]} ${it.text}${it.sub ? `, ${it.sub}` : ''}${active ? '' : ' (off)'}`}>
          {#if c}<img src={c.icon} alt="" />{:else}<span class="blank"></span>{/if}
          <span class="txt">
            <span class="lab">{LABEL[it.kind]}{it.sub ? ` · ${it.sub}` : ''}</span>
            <span class="nm">{it.text}</span>
          </span>
        </span>
      {/if}
    {/each}
  </div>
{/if}

<style>
  .chips { display: flex; flex-wrap: wrap; align-items: center; gap: 6px; }
  .center { justify-content: center; }
  .champ {
    --c: var(--g-gold);
    display: inline-flex; align-items: center; gap: 8px; padding: 3px 12px 3px 3px; border-radius: 10px;
    background: rgba(6,10,18,.75); border: 1px solid color-mix(in srgb, var(--c) 45%, transparent);
  }
  .champ.ban { --c: #E5484D; }
  .champ.lock { --c: var(--g-teal); }
  .champ img, .blank { width: 30px; height: 30px; border-radius: 7px; border: 2px solid var(--c); box-sizing: border-box; display: block; }
  .blank { background: var(--card2); }
  .champ.hover img { border-style: dashed; }
  .txt { display: flex; flex-direction: column; line-height: 1.1; }
  .lab { font: 700 9.5px var(--g-body); letter-spacing: .12em; text-transform: uppercase; color: var(--c); }
  .nm { font-size: 13px; font-weight: 600; color: var(--g-fg); }
  .off .champ { opacity: .5; }
  .off .champ img { filter: grayscale(1); }
  .off .nm { text-decoration: line-through; }
</style>
