<script lang="ts">
  // Champ select (SPEC "Champ select layout"): my team stacked in the top left, the enemy team in the top right, my skin preview in the
  // middle with arrows for skins and dots for chromas. Spells change inline, runes open a sheet. Bans sit as a badge on the player who banned.
  import { untrack } from 'svelte'
  import { champions } from '../../lib/champions.svelte'
  import ChampionPicker from '../../lib/components/ChampionPicker.svelte'
  import { asset } from '../../lib/assets'
  import { getJson } from '../../lib/api'
  import { live } from '../../lib/live.svelte'
  import { queueNames } from '../../lib/queues.svelte'
  import BenchCard from '../../lib/sections/BenchCard.svelte'
  import { spells, spellsForMode } from '../../lib/spells.svelte'
  import type { Board, BoardPlayer, Skin, Swap } from '../../lib/types'
  import { demoFromUrl } from '../demo'
  import Chips from '../parts/Chips.svelte'
  import Lane from '../parts/Lane.svelte'
  import Ring from '../parts/Ring.svelte'
  import { tick } from '../tick.svelte'
  import { art, headOf, laneShort, ordinal, partySummary, statusOf } from '../util'
  import Sheet from '../parts/Sheet.svelte'
  import RunePages from './RunePages.svelte'
  import SwapSheet from './SwapSheet.svelte'
  import CounterSheet from './CounterSheet.svelte'
  import { counterFeed } from '../../lib/counterFeed.svelte'
  import { maybeOpponents, percent } from '../../lib/counterPick'
  import { pickSlot, takenKey, takenMap } from '../../lib/pickActions'

  let { board }: { board: Board } = $props()
  // The demo has no real champ select behind it: skins are made up and skin or spell taps only change this screen.
  const demo = demoFromUrl(location.search) !== null

  const realMe = $derived(board.myTeam.find((p) => p.isMe))
  let demoSkin = $state<number | null>(null)
  let demoSpells = $state<[number, number] | null>(null)
  const me = $derived(realMe && demo ? { ...realMe, skinId: demoSkin ?? realMe.skinId, spell1Id: demoSpells?.[0] ?? realMe.spell1Id, spell2Id: demoSpells?.[1] ?? realMe.spell2Id } : realMe)
  const turn = $derived(live.now.turn)
  const myChamp = $derived(champions.byId(me?.championId ?? null))
  const dealt = $derived(board.mode === 'aram' || board.mode === 'mayhem')
  const nameOf = (p: BoardPlayer) => p.name?.split('#')[0] || champions.byId(p.championId)?.name || `Ally ${p.cellId + 1}`
  const head = $derived(headOf(board, turn?.type ?? null, nameOf))

  let boardAt = $state(Date.now())
  $effect(() => { void board; boardAt = Date.now() })
  $effect(() => tick.use())
  const left = $derived(Math.max(0, board.timeLeftMs - (tick.now - boardAt)))
  let phaseMs = $state(30000)
  const turnKey = $derived(`${board.stage}:${board.acting.map((t) => t.cellId).join(',')}`)
  $effect(() => { void turnKey; phaseMs = untrack(() => Math.max(board.timeLeftMs, 1000)) })
  $effect(() => { champions.ensure(); spells.ensure(); queueNames.ensure() })

  // Bans: who banned what. banList carries the banning cell; without it only the two team lists are known.
  const bansBy = $derived.by(() => {
    const m = new Map<number, number>()
    for (const b of board.banList ?? []) if (b.championId > 0) m.set(b.cellId, b.championId)
    return m
  })
  const myBans = $derived(board.banList ? board.banList.filter((b) => b.mine).map((b) => b.championId) : board.myBans)
  const theirBans = $derived(board.banList ? board.banList.filter((b) => !b.mine).map((b) => b.championId) : board.theirBans)
  // Without banList (no actor known) the bans fall back to a strip under each team.
  const showBadges = $derived(!!board.banList)

  // Skins: arrows step through the skins I own, dots pick a chroma of the one shown.
  // The client fills its skin carousel a moment after a hover or lock (it was empty at a hover, glamour.log 2026-10-09 13:55:57), so the list is
  // asked for again on lock and retried while it comes back empty. Right after a hover change it can still hold the last champion's skins
  // (Mundo's art under a Warwick hover, live): those count as empty.
  let skins = $state<Skin[]>([])
  let skinsFor = ''
  let skinErr = $state('')
  $effect(() => {
    const id = me?.championId ?? 0
    const key = `${id}:${me?.locked ?? false}`
    if (id === 0 || key === skinsFor) return
    skinsFor = key
    skinErr = ''
    if (demo) { skins = demoSkins(id); return }
    const load = (tries: number) =>
      getJson<Skin[]>(`/champselect/skins?championId=${id}`)
        .then((all) => {
          if (skinsFor !== key) return
          const s = all.filter((k) => Math.floor(k.id / 1000) === id)
          if (s.length > 0 || tries <= 1) skins = s
          else setTimeout(() => load(tries - 1), 1500)
        })
        .catch((e) => { if (skinsFor === key) skinErr = e instanceof Error ? e.message : 'Could not load skins.' })
    if (untrack(() => skins).some((s) => Math.floor(s.id / 1000) !== id)) skins = [] // another champion's; on lock the same list stays up
    load(5)
  })
  const skinIdx = $derived(Math.max(0, skins.findIndex((s) => s.id === me?.skinId || s.chromas.some((c) => c.id === me?.skinId))))
  const skin = $derived(skins[skinIdx])
  const chroma = $derived(skin?.chromas.find((c) => c.id === me?.skinId))
  const baseSkinId = $derived(skin?.id ?? me?.skinId ?? 0)
  const bg = $derived(art(myChamp, baseSkinId, 'splash'))
  const portrait = $derived(chroma?.preview ? asset(chroma.preview) : art(myChamp, baseSkinId, 'portrait'))

  let busy = $state(false)
  let err = $state('')
  async function chooseSkin(id: number) {
    if (demo) { demoSkin = id; return }
    busy = true; err = ''
    err = (await live.act('/champselect/skin', 'PUT', { skinId: id })) ?? ''
    busy = false
  }
  const step = (d: number) => { if (skins.length > 1) chooseSkin(skins[(skinIdx + d + skins.length) % skins.length].id) }
  // Swipe on the preview steps skins too.
  let swipeX = 0
  const swipeStart = (e: PointerEvent) => { swipeX = e.clientX }
  const swipeEnd = (e: PointerEvent) => { const dx = e.clientX - swipeX; if (Math.abs(dx) > 40) step(dx < 0 ? 1 : -1) }

  // Spells: tap a slot, the choices open in place; picking the other slot's spell swaps them.
  let spellSlot = $state<1 | 2 | null>(null)
  const spellChoices = $derived(spellsForMode(spells.list, queueNames.modeOf(board.queueId)))
  async function chooseSpell(id: number) {
    if (!me || !spellSlot) return
    const [a, b] = spellSlot === 1 ? [id, me.spell2Id] : [me.spell1Id, id]
    const pair: [number, number] = a === b ? (spellSlot === 1 ? [id, me.spell1Id] : [me.spell2Id, id]) : [a, b]
    spellSlot = null
    if (demo) { demoSpells = pair; return }
    busy = true; err = ''
    err = (await live.act('/champselect/spells', 'PUT', { spell1Id: pair[0], spell2Id: pair[1] })) ?? ''
    busy = false
  }
  let runes = $state(false)

  // Swaps.
  const received = $derived((live.now.swaps as Swap[]).filter((w) => w.state === 'RECEIVED'))
  const sent = $derived((live.now.swaps as Swap[]).filter((w) => w.state === 'SENT'))
  const ally = (cellId: number) => board.myTeam.find((p) => p.cellId === cellId)
  const SWAP_SHORT: Record<string, string> = { position: 'Lane swap', 'pick-order': 'Pick order', trade: 'Trade' }
  // A request shows on the teammate it is with, as a small bubble beside them, not as a banner over the screen.
  const swapWith = (cellId: number) => received.find((w) => w.cellId === cellId) ?? sent.find((w) => w.cellId === cellId)
  const swapPreview = (w: Swap) => {
    const p = ally(w.cellId)
    if (!p || !me) return ''
    if (w.kind === 'position') return `${laneShort(me.lane)} → ${laneShort(p.lane)}`
    if (w.kind === 'pick-order') return `Pick ${p.pickTurn ? ordinal(p.pickTurn) : '?'}`
    return `Get ${champions.byId(p.championId)?.name ?? 'theirs'}`
  }
  async function answer(w: Swap, verb: 'accept' | 'decline' | 'cancel') {
    busy = true
    err = (await live.act(`/swap/${w.kind}/${w.id}/${verb}`, 'POST')) ?? ''
    busy = false
  }
  let target = $state<BoardPlayer | null>(null)

  // Picking: only the client's pickable or bannable list, banned and locked champions greyed out, lock only on a live BAN_PICK turn.
  let only = $state<number[] | undefined>(undefined)
  let choosing = $state<number | null>(null)
  const slot = $derived(pickSlot(turn, board))
  $effect(() => {
    void takenKey(board, slot)
    if (demo || !slot || (slot.type !== 'pick' && slot.type !== 'ban')) return
    const path = slot.type === 'ban' ? '/champselect/bannable' : '/champselect/pickable'
    getJson<number[]>(path).then((ids) => { const ok = ids.filter((i) => i > 0); only = ok.length ? ok : undefined }).catch(() => {})
  })
  const taken = $derived(takenMap(board))
  async function hover(id: number | null) {
    if (!slot || id == null) return
    choosing = id
    if (demo) return
    busy = true
    err = (await live.act(`/champselect/actions/${slot.actionId}/hover`, 'POST', { championId: id })) ?? ''
    busy = false
  }
  // A ban or lock automation is about to send: Skip leaves it to the user for this draft.
  async function skipAuto() { if (!demo) err = (await live.act('/champselect/auto/skip', 'POST')) ?? '' }
  const canComplete = $derived(board.stage === 'BAN_PICK' && !!turn)
  const chosen = $derived(slot?.type === 'pick' ? (choosing ?? (me && !me.locked && me.championId ? me.championId : null)) : choosing)
  const chosenGone = $derived(chosen != null ? taken.get(chosen) : undefined)
  $effect(() => { void slot?.actionId; choosing = null })
  async function complete() {
    if (!turn || !chosen || demo) return
    busy = true
    err = (await live.act(`/champselect/actions/${turn.actionId}/complete`, 'POST', { championId: chosen, type: turn.type })) ?? ''
    busy = false
  }

  // Counter picks: a line under the board opens the sheet; a pick there hovers like the picker does.
  const feed = counterFeed(() => board, demo)
  const counter = $derived(feed.view)
  let counters = $state(false)
  const best = $derived(counter?.state === 'ready' ? (counter.picks ?? []).filter((p) => !p.fewGames).slice(0, 3) : [])
  const usable = (id: number) => !!slot && slot.type === 'pick' && !me?.locked && !taken.has(id) && (only === undefined || only.includes(id))

  // Made-up skins for the demo (Neeko's real skin numbers, so the splash and portrait art exist).
  function demoSkins(id: number): Skin[] {
    const nums = id === 518 ? [[0, 'Default'], [1, 'Winter Wonder Neeko'], [10, 'Star Guardian Neeko'], [12, 'Shan Hai Scrolls Neeko']] as const : [[0, 'Default']] as const
    return nums.map(([n, name], i) => ({
      id: id * 1000 + n, name, isBase: n === 0, tile: '',
      chromas: i === 1 ? ['#5DA9E9', '#E24FA6', '#C8AA6E'].map((c, j) => ({ id: id * 1000 + 30 + j, name: `${name} (${['Sapphire', 'Rose', 'Gold'][j]})`, colors: [c], preview: '' })) : [],
    }))
  }
</script>

{#snippet player(p: BoardPlayer, mine: boolean)}
  {@const c = champions.byId(p.championId)}
  {@const st = statusOf(board, p.cellId)}
  {@const ban = showBadges ? champions.byId(bansBy.get(p.cellId) ?? 0) : undefined}
  {@const w = mine && !p.isMe ? swapWith(p.cellId) : undefined}
  <div class="slotw">
  {#if w}
    <div class="bub" class:in={w.state === 'RECEIVED'} role={w.state === 'RECEIVED' ? 'alert' : 'status'}>
      <span class="bt">{w.state === 'RECEIVED' ? SWAP_SHORT[w.kind] ?? 'Swap' : `Asked · ${SWAP_SHORT[w.kind] ?? 'swap'}`}</span>
      {#if w.state === 'RECEIVED' && w.kind === 'position' && me}
        <span class="lanes" aria-label={`Your lane ${laneShort(me.lane)} to ${laneShort(p.lane)}`}>
          <Lane lane={me.lane} size={24} />
          <svg class="to" width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.4" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M5 12h13M13 6l6 6-6 6" /></svg>
          <Lane lane={p.lane} size={24} />
        </span>
      {:else if w.state === 'RECEIVED'}<span class="bp">{swapPreview(w)}</span>{/if}
      <span class="bb">
        {#if w.state === 'RECEIVED'}
          <button type="button" class="yes" aria-label={`Accept ${SWAP_SHORT[w.kind] ?? 'swap'} from ${nameOf(p)}`} disabled={busy} onclick={() => answer(w, 'accept')}>✓</button>
          <button type="button" class="no" aria-label="Decline" disabled={busy} onclick={() => answer(w, 'decline')}>✕</button>
        {:else}
          <button type="button" class="no wide" disabled={busy} onclick={() => answer(w, 'cancel')}>Cancel</button>
        {/if}
      </span>
    </div>
  {/if}
  <button type="button" class="pl" class:mine class:self={p.isMe} disabled={!mine || p.isMe || dealt} onclick={() => (target = p)}
    aria-label={p.isMe ? 'You' : mine ? `Ask ${nameOf(p)} for a swap` : `Enemy ${c?.name ?? 'not picked yet'}`}>
    <span class="pic" class:hov={!p.locked && !!c} class:acting={st === 'PICKING'} class:next={st === 'NEXT'}>
      {#if c}<img src={c.icon} alt="" />{:else}<span class="unk">{#if mine && p.lane}<Lane lane={p.lane} size={26} />{:else}?{/if}</span>{/if}
      {#if p.pickTurn && !dealt}<span class="ord">{p.pickTurn}</span>{/if}
      {#if ban}<img class="banb" src={ban.icon} alt={`Banned ${ban.name}`} title={`Banned ${ban.name}`} />{/if}
    </span>
    <span class="who">
      {#if mine}{#if p.lane}<Lane lane={p.lane} size={11} />{/if}{p.isMe ? 'YOU' : laneShort(p.lane)}{:else}{st || (p.locked ? '' : '…')}{/if}
    </span>
  </button>
  </div>
{/snippet}

{#snippet strip(ids: number[])}
  {#if !showBadges && ids.length}
    <div class="strip">
      {#each ids as b, i (i)}{@const c = champions.byId(b)}{#if c}<img src={c.icon} alt={`${c.name} banned`} />{/if}{/each}
    </div>
  {/if}
{/snippet}

<div class="take" role="dialog" aria-modal="true" aria-label="Champion select">
  {#if bg}<img class="bg" src={bg} alt="" />{/if}
  <div class="scrim" aria-hidden="true"></div>

  <div class="scroll">
    <div class="col">
      <div class="top">
        <div class="headtxt">
          <span class="label" class:mine={head.mine}>{head.label}</span>
          {#if head.sub}<span class="sub">{head.sub}</span>{/if}
        </div>
        <Ring size={48} stroke={4} fraction={left / phaseMs}><span class="num secs">{Math.ceil(left / 1000)}</span></Ring>
      </div>

      <div class="stage">
        <div class="team">
          {#each board.myTeam as p (p.cellId)}{@render player(p, true)}{/each}
          {@render strip(myBans)}
        </div>

        <div class="mid">
          <div class="preview" onpointerdown={swipeStart} onpointerup={swipeEnd} role="presentation">
            {#if portrait}<img src={portrait} alt="" draggable="false" />{:else}<span class="noart">{dealt ? 'Waiting' : 'No pick yet'}</span>{/if}
            {#if skins.length > 1}
              <button type="button" class="arrow l" aria-label="Previous skin" disabled={busy} onclick={() => step(-1)}>‹</button>
              <button type="button" class="arrow r" aria-label="Next skin" disabled={busy} onclick={() => step(1)}>›</button>
            {/if}
          </div>
          <span class="skinname">{chroma?.name ?? (skin ? (skin.isBase ? 'Default' : skin.name) : me?.locked ? 'Locked in' : me?.championId ? 'Hovering' : '')}</span>
          {#if skins.length > 1}<span class="count num">{skinIdx + 1} / {skins.length}</span>{/if}
          {#if skin?.chromas.length}
            <div class="chromas" role="group" aria-label="Chromas">
              <button type="button" class="sw" class:on={me?.skinId === skin.id} aria-label="Base" aria-pressed={me?.skinId === skin.id} disabled={busy} onclick={() => chooseSkin(skin.id)}>
                <span style:background="linear-gradient(135deg, #C8AA6E, #2DE2C4)"></span>
              </button>
              {#each skin.chromas as c (c.id)}
                <button type="button" class="sw" class:on={me?.skinId === c.id} aria-label={c.name} aria-pressed={me?.skinId === c.id} disabled={busy} onclick={() => chooseSkin(c.id)}>
                  {#if c.preview}<img src={asset(c.preview)} alt="" />{:else}<span style:background={c.colors[0] ?? '#888'}></span>{/if}
                </button>
              {/each}
            </div>
          {/if}
          {#if skinErr}<span class="muted small">{skinErr}</span>{/if}
          <span class="display champ">{myChamp ? myChamp.name.toUpperCase() : dealt ? 'WAITING' : 'NO PICK YET'}</span>
        </div>

        {#if board.theirTeam.length}
          <div class="team">
            {#each board.theirTeam as p (p.cellId)}{@render player(p, false)}{/each}
            {@render strip(theirBans)}
          </div>
        {/if}
      </div>

      {#if counter && counter.state !== 'none' && (counter.state !== 'locked' || counter.mine)}
        {@const vs = champions.byId(counter.enemyId)}
        <button type="button" class="ctr" onclick={() => (counters = true)}>
          <span class="ct">{counter.state === 'locked' ? 'Your matchups' : counter.state === 'waiting' ? maybeOpponents(counter).length ? `Counters · their ${laneShort(counter.lane).toLowerCase()} unsure` : `Counters · waiting for their ${laneShort(counter.lane).toLowerCase()}` : `Counters vs ${vs?.name ?? 'them'}`}</span>
          {#each best as p (p.championId)}
            {@const c = champions.byId(p.championId)}
            {#if c}<span class="cp"><img src={c.icon} alt="" /><span class="num">{percent(p.winRate)}</span></span>{/if}
          {/each}
          <svg class="chev" width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.4" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M9 6l6 6-6 6" /></svg>
        </button>
      {/if}
      {#if live.now.parties}<p class="parties">{partySummary(live.now.parties)}</p>{/if}
      {#if dealt}<div class="bench"><BenchCard /></div>{/if}

      <Chips lane={me?.lane || null} />
      {#if err}<p class="err" role="alert">{err}</p>{/if}
      <div class="gap"></div>
    </div>
  </div>

  <!-- The bar, as in the client: runes, the one action, then the spells. Always there, so spells change in ARAM,
       Mayhem and after locking in too. Spell choices open above it. -->
  <div class="act">
    {#if spellSlot}
      <div class="spells" role="group" aria-label={`Choose the ${spellSlot === 1 ? '1st' : '2nd'} spell`}>
        {#each spellChoices as s (s.id)}
          {@const badge = s.id === me?.spell1Id ? '1' : s.id === me?.spell2Id ? '2' : ''}
          <button type="button" class="spell" class:in={!!badge} disabled={busy} onclick={() => chooseSpell(s.id)} aria-label={s.name}>
            <img src={s.icon} alt="" />{#if badge}<b>{badge}</b>{/if}
          </button>
        {/each}
      </div>
    {/if}
    {#if live.now.draftAt}
      {@const d = live.now.draftAt}
      <span class="note auto">{d.type === 'ban' ? 'Banning' : 'Locking'} {champions.byId(d.championId)?.name ?? '…'} in {Math.max(0, Math.ceil((d.at - tick.now) / 1000))}s
        <button type="button" class="skip" onclick={skipAuto}>Skip</button></span>
    {:else if !dealt && slot && !me?.locked}
      {#if chosenGone}<span class="muted note">{champions.byId(chosen)?.name ?? 'That champion'} was {chosenGone === 'Banned' ? 'banned' : 'picked'}. Choose another.</span>
      {:else if !turn}<span class="muted note">Pick now to show your team; Lock in opens on your turn.</span>{/if}
    {/if}
    <div class="bar">
      {#if board.mode !== 'mayhem'}
        <button type="button" class="kbtn" aria-label="Runes" disabled={!me?.championId} onclick={() => (runes = true)}>
          <svg width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linejoin="round" aria-hidden="true"><path d="M12 2l3 7 7 3-7 3-3 7-3-7-7-3 7-3z" /></svg>
        </button>
      {/if}
      {#if dealt}
        <span class="grow"></span>
      {:else if slot && !me?.locked}
        <!-- Choosing sits right above Lock in, in the fixed bar, so it stays put while the page scrolls or the preview fills in. -->
        <div class="gocol">
          <div class="choose"><ChampionPicker label={slot.type === 'ban' ? 'Champion to ban' : 'Champion to pick'} value={chosen} {only} {taken} clearable={false} onpick={hover} /></div>
          <button type="button" class="btn btn-primary go" disabled={busy || !chosen || !!chosenGone || !canComplete} onclick={complete}>
            {slot.type === 'ban' ? 'Ban' : 'Lock in'}
          </button>
        </div>
      {:else}
        <button type="button" class="btn go" class:btn-done={me?.locked} class:btn-wait={!me?.locked} disabled>
          {me?.locked ? 'Locked in' : 'Waiting'}
        </button>
      {/if}
      {#each [1, 2] as n (n)}
        {@const s = spells.byId(n === 1 ? (me?.spell1Id ?? 0) : (me?.spell2Id ?? 0))}
        <button type="button" class="kbtn" class:on={spellSlot === n} aria-pressed={spellSlot === n} aria-label={`${n === 1 ? '1st' : '2nd'} spell: ${s?.name ?? 'none'}`}
          onclick={() => (spellSlot = spellSlot === n ? null : (n as 1 | 2))}>
          {#if s}<img src={s.icon} alt="" />{:else}<span class="blank"></span>{/if}
        </button>
      {/each}
    </div>
  </div>
</div>

{#if counters && counter}<CounterSheet view={counter} setForced={(id) => (feed.forced = id)} {usable} onpick={hover} onclose={() => (counters = false)} />{/if}
{#if target}<SwapSheet {board} {target} {nameOf} onclose={() => (target = null)} />{/if}
{#if runes && me}
  <Sheet label="Runes" onclose={() => (runes = false)}>
    <span class="display rtitle">{myChamp ? myChamp.name.toUpperCase() : ''} · RUNES</span>
    <RunePages {board} />
  </Sheet>
{/if}

<style>
  .take { position: fixed; inset: 0; z-index: var(--z-take); background: var(--g-bg); display: flex; flex-direction: column; overflow: hidden; }
  .bg { position: absolute; inset: 0; width: 100%; height: 100%; object-fit: cover; object-position: 50% 20%; opacity: .45; filter: blur(2px); transform: scale(1.04); }
  .scrim { position: absolute; inset: 0; background: linear-gradient(180deg, rgba(6,10,18,.75) 0%, rgba(6,10,18,.35) 30%, rgba(6,10,18,.6) 65%, #060A12 100%); }
  .scroll { position: relative; flex: 1; min-height: 0; overflow-y: auto; }
  .col { max-width: 34rem; margin-inline: auto; display: flex; flex-direction: column; gap: 10px; padding: calc(12px + env(safe-area-inset-top)) 12px 0; }
  .top { display: flex; align-items: center; gap: 12px; padding: 0 4px; }
  .headtxt { display: flex; flex-direction: column; gap: 2px; min-width: 0; flex: 1; }
  .headtxt .label { color: var(--g-gold); }
  .headtxt .label.mine { color: var(--g-teal); }
  .sub { font-size: 13px; color: var(--g-fg2); }
  .secs { font-size: 17px; font-weight: 700; }

  /* A swap request: a small bubble beside the teammate it is with, over the edge of the preview. */
  .slotw { position: relative; }
  .bub {
    position: absolute; left: calc(100% + 2px); top: 2px; z-index: 3; width: 108px; box-sizing: border-box; display: flex; flex-direction: column; align-items: center; gap: 6px;
    padding: 7px 8px 8px; text-align: center; border-radius: 10px; background: rgba(13,20,32,.97); border: 1px solid rgba(230,184,106,.7); box-shadow: 0 4px 14px rgba(0,0,0,.6);
  }
  .bub::before { content: ''; position: absolute; left: -5px; top: 16px; width: 8px; height: 8px; background: inherit; border-left: inherit; border-bottom: inherit; transform: rotate(45deg); }
  .bub.in { border-color: var(--g-magenta); animation: g-rise .2s ease-out both; }
  .bt { font: 700 10px var(--g-body); letter-spacing: .08em; text-transform: uppercase; color: var(--g-fg); }
  .bp { font-size: 11.5px; color: var(--g-fg2); line-height: 1.2; }
  .lanes { display: flex; align-items: center; justify-content: center; gap: 8px; color: var(--g-fg); }
  .lanes .to { color: var(--g-gold); }
  .bb { display: grid; grid-template-columns: 1fr 1fr; gap: 6px; width: 100%; }
  .bb .wide { grid-column: 1 / -1; }
  .bb button { flex: 1; min-height: 30px; padding: 0; border-radius: 7px; font: 700 14px var(--g-body); }
  .bb .yes { background: var(--g-teal); color: var(--g-teal-ink); }
  .bb .no { background: transparent; color: var(--g-fg); border: 1px solid rgba(200,170,110,.5); }
  .bb .wide { font-size: 11px; letter-spacing: .06em; }

  /* The three columns: teams at the edges, me in the middle. */
  .stage { display: grid; grid-template-columns: 58px minmax(0, 1fr) 58px; gap: 8px; align-items: start; }
  .team { display: flex; flex-direction: column; gap: 6px; }
  .pl { display: flex; flex-direction: column; align-items: center; gap: 2px; background: transparent; padding: 0; min-height: 0; color: inherit; font: inherit; }
  .pl:hover { filter: none; }
  .pl:disabled { opacity: 1; cursor: default; }
  .pic { position: relative; width: 52px; height: 52px; }
  .pic img:first-child, .unk { width: 52px; height: 52px; border-radius: 10px; border: 1px solid rgba(200,170,110,.45); display: flex; align-items: center; justify-content: center; box-sizing: border-box; background: rgba(6,10,18,.6); }
  .unk { border-style: dashed; color: var(--g-faint); font-weight: 700; font-size: 18px; }
  .pic.hov img:first-child { opacity: .55; border-style: dashed; }
  .self .pic img:first-child, .self .pic .unk { border: 2px solid var(--g-teal); }
  .pic.acting img:first-child, .pic.acting .unk { opacity: 1; box-shadow: 0 0 0 2px var(--g-teal), 0 0 14px rgba(45,226,196,.8); animation: g-pulse 1.4s ease-in-out infinite; }
  .pic.next img:first-child, .pic.next .unk { box-shadow: 0 0 0 2px var(--g-amber); }
  .ord { position: absolute; top: -5px; left: -5px; width: 17px; height: 17px; border-radius: 50%; background: var(--g-bg); border: 1px solid var(--g-gold); font: 700 10px var(--g-body); display: flex; align-items: center; justify-content: center; color: var(--g-fg); }
  .banb { position: absolute; right: -7px; bottom: -6px; width: 28px; height: 28px; border-radius: 5px; border: 1px solid var(--g-magenta); filter: grayscale(1) brightness(.75); background: var(--g-bg); }
  .who { font-size: 9.5px; letter-spacing: .1em; font-weight: 700; color: var(--g-muted); display: flex; align-items: center; gap: 2px; min-height: 12px; }
  .who :global(svg) { color: var(--g-gold); }
  .strip { display: grid; grid-template-columns: repeat(2, 24px); gap: 3px; justify-content: center; padding-top: 6px; border-top: 1px solid rgba(226,79,166,.35); }
  .strip img { width: 24px; height: 24px; border-radius: 4px; filter: grayscale(1) brightness(.7); border: 1px solid rgba(226,79,166,.5); }

  .mid { display: flex; flex-direction: column; align-items: center; gap: 4px; min-width: 0; }
  .preview { position: relative; width: 100%; aspect-ratio: 308 / 500; max-width: calc(34dvh * 308 / 500); border-radius: 14px; overflow: hidden; border: 1px solid rgba(200,170,110,.45); background: rgba(6,10,18,.5); touch-action: pan-y; display: flex; align-items: center; justify-content: center; }
  .preview img { width: 100%; height: 100%; object-fit: cover; object-position: 50% 15%; -webkit-user-drag: none; }
  .noart { color: var(--g-faint); font-weight: 700; letter-spacing: .1em; text-transform: uppercase; font-size: 12px; }
  .arrow { position: absolute; top: 50%; transform: translateY(-50%); width: 36px; height: 56px; min-height: 0; padding: 0; border-radius: 10px; background: rgba(6,10,18,.6); border: 1px solid rgba(200,170,110,.4); color: var(--g-fg); font-size: 28px; line-height: 1; display: flex; align-items: center; justify-content: center; }
  .arrow.l { left: 4px; }
  .arrow.r { right: 4px; }
  .champ { font-size: clamp(20px, 6.5vw, 28px); letter-spacing: .06em; line-height: 1.05; text-align: center; overflow-wrap: anywhere; margin-top: 4px; text-shadow: 0 2px 10px rgba(0,0,0,.9); }
  .skinname { font-size: 13px; color: var(--g-fg2); text-align: center; line-height: 1.25; }
  .count { font-size: 11px; color: var(--g-muted); }
  .chromas { display: flex; flex-wrap: wrap; justify-content: center; gap: 2px; }
  .sw { width: 36px; height: 36px; min-height: 0; border-radius: 50%; padding: 0; background: transparent; display: flex; align-items: center; justify-content: center; }
  .sw span, .sw img { width: 24px; height: 24px; border-radius: 50%; border: 2px solid rgba(240,230,210,.25); display: block; }
  .sw.on span, .sw.on img { border-color: var(--g-teal); box-shadow: 0 0 0 2px var(--g-bg), 0 0 0 4px var(--g-teal); }
  .small { font-size: 12px; text-align: center; }
  .ctr { display: flex; align-items: center; gap: 8px; width: 100%; min-height: 44px; padding: 6px 10px; border-radius: 10px; border: 1px solid rgba(200,170,110,.35); background: rgba(6,10,18,.6); color: var(--g-fg); font: inherit; text-align: left; }
  .ctr:hover { border-color: var(--g-teal); filter: none; }
  .ct { flex: 1; min-width: 0; font-size: 13.5px; font-weight: 600; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .cp { display: flex; flex-direction: column; align-items: center; gap: 1px; font-size: 10.5px; color: var(--g-teal); }
  .cp img { width: 26px; height: 26px; border-radius: 5px; }
  .chev { flex: none; color: var(--g-gold); }
  .parties { margin: 0; text-align: center; font-size: 12.5px; font-weight: 600; color: var(--g-fg2); }
  .bench :global(.card) { background: var(--g-card); }

  .choose :global(.field) { min-height: 2.9rem; padding: .35rem .6rem; background: rgba(6,10,18,.75); }
  .bar { display: flex; align-items: flex-end; gap: 8px; }
  .gocol { flex: 1; min-width: 0; display: flex; flex-direction: column; gap: 8px; }
  .gocol .go { width: 100%; }
  .bar .go { flex: 1; width: auto; min-height: 50px; letter-spacing: .08em; }
  .kbtn { flex: none; width: 50px; height: 50px; min-height: 0; padding: 0; border-radius: 10px; background: rgba(6,10,18,.7); border: 1px solid rgba(200,170,110,.5); display: flex; align-items: center; justify-content: center; overflow: hidden; color: var(--g-gold); }
  .kbtn img { width: 100%; height: 100%; display: block; }
  .kbtn.on { border: 2px solid var(--g-teal); box-shadow: 0 0 10px rgba(45,226,196,.5); }
  .blank { width: 100%; height: 100%; background: var(--card2); }
  .grow { flex: 1; }
  .spells { display: grid; grid-template-columns: repeat(auto-fill, minmax(44px, 1fr)); gap: 6px; padding: 8px; border-radius: 12px; background: var(--g-card); border: 1px solid var(--g-line); }
  .spell { position: relative; padding: 0; min-height: 0; background: transparent; display: flex; justify-content: center; }
  .spell img { width: 44px; height: 44px; border-radius: 8px; border: 1px solid rgba(200,170,110,.35); display: block; }
  .spell.in img { border: 2px solid var(--g-teal); }
  .spell b { position: absolute; top: -4px; right: 0; font: 700 10px var(--g-body); background: var(--g-teal); color: var(--g-teal-ink); border-radius: 4px; padding: 0 4px; }
  .gap { height: 8px; flex: none; }
  .rtitle { font-size: 18px; letter-spacing: .08em; }

  .act { position: relative; display: flex; flex-direction: column; gap: 8px; padding: 10px 12px calc(10px + env(safe-area-inset-bottom)); max-width: 34rem; width: 100%; box-sizing: border-box; margin-inline: auto; background: linear-gradient(180deg, rgba(6,10,18,0), rgba(6,10,18,.94) 30%, #060A12); }
  .note { font-size: 12.5px; text-align: center; }
  .note.auto { color: var(--g-gold); font-variant-numeric: tabular-nums; }
  .note .skip { margin-left: 8px; padding: 2px 10px; border-radius: 999px; border: 1px solid rgba(200,170,110,.45); background: none; color: inherit; font: inherit; font-weight: 700; cursor: pointer; }
</style>
