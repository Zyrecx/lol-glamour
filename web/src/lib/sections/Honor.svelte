<script lang="ts">
  import { live } from '../live.svelte'
  import Card from '../components/Card.svelte'
  import Toggle from '../components/Toggle.svelte'

  const s = $derived(live.view)
  // The list is edited as a draft and only sent on Save, so flipping the switch never saves half-typed text.
  let draft = $state<string | null>(null)
  let msg = $state('')
  const text = $derived(draft ?? s?.autoHonorFriends.join('\n') ?? '')

  async function save(change: Parameters<typeof live.patch>[0], done = '') {
    const err = await live.patch(change)
    msg = err ?? done
    return !err
  }
  async function saveList() {
    const friends = text.split('\n').map((x) => x.trim()).filter(Boolean)
    if (await save({ autoHonorFriends: friends }, 'Saved.')) draft = null
  }
</script>

<Card title="Honor after a game" hint="Honors friends from your list after each game.">
  {#if s}
    <Toggle label="Auto-honor" checked={s.autoHonorEnabled} onchange={(v) => save({ autoHonorEnabled: v })} />
    <label class="stack">
      <span class="muted">Riot IDs, one per line, best friend first</span>
      <textarea rows="4" placeholder="Name#Tag" autocapitalize="off" spellcheck="false" value={text} oninput={(e) => (draft = e.currentTarget.value)}></textarea>
    </label>
    <div class="row">
      <span class="muted" role="status">{msg}</span>
      <button type="button" disabled={draft === null} onclick={saveList}>Save list</button>
    </div>
    {#if s.autoHonorEnabled}
      <Toggle label="Stay after honoring friends" hint="Your other votes are yours to give." checked={s.honorStay} onchange={(v) => save({ honorStay: v })} />
    {/if}
    <Toggle label="Skip the honor screen when no listed friend played" checked={s.skipHonorScreen} onchange={(v) => save({ skipHonorScreen: v })} />
  {/if}
</Card>
