<script lang="ts">
  // The phone UI (SPEC "UI (decided)"): Play follows the client; match found, champ select and honor take over from any tab and close
  // by themselves when resolved, whoever resolved them. Tabs live in the URL hash (#/automation), so Back works.
  import './theme.css'
  import { live } from '../lib/live.svelte'
  import { clientStatus } from '../lib/now'
  import Mark from './parts/Mark.svelte'
  import { champions } from '../lib/champions.svelte'
  import { backdrop } from './backdrop.svelte'
  import { motion } from './motion.svelte'
  import { art } from './util'
  import Play from './play/Play.svelte'
  import AccountsTab from './tabs/AccountsTab.svelte'
  import AutomationTab from './tabs/AutomationTab.svelte'
  import MoreTab from './tabs/MoreTab.svelte'
  import ChampSelect from './takeover/ChampSelect.svelte'
  import Honor from './takeover/Honor.svelte'
  import MatchFound from './takeover/MatchFound.svelte'

  type Tab = 'play' | 'automation' | 'accounts' | 'more'
  const TABS: { id: Tab; label: string }[] = [
    { id: 'play', label: 'Play' }, { id: 'automation', label: 'Automation' }, { id: 'accounts', label: 'Accounts' }, { id: 'more', label: 'More' },
  ]
  let hash = $state(location.hash)
  const tab = $derived<Tab>(TABS.find((t) => `#/${t.id}` === hash)?.id ?? 'play')

  const now = $derived(live.now)
  // Honor closes here for one ballot (Skip or a sent Submit), even if the client keeps its ballot a while longer.
  let skippedGame = $state(0)
  const honor = $derived(now.honor && now.honor.gameId !== skippedGame ? now.honor : null)
  const board = $derived(now.phase === 'ChampSelect' ? now.board : null)
  const matchFound = $derived(now.phase === 'ReadyCheck' && now.ready === 'InProgress')
  const pill = $derived(clientStatus(now))
  const inGame = $derived(['GameStart', 'InProgress', 'Reconnect'].includes(now.phase))

  // The backdrop is the skin chosen as the profile background in the client; before that is known, the champion automation would play.
  const s = $derived(live.settings)
  const fallbackId = $derived((s?.autoLockChampionId ?? 0) > 0 ? s!.autoLockChampionId : s?.preHoverChampionId ?? null) // not "Best counter"
  const back = $derived(backdrop.championId > 0 ? art(champions.byId(backdrop.championId), backdrop.skinNum, 'splash') : art(champions.byId(fallbackId), 0, 'splash'))
  $effect(() => { backdrop.ensure(now.connected) })
  $effect(() => { live.now.connected; champions.ensure() })
  // Bright on a quiet Play screen, dimmer behind content-heavy screens so text stays readable.
  const bright = $derived(tab === 'play' && !now.lobby.members && !['Matchmaking', 'ReadyCheck'].includes(now.phase) && !inGame)
</script>

<svelte:window onhashchange={() => (hash = location.hash)} />

<div class="glam shell" class:calm={motion.reduced}>
  {#if back}<div class="backdrop" class:bright aria-hidden="true"><img src={back} alt="" /></div>{/if}
  <header class="top">
    <Mark size={24} />
    <span class="brand display">GLAMOUR</span>
    <span class="pill" class:warn={inGame || !now.connected}>
      <span class="dot"></span>{inGame && !now.connected ? 'Client paused for the game' : pill.text}
    </span>
  </header>

  {#if live.linkLost}
    <p class="lost" role="alert">Lost contact with the PC. Check Glamour is running. If it stays, scan a new QR from the PC.</p>
  {/if}

  <main>
    {#if tab === 'play'}<Play />{:else if tab === 'automation'}<AutomationTab />{:else if tab === 'accounts'}<AccountsTab />{:else}<MoreTab />{/if}
  </main>

  <nav class="tabs" aria-label="Main">
    {#each TABS as t (t.id)}
      <a href={`#/${t.id}`} class="tab" class:on={tab === t.id} aria-current={tab === t.id ? 'page' : undefined}>
        {#if t.id === 'play'}
          <svg width="22" height="22" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linejoin="round" aria-hidden="true"><path d="M12 4l2.4 5.6L20 12l-5.6 2.4L12 20l-2.4-5.6L4 12l5.6-2.4z" /></svg>
        {:else if t.id === 'automation'}
          <svg width="22" height="22" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linejoin="round" aria-hidden="true"><path d="M13 2L4 14h7l-1 8 9-12h-7z" /></svg>
        {:else if t.id === 'accounts'}
          <svg width="22" height="22" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" aria-hidden="true"><circle cx="12" cy="8" r="4" /><path d="M4 21c0-4 4-6 8-6s8 2 8 6" /></svg>
        {:else}
          <svg width="22" height="22" viewBox="0 0 24 24" fill="currentColor" aria-hidden="true"><circle cx="5" cy="12" r="1.8" /><circle cx="12" cy="12" r="1.8" /><circle cx="19" cy="12" r="1.8" /></svg>
        {/if}
        {t.label}
      </a>
    {/each}
  </nav>

  {#if honor}
    <Honor ballot={honor} onclose={() => (skippedGame = honor.gameId)} />
  {:else if board}
    <ChampSelect {board} />
  {:else if matchFound}
    <MatchFound />
  {/if}
</div>

<style>
  .shell { position: relative; max-width: 34rem; margin-inline: auto; padding: 0 16px calc(var(--g-tabs) + 24px + env(safe-area-inset-bottom)); overflow-x: clip; }
  .top { position: relative; display: flex; align-items: center; gap: 10px; padding: calc(14px + env(safe-area-inset-top)) 0 14px; }
  .brand { letter-spacing: .12em; font-size: 15px; }
  .pill { margin-left: auto; display: flex; align-items: center; gap: 7px; padding: 7px 11px; border: 1px solid rgba(200,170,110,.4); border-radius: 999px; font-size: 12px; font-weight: 600; background: rgba(6,10,18,.82); white-space: nowrap; min-width: 0; }
  .dot { width: 8px; height: 8px; border-radius: 50%; background: var(--g-teal); box-shadow: 0 0 8px var(--g-teal); }
  .pill.warn .dot { background: var(--g-amber); box-shadow: 0 0 8px var(--g-amber); }
  .lost { position: relative; background: #2a1d0c; border: 1px solid rgba(230,184,106,.5); border-radius: 10px; padding: 10px 12px; margin-bottom: 12px; font-size: 14px; color: var(--g-amber); }
  main { position: relative; }
  .tabs {
    position: fixed; left: 0; right: 0; bottom: 0; z-index: var(--z-tabs); height: calc(var(--g-tabs) + env(safe-area-inset-bottom)); padding-bottom: env(safe-area-inset-bottom);
    display: grid; grid-template-columns: repeat(4, minmax(0, 1fr)); background: rgba(6,10,18,.97); border-top: 1px solid rgba(200,170,110,.28);
  }
  .tab { display: flex; flex-direction: column; align-items: center; justify-content: center; gap: 5px; font-size: 11px; color: var(--g-muted); text-decoration: none; font-weight: 700; letter-spacing: .06em; text-transform: uppercase; }
  .tab:hover { color: var(--g-fg); }
  .tab.on, .tab.on:hover { color: var(--g-teal); }
</style>
