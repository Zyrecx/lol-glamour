<script lang="ts">
  // The panel's half of the band from the enemy I face (ringed on the client's own card by the tray app) into the counter list: over the rail,
  // then along the list's left edge. The light runs once when the enemy I face changes, as the tray app's lap round the ring does (marks.svelte.ts).
  import { PERIOD, band, bandPath, marks } from './marks.svelte'
  import { motion } from '../shell/motion.svelte'

  let { target }: { target: { left: number; top: number; bottom: number } } = $props()
  const startY = $derived(marks.y0 + band.slot * marks.step)
  const path = $derived(bandPath(startY, target))
  const x = $derived(target.left + 1)
  const edge = $derived(`M ${x} ${target.top} V ${target.bottom}`)
</script>

<svg class="marks-band" class:still={motion.reduced} style:--period={`${PERIOD}ms`} aria-hidden="true">
  <defs>
    <linearGradient id="mk-link" gradientUnits="userSpaceOnUse" x1="0" y1="0" x2={x} y2="0">
      <stop offset="0" stop-color="#2DE2C4" />
      <stop offset="1" stop-color="#7FB8B6" />
    </linearGradient>
    <linearGradient id="mk-edge" gradientUnits="userSpaceOnUse" x1="0" y1={target.top} x2="0" y2={target.bottom}>
      <stop offset="0" stop-color="#2DE2C4" />
      <stop offset="1" stop-color="#E24FA6" />
    </linearGradient>
  </defs>
  <path class="halo" d={path.link} stroke="url(#mk-link)" />
  <path class="halo" d={edge} stroke="url(#mk-edge)" />
  <path class="core" d={path.link} stroke="url(#mk-link)" />
  <path class="core" d={edge} stroke="url(#mk-edge)" />
  <!-- Keyed on the enemy: a new one starts the lap again. -->
  {#key band.slot}
    <path class="light link" d={path.link} pathLength="100" />
    <path class="light edge" d={`M ${x} ${path.entry} V ${target.top}`} pathLength="100" />
    <path class="light edge" d={`M ${x} ${path.entry} V ${target.bottom}`} pathLength="100" />
  {/key}
</svg>

<style>
  .marks-band { position: fixed; inset: 0; width: 100%; height: 100%; pointer-events: none; z-index: 5; overflow: visible; }
  path { fill: none; }
  .halo { stroke-width: 9; opacity: .2; }
  .core { stroke-width: 3; opacity: .85; }
  /* The light: one dash, from just before the start to past the end. It crosses the rail after the tray app's half, then runs both ways along the list. */
  .light { stroke: #F0E6D2; stroke-width: 3; stroke-linecap: butt; stroke-dasharray: 18 200; stroke-dashoffset: 18; filter: drop-shadow(0 0 3px #2DE2C4); }
  /* !important: the user's call, the band shimmers even with Windows animations off (theme.css stops all others then); the panel's Reduce motion still stops it. */
  .link { animation: mk-link var(--period) linear both !important; }
  .edge { animation: mk-edge var(--period) cubic-bezier(.3, .6, .5, 1) both !important; }
  /* 5%: BAND_SHARE, the tray app's half (keyframe offsets cannot be variables). */
  @keyframes mk-link { 0%, 5% { stroke-dashoffset: 18; } 18%, 100% { stroke-dashoffset: -100; } }
  @keyframes mk-edge { 0%, 18% { stroke-dashoffset: 18; opacity: 1; } 55% { stroke-dashoffset: -100; opacity: .2; } 100% { stroke-dashoffset: -100; opacity: 0; } }
  .still .light { display: none; }
</style>
