<script lang="ts">
  let { label, hint = '', checked, onchange, disabled = false }: {
    label: string; hint?: string; checked: boolean; onchange: (value: boolean) => void; disabled?: boolean
  } = $props()
</script>

<label class="toggle">
  <span class="text">
    <span class="label">{label}{#if hint}<!-- svelte-ignore a11y_no_noninteractive_tabindex --><!-- svelte-ignore a11y_click_events_have_key_events, a11y_no_noninteractive_element_interactions --><span class="info" tabindex="0" role="note" aria-label={hint} data-tip={hint} onclick={(e) => e.preventDefault()}>i</span>{/if}</span>
    {#if hint}<span class="muted hint">{hint}</span>{/if}
  </span>
  <input type="checkbox" role="switch" {checked} {disabled} onchange={(e) => onchange(e.currentTarget.checked)} />
</label>

<style>
  .toggle { display: flex; align-items: center; justify-content: space-between; gap: 1rem; min-height: 2.75rem; cursor: pointer; }
  .text { display: grid; gap: .1rem; }
  .label { font-weight: 600; }
  .info { display: none; }
  input { appearance: none; -webkit-appearance: none; flex: none; width: 3.1rem; height: 1.8rem; margin: 0; border-radius: 99px; background: var(--line); position: relative; transition: background .15s; cursor: inherit; }
  input::after { content: ""; position: absolute; top: .2rem; left: .2rem; width: 1.4rem; height: 1.4rem; border-radius: 50%; background: #fff; transition: transform .15s; }
  input:checked { background: var(--accent); }
  input:checked::after { transform: translateX(1.3rem); }
  input:disabled { opacity: .5; }
  input:focus-visible { outline: 2px solid var(--accent2); outline-offset: 2px; }
</style>
