<script lang="ts">
  import { onMount } from 'svelte'
  import { act, getJson } from '../api'
  import type { Device } from '../types'
  import Card from '../components/Card.svelte'

  let devices = $state<Device[] | null>(null)
  let err = $state('')

  async function load() {
    try { devices = await getJson<Device[]>('/auth/devices') } catch (e) { err = e instanceof Error ? e.message : 'Could not load devices.' }
  }
  onMount(load)

  async function revoke(d: Device) {
    err = (await act('/auth/devices/' + encodeURIComponent(d.id), 'DELETE')) ?? ''
    if (!err && d.current) location.replace('/login.html')
    else load()
  }
  async function logoutAll() {
    await act('/auth/logout-all')
    location.reload()
  }
</script>

<Card title="Paired devices" hint="Phones and browsers signed in to Glamour.">
  {#if devices === null}
    <p class="muted">Loading…</p>
  {:else if devices.length === 0}
    <p class="muted">No paired devices.</p>
  {:else}
    <ul>
      {#each devices as d (d.id)}
        <li>
          <span>
            {d.name || 'Device'}{#if d.current} <b>(this one)</b>{/if}
            <br /><span class="muted">last seen {new Date(d.lastSeen).toLocaleString([], { dateStyle: 'medium', timeStyle: 'short' })}</span>
          </span>
          <button type="button" class="quiet" onclick={() => revoke(d)}>Revoke</button>
        </li>
      {/each}
    </ul>
  {/if}
  {#if err}<p class="err" role="alert">{err}</p>{/if}
  <button type="button" class="danger full" onclick={logoutAll}>Log out all devices</button>
</Card>

<style>
  ul { list-style: none; margin: 0; padding: 0; display: grid; gap: .75rem; }
  li { display: flex; justify-content: space-between; align-items: center; gap: .75rem; }
</style>
