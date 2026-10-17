<script lang="ts">
  // The profile icon of the signed-in account, with a way back to the blank default the client's picker hides. Used by both apps.
  import { act, getJson } from '../api'
  import { asset } from '../assets'
  import { live } from '../live.svelte'

  let id = $state<number | null>(null)
  let blank = $state(false)
  let busy = $state(false)
  let err = $state('')

  async function load() {
    err = ''
    try {
      const j = await getJson<{ id: number; blank: boolean }>('/profile-icon')
      id = j.id; blank = j.blank
    } catch (e) {
      id = null
      err = e instanceof Error && e.message !== 'unauthorized' ? e.message : ''
    }
  }
  // Reload when the League client comes (back): a switch may have changed the account.
  let wasConnected = false
  $effect(() => {
    const on = live.now.connected
    if (on && !wasConnected) load()
    else if (!on) id = null
    wasConnected = on
  })
  // A change in the client (or our clear) arrives as an event.
  $effect(() => {
    const p = live.profileIcon
    if (p && live.now.connected) { id = p.id; blank = p.blank; err = '' }
  })

  async function clear() {
    busy = true; err = ''
    const e = await act('/profile-icon', 'DELETE')
    busy = false
    if (e) { err = e; return }
    await load()
  }
</script>

<section class="panel picon">
  <h2 class="display">Profile icon</h2>
  {#if err}<p class="err" role="alert">{err}</p>{/if}
  {#if id === null && !err}
    <p class="muted">{live.now.connected ? 'Loading…' : 'Start League to change the icon.'}</p>
  {:else if id !== null}
    <div class="row">
      <img src={asset(`/lol-game-data/assets/v1/profile-icons/${id}.jpg`)} alt="" width="48" height="48" />
      <div class="side">
        <p class="muted">{blank ? 'Blank, like a new account.' : 'Back to the blank new-account icon.'}</p>
        <button type="button" class="danger" disabled={busy || blank} onclick={clear}>Clear icon</button>
      </div>
    </div>
  {/if}
</section>

<style>
  .picon { display: flex; flex-direction: column; gap: 10px; }
  h2 { font-size: 16px; margin: 0; }
  p { margin: 0; }
  .row { display: flex; gap: 12px; align-items: center; }
  .side { display: flex; flex-direction: column; align-items: flex-start; gap: 8px; min-width: 0; }
  .side button { white-space: nowrap; }
  img { border-radius: 50%; flex: none; }
  .err { color: var(--g-magenta); }
</style>
