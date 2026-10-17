<script lang="ts">
  import { overriddenKeys } from '../scope'
  import { label, server } from '../profileLabel'
  import { ProfileActions } from '../profileActions.svelte'
  import { rankText } from '../rank'
  import Card from '../components/Card.svelte'
  import Icon from '../components/Icon.svelte'

  const c = new ProfileActions()
  $effect(() => c.watchSignIn())
  const snap = $derived(c.snap)
  const list = $derived(c.list)

</script>

{#if !snap}
  <Card title="Accounts"><p class="muted">Loading…</p></Card>
{:else}
  <Card title="Accounts" hint="Switch accounts without a password.">
    {#if c.busy}<p class="busy" role="status">{c.busy}</p>{/if}
    {#if c.confirmClose}
      <div class="notice" role="alert">
        <p>This closes the Riot Client{c.confirmClose.willClose.length ? ` and ${c.confirmClose.willClose.join(' and ')}` : ''}.</p>
        <div class="row"><button type="button" class="quiet" onclick={() => (c.confirmClose = null)}>Not now</button><button type="button" onclick={c.confirmClose.run}>Close and continue</button></div>
      </div>
    {/if}
    {#if c.err}<p class="err" role="alert">{c.err}</p>{/if}

    {#if list.length === 0}
      <p class="muted">No saved accounts yet.</p>
    {:else}
      <ul>
        {#each list as p, i (p.id)}
          {@const own = overriddenKeys(p.overrides).length}
          {@const active = p.state === 'active' && !snap.adding}
          {@const act = p.state === 'needs-sign-in' || !active}
          <li class:active>
              <!-- The signed-in account is told by the lit ring round its icon, nothing else. -->
              <span class="av" title={active ? 'Signed in' : undefined}>
                {#if p.hasIcon}<img src={`/api/profiles/${p.id}/icon?v=${p.card?.iconId ?? 0}`} alt="" width="44" height="44" />{:else}{label(p).slice(0, 1).toUpperCase()}{/if}
              </span>
              <div class="who">
                <b>{label(p)}</b>
                {#if p.card}<span class="muted">{rankText(p.card.solo) ? `Solo ${rankText(p.card.solo)}` : 'Unranked'}</span>{/if}
                {#if p.region || p.card}<span class="muted">{[server(p.region), p.card ? `Level ${p.card.level}` : ''].filter(Boolean).join(' · ')}</span>{/if}
                {#if p.state === 'needs-sign-in' || own > 0}<span class="muted">{[p.state === 'needs-sign-in' ? 'Needs sign-in' : '', own > 0 ? `Own settings: ${own}` : ''].filter(Boolean).join(' · ')}</span>{/if}
              </div>
              <!-- Small tools in a column beside the name; only moves that can happen are shown. -->
              <div class="tools">
                {#if i > 0}<button type="button" class="quiet" aria-label={`Move ${label(p)} up`} title="Move up" disabled={!!c.busy} onclick={() => c.move(i, -1)}><Icon name="up" /></button>{/if}
                {#if i < list.length - 1}<button type="button" class="quiet" aria-label={`Move ${label(p)} down`} title="Move down" disabled={!!c.busy} onclick={() => c.move(i, 1)}><Icon name="down" /></button>{/if}
                <button type="button" class="quiet" class:armed={c.removing === p.id} aria-label={`Remove ${label(p)}`} title="Remove" onclick={() => (c.removing = c.removing === p.id ? null : p.id)}><Icon name="bin" /></button>
              </div>
              {#if c.removing === p.id || act}
                <div class="actions">
                  {#if c.removing === p.id}
                    <button type="button" class="danger" disabled={!!c.busy} onclick={() => c.remove(p.id)}>Remove</button>
                    <button type="button" class="quiet" onclick={() => (c.removing = null)}>Keep</button>
                  {:else if p.state === 'needs-sign-in'}
                    <button type="button" disabled={!!c.busy || snap.adding} onclick={() => c.beginAdd(p.id)}>Sign in again</button>
                  {:else}
                    <button type="button" disabled={!!c.busy || snap.adding} onclick={() => c.switchTo(p)}>Switch</button>
                  {/if}
                </div>
              {/if}
          </li>
        {/each}
      </ul>
    {/if}
  </Card>

  {#if c.unsaved}
    <Card title="Save the account that is signed in" hint="Save it so you can switch back to it.">
      <form class="stack" onsubmit={(e) => { e.preventDefault(); c.save(null) }}>
        <button type="submit" disabled={!!c.busy}>Save this account</button>
      </form>
    </Card>
  {:else if snap.adding}
    <Card title="Add an account" hint="Sign in on the PC.">
      <ol>
        <li>On the PC, sign in to the account you want to add. Tick “Stay signed in”.</li>
        <li>Come back here and save it.</li>
      </ol>
      <form class="stack" onsubmit={(e) => { e.preventDefault(); c.save(c.saveAs === 'new' ? null : c.saveAs) }}>
        {#if c.needSignIn.length > 0}
          <select bind:value={c.saveAs} aria-label="Save as">
            <option value="new">A new account</option>
            {#each c.needSignIn as p (p.id)}<option value={p.id}>Sign-in again for {label(p)}</option>{/each}
          </select>
        {/if}
        <p class="muted">{snap.signedIn ? 'Signed in. You can save it.' : 'Waiting for a sign-in to appear.'}</p>
        <div class="row">
          <button type="button" class="quiet" disabled={!!c.busy} onclick={() => c.refresh()}>Check again</button>
          <button type="submit" disabled={!!c.busy || !snap.signedIn}>Save account</button>
        </div>
        <button type="button" class="danger" disabled={!!c.busy} onclick={() => c.cancelAdd()}>Cancel and go back to the previous account</button>
      </form>
    </Card>
  {:else}
    <Card>
      <button type="button" class="full" disabled={!!c.busy} onclick={() => c.beginAdd()}>Add an account</button>
    </Card>
  {/if}
{/if}

<style>
  ul { list-style: none; margin: 0; padding: 0; display: grid; gap: .6rem; }
  li { display: grid; grid-template-columns: auto minmax(0, 1fr) auto; align-items: center; gap: .5rem .7rem; background: var(--card2); border-radius: var(--radius-sm); padding: .7rem .8rem; }
  .av { width: 44px; height: 44px; border-radius: 50%; display: grid; place-items: center; overflow: hidden; background: var(--line); color: var(--muted); font-weight: 700; box-shadow: 0 0 0 1px var(--line); transition: box-shadow .2s; }
  .av img { width: 100%; height: 100%; object-fit: cover; display: block; }
  .active .av { box-shadow: 0 0 0 2px var(--g-teal, var(--accent2)), 0 0 12px 1px color-mix(in srgb, var(--g-teal, var(--accent2)) 55%, transparent); }
  .who { display: grid; min-width: 0; overflow-wrap: anywhere; }
  .tools { display: grid; gap: .25rem; align-self: center; }
  .tools button { min-height: 0; width: 1.9rem; height: 1.7rem; padding: 0; display: grid; place-items: center; }
  .tools :global(svg) { width: .95rem; height: .95rem; }
  .tools .armed { color: #E86A6C; border-color: #5A2A2A; }
  .actions { grid-column: 2 / -1; display: flex; flex-wrap: wrap; gap: .4rem; }
  .actions button { min-height: 2.2rem; padding: .3rem .8rem; }
  .full { width: 100%; }
  .busy { color: var(--accent2); }
  .notice { background: #3a2a14; border-radius: var(--radius-sm); padding: .8rem 1rem; display: grid; gap: .6rem; }
  ol { margin: 0; padding-left: 1.3rem; display: grid; gap: .3rem; }
  ol li { display: list-item; background: none; padding: 0; border: 0; }
</style>
