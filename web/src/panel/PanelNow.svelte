<script lang="ts">
  // The panel's Now outside champ select: the automation switch, then what Glamour will do in the next game, in the order it happens
  // (accept, ban, pick, honor), each editable in place. It shows the settings in effect for the signed-in account and edits those
  // (live.patchInEffect), whatever the Auto tab's "For" is set to. The honor list is edited here too; lane setups and delays stay in Auto.
  import { champions } from '../lib/champions.svelte'
  import { live } from '../lib/live.svelte'
  import { playable } from '../shell/playable.svelte'
  import { summonerNames } from '../lib/summonerNames.svelte'
  import ChampionPicker from '../lib/components/ChampionPicker.svelte'
  import { asset } from '../lib/assets'
  import type { LaneSetup, LobbyMember, Settings, Timing } from '../lib/types'
  import { champsRunIn, honorInParty, laneKeyOf, planOf, setupChange } from './plan'
  import HonorList from './HonorList.svelte'
  import { friends } from './friends.svelte'

  const s = $derived(live.settings)
  const now = $derived(live.now)
  const on = $derived(s?.automationEnabled ?? true)
  const paused = $derived(now.paused)
  const active = $derived(on && !paused)

  const inParty = $derived(now.phase === 'Lobby' || now.phase === 'Matchmaking' || now.phase === 'ReadyCheck')
  const roster = $derived(inParty ? (now.lobby.roster ?? []) : [])
  const lane = $derived(inParty && now.lobby.showPositions ? laneKeyOf(now.lobby.first) : null)
  const plan = $derived(s ? planOf(s, lane) : null)
  const LANE_NAME = { TOP: 'Top', JUNGLE: 'Jungle', MIDDLE: 'Mid', BOTTOM: 'Bot', SUPPORT: 'Support' } as const
  // Ban, lock and pre-select do nothing in bench queues (ARAM, Mayhem).
  const mode = $derived(inParty ? playable.byId(now.lobby.queueId)?.gameMode : undefined)
  const champsRun = $derived(champsRunIn(mode))

  const others = $derived(roster.filter((m) => !m.isMe && !m.isBot))
  $effect(() => summonerNames.ensure(roster.filter((m) => !m.isBot).map((m) => m.summonerId)))
  const nameOf = (m: LobbyMember) => (m.isBot ? 'Bot' : m.name || summonerNames.byId[m.summonerId] || (m.isMe ? 'You' : '…'))
  const honored = $derived(s ? honorInParty(s.autoHonorFriends, others.map(nameOf)) : [])
  const honors = (m: LobbyMember) => !!s?.autoHonorEnabled && !m.isMe && honored.some((h) => h.toLowerCase() === nameOf(m).toLowerCase())
  const listed = $derived(s?.autoHonorFriends.filter((f) => f.trim()).length ?? 0)
  // A party member who is a League friend but not on the list gets a one-tap add (auto-honor only reaches friends).
  $effect(() => { if (others.length) friends.load() })
  const canList = (m: LobbyMember) => !m.isMe && !m.isBot && !!s && nameOf(m).includes('#') && friends.has(nameOf(m))
    && !s.autoHonorFriends.some((f) => f.trim().toLowerCase() === nameOf(m).toLowerCase())
  const short = (riotId: string) => riotId.split('#')[0]
  let showFriends = $state(false)
  const honorText = $derived(!listed ? 'No friends listed' : others.length ? (honored.length ? honored.map(short).join(', ') : 'No listed friend here')
    : `${listed} friend${listed === 1 ? '' : 's'} listed`)
  const POS: Record<string, string> = { TOP: 'Top', JUNGLE: 'Jungle', MIDDLE: 'Mid', BOTTOM: 'Bot', UTILITY: 'Support', FILL: 'Fill' }
  const lanesOf = (m: LobbyMember) => [m.first, m.second].filter((x) => x && x !== 'UNSELECTED').map((x) => POS[x] ?? x).join(' / ')
  const skips = $derived(s ? s.skipScreens.length + (s.skipHonorScreen ? 1 : 0) : 0)

  let err = $state('')
  let busy = $state(false)
  async function save(change: Partial<Settings>) {
    busy = true
    err = (await live.patchInEffect(change)) ?? ''
    busy = false
  }
  const edit = (c: Partial<LaneSetup>) => s && plan && save(setupChange(s, plan, c))
  const flip = (t: Timing): Timing => (t === 'Instant' ? 'LastSecond' : 'Instant')
  const TIMING: Record<Timing, string> = { Instant: 'On my turn', LastSecond: 'Last second' }
  const stateText = $derived(!on ? 'Off' : paused ? 'Paused for the next game' : s?.dryRun ? 'On · dry run, nothing is sent' : 'On')
  const champName = (id: number | null) => (id == null ? 'Not set' : champions.byId(id)?.name ?? (champions.list.length ? `#${id}` : '…'))
  $effect(() => { if (plan) champions.ensure() })

  const GLYPH = {
    accept: 'M5 12.5l4.5 4.5L19 7.5',
    honor: 'M12 20s-7-4.4-7-10a4 4 0 017-2.6A4 4 0 0119 10c0 5.6-7 10-7 10z',
    skip: 'M5 6l7 6-7 6zM13 6l7 6-7 6z',
  }
</script>

{#snippet glyph(path: string)}
  <span class="ico" aria-hidden="true"><svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><path d={path} /></svg></span>
{/snippet}

<label class="master" class:on={active} class:paused={on && paused}>
  <span class="txt-col"><span class="name">Automation</span><span class="state">{stateText}</span></span>
  <input class="switch" type="checkbox" role="switch" checked={on} disabled={busy} onchange={() => save({ automationEnabled: !on })} />
</label>

{#if s && plan}
  <div class="plan" class:off={!active}>
    <h2 class="band">This game<span class="aside">{plan.lane ? `${LANE_NAME[plan.lane]} setup` : 'All lanes'}</span></h2>
    <ul class="rows">
      <li class="row">
        {@render glyph(GLYPH.accept)}
        <span class="txt-col"><span class="kind">Auto-accept</span><span class="val" class:unset={s.autoAccept !== 'Always'}>{s.autoAccept !== 'Always' ? 'Off' : 'Match found'}</span></span>
        {#if s.autoAccept === 'Always'}
          <button type="button" class="timing accept" disabled={busy} onclick={() => save({ acceptTiming: flip(s.acceptTiming) })}
            title={`Switch to ${s.acceptTiming === 'LastSecond' ? 'at once' : 'last second'}`}>{s.acceptTiming === 'LastSecond' ? 'Last second' : s.autoAcceptDelaySeconds > 0 ? `After ${s.autoAcceptDelaySeconds} s` : 'At once'}</button>
        {/if}
        <input class="switch" type="checkbox" role="switch" aria-label="Auto-accept" checked={s.autoAccept === 'Always'} disabled={busy} onchange={(e) => save({ autoAccept: e.currentTarget.checked ? 'Always' : 'Off' })} />
      </li>
      {@render champRow('ban', 'Auto-ban', plan.setup.banChampionId, plan.setup.banTiming, (id) => edit({ banChampionId: id }), (t) => edit({ banTiming: t }))}
      {#if plan.setup.lockChampionId == null && plan.setup.hoverChampionId != null}
        {@render champRow('hover', 'Pre-select', plan.setup.hoverChampionId, null, (id) => edit({ hoverChampionId: id }), null)}
      {:else}
        {@render champRow('lock', 'Auto-lock', plan.setup.lockChampionId, plan.setup.lockTiming, (id) => edit({ lockChampionId: id }), (t) => edit({ lockTiming: t }))}
      {/if}
    </ul>
    <h2 class="band">After the game</h2>
    <ul class="rows">
      <li class="row">
        <button type="button" class="open" aria-expanded={showFriends} onclick={() => (showFriends = !showFriends)} title={showFriends ? 'Hide the list' : 'Show the list'}>
          {@render glyph(GLYPH.honor)}
          <span class="txt-col"><span class="kind">Auto-honor</span><span class="val" class:unset={!s.autoHonorEnabled || !honored.length}>{s.autoHonorEnabled ? honorText : 'Off'}</span></span>
          <svg class="chev" class:up={showFriends} width="12" height="12" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.4" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M6 9l6 6 6-6" /></svg>
        </button>
        <input class="switch" type="checkbox" role="switch" aria-label="Auto-honor" checked={s.autoHonorEnabled} disabled={busy} onchange={(e) => save({ autoHonorEnabled: e.currentTarget.checked })} />
      </li>
      {#if showFriends}
        <li class="friends">
          <HonorList list={s.autoHonorFriends} here={honored} {busy} onsave={(next) => save({ autoHonorFriends: next })} />
        </li>
      {/if}
      {#if skips}
        <li class="row">
          {@render glyph(GLYPH.skip)}
          <span class="txt-col"><span class="kind">Skips</span><span class="val">{skips} screen{skips === 1 ? '' : 's'}</span></span>
        </li>
      {/if}
    </ul>
    {#if err}<p class="err" role="alert">{err}</p>{/if}
  </div>
{/if}

{#if roster.length > 1}
  <h2 class="band">Party</h2>
  <ul class="rows party">
    {#each roster as m, n (m.puuid || n)}
      <li class="row member" class:me={m.isMe}>
        {#if m.iconId}<img src={asset(`/lol-game-data/assets/v1/profile-icons/${m.iconId}.jpg`)} alt="" width="32" height="32" loading="lazy" />{:else}<span class="slot" aria-hidden="true"></span>{/if}
        <span class="txt-col">
          <span class="val">{short(nameOf(m))}{#if m.isLeader}<small>leader</small>{/if}</span>
          <span class="kind">{lanesOf(m) || (m.level ? `Level ${m.level}` : '')}</span>
        </span>
        {#if honors(m)}<span class="heart" title="Auto-honor after the game" aria-label="Honored after the game">{@render glyph(GLYPH.honor)}</span>
        {:else if canList(m)}
          <button type="button" class="list-add" disabled={busy} onclick={() => s && save({ autoHonorFriends: [...s.autoHonorFriends.filter((f) => f.trim()), nameOf(m)] })}
            title="Add to the auto-honor list" aria-label={`Add ${short(nameOf(m))} to the auto-honor list`}>+{@render glyph(GLYPH.honor)}</button>
        {/if}
      </li>
    {/each}
  </ul>
{/if}

{#snippet champRow(kind: 'ban' | 'lock' | 'hover', title: string, id: number | null, timing: Timing | null, pick: (id: number | null) => void, setTiming: ((t: Timing) => void) | null)}
  {@const c = champions.byId(id)}
  <li class={`row champ ${kind}`} class:idle={!champsRun}>
    <ChampionPicker label={title} bestCounter={kind === 'lock'} value={id} onpick={pick}>
      {#snippet trigger(show)}
        <button type="button" class="open" onclick={show} disabled={busy} aria-haspopup="dialog" title={`Choose the champion to ${kind === 'ban' ? 'ban' : kind === 'lock' ? 'lock' : 'pre-select'}`}>
          {#if c}<img src={c.icon} alt="" width="32" height="32" />{:else}<span class="slot" aria-hidden="true">+</span>{/if}
          <span class="txt-col"><span class="kind">{title}</span><span class="val" class:unset={id == null}>{champName(id)}</span></span>
        </button>
      {/snippet}
    </ChampionPicker>
    {#if !champsRun}
      <span class="note">Not in this mode</span>
    {:else if id != null && timing && setTiming}
      <button type="button" class="timing" disabled={busy} onclick={() => setTiming(flip(timing))} title={`Switch to ${TIMING[flip(timing)].toLowerCase()}`}>{TIMING[timing]}</button>
    {/if}
  </li>
{/snippet}

<style>
  .master { display: flex; align-items: center; gap: 10px; min-height: 52px; padding: 0 12px; cursor: pointer; border-bottom: 1px solid var(--l-frame);
    background: linear-gradient(90deg, rgba(91, 90, 86, .12), transparent); }
  .master.on { background: linear-gradient(90deg, rgba(10, 200, 185, .12), transparent 80%); }
  .master.paused { background: linear-gradient(90deg, rgba(200, 155, 60, .12), transparent 80%); }
  .master .txt-col { flex: 1; }
  .master .name { font: 700 13px var(--g-display); letter-spacing: .08em; text-transform: uppercase; color: var(--l-gold1); }
  .master .state { font-size: 11px; color: var(--l-grey1); }
  .master.on .state { color: var(--l-blue); }
  .master.paused .state { color: #C89B3C; }

  .txt-col { display: flex; flex-direction: column; min-width: 0; line-height: 1.25; }
  .band { margin: 0; display: flex; align-items: center; gap: 8px; min-height: 30px; padding: 0 12px; background: var(--l-band); border-bottom: 1px solid var(--l-frame);
    font: 700 11px var(--g-body); letter-spacing: .1em; text-transform: uppercase; color: var(--l-grey1); }
  .rows + .band { border-top: 1px solid var(--l-frame); }
  .band .aside { margin-left: auto; color: var(--l-grey2); letter-spacing: .04em; text-transform: none; font-weight: 400; }
  .rows { list-style: none; margin: 0; padding: 4px 0; }
  .row { display: flex; align-items: center; gap: 10px; min-height: 48px; padding: 0 12px; }
  .kind { font-size: 11px; color: var(--l-grey1); }
  .val { font-size: 13px; color: var(--l-gold1); white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
  .val.unset { color: var(--l-grey2); }
  .row .txt-col { flex: 1; }
  .ico { width: 32px; height: 32px; flex: none; display: grid; place-items: center; border: 1px solid var(--l-grey3); color: var(--l-gold3); box-sizing: border-box; }

  /* A champion row: the picture and name open the picker, the timing on the right flips between the two. */
  .row .open { flex: 1; min-width: 0; display: flex !important; align-items: center; gap: 10px; text-align: left; min-height: 44px !important; padding: 0 !important;
    border: 0 !important; background: none !important; text-transform: none !important; letter-spacing: 0 !important; font: inherit !important; cursor: pointer; }
  .row .open:hover:not(:disabled) .val { color: var(--l-gold2); }
  .champ img, .slot { width: 32px; height: 32px; flex: none; box-sizing: border-box; border: 1px solid var(--l-gold4); display: block; }
  .slot { display: grid; place-items: center; border-style: dashed; border-color: var(--l-grey3); color: var(--l-grey2); font-size: 16px; }
  .champ .open:hover .slot { border-color: var(--l-gold4); color: var(--l-gold2); }
  .ban img { border-color: #6B2A2C; }
  .hover img { border-style: dashed; }
  .timing { flex: none; min-height: 24px !important; padding: 0 8px !important; font-size: 10px !important; }
  .timing.accept { border-color: #0A5A60 !important; color: var(--l-blue-ink) !important; }
  .lock .timing { border-color: #0A5A60 !important; color: var(--l-blue-ink) !important; }
  .ban .timing { border-color: #5A2A2A !important; color: #F0B4B4 !important; }
  .note { font-size: 11px; color: var(--l-grey2); flex: none; }
  .idle img { filter: grayscale(1) brightness(.6); }
  .idle .val { color: var(--l-grey2); }

  .off .rows { opacity: .5; }
  .plan + .band, .master + .band { border-top: 1px solid var(--l-frame); }
  .member { min-height: 44px; }
  .member img, .member .slot { width: 32px; height: 32px; flex: none; box-sizing: border-box; border: 1px solid var(--l-gold4); border-radius: 50%; display: block; }
  .member .slot { background: var(--l-btn); border-style: solid; }
  .member.me .val { color: var(--l-gold2); }
  .val small { margin-left: 6px; color: var(--l-grey2); font-size: 9px; font-weight: 700; letter-spacing: .1em; text-transform: uppercase; }
  .row .open:disabled { cursor: default; }
  .chev { flex: none; color: var(--l-grey1); transition: transform .15s; }
  .chev.up { transform: rotate(180deg); }
  .open:hover:not(:disabled) .chev { color: var(--l-gold2); }
  .friends { padding: 0 12px 6px 46px; }
  .heart { flex: none; color: var(--l-blue); }
  .heart .ico, .list-add .ico { border: 0; width: 20px; height: 20px; color: inherit; }
  .list-add { flex: none; display: flex !important; align-items: center; gap: 0; min-height: 26px !important; padding: 0 4px !important; border: 1px dashed var(--l-grey3) !important;
    background: none !important; color: var(--l-grey1) !important; font: 700 12px var(--g-body) !important; cursor: pointer; }
  .list-add:hover:not(:disabled) { color: var(--l-blue) !important; border-color: var(--l-blue) !important; }
  .err { margin: 6px 12px; font-size: 12px; color: #E86A6C; }
</style>
