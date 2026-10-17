<script lang="ts">
  // Account switching is a main job on the PC: saved accounts as cards, one click to switch, plus the guided add and the per-account extras.
  import { overriddenKeys } from '../../lib/scope'
  import { profileActions as pa } from '../profiles.svelte'
  import AccountCard from '../parts/AccountCard.svelte'
  import { label } from '../../lib/profileLabel'
  import Icon from '../../lib/components/Icon.svelte'

  $effect(() => pa.watchSignIn())
  const snap = $derived(pa.snap)
  const list = $derived(pa.list)
  const locked = $derived(!!pa.busy || !!snap?.adding)
</script>

<header class="page-head">
  <span class="eyebrow">Your wardrobe</span>
  <h1 class="display">Accounts</h1>
  <p>Switch accounts without a password.</p>
</header>

{#if pa.err}<p class="err" role="alert">{pa.err}</p>{/if}

{#if !snap}
  <p class="muted">Loading…</p>
{:else}
  {#if pa.unsaved}
    <section class="panel add">
      <h2 class="display">Save the account that is signed in</h2>
      <p class="muted">Save it so you can switch back to it.</p>
      <form class="inline" onsubmit={(e) => { e.preventDefault(); pa.save(null) }}>
        <button type="submit" disabled={!!pa.busy}>Save this account</button>
      </form>
    </section>
  {/if}

  <div class="grid">
    {#each list as p, i (p.id)}
      {@const own = overriddenKeys(p.overrides).length}
      <AccountCard {p} active={p.state === 'active' && !snap.adding}>
        {#if pa.removing === p.id}
          <button type="button" class="danger" disabled={!!pa.busy} onclick={() => pa.remove(p.id)}>Remove {label(p)}</button>
          <button type="button" class="quiet" onclick={() => (pa.removing = null)}>Keep</button>
        {:else}
          {#if p.state === 'needs-sign-in'}
            <button type="button" disabled={locked} onclick={() => pa.beginAdd(p.id)}>Sign in again</button>
          {:else if p.state !== 'active' || snap.adding}
            <button type="button" disabled={locked} onclick={() => pa.switchTo(p)}>Switch to this account</button>
          {/if}
          <span class="tools">
            {#if i > 0}<button type="button" class="quiet icon" aria-label={`Move ${label(p)} earlier`} title="Move earlier" disabled={!!pa.busy} onclick={() => pa.move(i, -1)}><Icon name="left" /></button>{/if}
            {#if i < list.length - 1}<button type="button" class="quiet icon" aria-label={`Move ${label(p)} later`} title="Move later" disabled={!!pa.busy} onclick={() => pa.move(i, 1)}><Icon name="right" /></button>{/if}
            <button type="button" class="quiet icon" aria-label={`Remove ${label(p)}`} title="Remove" onclick={() => (pa.removing = p.id)}><Icon name="bin" /></button>
          </span>
          {#if own > 0}<span class="own">Own automation settings: {own}</span>{/if}
        {/if}
      </AccountCard>
    {/each}

    {#if snap.adding}
      <section class="panel add wide">
        <h2 class="display">Add an account</h2>
        <p class="muted">The Riot Client is open at its sign-in screen.</p>
        <ol>
          <li>Sign in to the account you want to add. Tick “Stay signed in”.</li>
          <li>Come back here and save it.</li>
        </ol>
        <form class="stack" onsubmit={(e) => { e.preventDefault(); pa.save(pa.saveAs === 'new' ? null : pa.saveAs) }}>
          {#if pa.needSignIn.length > 0}
            <select bind:value={pa.saveAs} aria-label="Save as">
              <option value="new">A new account</option>
              {#each pa.needSignIn as p (p.id)}<option value={p.id}>Sign-in again for {label(p)}</option>{/each}
            </select>
          {/if}
          <p class="muted" role="status">{snap.signedIn ? 'Signed in. You can save it.' : 'Waiting for a sign-in to appear…'}</p>
          <div class="inline">
            <button type="submit" disabled={!!pa.busy || !snap.signedIn}>Save account</button>
            <button type="button" class="quiet" disabled={!!pa.busy} onclick={() => pa.refresh()}>Check again</button>
            <button type="button" class="danger" disabled={!!pa.busy} onclick={() => pa.cancelAdd()}>Cancel and go back</button>
          </div>
        </form>
      </section>
    {:else if !pa.unsaved}
      <button type="button" class="tile" disabled={!!pa.busy} onclick={() => pa.beginAdd()}>
        <span class="plus" aria-hidden="true">+</span>
        <b class="display">Add an account</b>
        <small>Closes the Riot Client and its games.</small>
      </button>
    {/if}
  </div>
{/if}


<style>
  .grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(340px, 1fr)); gap: 14px; align-items: start; }
  .wide { grid-column: 1 / -1; }
  .add { display: flex; flex-direction: column; gap: 10px; }
  .add h2 { font-size: 16px; }
  .inline { display: flex; flex-wrap: wrap; gap: 8px; align-items: center; width: 100%; }
  .stack { display: grid; gap: 10px; }
  .tools { display: flex; flex-wrap: wrap; gap: 6px; margin-left: auto; }
  .icon { padding: 0; width: 36px; display: grid; place-items: center; }
  .icon :global(svg) { width: 18px; height: 18px; }
  .own { flex-basis: 100%; font-size: 12px; color: var(--g-muted); }
  ol { margin: 0; padding-left: 1.3rem; display: grid; gap: .3rem; font-size: 14px; }
  .tile {
    display: flex !important; flex-direction: column; align-items: center; justify-content: center; gap: 6px; min-height: 150px; padding: 18px !important; text-align: center;
    background: transparent !important; color: var(--g-fg) !important; border: 1px dashed rgba(200, 170, 110, .5) !important; border-radius: var(--g-radius) !important;
  }
  .tile:hover:not(:disabled) { border-color: var(--g-gold) !important; background: rgba(200, 170, 110, .05) !important; filter: none; }
  .plus { font-size: 34px; line-height: 1; color: var(--g-teal); }
  .tile small { color: var(--g-muted); font-weight: 400; letter-spacing: 0; max-width: 28ch; }
</style>
