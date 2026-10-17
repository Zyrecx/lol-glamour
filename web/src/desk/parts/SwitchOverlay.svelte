<script lang="ts">
  // Progress and the "this closes the Riot Client" question for any account change, whichever page started it.
  import Mark from '../../shell/parts/Mark.svelte'
  import { profileActions as pa } from '../profiles.svelte'
</script>

{#if pa.busy && !pa.busy.startsWith('Saving') && !pa.busy.startsWith('Checking')}
  <div class="veil" role="status" aria-live="polite">
    <div class="box"><span class="spin"><Mark size={46} /></span><p>{pa.busy}</p><small>The Riot Client restarts. This takes about half a minute.</small></div>
  </div>
{:else if pa.confirmClose}
  <div class="veil" role="alertdialog" aria-modal="true" aria-label="Close the Riot Client?">
    <div class="box">
      <h2 class="display">Close the Riot Client?</h2>
      <p>Glamour needs to restart it to change account{pa.confirmClose.willClose.length ? `, which also closes ${pa.confirmClose.willClose.join(' and ')}` : ''}.</p>
      <div class="row"><button type="button" class="quiet" onclick={() => (pa.confirmClose = null)}>Not now</button><button type="button" onclick={pa.confirmClose.run}>Close and continue</button></div>
    </div>
  </div>
{/if}

<style>
  .veil { position: fixed; inset: 0; z-index: 100; display: grid; place-items: center; background: rgba(3, 6, 12, .78); backdrop-filter: blur(4px); }
  .box { width: min(420px, 86vw); display: flex; flex-direction: column; align-items: center; gap: 12px; text-align: center; padding: 28px; background: #0B121E; border: 1px solid var(--g-gold); border-radius: 14px; }
  .box p { font-size: 15px; }
  small { color: var(--g-muted); }
  .spin { display: inline-flex; animation: g-spin 3.2s linear infinite; }
  .row { display: flex; gap: 10px; margin-top: 6px; }
</style>
