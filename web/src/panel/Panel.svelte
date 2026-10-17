<script lang="ts">
  // The side panel docked next to the League client (SPEC "PC app direction" 2), in the client's own look (lol.css).
  // A rail of section icons always sits against the client; opening a section slides its content out beside the rail. The tray app owns
  // the window: the page asks it to open or collapse ("open", "collapse", "toggle"), says how wide the open section wants to be ("width:N"
  // in css px at a 1280x720 client), and hears which side of the client it is on ("side:left|right"). Under 120 css px wide it is the rail only.
  import { onMount } from 'svelte'
  import { act, getJson } from '../lib/api'
  import { onClientMessage } from './clientClick.svelte'
  import { band, marks, onMarksMessage } from './marks.svelte'
  import MarksBand from './MarksBand.svelte'
  import '../shell/theme.css'
  import './lol.css'
  import { champions } from '../lib/champions.svelte'
  import { live } from '../lib/live.svelte'
  import { clientStatus } from '../lib/now'
  import { queueNames } from '../lib/queues.svelte'
  import { playable } from '../shell/playable.svelte'
  import { motion } from '../shell/motion.svelte'
  import Mark from '../shell/parts/Mark.svelte'
  import Accounts from '../lib/sections/Accounts.svelte'
  import Automation from '../lib/sections/Automation.svelte'
  import DeviceList from '../lib/sections/DeviceList.svelte'
  import Network from '../lib/sections/Network.svelte'
  import Pairing from '../lib/sections/Pairing.svelte'
  import Password from '../lib/sections/Password.svelte'
  import PhoneAlerts from '../lib/sections/PhoneAlerts.svelte'
  import ClientRepair from '../lib/sections/ClientRepair.svelte'
  import DesktopApp from '../lib/sections/DesktopApp.svelte'
  import SendLog from '../lib/sections/SendLog.svelte'
  import AboutApp from '../lib/sections/AboutApp.svelte'
  import Presence from '../lib/sections/Presence.svelte'
  import ScopeBar from '../lib/sections/ScopeBar.svelte'
  import Screens from '../lib/sections/Screens.svelte'
  import ProfileIcon from '../lib/sections/ProfileIcon.svelte'
  import Tokens from '../lib/sections/Tokens.svelte'
  import Card from '../lib/components/Card.svelte'
  import Toggle from '../lib/components/Toggle.svelte'
  import ChampPanel from './ChampPanel.svelte'
  import PanelSwitcher from './PanelSwitcher.svelte'
  import PanelNow from './PanelNow.svelte'
  import LaneTable from './LaneTable.svelte'
  import PanelHonor from './PanelHonor.svelte'
  import { label } from '../lib/profileLabel'

  type Tab = 'now' | 'automation' | 'phone' | 'profile' | 'settings'
  const ICON: Record<Exclude<Tab, 'settings'>, string> = {
    now: 'M12 4l2.4 5.6L20 12l-5.6 2.4L12 20l-2.4-5.6L4 12l5.6-2.4z',
    automation: 'M13 2L4 14h7l-1 8 9-12h-7z',
    phone: 'M8 2h8a2 2 0 012 2v16a2 2 0 01-2 2H8a2 2 0 01-2-2V4a2 2 0 012-2zm3 16h2',
    profile: 'M12 2l7 3v6c0 5-3 9-7 11-4-2-7-6-7-11V5z',
  }
  const COG = 'M19.14 12.94c.04-.3.06-.61.06-.94 0-.32-.02-.64-.07-.94l2.03-1.58c.18-.14.23-.41.12-.61l-1.92-3.32c-.12-.22-.37-.29-.59-.22l-2.39.96c-.5-.38-1.03-.7-1.62-.94l-.36-2.54c-.04-.24-.24-.41-.48-.41h-3.84c-.24 0-.43.17-.47.41l-.36 2.54c-.59.24-1.13.57-1.62.94l-2.39-.96c-.22-.08-.47 0-.59.22L2.74 8.87c-.12.21-.08.47.12.61l2.03 1.58c-.05.3-.09.63-.09.94s.02.64.07.94l-2.03 1.58c-.18.14-.23.41-.12.61l1.92 3.32c.12.22.37.29.59.22l2.39-.96c.5.38 1.03.7 1.62.94l.36 2.54c.05.24.24.41.48.41h3.84c.24 0 .44-.17.47-.41l.36-2.54c.59-.24 1.13-.56 1.62-.94l2.39.96c.22.08.47 0 .59-.22l1.92-3.32c.12-.22.07-.47-.12-.61l-2.01-1.58zM12 15.6c-1.98 0-3.6-1.62-3.6-3.6s1.62-3.6 3.6-3.6 3.6 1.62 3.6 3.6-1.62 3.6-3.6 3.6z'
  const LABEL: Record<Tab, string> = { now: 'Now', automation: 'Automation', phone: 'Phone', profile: 'Profile', settings: 'Settings' }
  const ACCOUNT = 'M12 12a4 4 0 100-8 4 4 0 000 8zm-8 9c0-4 4-6 8-6s8 2 8 6'
  const TOP = ['now', 'automation', 'profile', 'phone'] as const
  let tab = $state<Tab>('now')

  const now = $derived(live.now)
  const board = $derived(now.phase === 'ChampSelect' ? now.board : null)
  // Champ select brings Now to the front once when it starts; after that the tabs are the user's.
  let wasChamp = false
  $effect(() => {
    const champ = !!board
    if (champ && !wasChamp) tab = 'now'
    wasChamp = champ
  })

  // Width of the open panel, rail included, per section (css px at a 1280x720 client; the tray app scales it with the client).
  const RAIL = 40
  const widthOf = (t: Tab) => RAIL + (t === 'settings' ? 240 : 264)
  const width = $derived(widthOf(tab))

  type Host = { chrome?: { webview?: { postMessage(m: string): void; addEventListener(t: 'message', f: (e: { data: unknown }) => void): void } } }
  const webview = (window as Host).chrome?.webview
  const post = (m: string) => webview?.postMessage(m)
  $effect(() => post(`width:${width}`))

  let left = $state(new URLSearchParams(location.search).get('side') === 'left')
  webview?.addEventListener('message', (e) => {
    if (e.data === 'side:left' || e.data === 'side:right') left = e.data === 'side:left'
    else if (!onMarksMessage(e.data)) onClientMessage(e.data)
  })
  // Dev: `&marks=Y,STEP` with a `demo` shows the band as if the tray app marked the client's cards (no tray app in a browser).
  const query = new URLSearchParams(location.search)
  const demoMarks = query.has('demo') ? query.get('marks') : null
  if (demoMarks) onMarksMessage(`marks:${demoMarks}`)
  const wide = matchMedia('(min-width: 121px)')
  let wideNow = $state(wide.matches)
  // The account list over the collapsed rail: the window widens for it but the panel stays collapsed (the host cuts the window to the rail and
  // the list); it ends once the window is narrow again, or when the panel opens.
  let peek = $state(false)
  wide.addEventListener('change', () => { wideNow = wide.matches; if (!wide.matches) peek = false })
  const open = $derived(wideNow && !peek)

  // A rail icon opens its section; the open section's own icon collapses the panel again.
  function pick(t: Tab) {
    const wasOpen = open
    leavePeek()
    if (wasOpen && tab === t) { post('collapse'); return }
    tab = t
    post(`width:${widthOf(t)}`) // before "open", so the window opens at its final width
    if (!wasOpen) post('open')
  }

  const title = $derived(tab !== 'now' ? LABEL[tab] : board || now.phase === 'ChampSelect' ? 'Champ select' : now.phase === 'Matchmaking' ? 'In queue' : now.phase === 'Lobby' ? 'Lobby' : now.phase === 'ReadyCheck' ? 'Match found' : now.phase === 'Reconnect' ? 'Game running' : now.connected ? 'Home' : 'Waiting for League')
  const sub = $derived(tab !== 'now' ? '' : board ? (playable.byId(board.queueId)?.name ?? queueNames.label(board.queueId)) : now.connected ? '' : clientStatus(now).text)
  // Automation's light on the rail: on, paused for a game, or off.
  // Which account's settings Auto and Accounts edit (shared with the window's "Settings for"), shown in their bottom bar.
  const profiles = $derived(live.profiles?.profiles ?? [])
  // Accounts sit at the top of the rail, beside the client's own profile: the signed-in account's icon opens a small switcher over the panel.
  // From queue to the end of a game its rows are greyed (a switch is refused then anyway) and Settings leaves out the account list.
  const me = $derived(profiles.find((p) => p.id === live.profiles?.activeId) ?? null)
  const BUSY = ['Matchmaking', 'ReadyCheck', 'ChampSelect', 'GameStart', 'InProgress', 'Reconnect', 'WaitingForStats', 'PreEndOfGame']
  const switching = $derived(!BUSY.includes(now.phase))
  // The switcher drops over the open panel, or beside the collapsed rail without opening it (peek; the host sizes the window to the list).
  let menu = $state(false)
  function toggleMenu() {
    if (menu) { closeMenu(); return }
    menu = true
    peek = !open && !!webview
  }
  function closeMenu() {
    menu = false
    if (peek) post('menu:off')
  }
  function leavePeek() { if (peek) { peek = false; menu = false } }
  function manage() {
    const wasOpen = open
    leavePeek()
    menu = false
    tab = 'settings'
    post(`width:${widthOf('settings')}`)
    if (!wasOpen) post('open')
  }
  $effect(() => { if (!open && !peek) menu = false })
  // The list's place and size for the host, again whenever it changes size (the "closes the Riot Client" question).
  $effect(() => {
    if (!menu || !peek) return
    const el = document.querySelector<HTMLElement>('.pop')
    if (!el) return
    const ro = new ResizeObserver(() => post(`menu:${el.offsetTop},${el.offsetWidth},${el.offsetHeight}`))
    ro.observe(el)
    return () => ro.disconnect()
  })
  const paused = $derived(now.paused)
  const automationOn = $derived(live.settings?.automationEnabled ?? true)
  let pausing = $state(false)
  async function pause() {
    pausing = true
    await live.act('/automation/pause', 'PUT', { paused: !paused })
    pausing = false
  }
  // After a crash the client waits on Reconnect: the Now bar's one button then.
  let reconnecting = $state(false)
  async function reconnect() {
    reconnecting = true
    footErr = (await live.act('/game/reconnect')) ?? ''
    reconnecting = false
  }
  // Dodge: the first tap arms it for a few seconds, the second leaves. A new stage disarms it.
  let dodgeArmed = $state(false)
  let leaving = $state(false)
  let disarm: ReturnType<typeof setTimeout> | undefined
  // A primitive, so new board objects (any hover or spell change) do not disarm or clear anything; only a new stage does.
  const stage = $derived(board?.stage ?? '')
  $effect(() => { void stage; dodgeArmed = false })
  async function dodge() {
    clearTimeout(disarm)
    if (!dodgeArmed) { dodgeArmed = true; disarm = setTimeout(() => (dodgeArmed = false), 4000); return }
    dodgeArmed = false
    leaving = true
    footErr = (await live.act('/champselect/dodge')) ?? ''
    leaving = false
  }
  let footErr = $state('')
  $effect(() => { void tab; void stage; footErr = '' })
  const light = $derived(!(live.settings?.automationEnabled ?? true) ? 'off' : now.paused ? 'paused' : 'on')

  // The panel window never takes focus on a click (League keeps chat and hotkeys); a text field asks the tray app for the keyboard.
  // Only fields that take text: toggles are checkbox inputs, and asking for the keyboard on those would take chat away from League.
  const TEXT_INPUTS = ['text', 'search', 'number', 'password', 'email', 'url', 'tel']
  function onFocusIn(e: FocusEvent) {
    const t = e.target as HTMLElement | null
    if (!t) return
    if (t instanceof HTMLTextAreaElement || t.isContentEditable || (t instanceof HTMLInputElement && TEXT_INPUTS.includes(t.type))) post('keyboard')
  }
  // Whether the tray app opens the panel by itself in champ select; null outside the desktop app.
  let autoOpen = $state<boolean | null>(null)
  onMount(async () => {
    try { const d = await getJson<{ available: boolean; panelAutoOpen: boolean }>('/desktop'); autoOpen = d.available ? d.panelAutoOpen : null } catch { autoOpen = null }
  })
  async function setAutoOpen(on: boolean) {
    autoOpen = on
    footErr = (await act('/desktop/panel-auto-open', 'PUT', { enabled: on })) ?? ''
    if (footErr) autoOpen = !on
  }
  $effect(() => { playable.ensure(now.connected); queueNames.ensure() })
  $effect(() => { now.connected; champions.ensure() })
</script>

<svelte:window onfocusin={onFocusIn} onclick={(e) => { if (menu && !(e.target as Element).closest('.pop, .acct')) closeMenu() }} />

{#snippet icon(path: string)}
  <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d={path} /></svg>
{/snippet}

<div class="glam lol panel-app" class:left class:open class:calm={motion.reduced}>
  <nav class="rail" aria-label="Glamour">
    <button class="mark" onclick={() => { const wasOpen = open; leavePeek(); post(wasOpen ? 'collapse' : 'open') }} aria-label={open ? 'Collapse Glamour' : 'Open Glamour'} title="Glamour"><Mark size={20} /></button>
    <button class="ri acct" class:on={menu} aria-expanded={menu} aria-haspopup="menu" onclick={toggleMenu}
        title={me ? label(me) : 'Accounts'} aria-label="Accounts">
        {#if me?.hasIcon}<img src={`/api/profiles/${me.id}/icon?v=${me.card?.iconId ?? 0}`} alt="" width="26" height="26" />{:else}{@render icon(ACCOUNT)}{/if}
    </button>
    {#each TOP as t (t)}
      <button class="ri" class:on={open && tab === t} aria-current={open && tab === t ? 'page' : undefined} onclick={() => pick(t)} title={LABEL[t]} aria-label={LABEL[t]}>
        {@render icon(ICON[t])}
        {#if t === 'now' && board && !(open && tab === 'now')}<span class="dot" aria-hidden="true"></span>{/if}
      </button>
    {/each}
    <span class="grow"></span>
    <button class={`ri light is-${light}`} onclick={() => pick('now')} title={`Automation: ${light}`} aria-label={`Automation is ${light}`}><span class="lamp"></span></button>
    <div class="rail-foot">
      <button class="ri box bbtn" class:on={open && tab === 'settings'} onclick={() => pick('settings')} title="Settings" aria-label="Settings">
        <!-- Filled like the client's own bottom-bar icons, the hub cut out (Material Icons' cog, Copyright Google LLC, Apache License 2.0). -->
        <svg width="18" height="18" viewBox="0 0 24 24" fill="currentColor" fill-rule="evenodd" aria-hidden="true"><path d={COG} /></svg>
      </button>
    </div>
  </nav>

  {#if menu && (open || peek)}<PanelSwitcher busyPhase={!switching} {left} fit={open} onclose={closeMenu} onmanage={manage} />{/if}

  {#if open && !left && marks.on && band.slot >= 0 && band.target}<MarksBand target={band.target} />{/if}

  <section class="pane" aria-label={title}>
    <header class="top">
      <div class="head">
        <h1>{title}</h1>
        {#if sub}<span class="sub">{sub}</span>{/if}
      </div>
      <button class="close" onclick={() => post('collapse')} aria-label="Collapse" title="Collapse">
        <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" aria-hidden="true"><path d={left ? 'M9 6l6 6-6 6' : 'M15 6l-6 6 6 6'} /></svg>
      </button>
    </header>
    {#if live.linkLost}<p class="lost" role="alert">Lost contact. If this stays, exit Glamour from the tray and open it again.</p>{/if}
    <main>
      {#if tab === 'now'}
        {#if board}<ChampPanel {board} {post} {left} />{:else}<PanelNow />{/if}
      {:else if tab === 'automation'}
        <!-- The switches and this game's champions are on Now; here the rules behind them. -->
        <ScopeBar />
        <LaneTable />
        <Automation part="loadout" />
        <Automation part="timing" />
        <PanelHonor />
        <Screens />
        <Automation part="testing" />
      {:else if tab === 'phone'}
        <!-- Same parts as the window's Phone page, pairing first: it is what the panel is opened for. -->
        <Pairing />
        <DeviceList />
        <Network />
        <Password />
        <PhoneAlerts />
      {:else if tab === 'profile'}
        <!-- What friends see: rank and status message, the profile icon and the challenge tokens (per account, "For" in the bottom bar). -->
        <Presence />
        <ProfileIcon />
        <Tokens />
      {:else}
        {#if switching}<Accounts />{/if}
        <ClientRepair />
        <DesktopApp />
        <SendLog />
        <AboutApp />
        <Card title="Panel">
          {#if autoOpen !== null}<Toggle label="Open in champ select" checked={autoOpen} onchange={setAutoOpen} />{/if}
          <Toggle label="Reduce motion" checked={motion.reduced} onchange={(v) => motion.set(v)} />
        </Card>
      {/if}
    </main>
    {#if footErr}<p class="ferr" role="alert">{footErr}</p>{/if}
    <footer class="foot">
      {#if tab === 'settings'}
        <button onclick={() => post('open-window')}>Open the Glamour window</button>
      {:else if tab === 'profile' && profiles.length > 0}
        <label class="scope">
          <span>For</span>
          <select value={live.scopeProfile?.id ?? 'all'} onchange={(e) => (live.scope = e.currentTarget.value)}>
            <option value="all">All accounts</option>
            {#each profiles as p (p.id)}<option value={p.id}>{label(p)} only</option>{/each}
          </select>
        </label>
      {/if}
      {#if tab === 'now' && now.phase === 'Reconnect'}
        <button disabled={reconnecting} onclick={reconnect}>Reconnect</button>
      {:else if tab === 'now' && !board}
        <button disabled={pausing || !automationOn} onclick={pause}>{paused ? 'Resume' : 'Pause next game'}</button>
      {/if}
      {#if tab === 'now' && board}
        <!-- Alone in the bar, and only on a second tap; automation's Stop sits in its own section. -->
        <button class="dodge" class:armed={dodgeArmed} disabled={leaving || board.stage === 'GAME_STARTING'} onclick={dodge}>{dodgeArmed ? 'Dodge now?' : 'Dodge'}</button>
      {/if}
    </footer>
  </section>
</div>

<style>
  .panel-app { position: fixed; inset: 0; display: flex; overflow: hidden; min-height: 0; }
  .panel-app.left { flex-direction: row-reverse; }

  /* The rail: against the client, its bands level with the client's top bar (80) and bottom bar (36). */
  .rail { flex: none; width: 40px; display: flex; flex-direction: column; align-items: stretch; background: var(--l-black); border-left: 1px solid var(--l-frame); }
  .left .rail { border-left: 0; border-right: 1px solid var(--l-frame); }
  .rail button:not(.bbtn) { min-height: 0; padding: 0; border: 0; border-radius: 0; background: none; color: var(--l-grey1); cursor: pointer; display: grid; place-items: center; position: relative; }
  .rail button:not(.bbtn):hover { filter: none; color: var(--l-gold1); }
  .mark { height: 80px; border-bottom: 1px solid var(--l-frame) !important; }
  .ri { height: 44px; }
  .acct { border-bottom: 1px solid var(--l-frame) !important; }
  .acct img { width: 26px; height: 26px; border-radius: 50%; object-fit: cover; box-sizing: border-box; border: 1px solid var(--l-gold4); }
  .acct.on img, .acct:hover img { border-color: var(--l-gold2); }
  .ri.on { color: var(--l-gold2); background: linear-gradient(90deg, rgba(200, 170, 110, .12), transparent); }
  .ri.on::before { content: ''; position: absolute; left: 0; top: 8px; bottom: 8px; width: 2px; background: var(--l-gold2); }
  .left .ri.on { background: linear-gradient(270deg, rgba(200, 170, 110, .12), transparent); }
  .left .ri.on::before { left: auto; right: 0; }
  .dot { position: absolute; top: 10px; right: 9px; width: 6px; height: 6px; border-radius: 50%; background: var(--l-blue); box-shadow: 0 0 6px var(--l-blue); }
  .grow { flex: 1; }
  .lamp { width: 8px; height: 8px; border-radius: 50%; background: var(--l-grey2); }
  .light.is-on .lamp { background: var(--l-blue); box-shadow: 0 0 6px rgba(10, 200, 185, .8); }
  .light.is-paused .lamp { background: #C89B3C; }
  /* Level with the client's bottom bar: its 1 px line 34 px above the bottom, the 32 px square button (lol.css .bbtn) right under it. */
  .rail-foot { height: 34px; border-top: 1px solid var(--l-frame); display: flex; justify-content: center; align-items: flex-start; }
  .rail .box { width: 32px; height: 32px; padding: 0; border-radius: 0; display: grid; place-items: center; cursor: pointer; }
  .rail .box.on { border-image: linear-gradient(180deg, #F0E6D2, #C8AA6E 40%, #785A28) 1; color: var(--l-gold1); }
  .rail .box.on::before { display: none; }

  /* The open section. */
  .pane { flex: 1; min-width: 0; display: none; flex-direction: column; background: linear-gradient(180deg, #030E17, var(--l-black) 40%); }
  .open .pane { display: flex; }
  .top { flex: none; height: 80px; display: flex; align-items: center; gap: 8px; padding: 0 8px 0 14px; border-bottom: 1px solid var(--l-frame); }
  .left .top { padding: 0 14px 0 8px; flex-direction: row-reverse; }
  .head { flex: 1; min-width: 0; display: flex; flex-direction: column; gap: 2px; }
  h1 { margin: 0; font: 700 15px var(--g-display); letter-spacing: .08em; text-transform: uppercase; color: var(--l-gold1); white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
  .sub { color: var(--l-grey1); font-size: 12px; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
  .close { flex: none; width: 26px; height: 26px; min-height: 0; padding: 0; display: grid; place-items: center; border-radius: 0; border: 1px solid var(--l-grey3); background: none; color: var(--l-grey1); cursor: pointer; }
  .close:hover { filter: none; color: var(--l-gold1); border-color: var(--l-gold4); }
  main { flex: 1; min-height: 0; overflow-y: auto; display: flex; flex-direction: column; scrollbar-width: thin; scrollbar-color: var(--l-gold4) transparent; }
  .foot { flex: none; height: 34px; border-top: 1px solid var(--l-frame); display: flex; align-items: flex-start; justify-content: flex-end; padding: 0 8px; background: var(--l-black); }
  .foot { gap: 8px; }
  .foot button { flex: none; }
  .foot .dodge { border-color: #5A2A2A !important; color: #F0B4B4 !important; }
  .foot .dodge.armed { background: #6B2A2C !important; color: #FFE8E8 !important; }
  .ferr { flex: none; margin: 0; padding: 6px 12px; font-size: 12px; color: #E86A6C; border-top: 1px solid var(--l-frame); }
  .scope { flex: 1; min-width: 0; display: flex; align-items: center; gap: 6px; font-size: 11px; color: var(--l-grey1); }
  .scope select { flex: 1; min-width: 0; padding: 2px 4px; font-size: 11px; }
  /* No room anywhere: the rail is a short tab over the client's edge; only the mark fits. */
  @media (max-height: 240px) { .rail > :not(.mark) { display: none; } .mark { height: 100%; border-bottom: 0 !important; } }
  .lost { margin: 8px 12px 0; border: 1px solid #785A28; padding: 8px 10px; font-size: 12px; color: #C89B3C; }
</style>
