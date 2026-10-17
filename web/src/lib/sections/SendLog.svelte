<script lang="ts">
  // Settings "Send a log" (SPEC "Publishing"): the PC makes a zip of its logs without names or ids, shows it in Explorer and opens
  // a GitHub issue to attach it to. PC only: the host refuses a phone.
  import { api, errorText } from '../api'
  import Card from '../components/Card.svelte'

  let busy = $state(false)
  let err = $state('')
  let made = $state<{ file: string; issue: string | null } | null>(null)

  async function send() {
    busy = true
    err = ''
    try {
      const r = await api('/logs/report', 'POST')
      if (r.ok) made = await r.json()
      else err = await errorText(r)
    } catch {
      err = 'Cannot reach the PC.'
    }
    busy = false
  }
</script>

<Card title="Problem report">
  <button type="button" class="quiet" disabled={busy} onclick={send}>Send a log</button>
  <p class="muted">Names and ids are taken out first.</p>
  {#if made}<p class="muted">Saved {made.file}{made.issue ? '. Attach it to the issue that opened.' : '.'}</p>{/if}
  {#if err}<p class="warn">{err}</p>{/if}
</Card>
