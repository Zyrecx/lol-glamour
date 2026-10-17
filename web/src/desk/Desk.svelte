<script lang="ts">
  // The PC window (SPEC "UI (decided)"): not the phone UI scaled up. A left rail, the account switcher always in reach, and only what the League
  // client on this PC does not already do. Pages live in the URL hash (#/accounts) so Back works.
  import './desk.css'
  import { live } from '../lib/live.svelte'
  import { clientStatus } from '../lib/now'
  import { champions } from '../lib/champions.svelte'
  import { backdrop } from '../shell/backdrop.svelte'
  import { motion } from '../shell/motion.svelte'
  import { art } from '../shell/util'
  import Mark from '../shell/parts/Mark.svelte'
  import Home from './pages/Home.svelte'
  import AutomationPage from './pages/AutomationPage.svelte'
  import AccountsPage from './pages/AccountsPage.svelte'
  import PhonePage from './pages/PhonePage.svelte'
  import ProfilePage from './pages/ProfilePage.svelte'
  import SettingsPage from './pages/SettingsPage.svelte'
  import Switcher from './parts/Switcher.svelte'
  import SwitchOverlay from './parts/SwitchOverlay.svelte'
  import Tour from './parts/Tour.svelte'

  type Page = 'home' | 'automation' | 'accounts' | 'profile' | 'phone' | 'settings'
  const NAV: { id: Page; label: string; path: string }[] = [
    { id: 'home', label: 'Home', path: 'M4 11l8-7 8 7v9h-5v-6H9v6H4z' },
    { id: 'automation', label: 'Automation', path: 'M13 2L4 14h7l-1 8 9-12h-7z' },
    { id: 'accounts', label: 'Accounts', path: 'M12 12a4 4 0 100-8 4 4 0 000 8zm-8 9c0-4 4-6 8-6s8 2 8 6' },
    { id: 'profile', label: 'Profile', path: 'M12 3l2.6 5.6 6.1.7-4.5 4.1 1.2 6-5.4-3-5.4 3 1.2-6-4.5-4.1 6.1-.7z' },
    { id: 'phone', label: 'Phone', path: 'M8 2h8a1 1 0 011 1v18a1 1 0 01-1 1H8a1 1 0 01-1-1V3a1 1 0 011-1zm4 16.5h.01' },
    { id: 'settings', label: 'Settings', path: 'M12.22 2h-.44a2 2 0 0 0-2 2v.18a2 2 0 0 1-1 1.73l-.43.25a2 2 0 0 1-2 0l-.15-.08a2 2 0 0 0-2.73.73l-.22.38a2 2 0 0 0 .73 2.73l.15.1a2 2 0 0 1 1 1.72v.51a2 2 0 0 1-1 1.74l-.15.09a2 2 0 0 0-.73 2.73l.22.38a2 2 0 0 0 2.73.73l.15-.08a2 2 0 0 1 2 0l.43.25a2 2 0 0 1 1 1.73V20a2 2 0 0 0 2 2h.44a2 2 0 0 0 2-2v-.18a2 2 0 0 1 1-1.73l.43-.25a2 2 0 0 1 2 0l.15.08a2 2 0 0 0 2.73-.73l.22-.39a2 2 0 0 0-.73-2.73l-.15-.08a2 2 0 0 1-1-1.74v-.5a2 2 0 0 1 1-1.74l.15-.09a2 2 0 0 0 .73-2.73l-.22-.38a2 2 0 0 0-2.73-.73l-.15.08a2 2 0 0 1-2 0l-.43-.25a2 2 0 0 1-1-1.73V4a2 2 0 0 0-2-2zM15 12a3 3 0 11-6 0 3 3 0 016 0z' }, // Lucide "settings", Copyright (c) Lucide Contributors 2022, ISC License
  ]
  let hash = $state(location.hash)
  const page = $derived<Page>(NAV.find((n) => n.id === hash.split('/')[1])?.id ?? 'home')

  const now = $derived(live.now)
  const pill = $derived(clientStatus(now))
  const inGame = $derived(['GameStart', 'InProgress', 'Reconnect'].includes(now.phase))

  // The page backdrop: the skin chosen as the profile background in the client, else the champion automation would play.
  const s = $derived(live.settings)
  const fallbackId = $derived((s?.autoLockChampionId ?? 0) > 0 ? s!.autoLockChampionId : s?.preHoverChampionId ?? null) // not "Best counter"
  const back = $derived(backdrop.championId > 0 ? art(champions.byId(backdrop.championId), backdrop.skinNum, 'splash') : art(champions.byId(fallbackId), 0, 'splash'))
  $effect(() => { backdrop.ensure(now.connected) })
  // Names in chips and pickers need the list on every page, not only where a picker opens; again once connected, in case the first try failed.
  $effect(() => { now.connected; champions.ensure() })
</script>

<svelte:window onhashchange={() => (hash = location.hash)} />

<div class="glam desk" class:calm={motion.reduced}>
  {#if back}<div class="d-backdrop" aria-hidden="true"><img src={back} alt="" /></div>{/if}

  <aside class="rail">
    <div class="brand">
      <Mark size={30} />
      <span class="display">GLAMOUR</span>
    </div>
    <Switcher />
    <nav aria-label="Main">
      {#each NAV as n (n.id)}
        <a href={`#/${n.id}`} class="item" class:on={page === n.id} aria-current={page === n.id ? 'page' : undefined}>
          <svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d={n.path} /></svg>
          {n.label}
        </a>
      {/each}
    </nav>
    <div class="status" class:warn={inGame || !now.connected}>
      <span class="dot"></span>
      <span>{inGame && !now.connected ? 'Client paused for the game' : pill.text}</span>
    </div>
  </aside>

  <main>
    {#if live.linkLost}
      <p class="lost" role="alert">Lost contact. If this stays, exit Glamour from the tray and open it again.</p>
    {/if}
    {#key page}
      <div class="page rise">
        {#if page === 'home'}<Home />{:else if page === 'automation'}<AutomationPage />{:else if page === 'accounts'}<AccountsPage />{:else if page === 'profile'}<ProfilePage />{:else if page === 'phone'}<PhonePage />{:else}<SettingsPage />{/if}
      </div>
    {/key}
  </main>
  <SwitchOverlay />
  <Tour />
</div>

<style>
  .desk { position: relative; display: grid; grid-template-columns: 236px minmax(0, 1fr); height: 100dvh; overflow: hidden; }
  .d-backdrop { position: absolute; inset: 0; z-index: 0; pointer-events: none; overflow: hidden; }
  .d-backdrop img {
    position: absolute; right: 0; top: 0; width: 78%; height: 78%; object-fit: cover; object-position: 60% 18%; opacity: .5;
    -webkit-mask-image: radial-gradient(120% 110% at 82% 0%, #000 8%, transparent 72%); mask-image: radial-gradient(120% 110% at 82% 0%, #000 8%, transparent 72%);
  }
  .rail {
    position: relative; z-index: 2; display: flex; flex-direction: column; gap: 14px; padding: 18px 14px 14px;
    background: linear-gradient(180deg, rgba(6, 10, 18, .96), rgba(6, 10, 18, .9)); border-right: 1px solid var(--g-line);
  }
  .rail::after { content: ''; position: absolute; top: 0; bottom: 0; right: -1px; width: 1px; background: linear-gradient(180deg, transparent, var(--g-teal) 18%, var(--g-magenta) 60%, transparent); opacity: .55; }
  .brand { display: flex; align-items: center; gap: 10px; padding: 2px 6px 4px; }
  .brand .display { letter-spacing: .2em; font-size: 15px; }
  nav { display: flex; flex-direction: column; gap: 2px; margin-top: 4px; }
  .item {
    position: relative; display: flex; align-items: center; gap: 12px; height: 42px; padding: 0 12px; border-radius: 8px; color: var(--g-muted);
    text-decoration: none; font-weight: 600; letter-spacing: .05em; font-size: 14px; transition: color .15s, background .15s;
  }
  .item:hover { color: var(--g-fg); background: rgba(200, 170, 110, .07); }
  .item.on { color: var(--g-fg); background: linear-gradient(90deg, rgba(45, 226, 196, .12), transparent); }
  .item.on::before { content: ''; position: absolute; left: -14px; top: 8px; bottom: 8px; width: 3px; border-radius: 0 3px 3px 0; background: var(--g-grad); }
  .item.on svg { color: var(--g-teal); }
  .status { margin-top: auto; display: flex; align-items: center; gap: 9px; padding: 10px 10px; border: 1px solid var(--g-line-soft); border-radius: 8px; font-size: 12.5px; color: var(--g-muted); }
  .dot { flex: none; width: 8px; height: 8px; border-radius: 50%; background: var(--g-teal); box-shadow: 0 0 8px var(--g-teal); }
  .status.warn .dot { background: var(--g-amber); box-shadow: 0 0 8px var(--g-amber); }
  main { position: relative; z-index: 1; overflow-y: auto; padding: 26px 34px 40px; }
  .page { max-width: 1020px; display: flex; flex-direction: column; gap: 18px; }
  .lost { background: #2a1d0c; border: 1px solid rgba(230, 184, 106, .5); border-radius: 10px; padding: 10px 14px; margin-bottom: 14px; font-size: 14px; color: var(--g-amber); }
</style>
