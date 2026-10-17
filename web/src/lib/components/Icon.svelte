<script lang="ts">
  let { name }: { name: 'now' | 'automation' | 'accounts' | 'alerts' | 'devices' | 'close' | 'bin' | 'left' | 'right' | 'up' | 'down' } = $props()
  const paths: Record<string, string> = {
    now: '<circle cx="12" cy="12" r="9"/><path d="M12 7v5l3 2"/>',
    automation: '<path d="M13 3L5 14h6l-1 7 8-11h-6z"/>',
    accounts: '<circle cx="12" cy="8" r="3.5"/><path d="M5 20c0-3.9 3.1-6 7-6s7 2.1 7 6"/>',
    alerts: '<path d="M6 16V11a6 6 0 0 1 12 0v5l1.5 2h-15z"/><path d="M10 21h4"/>',
    devices: '<rect x="7" y="2.5" width="10" height="19" rx="2.5"/><path d="M11 18.5h2"/>',
    close: '<path d="M6 6l12 12M18 6L6 18"/>',
    left: '<path d="M14.5 6l-6 6 6 6"/>',
    right: '<path d="M9.5 6l6 6-6 6"/>',
    up: '<path d="M6 14.5l6-6 6 6"/>',
    down: '<path d="M6 9.5l6 6 6-6"/>',
    bin: '<path d="M4 7h16M9 7V4.5h6V7M6.5 7l1 13h9l1-13M10 11v5.5M14 11v5.5"/>',
  }
</script>

<svg viewBox="0 0 24 24" aria-hidden="true" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round">
  {@html paths[name]}
</svg>

<style>
  svg { width: 1.4rem; height: 1.4rem; flex: none; }
</style>
