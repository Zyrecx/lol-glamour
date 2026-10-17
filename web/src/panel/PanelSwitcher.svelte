<script lang="ts">
  // The rail avatar's account list (SPEC "PC app direction" 2): the same switcher as the window's rail, dropped over the panel beside the
  // avatar. A switch or an add asks first what it closes (the host's "confirm" answer); from queue to the end of a game the rows are greyed.
  import { label, server } from '../lib/profileLabel'
  import { ProfileActions } from '../lib/profileActions.svelte'
  import { rankText } from '../lib/rank'

  // fit: drawn over the open panel, so no wider than it; beside the collapsed rail the host sizes the window to the list instead.
  let { busyPhase, left, fit, onclose, onmanage }: { busyPhase: boolean; left: boolean; fit: boolean; onclose: () => void; onmanage: () => void } = $props()

  const c = new ProfileActions()
  const snap = $derived(c.snap)
  const list = $derived(c.list)
  const active = $derived(list.find((p) => p.id === snap?.activeId && !snap?.adding) ?? null)
  const others = $derived(list.filter((p) => p !== active))
  const locked = $derived(busyPhase || !!c.busy || !!snap?.adding)
</script>

<svelte:window onkeydown={(e) => { if (e.key === 'Escape') onclose() }} />

<div class="pop" class:left class:fit role="menu" aria-label="Accounts">
  <div class="me">
    <span class="ava">{#if active?.hasIcon}<img src={`/api/profiles/${active.id}/icon?v=${active.card?.iconId ?? 0}`} alt="" width="30" height="30" />{/if}</span>
    <span class="txt">
      <span class="cap">Playing as</span>
      <b>{active ? label(active) : snap?.signedIn ? 'Unsaved account' : 'Not signed in'}</b>
      {#if server(active?.region)}<span class="r">{server(active?.region)}</span>{/if}
    </span>
  </div>
  {#if busyPhase}<p class="note">Switching is off during queue and games.</p>{/if}
  {#if c.confirmClose}
    <div class="ask" role="alert">
      <p>This closes the Riot Client{c.confirmClose.willClose.length ? ` and ${c.confirmClose.willClose.join(' and ')}` : ''}.</p>
      <div class="row"><button type="button" class="quiet" onclick={() => (c.confirmClose = null)}>Not now</button><button type="button" onclick={c.confirmClose.run}>Close and continue</button></div>
    </div>
  {:else if c.busy}
    <p class="note busy" role="status">{c.busy}</p>
  {/if}
  {#if c.err}<p class="note err" role="alert">{c.err}</p>{/if}
  {#each others as p (p.id)}
    <button type="button" role="menuitem" class="opt" disabled={locked || p.state === 'needs-sign-in'} onclick={() => c.switchTo(p)}>
      <span class="ava sm">{#if p.hasIcon}<img src={`/api/profiles/${p.id}/icon?v=${p.card?.iconId ?? 0}`} alt="" width="24" height="24" />{/if}</span>
      <span class="txt"><b>{label(p)}</b><span class="r">{[server(p.region), p.state === 'needs-sign-in' ? 'Needs sign-in' : (rankText(p.card?.solo) ?? 'Unranked')].filter(Boolean).join(' · ')}</span></span>
    </button>
  {:else}
    <p class="note">No other saved accounts.</p>
  {/each}
  <div class="foot">
    <button type="button" class="link" disabled={locked} onclick={() => c.beginAdd()}>Add account</button>
    <button type="button" class="link" onclick={onmanage}>Manage</button>
  </div>
</div>

<style>
  .pop { position: absolute; z-index: 30; top: 80px; width: max-content; min-width: 190px; max-width: 300px; display: flex; flex-direction: column; gap: 2px; padding: 6px;
    background: #010A13; border: 1px solid var(--l-gold4); box-shadow: 0 12px 32px rgba(0, 0, 0, .7); left: 40px; }
  .pop.left { left: auto; right: 40px; }
  .pop.fit { max-width: calc(100vw - 48px); }
  /* As wide as the longest name: the sentences wrap inside that width instead of setting it. */
  .ask p { width: 0; min-width: 100%; }
  .note { width: 0; min-width: calc(100% - 12px); }
  .me { display: flex; align-items: center; gap: 8px; padding: 4px 6px 8px; border-bottom: 1px solid var(--l-frame); margin-bottom: 4px; }
  .ava { flex: none; width: 30px; height: 30px; border-radius: 50%; overflow: hidden; border: 1px solid var(--l-gold2); background: var(--l-grey3); }
  .ava.sm { width: 24px; height: 24px; border-color: var(--l-gold4); }
  .ava img { width: 100%; height: 100%; object-fit: cover; display: block; }
  .txt { display: flex; flex-direction: column; min-width: 0; line-height: 1.25; text-align: left; }
  .txt b { font-size: 13px; color: var(--l-gold1); white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
  .cap { font-size: 9.5px; letter-spacing: .16em; text-transform: uppercase; color: var(--l-grey1); }
  .r { font-size: 11px; color: var(--l-grey1); }
  .opt { display: flex; align-items: center; gap: 8px; width: 100%; padding: 6px; min-height: 0; border: 1px solid transparent; border-radius: 0; background: none; cursor: pointer; }
  .opt:hover:not(:disabled) { border-color: var(--l-gold4); background: #1E2328; filter: none; }
  .opt:disabled { cursor: default; opacity: .45; }
  .note { margin: 2px 6px 4px; font-size: 11.5px; color: var(--l-grey1); }
  .busy { color: var(--l-blue); }
  .err { color: #E86A6C; }
  .ask { margin: 2px 0 4px; padding: 8px; border: 1px solid #785A28; display: grid; gap: 8px; font-size: 12px; color: var(--l-gold1); }
  .ask p { margin: 0; }
  .row { display: flex; flex-wrap: wrap; gap: 6px; justify-content: flex-end; }
  .row button, .link { min-height: 0; height: 26px; padding: 0 10px; white-space: nowrap; border: 1px solid var(--l-gold4); border-radius: 0; background: #1E2328; color: var(--l-gold1);
    font: 700 10.5px var(--g-body); letter-spacing: .06em; text-transform: uppercase; cursor: pointer; }
  .row .quiet { background: none; border-color: var(--l-grey3); color: var(--l-grey1); }
  .foot { display: flex; gap: 6px; padding-top: 6px; margin-top: 4px; border-top: 1px solid var(--l-frame); }
  .link { flex: 1; padding: 0 6px; white-space: nowrap; background: none; }
  .link:disabled { color: var(--l-grey2); border-color: var(--l-grey3); cursor: default; }
</style>
