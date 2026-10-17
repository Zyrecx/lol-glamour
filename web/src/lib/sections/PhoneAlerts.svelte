<script lang="ts">
  // Which channel the PHONE gets alerts on, and for what. This window never subscribes itself: the PC does not need pings about a League client
  // sitting next to it. A phone subscribes from its own More tab; here we choose the channel, mute kinds and send a test.
  import { onMount } from 'svelte'
  import { live } from '../live.svelte'
  import { NOTIFY_KINDS } from '../notifyKinds'
  import { pushStatus, type PushStatus } from '../push'
  import Card from '../components/Card.svelte'
  import Toggle from '../components/Toggle.svelte'

  type Channel = 'off' | 'webpush' | 'ntfy'
  const s = $derived(live.settings)
  const channel = $derived<Channel>(s?.webPushEnabled ? 'webpush' : s?.ntfyEnabled ? 'ntfy' : 'off')
  let msg = $state('')
  let status = $state<PushStatus | null>(null)

  onMount(async () => { try { status = await pushStatus() } catch { status = null } })

  async function choose(next: Channel) {
    if (next === channel) return
    msg = (await live.patch({ webPushEnabled: next === 'webpush', ntfyEnabled: next === 'ntfy' })) ?? ''
  }
  async function setKind(id: string, on: boolean) {
    const muted = (s?.mutedNotifications ?? []).filter((k) => k !== id)
    msg = (await live.patch({ mutedNotifications: on ? muted : [...muted, id] })) ?? ''
  }
  async function test() { msg = (await live.act('/notify/test')) ?? 'Sent.' }
  async function copy() {
    try { await navigator.clipboard.writeText(s?.ntfyTopic ?? ''); msg = 'Topic copied.' } catch { msg = 'Copy not allowed here; select the topic text.' }
  }
</script>

<Card title="Phone alerts" hint="Alerts on your phone when you are away from the PC.">
  {#if s}
    <div class="seg" role="radiogroup" aria-label="Alert channel">
      <button type="button" role="radio" aria-checked={channel === 'off'} class:on={channel === 'off'} onclick={() => choose('off')}>Off</button>
      <button type="button" role="radio" aria-checked={channel === 'webpush'} class:on={channel === 'webpush'} onclick={() => choose('webpush')}>Web push</button>
      <button type="button" role="radio" aria-checked={channel === 'ntfy'} class:on={channel === 'ntfy'} onclick={() => choose('ntfy')}>ntfy</button>
    </div>
    {#if channel === 'webpush'}
      <p class="muted">
        {status ? `${status.devices} ${status.devices === 1 ? 'phone is' : 'phones are'} subscribed.` : ''}
        Subscribe a phone from its More tab.
      </p>
      <button type="button" class="quiet" onclick={test}>Send a test</button>
    {:else if channel === 'ntfy'}
      <p class="muted">Server <code>{s.ntfyServer}</code> · Topic <code>{s.ntfyTopic}</code></p>
      <div class="row2"><button type="button" class="quiet" onclick={copy}>Copy topic</button><button type="button" class="quiet" onclick={test}>Send a test</button></div>
    {/if}
    {#if channel !== 'off'}
      <h3 class="label">Notify me about</h3>
      {#each NOTIFY_KINDS as k (k.id)}
        <Toggle label={k.label} hint={k.hint} checked={!(s.mutedNotifications ?? []).includes(k.id)} onchange={(on) => setKind(k.id, on)} />
      {/each}
    {/if}
    {#if msg}<p class="muted" role="status">{msg}</p>{/if}
  {/if}
</Card>

<style>
  .seg { display: flex; gap: 6px; }
  .seg button { flex: 1; background: rgba(6, 10, 18, .55) !important; color: var(--g-fg) !important; border: 1px solid rgba(200, 170, 110, .4) !important; }
  .seg button.on { background: var(--g-teal) !important; color: var(--g-teal-ink) !important; border-color: var(--g-gold) !important; }
  .row2 { display: flex; gap: 8px; }
  h3 { margin-top: 8px; }
</style>
