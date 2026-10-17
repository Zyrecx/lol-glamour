<script lang="ts">
  import { onMount } from 'svelte'
  import { act, api, errorText, getJson } from '../api'
  import type { NetworkInfo } from '../types'
  import Card from '../components/Card.svelte'
  import Toggle from '../components/Toggle.svelte'

  const problems: Record<string, string> = {
    'lan-off': 'Phone access is off.',
    'port-in-use': 'Another program is using the phone port. Close it, then turn phone access off and on.',
    'network-public': 'This network is set to Public in Windows. Set it to Private.',
    'firewall-blocked': 'Windows Firewall blocks phones from reaching Glamour.',
  }

  let net = $state<NetworkInfo | null>(null)
  let busy = $state(false)
  let restarting = $state(false)
  let note = $state('')
  let err = $state('')

  async function load() {
    try { net = await getJson<NetworkInfo>('/network'); return true } catch (e) { err = e instanceof Error ? e.message : 'Could not read network state.'; return false }
  }
  onMount(load)

  /** Turns phone access on or off. The host restarts its listeners (the desktop app does it by itself), so wait until it answers again. */
  async function setPhoneAccess(enabled: boolean) {
    busy = true
    err = note = ''
    try {
      const r = await api('/network/phone-access', 'PUT', { enabled })
      if (!r.ok) { err = await errorText(r); return }
      const j = await r.json()
      if (!j.restarting) { note = 'Saved. Restart Glamour for it to take effect.'; await load(); return }
      restarting = true
      for (let i = 0; i < 40; i++) {
        await new Promise((res) => setTimeout(res, 500))
        err = ''
        if (await load() && net?.phoneAccessWanted === enabled && (i > 1 || net.listeningOnLan === enabled || net.problem === 'port-in-use')) break
      }
      err = ''
    } finally { restarting = false; busy = false }
  }

  async function setWebUi(on: boolean) {
    err = (await act('/network/web-ui', 'PUT', { enabled: on })) ?? ''
    load()
  }
  async function setRequireHttps(on: boolean) {
    err = (await act('/network/require-https', 'PUT', { enabled: on })) ?? ''
    load()
  }
  async function fix() {
    busy = true
    err = (await act('/network/firewall')) ?? ''
    busy = false
    load()
  }
</script>

<Card title="Phone access" hint="Use Glamour from a phone on your Wi-Fi.">
  {#if net}
    <Toggle label="Allow phone access" hint={net.phoneAccessForced ? 'Set by a start-up argument.' : restarting ? 'Restarting Glamour…' : ''} checked={net.phoneAccessWanted} disabled={busy || net.phoneAccessForced} onchange={setPhoneAccess} />
    {#if net.phoneAccessWanted && !restarting}
      {#if net.problem}<p class="warn">{problems[net.problem] ?? net.problem}</p>{:else if net.listeningOnLan}<p class="ok">Ready for phones.</p>{/if}
      {#if net.problem === 'firewall-blocked'}
        <button type="button" class="full" disabled={busy} onclick={fix}>Fix firewall (asks for admin)</button>
      {/if}
    {/if}
    {#if note}<p class="muted" role="status">{note}</p>{/if}

    <details>
      <summary>Advanced</summary>
      <p class="muted">Port {net.port}. Addresses: {#each net.addresses as a (a)}<code>{a}</code> {:else}none found{/each}</p>
      <Toggle label="Serve the web UI to phones" hint="Off: phones get no web page." checked={net.webUiEnabled} onchange={setWebUi} />
      {#if net.https}
        <p class="muted">Secure connection (HTTPS) on port {net.https.port}. Certificate fingerprint, compare it on the phone's setup page:</p>
        <p><code>{net.https.fingerprint}</code></p>
        <Toggle label="Require HTTPS for phones" hint="Blocks phones paired without HTTPS." checked={net.requireHttps} onchange={setRequireHttps} />
      {:else if net.listeningOnLan}
        <p class="muted">No certificate yet.</p>
      {/if}
    </details>
  {:else if !err}
    <p class="muted">Loading…</p>
  {/if}
  {#if err}<p class="err" role="alert">{err}</p>{/if}
</Card>

<style>
  details { margin-top: .4rem; display: grid; gap: .5rem; }
  summary { cursor: pointer; color: var(--g-muted, var(--muted)); font-size: .85rem; letter-spacing: .08em; text-transform: uppercase; }
</style>
