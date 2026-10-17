<script lang="ts">
  import { onMount } from 'svelte'
  import { live } from '../live.svelte'
  import Card from '../components/Card.svelte'
  import { NOTIFY_KINDS } from '../notifyKinds'
  import Toggle from '../components/Toggle.svelte'
  import { pushStatus, pushSupported, subscribeThisDevice, unsubscribeThisDevice, type PushStatus } from '../push'

  type Channel = 'off' | 'webpush' | 'ntfy'

  const s = $derived(live.settings)
  const canPush = pushSupported()
  const channel = $derived<Channel>(s?.webPushEnabled ? 'webpush' : s?.ntfyEnabled ? 'ntfy' : 'off')
  let msg = $state('')
  let busy = $state(false)
  let status = $state<PushStatus | null>(null)

  const deepLink = $derived.by(() => {
    if (!s?.ntfyEnabled || !s.ntfyTopic) return ''
    try { return `ntfy://${new URL(s.ntfyServer).host}/${encodeURIComponent(s.ntfyTopic)}` } catch { return '' }
  })

  async function refresh() {
    try { status = await pushStatus() } catch { status = null }
  }
  onMount(refresh)

  async function choose(next: Channel) {
    if (busy || next === channel) return
    busy = true
    msg = ''
    try {
      if (next === 'webpush') {
        const err = await subscribeThisDevice()
        if (err) { msg = err; return }
        msg = (await live.patch({ webPushEnabled: true })) ?? ''
      } else {
        msg = (await live.patch({ webPushEnabled: false, ntfyEnabled: next === 'ntfy' })) ?? ''
      }
      await refresh()
    } finally { busy = false }
  }

  async function subscribeHere() {
    busy = true
    msg = (await subscribeThisDevice()) ?? 'This phone is subscribed.'
    await refresh()
    busy = false
  }
  async function unsubscribeHere() {
    busy = true
    msg = (await unsubscribeThisDevice()) ?? 'This phone will no longer get pushes.'
    await refresh()
    busy = false
  }
  async function setKind(id: string, on: boolean) {
    const muted = (s?.mutedNotifications ?? []).filter((k) => k !== id)
    msg = (await live.patch({ mutedNotifications: on ? muted : [...muted, id] })) ?? ''
  }
  async function test() {
    msg = (await live.act('/notify/test')) ?? 'Sent.'
  }
  async function copy() {
    try { await navigator.clipboard.writeText(s?.ntfyTopic ?? ''); msg = 'Topic copied.' } catch { msg = 'Copy not allowed here; select the topic text.' }
  }
</script>

<Card title="Phone notifications">
  {#if s}
    <div class="seg" role="radiogroup" aria-label="Notification channel">
      <button type="button" role="radio" aria-checked={channel === 'off'} class:on={channel === 'off'} disabled={busy} onclick={() => choose('off')}>Off</button>
      <button type="button" role="radio" aria-checked={channel === 'webpush'} class:on={channel === 'webpush'} disabled={busy || !canPush} onclick={() => choose('webpush')}>Web push</button>
      <button type="button" role="radio" aria-checked={channel === 'ntfy'} class:on={channel === 'ntfy'} disabled={busy} onclick={() => choose('ntfy')}>ntfy</button>
    </div>

    {#if !canPush}
      <p class="muted">Web push needs Glamour installed as an app. Use ntfy otherwise.</p>
    {/if}

    {#if channel === 'webpush'}
      <p class="muted">
        {#if status}{status.subscribed ? 'This phone is subscribed.' : 'This phone is not subscribed.'} {status.devices} subscribed in all.{/if}
      </p>
      {#if canPush}
        {#if status && !status.subscribed}
          <button type="button" class="quiet full" disabled={busy} onclick={subscribeHere}>Subscribe this phone</button>
        {:else if status?.subscribed}
          <button type="button" class="quiet full" disabled={busy} onclick={unsubscribeHere}>Unsubscribe this phone</button>
        {/if}
      {/if}
      <button type="button" class="quiet full" onclick={test}>Send a test</button>
    {:else if channel === 'ntfy'}
      <p class="muted">Through the ntfy app.<br />Server <code>{s.ntfyServer}</code><br />Topic <code>{s.ntfyTopic}</code></p>
      {#if deepLink}<a class="btn" href={deepLink}>Subscribe in ntfy (one tap)</a>{/if}
      <button type="button" class="quiet full" onclick={copy}>Copy topic</button>
      <button type="button" class="quiet full" onclick={test}>Send a test</button>
    {/if}
    {#if channel !== 'off'}
      <h4>Notify me about</h4>
      {#each NOTIFY_KINDS as k (k.id)}
        <Toggle label={k.label} hint={k.hint} checked={!(s.mutedNotifications ?? []).includes(k.id)} onchange={(on) => setKind(k.id, on)} />
      {/each}
    {/if}
    {#if msg}<p class="muted" role="status">{msg}</p>{/if}
  {/if}
</Card>

<style>
  .btn { display: block; text-align: center; background: var(--accent); color: var(--accent-fg); text-decoration: none; padding: .7rem 1rem; border-radius: var(--radius-sm); }
  h4 { margin: .9rem 0 .2rem; font-size: .85rem; opacity: .7; }
  .seg { display: flex; gap: .25rem; margin-bottom: .6rem; }
  .seg button { flex: 1; padding: .55rem .5rem; border-radius: var(--radius-sm); border: 1px solid var(--border, currentColor); background: transparent; color: inherit; }
  .seg button.on { background: var(--accent); color: var(--accent-fg); border-color: transparent; }
  .seg button:disabled { opacity: .5; }
</style>
