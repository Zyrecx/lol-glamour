<script lang="ts">
  // Settings > Glamour (SPEC "Publishing"): "Help improve Glamour", updates, version and install folder. PC only: the host refuses a phone.
  import { act } from '../api'
  import { live } from '../live.svelte'
  import Card from '../components/Card.svelte'
  import Segmented from '../components/Segmented.svelte'
  import Toggle from '../components/Toggle.svelte'

  type Mode = 'automatic' | 'notify' | 'off'
  const modes: { value: Mode; label: string }[] = [{ value: 'automatic', label: 'Automatic' }, { value: 'notify', label: 'Notify me' }, { value: 'off', label: 'Off' }]
  let err = $state('')
  let busy = $state(false)
  let drawn = $state(0)

  const app = $derived(live.app)
  const status = $derived.by(() => {
    const u = app?.updates
    if (!u?.available) return ''
    switch (u.state) {
      case 'checking': return 'Looking for an update…'
      case 'downloading': return `Downloading ${u.version ?? 'an update'}…`
      case 'ready': return u.mode === 'automatic' ? `${u.version} goes in when Glamour exits.` : `${u.version} is ready.`
      case 'error': return u.error ?? 'Could not look for updates.'
      case 'off': return ''
      default: return 'Up to date.'
    }
  })

  async function run(path: string, method: string, body?: unknown) {
    busy = true
    err = (await act(path, method, body)) ?? ''
    await live.refreshApp()
    busy = false
    drawn++
  }
</script>

{#if app}
  <Card title="Glamour">
    {#key drawn}
      <Toggle label="Help improve Glamour" hint="Sends errors and anonymous usage counts. No names or ids." checked={app.telemetry}
        onchange={(enabled) => run('/app/telemetry', 'PUT', { enabled })} />
    {/key}
    {#if app.updates.available}
      <p class="muted">Updates</p>
      <Segmented label="Updates" options={modes} value={app.updates.mode} onchange={(mode) => run('/app/updates', 'PUT', { mode })} />
      {#if status}<p class="muted">{status}</p>{/if}
      <div class="row">
        {#if app.updates.state === 'ready' && app.updates.mode !== 'automatic'}
          <button type="button" disabled={busy} onclick={() => run('/app/updates/restart', 'POST')}>Restart to update</button>
        {/if}
        <button type="button" class="quiet" disabled={busy || app.updates.state === 'checking' || app.updates.state === 'downloading'} onclick={() => run('/app/updates/check', 'POST')}>Check now</button>
      </div>
    {/if}
    <p class="muted">Version {app.version}{app.channel === 'dev' ? ' (dev build)' : ''}</p>
    {#if app.installFolder}
      <p class="muted folder">Installed to {app.installFolder}</p>
      <button type="button" class="quiet" onclick={() => run('/app/open-folder', 'POST')}>Open folder</button>
    {/if}
    {#if err}<p class="warn">{err}</p>{/if}
  </Card>
{/if}

<style>
  .row { display: flex; gap: .5rem; flex-wrap: wrap; }
  .folder { overflow-wrap: anywhere; }
</style>
