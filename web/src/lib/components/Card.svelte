<script lang="ts">
  import type { Snippet } from 'svelte'
  let { title = '', hint = '', children, aside }: { title?: string; hint?: string; children: Snippet; aside?: Snippet } = $props()
</script>

<section class="card">
  {#if title}
    <header>
      <div>
        <h2>{title}{#if hint}<!-- svelte-ignore a11y_no_noninteractive_tabindex --><span class="info" tabindex="0" role="note" aria-label={hint} data-tip={hint}>i</span>{/if}</h2>
        {#if hint}<p class="muted hint">{hint}</p>{/if}
      </div>
      {#if aside}{@render aside()}{/if}
    </header>
  {/if}
  <div class="body stack">{@render children()}</div>
</section>

<style>
  .card { background: var(--card); border-radius: var(--radius); padding: 1rem; border: 1px solid transparent; }
  header { display: flex; justify-content: space-between; align-items: flex-start; gap: 1rem; margin-bottom: .75rem; }
  .info { display: none; }
  header .muted { margin-top: .15rem; }
</style>
