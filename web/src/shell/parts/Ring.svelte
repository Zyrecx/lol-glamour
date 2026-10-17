<script lang="ts">
  // A countdown or progress ring. `fraction` is how much of the ring is drawn (1 = full); `spin` draws a moving arc instead (no known end).
  import type { Snippet } from 'svelte'
  let { size = 220, stroke = 8, fraction = 1, spin = false, done = false, children }: {
    size?: number; stroke?: number; fraction?: number; spin?: boolean; done?: boolean; children?: Snippet
  } = $props()
  const r = $derived(size / 2 - stroke - 4)
  const c = $derived(2 * Math.PI * r)
  const id = `rg${Math.random().toString(36).slice(2, 8)}`
</script>

<div class="ring" style:width={`${size}px`} style:height={`${size}px`}>
  <svg width={size} height={size} viewBox={`0 0 ${size} ${size}`} aria-hidden="true">
    <defs><linearGradient {id} x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="#2DE2C4" /><stop offset="1" stop-color="#E24FA6" /></linearGradient></defs>
    <circle cx={size / 2} cy={size / 2} r={r + stroke / 2 + 2} fill="none" stroke="rgba(200,170,110,.4)" stroke-width="1" />
    <circle cx={size / 2} cy={size / 2} {r} fill="rgba(13,20,32,.9)" stroke="rgba(200,170,110,.18)" stroke-width={stroke} />
    {#if spin}
      <g class="orbit" style:transform-origin={`${size / 2}px ${size / 2}px`}>
        <circle cx={size / 2} cy={size / 2} {r} fill="none" stroke={`url(#${id})`} stroke-width={stroke} stroke-linecap="round" stroke-dasharray={`${c * 0.2} ${c}`} />
      </g>
    {:else}
      <circle cx={size / 2} cy={size / 2} {r} fill="none" stroke={done ? '#2DE2C4' : `url(#${id})`} stroke-width={stroke} stroke-linecap="round"
        stroke-dasharray={c} stroke-dashoffset={c * (1 - Math.max(0, Math.min(1, fraction)))} transform={`rotate(-90 ${size / 2} ${size / 2})`} class="arc" />
    {/if}
  </svg>
  <div class="inner">{#if children}{@render children()}{/if}</div>
</div>

<style>
  .ring { position: relative; flex: none; }
  svg { position: absolute; inset: 0; }
  .inner { position: absolute; inset: 0; display: flex; flex-direction: column; align-items: center; justify-content: center; gap: 2px; }
  .orbit { animation: g-spin 3.2s linear infinite; }
  .arc { transition: stroke-dashoffset .9s linear; }
</style>
