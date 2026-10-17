<script lang="ts">
  import { asset } from '../assets'
  import { live } from '../live.svelte'
  import { summonerNames } from '../summonerNames.svelte'
  import Card from '../components/Card.svelte'
  import type { LobbyMember } from '../types'

  const roster = $derived(live.now.phase === 'Lobby' || live.now.phase === 'Matchmaking' ? (live.now.lobby.roster ?? []) : [])
  const LANES: Record<string, string> = { TOP: 'Top', JUNGLE: 'Jungle', MIDDLE: 'Mid', BOTTOM: 'Bot', UTILITY: 'Support', FILL: 'Fill' }

  const names = $derived(summonerNames.byId)
  $effect(() => summonerNames.ensure(roster.filter((m) => !m.isBot).map((m) => m.summonerId)))

  // Kick and promote touch another player: leader only, never while queued, and a second tap confirms.
  const iLead = $derived(roster.some((m) => m.isMe && m.isLeader) && live.now.phase === 'Lobby')
  let armed = $state<{ puuid: string; verb: 'kick' | 'promote' } | null>(null)
  let err = $state('')
  let busy = $state(false)
  async function member(m: LobbyMember, verb: 'kick' | 'promote') {
    if (armed?.puuid !== m.puuid || armed.verb !== verb) { armed = { puuid: m.puuid, verb }; return }
    busy = true
    armed = null
    err = (await live.act(`/lobby/members/${encodeURIComponent(m.puuid)}/${verb}`)) ?? ''
    busy = false
  }
  const isArmed = (m: LobbyMember, verb: string) => armed?.puuid === m.puuid && armed.verb === verb

  const nameOf = (m: LobbyMember, n: number) => (m.isBot ? 'Bot' : m.name || names[m.summonerId] || (m.isMe ? 'You' : `Player ${n + 1}`))
  const lanes = (m: LobbyMember) => [m.first, m.second].filter((x) => x && x !== 'UNSELECTED').map((x) => LANES[x] ?? x).join(' / ')
</script>

{#if roster.length > 0}
  <Card title="Party" hint={roster.length === 1 ? 'Just you' : `${roster.length} in the lobby`}>
    <ul>
      {#each roster as m, n (m.puuid || n)}
        <li class:me={m.isMe}>
          {#if m.iconId}<img src={asset(`/lol-game-data/assets/v1/profile-icons/${m.iconId}.jpg`)} alt="" loading="lazy" />{:else}<span class="blank" aria-hidden="true"></span>{/if}
          <span class="who">
            <span class="nm">{nameOf(m, n)}{m.isMe && names[m.summonerId] ? ' (you)' : ''}</span>
            <span class="sub muted">{m.level ? `Level ${m.level}` : ''}{lanes(m) ? ` · ${lanes(m)}` : ''}</span>
          </span>
          {#if m.isLeader}<span class="tag">Leader</span>{/if}
          {#if m.ready}<span class="tag ok">Ready</span>{/if}
          {#if iLead && !m.isMe && !m.isBot}
            <button type="button" disabled={busy} onclick={() => member(m, 'promote')}>{isArmed(m, 'promote') ? 'Sure?' : 'Make leader'}</button>
            <button type="button" class="danger" disabled={busy} onclick={() => member(m, 'kick')}>{isArmed(m, 'kick') ? 'Sure?' : 'Kick'}</button>
          {/if}
        </li>
      {/each}
    </ul>
    {#if err}<p class="err" role="alert">{err}</p>{/if}
  </Card>
{/if}

<style>
  ul { list-style: none; margin: 0; padding: 0; display: grid; gap: .35rem; }
  li { display: flex; align-items: center; gap: .6rem; padding: .35rem .5rem; border-radius: var(--radius-sm); background: var(--card2); min-height: 3rem; }
  li.me { background: #35244b; }
  img, .blank { width: 2.2rem; height: 2.2rem; border-radius: 50%; flex: none; }
  .blank { background: #0f0a16; }
  .who { display: grid; min-width: 0; line-height: 1.2; flex: 1; }
  .nm { font-weight: 600; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .sub { font-size: .78rem; }
  .tag { font-size: .75rem; color: var(--warn); }
  button { padding: .3rem .55rem; font-size: .8rem; }
  .err { color: var(--bad, #e66); margin: .4rem 0 0; }
  .tag.ok { color: var(--ok); }
</style>
