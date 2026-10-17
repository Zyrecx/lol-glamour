<script lang="ts">
  // The rail's quick account switcher: who is signed in, and every other saved account one click away. Switching shows progress in the overlay.
  import { label } from '../../lib/profileLabel'
  import { rankText } from '../../lib/rank'
  import { profileActions as pa } from '../profiles.svelte'
  let open = $state(false)
  const snap = $derived(pa.snap)
  const list = $derived(pa.list)
  const active = $derived(list.find((p) => p.id === snap?.activeId && !snap?.adding) ?? null)
  const others = $derived(list.filter((p) => p !== active))
  const busy = $derived(!!pa.busy)
</script>

<svelte:window onkeydown={(e) => { if (e.key === 'Escape') open = false }} onclick={(e) => { if (open && !(e.target as Element).closest('.sw')) open = false }} />

<div class="sw">
  <button type="button" class="now" aria-expanded={open} aria-haspopup="menu" onclick={() => (open = !open)} disabled={!snap}>
    <span class="ava" class:ghost={!active?.hasIcon}>
      {#if active?.hasIcon}<img src={`/api/profiles/${active.id}/icon?v=${active.card?.iconId ?? 0}`} alt="" width="38" height="38" />{:else}?{/if}
    </span>
    <span class="txt">
      <span class="label">Playing as</span>
      <b class="display">{active ? label(active) : (snap?.signedIn ? 'Unsaved account' : 'Not signed in')}</b>
      {#if active?.card?.solo}<span class="r">{rankText(active.card.solo)}</span>{/if}
    </span>
    <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.4" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M6 9l6 6 6-6" /></svg>
  </button>
  {#if open}
    <div class="menu" role="menu">
      {#each others as p (p.id)}
        <button type="button" role="menuitem" class="opt" disabled={busy || snap?.adding || p.state === 'needs-sign-in'} onclick={() => { open = false; pa.switchTo(p) }}>
          <span class="ava sm" class:ghost={!p.hasIcon}>{#if p.hasIcon}<img src={`/api/profiles/${p.id}/icon?v=${p.card?.iconId ?? 0}`} alt="" width="30" height="30" />{:else}?{/if}</span>
          <span class="txt"><b>{label(p)}</b><span class="r">{p.state === 'needs-sign-in' ? 'Needs sign-in' : (rankText(p.card?.solo) ?? 'Switch')}</span></span>
        </button>
      {:else}
        <p class="empty">No other saved accounts yet. Add one on the Accounts page.</p>
      {/each}
      <a class="all" href="#/accounts" onclick={() => (open = false)}>Manage accounts</a>
    </div>
  {/if}
</div>

<style>
  .sw { position: relative; }
  .now, .opt { display: flex; align-items: center; gap: 10px; width: 100%; text-align: left; }
  .now { padding: 10px; height: auto; min-height: 0; background: rgba(13, 20, 32, .9) !important; color: var(--g-fg) !important; border: 1px solid var(--g-line) !important; border-radius: 10px; }
  .now:hover:not(:disabled) { border-color: var(--g-gold) !important; filter: none; }
  .now svg { margin-left: auto; color: var(--g-muted); }
  .ava { flex: none; width: 42px; height: 42px; border-radius: 50%; padding: 2px; background: var(--g-grad); display: grid; place-items: center; color: var(--g-fg2); font-weight: 700; }
  .ava.sm { width: 34px; height: 34px; background: var(--g-gold); }
  .ava img { width: 100%; height: 100%; border-radius: 50%; object-fit: cover; border: 2px solid var(--g-bg); display: block; }
  .ava.ghost { background: rgba(200, 170, 110, .3); }
  .txt { display: flex; flex-direction: column; min-width: 0; line-height: 1.25; }
  .txt b { font-size: 14px; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
  .label { font-size: 9.5px; letter-spacing: .2em; text-transform: uppercase; color: var(--g-muted); font-weight: 700; }
  .r { font-size: 11.5px; color: var(--g-muted); white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
  .menu {
    position: absolute; z-index: 20; left: 0; right: 0; top: calc(100% + 6px); display: flex; flex-direction: column; gap: 2px; padding: 6px;
    background: #0B121E; border: 1px solid var(--g-gold); border-radius: 10px; box-shadow: 0 14px 40px rgba(0, 0, 0, .6);
  }
  .opt { padding: 7px 8px; height: auto; background: transparent !important; color: var(--g-fg) !important; border: 1px solid transparent !important; border-radius: 8px; }
  .opt:hover:not(:disabled) { background: rgba(45, 226, 196, .09) !important; border-color: rgba(45, 226, 196, .35) !important; filter: none; }
  .empty { padding: 8px; font-size: 12.5px; color: var(--g-muted); }
  .all { margin-top: 2px; padding: 8px; text-align: center; font-size: 12px; letter-spacing: .08em; text-transform: uppercase; text-decoration: none; color: var(--g-gold); border-top: 1px solid var(--g-line-soft); }
</style>
