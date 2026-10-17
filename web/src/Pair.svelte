<script lang="ts">
  import { onMount } from 'svelte'

  let msg = $state('Pairing this device…')
  let showAlt = $state(false)

  // The secret lives in the URL fragment: it is never sent to the server or written to logs.
  // Spending it needs a POST from this script, so a link preview or QR scanner that only fetches the page cannot use it up.
  onMount(async () => {
    const secret = location.hash.slice(1)
    history.replaceState(null, '', location.pathname)
    if (!secret) { msg = 'No pairing code in this link. Scan the QR again.'; return }
    try {
      const r = await fetch('/api/auth/pair', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ secret }) })
      if (r.ok) { msg = 'Paired. Opening…'; location.replace('/'); return }
      msg = r.status === 429 ? 'Too many attempts. Wait a bit and try again.' : 'This QR has expired or was already used. Show a new one on the PC.'
    } catch {
      msg = 'Cannot reach the PC.'
    }
    showAlt = true
  })
</script>

<main>
  <h1>Glamour</h1>
  <p role="status">{msg}</p>
  {#if showAlt}<p><a href="/login.html">Use the password instead</a></p>{/if}
</main>

<style>
  main { max-width: 24rem; margin: 0 auto; padding: calc(env(safe-area-inset-top) + 3rem) 1rem 2rem; display: grid; gap: 1rem; text-align: center; }
  h1 { font-size: 1.8rem; }
</style>
