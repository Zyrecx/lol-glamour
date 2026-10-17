<script lang="ts">
  // Post-game honor, over any tab. Follows the host's `honor` event, so votes staged in the client show here too, and it closes by itself
  // when the ballot closes (submitted here or in the client). One honor per player, like the client: the hidden categories all reach the
  // player as the same honor (SPEC "Manual honor"), so it sends COOL, the type proven to arrive, like auto-honor.
  import { champions } from '../../lib/champions.svelte'
  import { live } from '../../lib/live.svelte'
  import type { HonorBallot, HonorCandidate } from '../../lib/types'

  let { ballot, onclose }: { ballot: HonorBallot; onclose: () => void } = $props()
  const left = $derived(Math.max(0, ballot.votes - ballot.staged))
  const groups = $derived([
    { title: 'Your team', list: ballot.candidates.filter((c) => c.ally) },
    { title: 'Opponents', list: ballot.candidates.filter((c) => !c.ally) },
  ])
  let busy = $state(false)
  let err = $state('')

  $effect(() => { champions.ensure() })

  async function vote(c: HonorCandidate) {
    busy = true
    err = (await live.act('/honor/vote', 'POST', { puuid: c.puuid, honorType: 'COOL' })) ?? ''
    busy = false
  }
  async function submit() {
    busy = true
    err = (await live.act('/honor/ballot')) ?? ''
    busy = false
    // Close on success instead of waiting for the ballot's Delete event: live, the phone stayed open after the client had closed (INSIGHTS "Post-game").
    if (!err) onclose()
  }
  async function skip() {
    busy = true
    err = (await live.act('/honor/skip')) ?? ''
    busy = false
    onclose() // closes here even when the client's screen had already gone
  }
  const nameOf = (c: HonorCandidate) => c.name || `Player #${c.summonerId}`
</script>

<div class="take" role="dialog" aria-modal="true" aria-label="Honor">
  <div class="glow" aria-hidden="true"></div>
  <div class="col">
    <div class="head">
      <div class="hrow">
        <span class="label gold grow">Post-game</span>
        <button type="button" class="link" disabled={busy} onclick={skip}>Skip</button>
      </div>
      <span class="display title">Honor</span>
      <div class="hrow">
        <span class="pips">{#each Array(Math.max(ballot.votes, ballot.staged)) as _, i (i)}<span class="pip" class:on={i < ballot.staged}></span>{/each}</span>
        <span class="num left">{left} of {ballot.votes} honor{ballot.votes === 1 ? '' : 's'} left</span>
      </div>
      <span class="muted small">Closes by itself when you submit here or in the client.</span>
    </div>

    <div class="list">
      {#each groups as g (g.title)}
        {#if g.list.length}
          <span class="label grp">{g.title}</span>
          {#each g.list as c (c.puuid)}
            {@const champ = champions.byId(c.championId)}
            {@const on = c.honor !== null}
            {@const dim = !on && left === 0}
            <div class="row">
              {#if champ}<img src={champ.icon} alt="" />{:else}<span class="blank"></span>{/if}
              <div class="who">
                <span class="champ">{champ?.name ?? 'Unknown'}</span>
                <span class="nm">{#if c.isFriend}<span class="tag">FRIEND</span>{/if}{nameOf(c)}</span>
              </div>
              <button type="button" class="hb" class:on class:dim aria-label={`Honor ${nameOf(c)}`} aria-pressed={on} disabled={busy || dim} onclick={() => vote(c)}>
                <svg width="20" height="20" viewBox="0 0 24 24" fill={on ? 'currentColor' : 'none'} stroke="currentColor" stroke-width="2" stroke-linejoin="round" aria-hidden="true"><path d="M12 20s-7-4.4-7-10a4 4 0 0 1 7-2.6A4 4 0 0 1 19 10c0 5.6-7 10-7 10z" /></svg>
              </button>
            </div>
          {/each}
        {/if}
      {/each}
    </div>

    <div class="foot">
      <button type="button" class="btn" class:btn-primary={ballot.staged > 0} class:btn-wait={ballot.staged === 0}
        disabled={busy || ballot.staged === 0} onclick={submit}>
        {`Submit ${ballot.staged} honor${ballot.staged === 1 ? '' : 's'}`}
      </button>
      {#if err}<p class="err" role="alert">{err}</p>{/if}
    </div>
  </div>
</div>

<style>
  .take { position: fixed; inset: 0; z-index: 35; background: var(--g-bg); overflow-y: auto; }
  .glow { position: absolute; inset: 0 0 auto 0; height: 200px; background: radial-gradient(ellipse at 50% 0%, rgba(45,226,196,.18), rgba(226,79,166,.06) 50%, transparent 75%); }
  .col { position: relative; max-width: 34rem; margin-inline: auto; min-height: 100%; display: flex; flex-direction: column; }
  .head { display: flex; flex-direction: column; gap: 6px; padding: calc(18px + env(safe-area-inset-top)) 16px 10px; }
  .hrow { display: flex; align-items: center; gap: 10px; }
  .grow { flex: 1; }
  .gold { color: var(--g-gold); }
  .link { background: transparent; color: var(--g-teal); font: 600 14px var(--g-body); padding: 0 4px; min-height: 44px; }
  .title { font-size: 30px; letter-spacing: .05em; }
  .pips { display: flex; gap: 4px; }
  .pip { width: 22px; height: 6px; border-radius: 3px; background: rgba(166,159,143,.3); }
  .pip.on { background: var(--g-teal); }
  .left { font-size: 14px; color: var(--g-fg2); }
  .small { font-size: 12.5px; }
  .list { flex: 1; padding: 0 16px; display: flex; flex-direction: column; gap: 2px; }
  .grp { padding: 8px 0 2px; }
  .row { display: grid; grid-template-columns: 38px 1fr 44px; align-items: center; gap: 6px 10px; min-height: 52px; }
  .row img, .blank { width: 38px; height: 38px; border-radius: 7px; border: 1px solid rgba(200,170,110,.4); background: var(--g-panel); }
  .who { display: flex; flex-direction: column; min-width: 0; }
  .champ { font-weight: 700; font-size: 14.5px; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
  .nm { font-size: 12px; color: var(--g-muted); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .tag { font-size: 10px; letter-spacing: .12em; font-weight: 700; color: var(--g-gold); border: 1px solid rgba(200,170,110,.5); border-radius: 4px; padding: 0 4px; margin-right: 5px; }
  .hb { width: 44px; height: 44px; min-height: 0; padding: 0; border-radius: 10px; border: 1px solid var(--g-line); background: rgba(13,20,32,.9); color: var(--g-muted); display: flex; align-items: center; justify-content: center; }
  .hb:hover:not(:disabled) { border-color: var(--g-gold); color: var(--g-fg); filter: none; }
  .hb.on { background: rgba(45,226,196,.18); border-color: var(--g-teal); color: var(--g-teal); opacity: 1; }
  .hb.dim { opacity: .3; }
  .foot { position: sticky; bottom: 0; padding: 12px 16px calc(18px + env(safe-area-inset-bottom)); border-top: 1px solid rgba(200,170,110,.25); background: var(--g-bg); }
</style>
