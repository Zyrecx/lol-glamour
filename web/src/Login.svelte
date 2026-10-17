<script lang="ts">
  let name = $state('')
  let password = $state('')
  let err = $state('')
  let busy = $state(false)

  async function submit(e: SubmitEvent) {
    e.preventDefault()
    err = ''
    busy = true
    try {
      const r = await fetch('/api/auth/login', {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ password, name }),
      })
      if (r.ok) { location.replace('/'); return }
      err = r.status === 429 ? `Too many attempts. Try again in ${r.headers.get('Retry-After')} s.` : 'Wrong password, or no password is set on the PC.'
    } catch {
      err = 'Cannot reach the PC.'
    }
    busy = false
  }
</script>

<main>
  <h1>Glamour</h1>
  <p class="muted">Sign in to control Glamour from this device. You can also scan the QR code shown on the PC.</p>
  <form class="stack" onsubmit={submit}>
    <input type="text" placeholder="Name this device (optional)" maxlength="40" autocomplete="off" bind:value={name} />
    <input type="password" inputmode="numeric" pattern="[0-9]*" placeholder="PIN" autocomplete="current-password" required bind:value={password} />
    <button disabled={busy}>Sign in</button>
    <p class="err" role="alert">{err}</p>
  </form>
</main>

<style>
  main { max-width: 22rem; margin: 0 auto; padding: calc(env(safe-area-inset-top) + 3rem) 1rem 2rem; display: grid; gap: 1rem; }
  h1 { font-size: 1.8rem; }
</style>
