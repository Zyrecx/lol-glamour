<script lang="ts">
  import { live } from '../live.svelte'
  import Card from '../components/Card.svelte'
  import Toggle from '../components/Toggle.svelte'

  const s = $derived(live.view)
  let msg = $state('')

  // The names are the client's own; the host refuses any other.
  const screens = [
    { id: 'challenge-level-up-celebration', label: 'Challenge level-up', hint: '' },
    { id: 'missions-celebration', label: 'Missions complete', hint: '' },
    { id: 'ranked-celebration', label: 'Ranked result', hint: '' },
    { id: 'end-of-game-stats', label: 'Stats screen', hint: '' },
  ]

  async function toggle(id: string, on: boolean) {
    if (!s) return
    const list = on ? [...new Set([...s.skipScreens, id])] : s.skipScreens.filter((x) => x !== id)
    msg = (await live.patch({ skipScreens: list })) ?? ''
  }
</script>

<Card title="Skip post-game screens" hint="Closes the screens you pick as soon as they appear.">
  {#if s}
    {#each screens as x (x.id)}
      <Toggle label={x.label} hint={x.hint} checked={s.skipScreens.includes(x.id)} onchange={(v) => toggle(x.id, v)} />
    {/each}
    {#if msg}<p class="err" role="alert">{msg}</p>{/if}
  {/if}
</Card>
