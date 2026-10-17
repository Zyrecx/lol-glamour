<script lang="ts">
  // The PC's front page: what the client is doing right now, read only (acting stays in the League client), then the automation switch.
  // In game the League client is gone, so the game shows from the phase that stays and the game's own clock, never from `connected`.
  import { champions } from '../../lib/champions.svelte'
  import { live } from '../../lib/live.svelte'
  import { clientStatus, SWAP_NAMES } from '../../lib/now'
  import { queueNames } from '../../lib/queues.svelte'
  import AutomationCard from '../../shell/parts/AutomationCard.svelte'
  import Parties from '../../shell/parts/Parties.svelte'
  import { playable } from '../../shell/playable.svelte'
  import { tick } from '../../shell/tick.svelte'
  import { art, clock, laneName } from '../../shell/util'
  import Board from '../parts/Board.svelte'

  const GAME = ['GameStart', 'InProgress', 'Reconnect', 'WaitingForStats', 'PreEndOfGame', 'EndOfGame']
  const now = $derived(live.now)
  const view = $derived.by(() => {
    if (GAME.includes(now.phase) || now.gameStart) return 'game'
    if (!now.connected) return 'off'
    if (now.phase === 'ChampSelect' && now.board) return 'champ'
    if (now.phase === 'ReadyCheck' && now.ready === 'InProgress') return 'match'
    if (now.phase === 'Matchmaking' || now.queue === 'Searching') return 'queue'
    if (now.phase === 'Lobby') return 'lobby'
    return 'idle'
  })

  const queueName = (id: number) => playable.byId(id)?.name ?? queueNames.label(id)
  const lanes = $derived([now.lobby.first, now.lobby.second].filter((l) => l && l !== 'UNSELECTED' && l !== 'FILL').map(laneName).filter((v, i, a) => v && a.indexOf(v) === i))
  const post = $derived(['WaitingForStats', 'PreEndOfGame', 'EndOfGame'].includes(now.phase))
  const pick = $derived(now.myPick)
  const champ = $derived(champions.byId(pick?.championId ?? null))

  const head = $derived.by((): { title: string; sub: string } => {
    switch (view) {
      case 'game': return { title: post ? 'Game over' : 'In game', sub: [champ ? `${champ.name}${pick?.lane ? ` · ${laneName(pick.lane)}` : ''}` : '', pick ? queueName(pick.queueId) : ''].filter(Boolean).join(' · ') }
      case 'off': return { title: clientStatus(now).text, sub: '' }
      case 'champ': return { title: 'Champ select', sub: queueName(now.board!.queueId) }
      case 'match': return { title: 'Match found', sub: now.readyResponse === 'Accepted' ? 'Accepted. Waiting for the others.' : now.readyResponse === 'Declined' ? 'Declined.' : queueName(now.lobby.queueId) }
      case 'queue': return { title: 'In queue', sub: queueName(now.lobby.queueId) }
      case 'lobby': return { title: 'In lobby', sub: [queueName(now.lobby.queueId), now.lobby.members > 1 ? `party of ${now.lobby.members}` : '', lanes.join(' / ')].filter(Boolean).join(' · ') }
      default: return { title: 'Ready when you are', sub: '' }
    }
  })

  // Swap requests are answered in the client (or on the phone); here they are only named.
  const asks = $derived(now.swaps.filter((w) => w.state === 'RECEIVED').map((w) => {
    const p = now.board?.myTeam.find((x) => x.cellId === w.cellId)
    const who = p?.name?.split('#')[0] || champions.byId(p?.championId ?? 0)?.name || 'A teammate'
    return `${who} asks for a ${SWAP_NAMES[w.kind] ?? 'swap'}.`
  }))

  const queued = $derived(now.queueSince ? tick.now - now.queueSince : 0)
  const gameTime = $derived(now.gameStart ? tick.now - now.gameStart : 0)
  const splash = $derived(art(champ, pick?.skinId ?? 0, 'portrait'))
  $effect(() => tick.use())
  $effect(() => { playable.ensure(now.connected); queueNames.ensure(); champions.ensure() })
</script>

<header class="page-head">
  <span class="eyebrow">Right now</span>
  <h1 class="display">{head.title}</h1>
  {#if head.sub}<p>{head.sub}</p>{/if}
</header>

{#if view === 'champ' && now.board}
  <Board board={now.board} />
  {#each asks as a (a)}<p class="ask" role="status">{a}</p>{/each}
{:else if view === 'queue'}
  <section class="panel big">
    <span class="num clock" aria-label="Time in queue">{clock(queued)}</span>
    {#if now.queueEstimate > 0}<span class="muted">Estimated {clock(now.queueEstimate * 1000)}</span>{/if}
  </section>
{:else if view === 'game'}
  <div class="game">
    {#if splash || now.gameStart || !post}
    <section class="panel big">
      {#if splash}<img class="portrait" src={splash} alt="" />{/if}
      <div class="gtxt">
        {#if now.gameStart}<span class="num clock" aria-label="Game time">{clock(gameTime)}</span>
        {:else if !post}<span class="muted">The game clock shows once the game has loaded.</span>{/if}
      </div>
    </section>
    {/if}
    {#if now.parties}<Parties parties={now.parties} />{/if}
  </div>
{/if}

<AutomationCard />

<style>
  .big { display: flex; align-items: center; gap: 18px; }
  .clock { font-size: 40px; font-weight: 700; }
  .game { display: grid; grid-template-columns: minmax(0, 260px) minmax(0, 1fr); gap: 14px; align-items: start; }
  .game .big { flex-direction: column; align-items: center; }
  .portrait { width: 140px; height: 254px; object-fit: cover; border-radius: 6px; border: 1px solid var(--g-gold); }
  .gtxt { display: flex; flex-direction: column; align-items: center; gap: 4px; text-align: center; }
  .ask { color: var(--g-magenta); font-weight: 600; }
  @media (max-width: 760px) { .game { grid-template-columns: minmax(0, 1fr); } }
</style>
