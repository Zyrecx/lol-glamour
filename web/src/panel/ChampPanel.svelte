<script lang="ts">
  // Champ select in the side panel: who I face, counter picks with Hover and Lock. No parties: the real groups come only in the last seconds (user).
  import { untrack } from 'svelte'
  import { champions } from '../lib/champions.svelte'
  import { live } from '../lib/live.svelte'
  import { summonerNames } from '../lib/summonerNames.svelte'
  import { getJson } from '../lib/api'
  import { counterRows, counterSorts, damageShares, enemyAtSlot, gamesLabel, opponentChance, percent, waitingNote, type CounterSort, type DamageSplit } from '../lib/counterPick'
  import { counterFeed } from '../lib/counterFeed.svelte'
  import { clientClick } from './clientClick.svelte'
  import { canLock, pickSlot, takenKey, takenMap } from '../lib/pickActions'
  import { BEST_COUNTER, type Board } from '../lib/types'
  import { demoFromUrl } from '../shell/demo'
  import { draftSteps, type DraftStep } from './plan'
  import { band, cardTags, marks, slotOf } from './marks.svelte'
  import { motion } from '../shell/motion.svelte'
  import { tick } from '../shell/tick.svelte'
  import LoadoutCard from './LoadoutCard.svelte'
  import { benchRates } from '../lib/benchRates.svelte'
  import { benchRows, benchTags, modeName, rateText, type BenchRow } from '../lib/benchAdvice'

  // post: a message to the tray app; left: the panel is on the client's left (the band to the client's cards only runs on the right).
  let { board, post = () => {}, left = false }: { board: Board; post?: (m: string) => void; left?: boolean } = $props()
  const now = $derived(live.now)
  // Counter picks: asked again only when my lane, my lock, the picked enemies or the opponent I chose by hand change.
  // The component lives only during one champ select, so the hand-picked opponent ends with it.
  const feed = counterFeed(() => board, demoFromUrl(location.search) !== null)
  const counter = $derived(feed.view)
  // Picks against my lane opponent or against their whole team, the user's choice; the team list alone until my opponent is known.
  let sortBy = $state<CounterSort>('lane')
  const sorts = $derived(counterSorts(counter))
  const list = $derived(counter && counter.state !== 'locked' ? counterRows(counter, sortBy) : null)
  const draft = $derived(board.mode === 'draft')
  const enemy = $derived(counter ? champions.byId(counter.enemyId) : null)
  const mc = $derived(counter?.mine ? champions.byId(counter.mine.championId) : null)
  const emptySlots = $derived(Array.from({ length: Math.max(0, 5 - (counter?.enemies?.length ?? 0)) }, (_, i) => i))
  const picksHint = $derived(!counter ? 'Loading…' : counter.state === 'nodata' ? `No data for ${enemy?.name ?? 'that champion'}.`
    : counter.state === 'none' ? 'No lane in this draft.' : 'Fills as the enemy picks.')
  // A click on an enemy's card in the client's own roster chooses them too (only clicks made during this champ select count).
  const seqAtStart = clientClick.seq
  $effect(() => {
    if (clientClick.seq === seqAtStart) return
    const slot = clientClick.enemySlot
    const id = untrack(() => enemyAtSlot(board, slot)) // only a new click re-runs this, not every board update
    if (id) feed.forced = id
  })

  // The tray app rings the enemy I face on the client's own card and runs a band from it into the counter list (SPEC "Click layer"): it needs the
  // card, and where the list is for the band's panel half. The list moves with scrolling and with sections above it, so it is measured every frame
  // while a band can show (one rect read, written only when it changed).
  const facingSlot = $derived(counter && counter.state !== 'none' ? slotOf(board.theirTeam, counter.enemyId) : -1)
  let picksEl = $state<HTMLElement | null>(null)
  $effect(() => {
    if (!marks.on || facingSlot < 0 || left || !picksEl) { band.target = null; return }
    const el = picksEl
    let frame = 0
    const measure = () => {
      const r = el.getBoundingClientRect()
      const t = r.height > 0 && r.width > 0 ? { left: r.left, top: r.top, bottom: r.bottom } : null
      const o = band.target
      if (t?.left !== o?.left || t?.top !== o?.top || t?.bottom !== o?.bottom) band.target = t
      frame = requestAnimationFrame(measure)
    }
    untrack(measure) // its read of band.target must not make this effect depend on what it writes
    return () => { cancelAnimationFrame(frame); band.target = null }
  })
  $effect(() => { band.slot = facingSlot })
  const drawsBand = $derived(!!band.target)
  $effect(() => post(`facing:${facingSlot}:${drawsBand ? 1 : 0}:${motion.reduced ? 1 : 0}`))
  // My champion's win rate goes on the client's enemy cards too; the panel keeps its "vs enemy team" rows all the same (user).
  const tags = $derived(cardTags(board.theirTeam, counter?.mine))
  $effect(() => post(tags))
  // Their damage and my team's go under the client's enemy cards too (the tray draws them while the marks show; the panel's own section steps aside then).
  const shares = (d: DamageSplit | null) => (d ? `${d.physical},${d.magic},${d.true}` : '')
  const dmg = $derived(damageShares(counter?.damage))
  const ourDmg = $derived(damageShares(counter?.allyDamage))
  $effect(() => post(`damage:${shares(dmg)}|${shares(ourDmg)}`))
  $effect(() => () => { band.slot = -1; post('facing:-1:0:0'); post('tags:'); post('damage:') }) // champ select ended or another section opened

  // A click on my own card in the client shows who beats my champion (who to ban, or what to fear); a second click hides it.
  // Without the marks there is no card to click: the hardest matchups show by themselves before any enemy pick, as before.
  const ownAtStart = clientClick.ownSeq
  let showThreats = $state(new URLSearchParams(location.search).has('threats'))
  $effect(() => {
    if (clientClick.ownSeq === ownAtStart) return
    untrack(() => (showThreats = !showThreats))
  })
  const banTurn = $derived(now.turn?.type === 'ban' ? now.turn : null)
  async function banHover(id: number) {
    if (!banTurn || busy) return
    busy = true
    err = (await live.act(`/champselect/actions/${banTurn.actionId}/hover`, 'POST', { championId: id })) ?? ''
    busy = false
  }

  // Hover and lock a suggestion: the buttons are my own taps. Only champions the client lists as pickable and nobody took; lock only on my live pick turn, after a second tap.
  const me = $derived(board.myTeam.find((p) => p.isMe))
  // Picks are for choosing: they go at my lock, and the loadout for what I locked takes their turn (user).
  const locked = $derived(!!me?.locked)
  const slot = $derived(pickSlot(now.turn, board))
  const taken = $derived(takenMap(board))
  let pickable = $state<number[] | null>(null)
  $effect(() => {
    if (!slot || slot.type !== 'pick') { pickable = null; return }
    void takenKey(board, slot)
    getJson<number[]>('/champselect/pickable').then((ids) => { pickable = ids }).catch(() => { pickable = null })
  })
  const usable = (id: number) => !!slot && slot.type === 'pick' && !taken.has(id) && (pickable === null || pickable.includes(id))
  let confirming = $state<number | null>(null)
  let busy = $state(false)
  let err = $state('')
  // A primitive: pickSlot makes a new object on every board update, which would clear "Sure?" before the second tap.
  const slotId = $derived(slot?.actionId ?? 0)
  $effect(() => { void slotId; confirming = null; err = '' })
  async function hover(id: number) {
    if (!slot || busy) return
    busy = true; confirming = null
    err = (await live.act(`/champselect/actions/${slot.actionId}/hover`, 'POST', { championId: id })) ?? ''
    busy = false
  }
  async function lock(id: number) {
    if (!slot || !canLock(board, now.turn) || busy) return
    if (confirming !== id) { confirming = id; return }
    busy = true; confirming = null
    err = (await live.act(`/champselect/actions/${slot.actionId}/complete`, 'POST', { championId: id, type: 'pick' })) ?? ''
    busy = false
  }
  const laneName = (l: string | null) => (l ? l.charAt(0) + l.slice(1).toLowerCase() : '')

  // Automation in this draft: what it bans, locks or pre-selects for my lane and how far it got.
  const steps = $derived(live.settings ? draftSteps(live.settings, board) : [])
  // The matchup loadout is automation too: its row keeps the band (and Stop) even with nothing else set.
  const loadoutOn = $derived(draft && !!live.settings?.matchupLoadout)
  const autoState = $derived(!live.settings?.automationEnabled ? 'off' : now.paused ? 'paused' : now.draftStopped ? 'stopped' : live.settings.dryRun ? 'dry' : 'on')
  const AUTO_TEXT = { on: 'On', off: 'Off', paused: 'Paused', stopped: 'Stopped for this draft', dry: 'Dry run' } as const
  const VERB = { ban: 'Ban', lock: 'Lock', hover: 'Pre-select' } as const
  const DONE = { ban: 'Banned', lock: 'Locked', hover: 'Hovered' } as const
  const nameOf = (id: number) => champions.byId(id)?.name ?? '…'
  const stepText = (st: DraftStep) => (st.state === 'yours' ? `You chose ${nameOf(st.otherId!)}` : `${st.state === 'done' ? DONE[st.kind] : VERB[st.kind]} ${nameOf(st.championId)}`)
  // Stop: automation leaves this draft alone (ends with it); Resume undoes that.
  let stopping = $state(false)
  let stopErr = $state('')
  async function stopDraft() {
    stopping = true
    stopErr = (await live.act('/automation/champ-select', 'PUT', { paused: !now.draftStopped })) ?? ''
    stopping = false
  }

  // A ban or lock waiting on its delay counts down on its row, with Skip; a skipped one is left to the user for this draft.
  const TYPE = { ban: 'ban', lock: 'pick', hover: '' } as const
  const waiting = (st: DraftStep) => st.state === 'pending' && now.draftAt?.type === TYPE[st.kind] && (now.draftAt.championId === st.championId || st.championId === BEST_COUNTER)
  const skipped = (st: DraftStep) => st.state === 'pending' && now.draftSkipped.includes(TYPE[st.kind])
  $effect(() => (now.draftAt ? tick.use() : undefined))
  const secsLeft = $derived(now.draftAt ? Math.max(0, Math.ceil((now.draftAt.at - tick.now) / 1000)) : 0)
  const ING = { ban: 'Banning', lock: 'Locking', hover: '' } as const
  let skipErr = $state('')
  async function skip() { skipErr = (await live.act('/champselect/auto/skip', 'POST')) ?? '' }
  // The automatic loadout waits a few seconds after it is ready: the row counts down with Cancel (no loadout by itself for the rest of this draft).
  const loadout = $derived(now.loadout)
  $effect(() => (loadout?.state === 'waiting' ? tick.use() : undefined))
  const loadoutSecs = $derived(loadout?.at ? Math.max(0, Math.ceil((loadout.at - tick.now) / 1000)) : 0)
  async function cancelLoadout() { skipErr = (await live.act('/loadout/cancel', 'POST')) ?? '' }

  // ARAM and Mayhem: my champion, the offered pair (Mayhem) and the bench by win rate or tier; a tap takes one (never by itself: the user's pick stays theirs).
  const rates = benchRates(() => board, demoFromUrl(location.search) !== null)
  const benchList = $derived(rates.mode ? benchRows(board, rates.rates) : null)
  const canSwap = $derived(board.stage === 'BAN_PICK' || board.stage === 'FINALIZATION')
  // The tray app tags the client's own bench slots with the same numbers (Mayhem; ARAM once its strip is measured).
  const benchMsg = $derived(rates.mode ? benchTags(board, rates.rates) : 'bench:')
  $effect(() => post(benchMsg))
  $effect(() => () => post('bench:'))
  let benchErr = $state('')
  let swapping = $state(false)
  async function takeBench(id: number) {
    swapping = true
    benchErr = (await live.act(`/champselect/bench/${id}`)) ?? ''
    swapping = false
  }
  // The offered pair is picked with the normal pick action, as the phone's Bench card does (unverified live, INSIGHTS open gaps).
  const canPick = $derived(now.turn?.type === 'pick')
  async function pickOffered(id: number) {
    if (!now.turn) return
    swapping = true
    benchErr = (await live.act(`/champselect/actions/${now.turn.actionId}/complete`, 'POST', { championId: id, type: 'pick' })) ?? ''
    swapping = false
  }

  const stepWhen =(st: DraftStep) => (st.state === 'done' ? 'Done' : st.state === 'yours' ? 'Left alone' : skipped(st) ? 'Skipped' : st.timing === 'LastSecond' ? 'Last second' : st.timing === 'Instant' ? 'On my turn' : 'Before my turn')
</script>

<!-- Every section keeps its place for the whole draft (user: sections popping in as the draft moves were a lot to take in); one with nothing yet says what fills it. -->
{#if draft}
  <!-- With the marks on, the client's own cards stand in for this strip (a click there chooses the enemy); that holds for the whole draft. -->
  {#if !marks.on}
    <section class="block">
      <h2 class="band">Who you face{#if counter?.forced}<button class="link" onclick={() => (feed.forced = null)}>Auto</button>{/if}</h2>
      <div class="enemies">
        {#each counter?.enemies ?? [] as id (id)}
          {@const c = champions.byId(id)}
          <button class="enemy" class:on={id === counter?.enemyId} onclick={() => (feed.forced = id)} title={c?.name ?? `#${id}`} aria-pressed={id === counter?.enemyId}>
            {#if c}<img src={c.icon} alt={c.name} width="36" height="36" />{:else}<span class="ph"></span>{/if}
          </button>
        {/each}
        {#each emptySlots as i (i)}<span class="enemy empty"></span>{/each}
      </div>
    </section>
  {/if}
  {#if !locked}
  <section class="block picks" bind:this={picksEl}>
    <h2 class="band">Picks
      {#if counter?.forced && marks.on}<button class="link auto" onclick={() => (feed.forced = null)} title="Back to the lane opponent">Auto</button>{/if}
      {#if list}
        <span class="sorts" role="group" aria-label="Sort picks">
          {#if sorts.includes('lane')}<button class:on={list.sort === 'lane'} aria-pressed={list.sort === 'lane'} onclick={() => (sortBy = 'lane')} title={`Win rate against ${enemy?.name ?? 'your lane opponent'} in ${laneName(counter?.dataLane ?? counter?.lane ?? null).toLowerCase()}`}>vs {enemy?.name ?? 'lane'}{#if counter && opponentChance(counter) != null} {opponentChance(counter)}%{/if}</button>{/if}
          {#if sorts.includes('team')}<button class:on={list.sort === 'team'} aria-pressed={list.sort === 'team'} onclick={() => (sortBy = 'team')} title="Average win rate against every picked enemy">vs team</button>{/if}
        </span>
      {/if}
    </h2>
    {#if list && counter}
      {#if list.sort === 'lane' && counter.dataLane}<p class="muted pad note">{enemy?.name ?? 'They'} rarely plays {laneName(counter.lane).toLowerCase()}: counters from {laneName(counter.dataLane).toLowerCase()}.</p>
        {:else if counter.state === 'nodata'}<p class="muted pad note">No lane data for {enemy?.name ?? 'that champion'}.</p>
        {:else if counter.state === 'waiting'}<p class="muted pad note">{waitingNote(counter, laneName(counter.lane).toLowerCase(), (id) => champions.byId(id)?.name ?? `#${id}`)}</p>{/if}
      {#if counter.duo?.best.length}<p class="pad note duo-line">Good with {champions.byId(counter.duo.partnerId)?.name ?? 'your duo'}: {counter.duo.best.map((id) => champions.byId(id)?.name ?? `#${id}`).join(', ')}</p>{/if}
      <ul class="rows scroll">
        {#each list.rows as p (p.championId)}
          {@const c = champions.byId(p.championId)}
          {@const hovered = me?.championId === p.championId && !me.locked}
          {@const can = usable(p.championId)}
          <li class="row" class:hovered>
            <button class="pick" disabled={!can || busy || hovered} onclick={() => hover(p.championId)} title={can ? (hovered ? 'Hovered' : `Hover ${c?.name ?? ''}`) : ''}>
              {#if c}<img src={c.icon} alt="" width="32" height="32" />{:else}<span class="ph"></span>{/if}
              <span class="who">
                <span class="nm">{c?.name ?? `#${p.championId}`}{#if p.inPool}<small>pool</small>{/if}{#if p.duo}<small class="duo" title={`Good with ${champions.byId(counter.duo?.partnerId ?? 0)?.name ?? 'your duo'}`}>duo</small>{/if}{#if p.auto}<small class="autolock" title="Auto-lock takes this one">auto</small>{/if}</span>
                <span class="games" title={hovered ? '' : p.sub.title}>{hovered ? 'Hovered' : p.sub.text}</span>
              </span>
              <span class="nums">
                <span class="wr" class:good={p.main >= 50}>{percent(p.main)}</span>
                {#if p.alt != null}<span class="alt">{p.altLabel} {p.alt.toFixed(1)}</span>{/if}
              </span>
            </button>
            {#if can && canLock(board, now.turn)}
              <button class="act lock" class:sure={confirming === p.championId} disabled={busy} onclick={() => lock(p.championId)}>{confirming === p.championId ? 'Sure?' : 'Lock'}</button>
            {/if}
          </li>
        {:else}
          <li class="muted pad">No data for this matchup.</li>
        {/each}
      </ul>
    {:else}
      <p class="muted pad">{picksHint}</p>
    {/if}
    {#if err}<p class="err pad">{err}</p>{/if}
  </section>
  {/if}
  <section class="block">
    {#if marks.on && showThreats && counter?.mine}
      <!-- A click on my own card in the client: who beats my champion, in this section's place until hidden. -->
      <h2 class="band">Who beats {mc?.name ?? 'your pick'}<span class="lane">{laneName(counter.lane)}</span><button class="link auto" onclick={() => (showThreats = false)}>Hide</button></h2>
      <ul class="rows">
        {#each counter.threats ?? [] as t (t.championId)}
          {@const c = champions.byId(t.championId)}
          {@const g = gamesLabel(t)}
          <li class="row">
            <button class="pick" disabled={!banTurn || busy} onclick={() => banHover(t.championId)} title={banTurn ? `Hover ban ${c?.name ?? ''}` : ''}>
              {#if c}<img src={c.icon} alt="" width="32" height="32" />{:else}<span class="ph"></span>{/if}
              <span class="who">
                <span class="nm">{c?.name ?? `#${t.championId}`}</span>
                <span class="games" title={g.title}>{g.text}</span>
              </span>
              <span class="wr">{percent(t.winRate)}</span>
            </button>
          </li>
        {:else}
          <li class="muted pad">No data for {mc?.name ?? 'this champion'} in {laneName(counter.lane).toLowerCase()}.</li>
        {/each}
      </ul>
      {#if err && banTurn}<p class="err pad">{err}</p>{/if}
    {:else if counter?.mine}
      {@const m = counter.mine}
      <h2 class="band">{mc?.name ?? 'Your pick'} {m.picked ? 'vs enemy team' : 'is weak vs'}<span class="lane">{laneName(counter.lane)}</span></h2>
      <ul class="rows">
        {#each m.lines as l (l.championId)}
          {@const c = champions.byId(l.championId)}
          {@const g = gamesLabel(l)}
          <li class="mline" class:opp={m.picked && l.championId === counter.enemyId}>
            {#if c}<img src={c.icon} alt="" width="24" height="24" />{:else}<span class="ph"></span>{/if}
            <span class="nm">{c?.name ?? `#${l.championId}`}</span>
            <span class="games" title={g.title}>{g.text}</span>
            <span class="wr" class:good={l.winRate >= 50}>{percent(l.winRate)}</span>
          </li>
        {:else}
          <li class="muted pad">No games against them in {laneName(counter.lane).toLowerCase()}.</li>
        {/each}
      </ul>
    {:else}
      <h2 class="band">{mc?.name ?? 'Your champion'}{#if counter?.lane}<span class="lane">{laneName(counter.lane)}</span>{/if}</h2>
      <p class="muted pad">Its matchups, once you hover one.</p>
    {/if}
  </section>
  {#if locked}<LoadoutCard {board} enemyId={counter && counter.state !== 'none' ? counter.enemyId : 0} />{/if}
{:else if benchList && rates.mode}
  <section class="block picks">
    <h2 class="band">Bench<span class="lane" title={rates.rates ? `${modeName(rates.mode)}, patch ${rates.rates.patch}, ${rates.rates.source}` : ''}>{modeName(rates.mode)}</span></h2>
    {#if benchList.mine}
      {@const m = benchList.mine}
      {@const c = champions.byId(m.championId)}
      {@const g = m.rate?.games ? gamesLabel({ ...m.rate, fewGames: false }) : null}
      <div class="mline opp">
        {#if c}<img src={c.icon} alt="" width="24" height="24" />{:else}<span class="ph"></span>{/if}
        <span class="nm">{c?.name ?? `#${m.championId}`}<small>you</small></span>
        <span class="games" title={g?.title ?? ''}>{g?.text ?? ''}</span>
        <span class="wr" class:good={(m.rate?.winRate ?? 0) >= 50}>{rateText(m.rate) || '–'}</span>
      </div>
    {/if}
    <ul class="rows scroll">
      {#if benchList.offered.length}
        <li class="sub">Offered</li>
        {#each benchList.offered as r (r.championId)}{@render benchRow(r, canPick, () => pickOffered(r.championId), 'Pick')}{/each}
        <li class="sub">Bench</li>
      {/if}
      {#each benchList.bench as r (r.championId)}
        {@render benchRow(r, canSwap, () => takeBench(r.championId), 'Take')}
      {:else}
        <li class="muted pad">{rates.loaded && !rates.rates ? `No ${modeName(rates.mode)} numbers right now.` : 'Empty. Champions given up land here.'}</li>
      {/each}
    </ul>
    {#if benchErr}<p class="err pad">{benchErr}</p>{/if}
  </section>
{/if}

{#snippet benchRow(r: BenchRow, can: boolean, take: () => void, verb: string)}
  {@const c = champions.byId(r.championId)}
  {@const g = r.rate?.games ? gamesLabel({ ...r.rate, fewGames: false }) : null}
  <li class="row">
    <button class="pick" disabled={!can || swapping} onclick={take} title={can ? `${verb} ${c?.name ?? ''}` : ''}>
      {#if c}<img src={c.icon} alt="" width="32" height="32" />{:else}<span class="ph"></span>{/if}
      <span class="who">
        <span class="nm">{c?.name ?? `#${r.championId}`}{#if r.better}<small>better</small>{/if}</span>
        <span class="games" title={g?.title ?? ''}>{g?.text ?? (r.rate ? '' : 'No data')}</span>
      </span>
      <span class="wr" class:good={r.better}>{rateText(r.rate) || '–'}</span>
    </button>
  </li>
{/snippet}
<!-- Automation sits at the bottom all draft (Picks fills the room above it before my lock, nothing after). -->
<!-- What automation does in this draft, with its own Stop / Resume beside the state it changes (Dodge stays alone in the bottom bar). -->
<section class="block auto bottom" class:idle={autoState !== 'on'}>
  <h2 class="band">Automation<span class="lane" class:warn={autoState !== 'on'}>{AUTO_TEXT[autoState]}</span>
    {#if (steps.length || loadoutOn) && (autoState === 'on' || autoState === 'dry' || autoState === 'stopped')}
      <button class="link stop" disabled={stopping} onclick={stopDraft}>{now.draftStopped ? 'Resume' : 'Stop'}</button>
    {/if}
  </h2>
  {#if stopErr || skipErr}<p class="err pad">{stopErr || skipErr}</p>{/if}
  <ul class="rows">
    {#each steps as st (st.kind)}
      {@const c = champions.byId(st.otherId ?? st.championId)}
      <li class={`step ${st.kind} ${st.state}`}>
        {#if c}<img src={c.icon} alt="" width="24" height="24" />{:else}<span class="ph"></span>{/if}
        {#if waiting(st)}
          <span class="nm">{ING[st.kind]} {nameOf(now.draftAt?.championId ?? st.championId)}</span>
          <span class="when soon">{secsLeft}s</span>
          <button class="link skip" onclick={skip}>Skip</button>
        {:else}
          <span class="nm">{stepText(st)}</span>
          <span class="when">{stepWhen(st)}</span>
        {/if}
      </li>
    {/each}
    {#if loadoutOn}
      {@const vs = loadout && loadout.enemyId > 0 ? champions.byId(loadout.enemyId) : null}
      <li class="step loadout">
        {#if vs}<img src={vs.icon} alt="" width="24" height="24" />{:else}<span class="ph"></span>{/if}
        <span class="nm">{vs ? `Loadout vs ${vs.name}` : 'Matchup loadout'}</span>
        {#if loadout?.state === 'waiting'}
          <span class="when soon">{loadoutSecs}s</span>
          <button class="link skip" onclick={cancelLoadout}>Cancel</button>
        {:else}
          <span class="when">{loadout?.state === 'done' ? 'Done' : loadout?.state === 'cancelled' ? 'Cancelled' : 'On lock'}</span>
        {/if}
      </li>
    {/if}
    {#each now.swapNotes as note, i (i)}
      <li class="step swap"><span class="ph"></span><span class="nm">{note}</span><span class="when">Swap</span></li>
    {/each}
    {#if !steps.length && !loadoutOn && !now.swapNotes.length}<li class="muted pad">{draft ? 'Nothing set for this lane.' : 'Nothing in this mode.'}</li>{/if}
  </ul>
</section>

<style>
  .block { display: flex; flex-direction: column; flex: none; }
  /* Picks takes the height left over and scrolls inside, so the sections under it never move. */
  .block.picks { flex: 1 1 0; min-height: 190px; }
  .block.bottom { margin-top: auto; }
  .rows.scroll { flex: 1; min-height: 0; overflow-y: auto; scrollbar-width: thin; scrollbar-color: var(--l-gold4) transparent; }
  .enemy.empty { width: 36px; height: 36px; box-sizing: border-box; border: 1px dashed var(--l-grey3); opacity: .5; }
  .band { margin: 0; display: flex; align-items: center; gap: 8px; min-height: 30px; padding: 0 12px; background: var(--l-band); border-bottom: 1px solid var(--l-frame); border-top: 1px solid var(--l-frame);
    font: 700 11px var(--g-body); letter-spacing: .1em; text-transform: uppercase; color: var(--l-grey1); }
  .block:first-child .band { border-top: 0; }
  .band .lane { margin-left: auto; color: var(--l-grey2); letter-spacing: .06em; }
  .pad { padding: 8px 12px; margin: 0; }
  .muted { color: var(--l-grey1); font-size: 12px; }
  .sub { padding: 8px 12px 2px; font: 700 10px var(--g-body); letter-spacing: .1em; text-transform: uppercase; color: var(--l-grey2); }
  .err { color: #E86A6C; font-size: 12px; }
  .enemies { display: flex; gap: 6px; padding: 10px 12px; }
  .enemy { padding: 0; min-height: 0; border: 1px solid var(--l-grey3) !important; background: none !important; cursor: pointer; line-height: 0; opacity: .55; }
  .enemy:hover { opacity: .85; }
  .enemy.on { border-color: var(--l-gold2) !important; opacity: 1; box-shadow: 0 0 0 1px var(--l-gold4); }
  .enemy img, .enemy .ph { width: 36px; height: 36px; display: block; }
  .link { margin-left: auto; min-height: 0 !important; padding: 0 !important; border: 0 !important; background: none !important; color: var(--l-blue) !important; font: 700 11px var(--g-body); letter-spacing: .06em; cursor: pointer; }
  .rows { list-style: none; margin: 0; padding: 2px 0 6px; }
  .row { display: flex; align-items: center; gap: 8px; padding: 0 12px 0 0; }
  .row:hover, .row.hovered { background: linear-gradient(90deg, rgba(200, 170, 110, .1), transparent); }
  .pick { flex: 1; min-width: 0; display: grid !important; grid-template-columns: 32px minmax(0, 1fr) auto; column-gap: 10px; align-items: center; text-align: left;
    min-height: 46px !important; padding: 0 0 0 12px !important; border: 0 !important; background: none !important; text-transform: none !important; letter-spacing: 0 !important; cursor: pointer; }
  .pick:disabled { cursor: default; color: inherit !important; }
  .pick img, .pick .ph { width: 32px; height: 32px; border: 1px solid var(--l-gold4); box-sizing: border-box; display: block; background: var(--l-btn); }
  .row.hovered img { border-color: var(--l-gold2); }
  .who { display: flex; flex-direction: column; min-width: 0; line-height: 1.25; }
  .nm { font: 400 13px var(--g-body); color: var(--l-gold1); white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
  .nm small { margin-left: 6px; color: var(--l-gold2); font-size: 9px; font-weight: 700; letter-spacing: .1em; text-transform: uppercase; }
  .games { font: 400 11px var(--g-body); color: var(--l-grey2); }
  .row.hovered .games { color: var(--l-gold2); }
  .wr { font: 700 13px var(--g-body); color: var(--l-grey1); font-variant-numeric: tabular-nums; }
  .wr.good { color: var(--l-blue); }
  .nums { display: flex; flex-direction: column; align-items: flex-end; line-height: 1.2; }
  .alt { font: 400 10px var(--g-body); color: var(--l-grey2); font-variant-numeric: tabular-nums; white-space: nowrap; }
  .nm small.autolock { color: var(--l-blue); }
  .nm small.duo { color: var(--l-gold1); }
  .duo-line { color: var(--l-gold1); font-size: 12px; }
  .sorts { margin-left: auto; display: flex; gap: 2px; }
  .sorts button { min-height: 20px !important; padding: 0 7px !important; border: 1px solid var(--l-grey3) !important; background: none !important; color: var(--l-grey1) !important;
    font: 700 10px var(--g-body); letter-spacing: .04em; text-transform: none; max-width: 110px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; cursor: pointer; }
  .sorts button.on { border-color: var(--l-gold2) !important; color: var(--l-gold1) !important; }
  .band .auto + .sorts { margin-left: 8px; }
  .act { flex: none; min-height: 26px !important; padding: 0 9px !important; font-size: 10px !important; }
  .act.lock { border-color: var(--l-blue) !important; color: var(--l-blue-ink) !important; background: #0A323C !important; }
  .act.lock.sure { background: var(--l-blue) !important; color: var(--l-black) !important; }

  .note { padding-top: 6px; padding-bottom: 0; }
  .mline { display: grid; grid-template-columns: 24px minmax(0, 1fr) auto auto; column-gap: 8px; align-items: center; min-height: 32px; padding: 0 12px; }
  .mline img, .mline .ph { width: 24px; height: 24px; box-sizing: border-box; border: 1px solid var(--l-gold4); display: block; background: var(--l-btn); }
  .mline .nm { font-size: 12px; }
  .mline.opp { background: linear-gradient(90deg, rgba(200, 170, 110, .1), transparent); }
  .mline.opp img { border-color: var(--l-gold2); }
  .band .stop { margin-left: 10px; }
  .band .lane + .auto { margin-left: 10px; }
  .block.auto .band { border-top: 1px solid var(--l-frame); }
  .band .lane.warn { color: #C89B3C; }
  .step { display: flex; align-items: center; gap: 8px; min-height: 34px; padding: 0 12px; }
  .step img, .step .ph { width: 24px; height: 24px; flex: none; box-sizing: border-box; border: 1px solid var(--l-gold4); display: block; background: var(--l-btn); }
  .step.ban img { border-color: #6B2A2C; }
  .step.hover img { border-style: dashed; }
  .step .nm { flex: 1; min-width: 0; font-size: 12px; }
  .step .when { flex: none; font-size: 11px; color: var(--l-grey1); }
  .step.done .when { color: var(--l-blue); }
  .step .when.soon { color: var(--l-gold1); font-variant-numeric: tabular-nums; }
  .step .skip { margin-left: 4px; }
  .step.yours .nm, .step.yours .when { color: var(--l-grey2); }
  .block.auto.idle .step { opacity: .5; }
</style>
