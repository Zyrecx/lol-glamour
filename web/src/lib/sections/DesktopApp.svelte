<script lang="ts">
  import { onMount } from 'svelte'
  import { act, getJson } from '../api'
  import Card from '../components/Card.svelte'
  import Segmented from '../components/Segmented.svelte'
  import Toggle from '../components/Toggle.svelte'

  type Side = 'auto' | 'right' | 'left'
  let info = $state<{ available: boolean; autostart: boolean; panelSide: Side } | null>(null)
  let err = $state('')
  const sides: { value: Side; label: string }[] = [{ value: 'auto', label: 'Auto' }, { value: 'left', label: 'Left' }, { value: 'right', label: 'Right' }]

  onMount(async () => {
    try { info = await getJson('/desktop') } catch { info = null }
  })

  // A refused change leaves `info` as it was, so the switch would keep showing the click: redraw it from `info` after every answer.
  let drawn = $state(0)
  async function setAutostart(enabled: boolean) {
    err = (await act('/desktop/autostart', 'PUT', { enabled })) ?? ''
    try { info = await getJson('/desktop') } catch { /* keep what we had */ }
    drawn++
  }

  async function setSide(side: Side) {
    err = (await act('/desktop/panel-side', 'PUT', { side })) ?? ''
    try { info = await getJson('/desktop') } catch { /* keep what we had */ }
  }
</script>

{#if info?.available}
  <Card title="Desktop app">
    {#key drawn}<Toggle label="Start with Windows" checked={info.autostart} onchange={setAutostart} />{/key}
    <p class="muted">Closing the window keeps Glamour in the tray.</p>
    <p class="muted">Panel side next to League (Auto takes the side with room).</p>
    <Segmented label="Panel side" options={sides} value={info.panelSide} onchange={setSide} />
    {#if err}<p class="warn">{err}</p>{/if}
  </Card>
{/if}
