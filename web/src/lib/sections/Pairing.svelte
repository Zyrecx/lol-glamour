<script lang="ts">
  import { onDestroy } from 'svelte'
  import { api, errorText } from '../api'
  import Card from '../components/Card.svelte'

  interface Pairing { url: string; otherUrls: string[]; svg: string; fingerprint: string | null; expiresAt: string }

  const saved = (() => { try { return localStorage.getItem('glamour.pairMode') === 'app' } catch { return false } })()
  let asApp = $state(saved)
  let pairing = $state<Pairing | null>(null)
  let left = $state(0)
  let err = $state('')
  let timer: ReturnType<typeof setInterval> | undefined

  async function show() {
    err = ''
    try { localStorage.setItem('glamour.pairMode', asApp ? 'app' : 'browser') } catch { /* private window: the choice just is not remembered */ }
    const r = await api('/auth/pairing', 'POST', { https: asApp })
    if (!r.ok) { err = await errorText(r); return }
    pairing = await r.json()
    clearInterval(timer)
    const tick = () => {
      left = Math.max(0, Math.round((new Date(pairing!.expiresAt).getTime() - Date.now()) / 1000))
      if (left === 0) { pairing = null; clearInterval(timer) }
    }
    tick()
    timer = setInterval(tick, 1000)
  }
  onDestroy(() => clearInterval(timer))

  const strip = (u: string) => u.split('#')[0]
</script>

<Card title="Pair a phone">
  <fieldset class="modes" disabled={pairing !== null}>
    <label class="mode"><input type="radio" name="mode" checked={!asApp} onchange={() => (asApp = false)} />
      <span><b>In the browser</b><small>Opens in the phone's browser.</small></span></label>
    <label class="mode"><input type="radio" name="mode" checked={asApp} onchange={() => (asApp = true)} />
      <span><b>Install as an app</b><small>A real app on the phone. One extra setup step the first time.</small></span></label>
  </fieldset>
  {#if pairing}
    <div class="qr">{@html pairing.svg}</div>
    <p class="muted">Scan with the phone camera. Expires in {left} s.</p>
    <p class="muted">Address <code>{strip(pairing.url)}</code></p>
    {#if pairing.fingerprint}
      <p class="muted">Certificate <code>{pairing.fingerprint}</code></p>
    {/if}
    {#if pairing.otherUrls.length}
      <p class="muted">If it does not open, scan again after trying one of: {#each pairing.otherUrls as u (u)}<code>{strip(u)}</code> {/each}</p>
    {/if}
  {/if}
  <button type="button" class="full" class:quiet={pairing !== null} onclick={show}>{pairing ? 'New code' : 'Show QR code'}</button>
  {#if err}<p class="err" role="alert">{err}</p>{/if}
</Card>

<style>
  .modes { border: 0; padding: 0; margin: 0 0 .8rem; display: grid; gap: .5rem; }
  .mode { display: flex; gap: .6rem; align-items: flex-start; padding: .6rem .7rem; border-radius: .7rem; background: rgba(255, 255, 255, .04); }
  .mode small { display: block; opacity: .7; }
  .qr :global(svg) { display: block; width: min(15rem, 100%); height: auto; margin: 0 auto; background: #fff; padding: .6rem; border-radius: .8rem; }
</style>
