<script lang="ts">
  // Nothing running: who is signed in, the mode shortcuts (pinned, or the first playable ones), and what automation will do.
  // The first shortcut is the big Play button in the dock above the tabs.
  import { live } from '../../lib/live.svelte'
  import { clientStatus } from '../../lib/now'
  import JoinParty from '../../lib/sections/JoinParty.svelte'
  import AutomationCard from '../parts/AutomationCard.svelte'
  import Sheet from '../parts/Sheet.svelte'
  import { playable } from '../playable.svelte'
  import { movePin, shortcuts } from '../util'

  const connected = $derived(live.now.connected)
  const profile = $derived(live.profiles?.profiles.find((p) => p.id === live.profiles?.activeId) ?? null)
  const card = $derived(profile?.card ?? null)
  const rank = $derived(card?.solo ? `Solo ${cap(card.solo.tier)} ${card.solo.division} · ${card.solo.lp} LP` : card ? 'Unranked' : '')
  const pinned = $derived(live.settings?.pinnedQueueIds ?? [])
  let showAll = $state(readAll())
  // Advanced mode adds the queues the client hides, so a pin on one of them still resolves.
  const known = $derived(showAll ? [...playable.list, ...playable.all.filter((q) => !playable.list.some((p) => p.id === q.id))] : playable.list)
  const quick = $derived(shortcuts(known, pinned))
  let modes = $state(false)
  let err = $state('')
  let busy = $state(false)

  function readAll() { try { return localStorage.getItem('glamour.allQueues') === '1' } catch { return false } }
  $effect(() => { playable.ensure(connected) })
  $effect(() => { if (connected && showAll) playable.loadAll() })

  function cap(s: string) { return s ? s[0] + s.slice(1).toLowerCase() : s }
  function setAll(on: boolean) {
    showAll = on
    try { localStorage.setItem('glamour.allQueues', on ? '1' : '0') } catch { /* a convenience only */ }
  }

  async function play(id: number) {
    busy = true
    err = (await live.act(`/lobby/${id}`)) ?? ''
    if (!err) modes = false
    busy = false
  }
  // Starting from the shown defaults keeps the screen stable when the first pin is set.
  const base = () => (pinned.length ? pinned : quick.map((q) => q.id))
  async function pin(id: number) {
    const b = base()
    err = (await live.patch({ pinnedQueueIds: b.includes(id) ? b.filter((x) => x !== id) : [...b, id] })) ?? ''
  }
  async function move(id: number, by: -1 | 1) {
    err = (await live.patch({ pinnedQueueIds: movePin(base(), id, by) })) ?? ''
  }
  // The sheet lists pinned modes first (in pin order, with arrows), then the rest.
  const pinnedRows = $derived(base().map((id) => known.find((q) => q.id === id)).filter((q): q is NonNullable<typeof q> => !!q))
  const otherRows = $derived(known.filter((q) => !pinnedRows.some((p) => p.id === q.id)))
</script>

<div class="idle">
  <div class="content">
    <div class="who">
      {#if profile}
        <div class="avatar">
          {#if profile.hasIcon}<img src={`/api/profiles/${encodeURIComponent(profile.id)}/icon`} alt="" />{:else}<span class="blank"></span>{/if}
          {#if card}<span class="lvl num">{card.level}</span>{/if}
        </div>
        <div class="id">
          <span class="display name">{profile.riotId?.split('#')[0] ?? profile.name}</span>
          <span class="sub">{[profile.riotId?.includes('#') ? '#' + profile.riotId.split('#')[1] : '', rank].filter(Boolean).join(' · ')}</span>
          <a href="#/accounts">Switch account</a>
        </div>
      {:else}
        <div class="id"><span class="display name">Glamour</span><a href="#/accounts">Save this account</a></div>
      {/if}
    </div>

    {#if !connected}
      <section class="panel">
        <span class="label">League</span>
        <p class="status">{clientStatus(live.now).text}</p>
        <p class="muted">{live.now.clientStage === 'none' ? 'Start the Riot Client on the PC.' : 'Waiting for the League client to finish starting.'}</p>
      </section>
    {:else}
      <section class="panel play">
        <span class="label">Modes</span>
        <div class="more-row">
          {#each quick.slice(1) as q (q.id)}
            <button type="button" class="btn btn-ghost small" disabled={busy} onclick={() => play(q.id)}>{q.name}</button>
          {/each}
          <button type="button" class="btn btn-ghost small" onclick={() => (modes = true)} aria-label="All modes, pin and reorder">
            All modes
            <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.4" stroke-linecap="round" aria-hidden="true"><path d="M6 9l6 6 6-6" /></svg>
          </button>
        </div>
        {#if playable.error}<p class="err">{playable.error}</p>{/if}
        {#if err && !modes}<p class="err" role="alert">{err}</p>{/if}
      </section>
      <JoinParty />
    {/if}

    <AutomationCard />
    {#if connected && quick[0]}<div class="dock-gap"></div>{/if}
  </div>
</div>

{#if connected && quick[0]}
  <div class="dock">
    <button type="button" class="btn btn-primary" disabled={busy} onclick={() => play(quick[0].id)}>
      <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linejoin="round" aria-hidden="true"><path d="M7 4l12 8-12 8z" /></svg>
      {quick[0].name}
    </button>
  </div>
{/if}

{#if modes}
  <Sheet label="All modes" onclose={() => (modes = false)} tall>
    <div class="sheet-head">
      <span class="display" style="font-size: 20px">All modes</span>
      <span class="muted">Star = pin to Play</span>
    </div>
    {#if err}<p class="err" role="alert">{err}</p>{/if}
    {#if pinnedRows.length}<span class="label">Pinned · the first is the big button</span>{/if}
    {#each pinnedRows as q, i (q.id)}
      <div class="mode">
        <button type="button" class="pick" disabled={busy} onclick={() => play(q.id)}><b>{q.name}</b><small>{q.mapName}</small></button>
        <button type="button" class="pin arrow" aria-label={`Move ${q.name} up`} disabled={i === 0} onclick={() => move(q.id, -1)}>
          <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" aria-hidden="true"><path d="M6 15l6-6 6 6" /></svg>
        </button>
        <button type="button" class="pin arrow" aria-label={`Move ${q.name} down`} disabled={i === pinnedRows.length - 1} onclick={() => move(q.id, 1)}>
          <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" aria-hidden="true"><path d="M6 9l6 6 6-6" /></svg>
        </button>
        <button type="button" class="pin on" aria-label={`Unpin ${q.name}`} aria-pressed="true" onclick={() => pin(q.id)}>
          <svg width="20" height="20" viewBox="0 0 24 24" fill="currentColor" stroke="currentColor" stroke-width="1.8" stroke-linejoin="round" aria-hidden="true"><path d="M12 3l2.7 5.6 6.1.8-4.5 4.2 1.1 6.1L12 16.8l-5.4 2.9 1.1-6.1L3.2 9.4l6.1-.8z" /></svg>
        </button>
      </div>
    {/each}
    {#if otherRows.length}<span class="label" style="padding-top: 6px">{pinnedRows.length ? 'Other modes' : 'Modes'}</span>{/if}
    {#each otherRows as q (q.id)}
      <div class="mode">
        <button type="button" class="pick" disabled={busy} onclick={() => play(q.id)}>
          <b>{q.name}</b><small>{showAll ? `#${q.id} · ${q.category}${q.mapName ? ' · ' + q.mapName : ''}` : q.mapName}</small>
        </button>
        <button type="button" class="pin" aria-label={`Pin ${q.name}`} aria-pressed="false" onclick={() => pin(q.id)}>
          <svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linejoin="round" aria-hidden="true"><path d="M12 3l2.7 5.6 6.1.8-4.5 4.2 1.1 6.1L12 16.8l-5.4 2.9 1.1-6.1L3.2 9.4l6.1-.8z" /></svg>
        </button>
      </div>
    {/each}
    {#if !pinnedRows.length && !otherRows.length}<p class="muted">No modes are open right now.</p>{/if}
    <label class="adv">
      <input type="checkbox" checked={showAll} onchange={(e) => setAll(e.currentTarget.checked)} />
      <span><b>Show all queues (advanced)</b><small>Includes queues the client hides.</small></span>
    </label>
    {#if !showAll}<span class="muted">The list is the client's own: only queues it offers right now.</span>{/if}
  </Sheet>
{/if}

<style>
  .idle { position: relative; }
  .content { position: relative; display: flex; flex-direction: column; gap: 14px; padding-top: 24px; }
  .who { display: flex; align-items: center; gap: 14px; text-shadow: 0 1px 8px rgba(0,0,0,.8); }
  .avatar { position: relative; width: 78px; height: 78px; flex: none; }
  .avatar img, .avatar .blank { position: absolute; top: 3px; left: 3px; width: 72px; height: 72px; border-radius: 50%; border: 2px solid var(--g-gold); box-shadow: 0 0 0 3px var(--g-bg), 0 0 0 4px rgba(200,170,110,.5); object-fit: cover; background: var(--g-panel); }
  .lvl { position: absolute; bottom: -6px; left: 50%; transform: translateX(-50%); background: var(--g-bg); border: 1px solid var(--g-gold); border-radius: 999px; padding: 1px 8px; font-size: 12px; font-weight: 700; }
  .id { display: flex; flex-direction: column; gap: 4px; min-width: 0; }
  .name { font-size: 24px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .sub { font-size: 14px; color: var(--g-fg2); }
  .id a { font-size: 13px; font-weight: 600; }
  .status { font-size: 18px; font-weight: 700; }
  .play { display: flex; flex-direction: column; gap: 10px; }
  .more-row { display: grid; grid-template-columns: repeat(auto-fit, minmax(7.5rem, 1fr)); gap: 8px; }
  .btn.small { min-height: 44px; font-size: 12.5px; letter-spacing: .06em; padding: 0 6px; }
  .sheet-head { display: flex; align-items: baseline; justify-content: space-between; padding-bottom: 4px; }
  .mode { display: flex; align-items: center; border-bottom: 1px solid var(--g-line-soft); }
  .pick { flex: 1; display: flex; flex-direction: column; align-items: flex-start; gap: 2px; min-height: 54px; padding: 6px 4px; background: transparent; color: var(--g-fg); border-radius: 8px; text-align: left; min-width: 0; }
  .pick:hover { background: rgba(200,170,110,.05); filter: none; }
  .pick b { font-size: 15px; }
  .pick small { font-size: 12px; color: var(--g-muted); }
  .pin { width: 44px; height: 44px; padding: 0; display: flex; align-items: center; justify-content: center; background: transparent; color: var(--g-muted); flex: none; }
  .pin.arrow { width: 38px; }
  .pin:disabled { opacity: .25; }
  .pin.on { color: var(--g-gold); }
  .adv { display: flex; gap: 12px; align-items: flex-start; padding: 12px 4px 4px; }
  .adv input { width: 22px; height: 22px; margin-top: 2px; flex: none; accent-color: var(--g-teal); }
  .adv span { display: flex; flex-direction: column; gap: 2px; font-size: 14px; }
  .adv small { font-size: 12px; color: var(--g-muted); }
</style>
