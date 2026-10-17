<script lang="ts">
  import { getJson } from '../api'
  import { live } from '../live.svelte'
  import Card from '../components/Card.svelte'
  import type { OpenParty } from '../types'
  import { queueNames } from '../queues.svelte'

  let parties = $state<OpenParty[]>([])
  let busy = $state(false)
  let err = $state('')

  // Joining is offered only outside a lobby: the client does not switch parties for you.
  const idle = $derived(live.now.connected && live.now.phase === 'None')
  const invites = $derived(live.now.invitations)
  const label = (q: number) => queueNames.label(q) || 'a party'

  async function refresh() {
    try { parties = await getJson<OpenParty[]>('/party/open') } catch { /* keep the last list */ }
  }
  $effect(() => {
    if (!idle) { parties = []; return }
    queueNames.ensure()
    refresh()
    const t = setInterval(refresh, 10_000)
    return () => clearInterval(t)
  })

  async function run(path: string) {
    busy = true
    err = (await live.act(path, 'POST')) ?? ''
    busy = false
    if (!err) refresh()
  }
</script>

{#if invites.length > 0 || (idle && parties.length > 0)}
  <Card title="Join a party" hint="Friends' open parties and invitations.">
    {#each invites as i (i.invitationId)}
      <div class="item">
        <span><strong>{i.fromName}</strong> invited you<span class="muted"> · {label(i.queueId)}</span></span>
        <span class="btns">
          <button type="button" disabled={busy || !i.canAccept} onclick={() => run(`/invitations/${encodeURIComponent(i.invitationId)}/accept`)}>Accept</button>
          <button type="button" class="quiet" disabled={busy} onclick={() => run(`/invitations/${encodeURIComponent(i.invitationId)}/decline`)}>Decline</button>
        </span>
      </div>
    {/each}
    {#if idle}
      {#each parties as p (p.partyId)}
        <div class="item">
          <span><strong>{p.friendName}</strong><span class="muted"> · {label(p.queueId)} · {p.members}/{p.maxPlayers}</span></span>
          <button type="button" disabled={busy} onclick={() => run(`/party/${encodeURIComponent(p.partyId)}/join`)}>Join</button>
        </div>
      {/each}
    {/if}
    {#if err}<p class="err" role="alert">{err}</p>{/if}
  </Card>
{/if}

<style>
  .item { display: flex; align-items: center; justify-content: space-between; gap: .75rem; flex-wrap: wrap; }
  .btns { display: flex; gap: .5rem; }
</style>
