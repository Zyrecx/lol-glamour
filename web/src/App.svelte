<script lang="ts">
  import { onMount, type Component } from 'svelte'
  import { auth } from './lib/auth.svelte'
  import { live } from './lib/live.svelte'

  let ready = $state(false)

  // Two apps on one core (SPEC "UI (decided)"): the PC itself (the tray app's window) gets the desk UI, every other device the phone UI.
  // ?view=phone or ?view=desktop overrides it, for testing; ?view=panel is the side panel docked to the League client (the desktop app opens it).
  const forced = new URLSearchParams(location.search).get('view')
  const view = $derived(forced === 'phone' || forced === 'desktop' || forced === 'panel' ? forced : auth.isPc ? 'desktop' : 'phone')

  onMount(async () => {
    try { await auth.load() } catch { return }
    if (!auth.status?.authenticated) { location.replace('/login.html'); return }
    live.start()
    ready = true
  })

  // Each app is its own chunk: the side panel (always open next to the client) never loads the phone or PC app.
  const apps = { desktop: () => import('./desk/Desk.svelte'), panel: () => import('./panel/Panel.svelte'), phone: () => import('./shell/Shell.svelte') }
  let View = $state<Component | null>(null)
  let failed = $state(false)
  $effect(() => {
    if (!ready) return
    let stale = false
    apps[view]().then((m) => {
      if (stale) return
      View = m.default
      try { sessionStorage.removeItem('glamour-reloaded') } catch { /* fine */ }
    }).catch(() => {
      // A page kept from before a rebuild names chunks that are gone: load it fresh once, then say so instead of staying blank.
      let retried = false
      try { retried = sessionStorage.getItem('glamour-reloaded') === '1'; sessionStorage.setItem('glamour-reloaded', '1') } catch { retried = true } // no storage: a reload could loop
      if (!retried) location.reload()
      else failed = true
    })
    return () => { stale = true }
  })
</script>

{#if View}<View />{:else if failed}<p style="padding: 16px">Glamour could not load. Reload the page.</p>{/if}
