<script lang="ts">
  import { act } from '../api'
  import { auth } from '../auth.svelte'
  import Card from '../components/Card.svelte'

  let pw = $state('')
  let msg = $state('')

  async function set(e: SubmitEvent) {
    e.preventDefault()
    const err = await act('/auth/password', 'PUT', { password: pw })
    msg = err ?? 'Password saved.'
    pw = ''
    auth.load()
  }
  async function clear() {
    msg = (await act('/auth/password', 'DELETE')) ?? 'Password removed.'
    auth.load()
  }
</script>

<Card title="Password sign-in" hint="A fallback when scanning a QR is not possible.">
  <p class="muted">{auth.status?.passwordSet ? 'A password is set.' : 'No password set: phones can only sign in with a QR code.'}</p>
  <form class="stack" onsubmit={set}>
    <input type="password" inputmode="numeric" pattern="[0-9]*" placeholder="4 to 12 digits" autocomplete="new-password" minlength="4" maxlength="12" required bind:value={pw} />
    <button>Set password</button>
  </form>
  {#if auth.status?.passwordSet}<button type="button" class="quiet full" onclick={clear}>Remove password</button>{/if}
  {#if msg}<p class="muted" role="status">{msg}</p>{/if}
</Card>
