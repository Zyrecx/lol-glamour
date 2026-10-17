<script lang="ts">
  // Our own lane icons (tintable with `color`, offline). Accepts the client's words too (UTILITY = support).
  import { laneIcon, laneName } from '../util'
  let { lane, size = 20, label = false }: { lane: string | null | undefined; size?: number; label?: boolean } = $props()
  const id = $derived(laneIcon(lane))
</script>

{#if id}
  <svg width={size} height={size} viewBox="0 0 24 24" role={label ? 'img' : undefined} aria-label={label ? laneName(lane) : undefined} aria-hidden={label ? undefined : 'true'} fill="currentColor">
    {#if id === 'TOP'}
      <path d="M3 3h14l-4 4H7v6l-4 4z" /><path d="M21 7v14H7l3-3h8V10z" opacity=".35" /><rect x="10" y="10" width="4" height="4" opacity=".35" />
    {:else if id === 'JUNGLE'}
      <path d="M6 2c3.5 3 5 7 5.2 12L12 22l.8-8C13 9 14.5 5 18 2c-3.4 1.7-5 4.6-6 8.2C11 6.6 9.4 3.7 6 2z" /><path d="M4 8c2 2 3 4.5 3.5 7.5L9 12C8 10 6.3 8.8 4 8zM20 8c-2 2-3 4.5-3.5 7.5L15 12c1-2 2.7-3.2 5-4z" opacity=".55" />
    {:else if id === 'MIDDLE'}
      <path d="M16 3h5v5L8 21H3v-5z" /><path d="M3 3h8L3 11zM21 21h-8l8-8z" opacity=".35" />
    {:else if id === 'BOTTOM'}
      <path d="M21 21H7l4-4h6v-6l4-4z" /><path d="M3 17V3h14l-3 3H6v8z" opacity=".35" /><rect x="10" y="10" width="4" height="4" opacity=".35" />
    {:else if id === 'SUPPORT'}
      <path d="M12 7l2.6 2.6H21l-4.2 4.2h-2.3L12 21l-2.5-7.2H7.2L3 9.6h6.4z" /><path d="M9.5 3h5L12 6z" opacity=".55" />
    {:else}
      <path d="M12 2l2.2 7.8L22 12l-7.8 2.2L12 22l-2.2-7.8L2 12l7.8-2.2z" />
    {/if}
  </svg>
{/if}
