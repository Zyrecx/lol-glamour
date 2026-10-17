<script lang="ts">
  // One live screen whose content follows the client's gameflow phase. Match found, champ select and honor take over from Shell.
  import { live } from '../../lib/live.svelte'
  import Idle from './Idle.svelte'
  import InGame from './InGame.svelte'
  import Lobby from './Lobby.svelte'
  import Queue from './Queue.svelte'

  const GAME = ['GameStart', 'InProgress', 'Reconnect', 'WaitingForStats', 'PreEndOfGame', 'EndOfGame']
  const view = $derived.by(() => {
    const n = live.now
    // The League client closes at game start, so a game shows while disconnected: the phase stays, and the game clock comes from the game itself.
    if (GAME.includes(n.phase) || n.gameStart) return 'game'
    if (!n.connected) return 'idle'
    if (n.phase === 'Matchmaking' || n.phase === 'ReadyCheck' || n.queue === 'Searching') return 'queue'
    if ((n.phase === 'Lobby' || n.phase === 'ChampSelect') && n.lobby.members > 0) return 'lobby'
    return 'idle'
  })
</script>

{#key view}
  {#if view === 'game'}<InGame />{:else if view === 'queue'}<Queue />{:else if view === 'lobby'}<Lobby />{:else}<Idle />{/if}
{/key}
