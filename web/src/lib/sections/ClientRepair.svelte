<script lang="ts">
  // Client repair: Reconnect after a crash, and a restart of League's window (its backend keeps
  // lobby, queue and champ select). The restart asks for a second tap, like Dodge.
  import { live } from '../live.svelte'
  import Card from '../components/Card.svelte'

  const phase = $derived(live.now.phase)
  const inGame = $derived(phase === 'GameStart' || phase === 'InProgress')
  let err = $state('')
  let busy = $state(false)
  let armed = $state(false)
  let disarm: ReturnType<typeof setTimeout> | undefined

  async function send(path: string) {
    busy = true
    err = (await live.act(path)) ?? ''
    busy = false
  }
  function restart() {
    clearTimeout(disarm)
    if (!armed) { armed = true; disarm = setTimeout(() => (armed = false), 4000); return }
    armed = false
    send('/client/restart-ux')
  }
</script>

{#if live.now.connected}
  <Card title="League client">
    {#if phase === 'Reconnect'}
      <button type="button" disabled={busy} onclick={() => send('/game/reconnect')}>Reconnect to the game</button>
    {/if}
    <button type="button" class="quiet" disabled={busy || inGame} onclick={restart}>{armed ? 'Restart now?' : 'Restart client UI'}</button>
    <p class="muted">Reopens League's window, for a frozen or blank client.</p>
    {#if err}<p class="warn">{err}</p>{/if}
  </Card>
{/if}
