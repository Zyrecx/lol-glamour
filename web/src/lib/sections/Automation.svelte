<script lang="ts">
  import { live } from '../live.svelte'
  import type { LaneKey, LaneSetup, Settings } from '../types'
  import Card from '../components/Card.svelte'
  import ChampionPicker from '../components/ChampionPicker.svelte'
  import ChampionPool from '../components/ChampionPool.svelte'
  import Segmented from '../components/Segmented.svelte'
  import Toggle from '../components/Toggle.svelte'

  // The PC page shows the two halves on separate tabs; everywhere else both stay together. The side panel has its own lane table
  // (panel/LaneTable.svelte) and takes only the delays in one card (`timing`) and dry run from here.
  let { part = 'all' }: { part?: 'all' | 'accept' | 'champs' | 'testing' | 'timing' | 'loadout' } = $props()
  const shows = (p: typeof part) => part === 'all' || part === p
  const s = $derived(live.view)
  let msg = $state('')

  async function save(change: Partial<Settings>) {
    msg = (await live.patch(change)) ?? ''
  }

  // Which setup is being edited. The default applies to every lane that has no setup of its own.
  let lane = $state<'DEFAULT' | LaneKey>('DEFAULT')
  const laneTabs = [
    { value: 'DEFAULT' as const, label: 'All', name: 'All lanes', lane: 'FILL' }, { value: 'TOP' as const, label: 'Top', name: 'Top', lane: 'TOP' },
    { value: 'JUNGLE' as const, label: 'Jgl', name: 'Jungle', lane: 'JUNGLE' }, { value: 'MIDDLE' as const, label: 'Mid', name: 'Mid', lane: 'MIDDLE' },
    { value: 'BOTTOM' as const, label: 'Bot', name: 'Bot', lane: 'BOTTOM' }, { value: 'SUPPORT' as const, label: 'Sup', name: 'Support', lane: 'SUPPORT' },
  ]
  const laneName = $derived(laneTabs.find((t) => t.value === lane)!.name)
  // The role the cards below belong to, so it is clear which setup is being edited.
  const forRole = $derived(laneName)
  const own = $derived(lane === 'DEFAULT' ? undefined : s?.lanes?.[lane])
  // The setup shown in the cards: the lane's own one, else the default fields.
  const cur = $derived<LaneSetup | null>(!s ? null : own ?? { banChampionId: s.autoBanChampionId, banTiming: s.banTiming, lockChampionId: s.autoLockChampionId, lockTiming: s.lockTiming, hoverChampionId: s.preHoverChampionId })
  const editable = $derived(lane === 'DEFAULT' || own !== undefined)

  function change(c: Partial<LaneSetup>) {
    if (!s || !cur) return
    if (lane === 'DEFAULT') {
      save({
        ...('banChampionId' in c && { autoBanChampionId: c.banChampionId }), ...(c.banTiming && { banTiming: c.banTiming }),
        ...('lockChampionId' in c && { autoLockChampionId: c.lockChampionId }), ...(c.lockTiming && { lockTiming: c.lockTiming }),
        ...('hoverChampionId' in c && { preHoverChampionId: c.hoverChampionId }),
      })
    } else save({ lanes: { ...s.lanes, [lane]: { ...cur, ...c } } })
  }
  function useOwn(on: boolean) {
    if (!s || lane === 'DEFAULT') return
    const lanes = { ...s.lanes }
    if (on) lanes[lane] = { ...cur! } // starts as a copy of what applies now
    else delete lanes[lane]
    save({ lanes })
  }
  // Any setup (default or a lane's) that uses a timing needs its card: the margin for last second, the delay for "on my turn".
  function uses(x: Settings, t: 'Instant' | 'LastSecond') {
    const setups: LaneSetup[] = [{ banChampionId: x.autoBanChampionId, banTiming: x.banTiming, lockChampionId: x.autoLockChampionId, lockTiming: x.lockTiming, hoverChampionId: x.preHoverChampionId }, ...Object.values(x.lanes ?? {}) as LaneSetup[]]
    return setups.some((l) => (l.banChampionId != null && l.banTiming === t) || (l.lockChampionId != null && l.lockTiming === t))
  }

  const slots: { value: Settings['pickSlot']; label: string }[] = [{ value: 'Last', label: 'Last pick' }, { value: 'First', label: 'First pick' }, { value: 'NotFirst', label: 'Not first' }]

  // Ranges come from the host (settingsMeta), which refuses anything outside them.
  const maxAccept = $derived(live.meta?.maxAutoAcceptDelaySeconds ?? 8)
  const maxTurn = $derived(live.meta?.maxActionDelaySeconds ?? 20)
  const maxMargin = $derived(live.meta?.maxLastSecondMarginSeconds ?? 30)
  function num(e: Event & { currentTarget: HTMLInputElement }, max: number, apply: (v: number) => void) {
    const v = Number(e.currentTarget.value)
    if (Number.isFinite(v) && v >= 0 && v <= max) apply(v)
  }

  // Champion pool for the lane: always listed among the counter picks (the lane's lock and hover champions count too).
  const pool = $derived(lane === 'DEFAULT' ? [] : (s?.championPools?.[lane] ?? []))
  function setPool(ids: number[]) {
    if (!s || lane === 'DEFAULT') return
    const pools = { ...s.championPools }
    if (ids.length) pools[lane] = ids
    else delete pools[lane]
    save({ championPools: pools })
  }

  const timing = [{ value: 'Instant' as const, label: 'On my turn' }, { value: 'LastSecond' as const, label: 'Last second' }]
  const acceptTiming = [{ value: 'Instant' as const, label: 'At once' }, { value: 'LastSecond' as const, label: 'Last second' }]
</script>

{#if !s}
  <Card><p class="muted">Loading…</p></Card>
{:else}
  <!-- Phone and Off did the same on the PC (the phone can always accept), so it is one switch; a stored Phone shows as off. -->
  {#if shows('accept')}
  <Card title="Auto-accept">
    <Toggle label="Accept matches automatically" hint={s.autoAccept === 'Always' ? 'With a delay, the phone can stop it for that match.' : 'Off: accept from the phone or the client.'}
      checked={s.autoAccept === 'Always'} onchange={(v) => save({ autoAccept: v ? 'Always' : 'Off' })} />
    {#if s.autoAccept === 'Always'}
      <Segmented label="Accept timing" options={acceptTiming} value={s.acceptTiming} onchange={(v) => save({ acceptTiming: v })} />
      {#if s.acceptTiming === 'LastSecond'}
        <p class="muted">Accepts {maxAccept} s into the match found.</p>
      {:else}
        <label class="margin">
          <span>Delay</span>
          <input type="number" min="0" max={maxAccept} step="0.5" value={s.autoAcceptDelaySeconds} onchange={(e) => num(e, maxAccept, (v) => save({ autoAcceptDelaySeconds: v }))} />
          <span class="muted">seconds (0 = at once, up to {maxAccept})</span>
        </label>
      {/if}
    {/if}
  </Card>

  {/if}

  {#if shows('champs')}
  <Card title="Ban and lock" hint="Set it per lane, or use the default for all.">
    <Segmented label="Setup for" options={laneTabs} value={lane} onchange={(v) => (lane = v)} />
    {#if lane !== 'DEFAULT'}
      <Toggle label={`Separate setup for ${laneName}`} hint={editable ? 'Off goes back to the default.' : 'Off: the default setup applies in this lane.'} checked={editable} onchange={useOwn} />
    {/if}
  </Card>

  {#if cur && editable}
    <Card title={`Auto-ban · ${forRole}`} hint="Bans on your turn.">
      <ChampionPicker label="Champion to ban" value={cur.banChampionId} onpick={(id) => change({ banChampionId: id })} />
      {#if cur.banChampionId != null}
        <Segmented label="Ban timing" options={timing} value={cur.banTiming} onchange={(v) => change({ banTiming: v })} />
        <Toggle label="Skip if a teammate hovers it" checked={s.skipBanIfAllyHovered} onchange={(v) => save({ skipBanIfAllyHovered: v })} />
      {/if}
    </Card>

    <Card title={`Auto-lock · ${forRole}`} hint="Locks your champion on your turn.">
      <ChampionPicker label="Champion to lock" bestCounter value={cur.lockChampionId} onpick={(id) => change({ lockChampionId: id })} />
      {#if cur.lockChampionId != null}
        <Segmented label="Lock timing" options={timing} value={cur.lockTiming} onchange={(v) => change({ lockTiming: v })} />
      {/if}
    </Card>
  {/if}

  {#if cur && editable}
    <Card title={`Pre-select · ${forRole}`} hint="Shows your champion to the team before your turn.">
      <ChampionPicker label="Champion to pre-select" value={cur.hoverChampionId} onpick={(id) => change({ hoverChampionId: id })} />
    </Card>
  {/if}

  {#if lane !== 'DEFAULT'}
    <Card title={`Champion pool · ${forRole}`} hint="Always shown among the counter picks.">
      <ChampionPool ids={pool} onchange={setPool} />
    </Card>
  {/if}

  {#if uses(s, 'Instant')}
    <Card title="Turn delay" hint="Wait before banning or locking.">
      <label class="margin">
        <input type="number" min="0" max={maxTurn} step="0.5" value={s.actionDelaySeconds} onchange={(e) => num(e, maxTurn, (v) => save({ actionDelaySeconds: v }))} />
        <span class="muted">seconds (0 = at once)</span>
      </label>
    </Card>
  {/if}

  {#if uses(s, 'LastSecond')}
    <Card title="Last-second margin" hint="Seconds left in the turn when Glamour acts.">
      <label class="margin">
        <input type="number" min="1" max={maxMargin} step="0.5" value={s.lastSecondMarginSeconds} onchange={(e) => num(e, maxMargin, (v) => v >= 1 && save({ lastSecondMarginSeconds: v }))} />
        <span class="muted">seconds</span>
      </label>
    </Card>
  {/if}

  {/if}

  {#if shows('champs') || part === 'loadout'}
  <Card title="Swaps" hint="Your lanes are the two picked in the lobby.">
    <Toggle label="Accept a better lane" checked={s.laneSwapAccept} onchange={(v) => save({ laneSwapAccept: v })} />
    <Toggle label="Ask for my lane" hint="First lane, then second." checked={s.laneSwapAsk} onchange={(v) => save({ laneSwapAsk: v })} />
    <p class="muted">Pick slot</p>
    <Segmented label="Pick slot" options={slots} value={s.pickSlot} onchange={(v) => save({ pickSlot: v })} />
    <Toggle label="Accept a swap toward it" checked={s.pickSlotAccept} onchange={(v) => save({ pickSlotAccept: v })} />
    <Toggle label="Ask for it" checked={s.pickSlotAsk} onchange={(v) => save({ pickSlotAsk: v })} />
  </Card>
  <Card title="Matchup loadout" hint="Build, runes and spells against your lane opponent.">
    <Toggle label="Apply when you lock in" checked={s.matchupLoadout} onchange={(v) => save({ matchupLoadout: v })} />
  </Card>
  <Card title="Mayhem">
    <Toggle label="Win rates from arammayhem.com" hint="Off: tiers from u.gg." checked={s.mayhemWinRates} onchange={(v) => save({ mayhemWinRates: v })} />
    <Toggle label="Augment tags in game" hint="Each offered augment's win-rate change on your champion. Borderless window mode." checked={s.augmentTips} onchange={(v) => save({ augmentTips: v })} />
  </Card>
  <Card title="Loading screen">
    <Toggle label="Mark premades" hint="Who queued together, on both teams. Borderless window mode." checked={s.loadingParties} onchange={(v) => save({ loadingParties: v })} />
  </Card>
  {/if}

  {#if part === 'timing'}
  <Card title="Timing">
    {#if s.acceptTiming !== 'LastSecond'}
      <label class="margin row-num">
        <span>Accept after</span>
        <input type="number" min="0" max={maxAccept} step="0.5" value={s.autoAcceptDelaySeconds} onchange={(e) => num(e, maxAccept, (v) => save({ autoAcceptDelaySeconds: v }))} />
        <span class="muted">s</span>
      </label>
    {:else}
      <p class="margin row-num"><span>Accept at the last second</span><span class="muted">{maxAccept} s</span></p>
    {/if}
    <label class="margin row-num">
      <span>On my turn, wait</span>
      <input type="number" min="0" max={maxTurn} step="0.5" value={s.actionDelaySeconds} onchange={(e) => num(e, maxTurn, (v) => save({ actionDelaySeconds: v }))} />
      <span class="muted">s</span>
    </label>
    <label class="margin row-num">
      <span>Last second, act at</span>
      <input type="number" min="1" max={maxMargin} step="0.5" value={s.lastSecondMarginSeconds} onchange={(e) => num(e, maxMargin, (v) => v >= 1 && save({ lastSecondMarginSeconds: v }))} />
      <span class="muted">s left</span>
    </label>
  </Card>
  {/if}

  {#if shows('testing')}
  <Card title="Testing">
    <Toggle label="Dry run" hint="Does nothing in the client, only logs." checked={s.dryRun} onchange={(v) => save({ dryRun: v })} />
  </Card>
  {/if}

  {#if msg}<p class="err" role="alert">{msg}</p>{/if}
{/if}

<style>
  .margin { display: flex; gap: .6rem; align-items: center; }
  .margin input { width: 6rem; }
  .row-num > span:first-child { flex: 1; }
  .row-num input { width: 4.5rem; }
  .row-num .muted { width: 2.6rem; }
</style>
