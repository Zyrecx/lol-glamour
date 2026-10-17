<script lang="ts">
  // One Riot account as a card: icon in a gold ring (the signed-in one is lit teal: gradient ring, tinted card), name, Riot ID, level and solo rank.
  import type { Snippet } from 'svelte'
  import { label, server } from '../../lib/profileLabel'
  import { rankText, tierColor } from '../../lib/rank'
  import type { Profile } from '../../lib/types'
  let { p, active = false, children }: { p: Profile; active?: boolean; children?: Snippet } = $props()
  const solo = $derived(p.card?.solo ?? null)
  const sub = $derived(server(p.region) ?? '')
  const rankLine = $derived(rankText(solo) ?? (p.card ? 'Unranked' : p.state === 'needs-sign-in' ? 'Needs sign-in' : null))
</script>

<article class="acct" class:active>
  <div class="ring" class:ghost={!p.hasIcon}>
    {#if p.hasIcon}<img src={`/api/profiles/${p.id}/icon?v=${p.card?.iconId ?? 0}`} alt="" width="64" height="64" />{:else}<span class="display">{label(p).slice(0, 1).toUpperCase()}</span>{/if}
    {#if p.card}<span class="lvl num">{p.card.level}</span>{/if}
  </div>
  <div class="who">
    <b class="display name">{label(p)}</b>
    {#if sub}<span class="sub">{sub}</span>{/if}
    {#if rankLine}<span class="rank" style:--tier={solo ? tierColor[solo.tier] : null}>{rankLine}</span>{/if}
  </div>
  {#if children}<div class="acts">{@render children()}</div>{/if}
</article>

<style>
  .acct {
    position: relative; display: grid; grid-template-columns: auto minmax(0, 1fr); gap: 4px 14px; align-items: center; padding: 16px;
    background: linear-gradient(160deg, rgba(20, 30, 46, .96), rgba(10, 16, 26, .96)); border: 1px solid var(--g-line); border-radius: var(--g-radius);
    /* League-client cut corner */
    clip-path: polygon(0 0, calc(100% - 14px) 0, 100% 14px, 100% 100%, 14px 100%, 0 calc(100% - 14px));
    transition: border-color .2s;
  }
  .acct:hover { border-color: var(--g-gold); }
  .acct.active {
    border-color: var(--g-teal); box-shadow: inset 0 0 0 1px rgba(45, 226, 196, .3), inset 0 0 40px rgba(45, 226, 196, .1);
    background: linear-gradient(160deg, rgba(20, 52, 58, .96), rgba(10, 22, 30, .96));
  }
  .ring { position: relative; width: 72px; height: 72px; border-radius: 50%; padding: 3px; background: var(--g-gold); display: grid; place-items: center; }
  .active .ring { background: var(--g-grad); box-shadow: 0 0 18px rgba(45, 226, 196, .35); }
  .ring img { width: 100%; height: 100%; border-radius: 50%; object-fit: cover; display: block; border: 2px solid var(--g-bg); }
  .ring.ghost { background: rgba(200, 170, 110, .35); }
  .ring.ghost span { font-size: 28px; color: var(--g-fg2); }
  .lvl { position: absolute; bottom: -4px; left: 50%; transform: translateX(-50%); background: var(--g-bg); border: 1px solid var(--g-gold); border-radius: 999px; padding: 0 7px; font-size: 11px; line-height: 16px; color: var(--g-fg2); }
  .who { display: flex; flex-direction: column; min-width: 0; gap: 2px; }
  .name { font-size: 17px; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
  .sub { font-size: 12.5px; color: var(--g-muted); white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
  .rank { font-size: 13px; font-weight: 600; color: var(--g-fg2); display: flex; align-items: center; gap: 7px; }
  .rank::before { content: ''; width: 8px; height: 8px; transform: rotate(45deg); background: var(--tier, var(--g-faint)); }
  .acts { grid-column: 1 / -1; display: flex; flex-wrap: wrap; gap: 8px; margin-top: 10px; }
</style>
