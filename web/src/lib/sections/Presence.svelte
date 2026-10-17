<script lang="ts">
  import { live } from '../live.svelte'
  import Card from '../components/Card.svelte'
  import Toggle from '../components/Toggle.svelte'
  import Segmented from '../components/Segmented.svelte'
  import RankEmblem from '../components/RankEmblem.svelte'
  import Icon from '../components/Icon.svelte'
  import { TIERS, isApex, tierTitle } from '../rank'

  const s = $derived(live.view)
  let msg = $state('')
  let draft = $state<string | null>(null)
  const status = $derived(draft ?? s?.statusMessageOverride ?? '')

  const DIVISIONS = ['I', 'II', 'III', 'IV'].map((d) => ({ value: d, label: d }))
  const QUEUES = [{ value: 'RANKED_SOLO_5x5', label: 'Solo/Duo' }, { value: 'RANKED_FLEX_SR', label: 'Flex' }, { value: 'RANKED_TFT', label: 'TFT' }]
  const tierAt = $derived(Math.max(0, TIERS.indexOf(s?.rankTier ?? '')))
  const apex = $derived(isApex(s?.rankTier ?? ''))
  // Apex tiers carry division I (as real Master+ ranks do); a tier without a division picks I too.
  const stepTier = (to: string) => save(isApex(to) || s?.rankDivision === 'NA' ? { rankTier: to, rankDivision: 'I' } : { rankTier: to })

  async function save(change: Parameters<typeof live.patch>[0], done = '') {
    const err = await live.patch(change)
    msg = err ?? done
    return !err
  }
  async function saveStatus() {
    const text = status.trim()
    if (await save({ statusMessageOverride: text === '' ? null : text }, text === '' ? 'Status override off.' : 'Saved.')) draft = null
  }
</script>

<Card title="Rank and status" hint="Changes what friends see. Your real rank is untouched.">
  {#if s}
    <Toggle label="Show a different rank" checked={s.rankOverrideEnabled} onchange={(v) => save({ rankOverrideEnabled: v })} />
    {#if s.rankOverrideEnabled}
      <div class="tier">
        <button type="button" class="quiet step" aria-label="Lower tier" disabled={tierAt === 0} onclick={() => stepTier(TIERS[tierAt - 1])}><Icon name="left" /></button>
        <div class="crest">
          <RankEmblem tier={s.rankTier} size={120} />
          <b>{tierTitle(s.rankTier)}{apex || s.rankDivision === 'NA' ? '' : ` ${s.rankDivision}`}</b>
        </div>
        <button type="button" class="quiet step" aria-label="Higher tier" disabled={tierAt === TIERS.length - 1} onclick={() => stepTier(TIERS[tierAt + 1])}><Icon name="right" /></button>
      </div>
      {#if !apex}<Segmented label="Division" options={DIVISIONS} value={s.rankDivision} onchange={(v) => save({ rankDivision: v })} />{/if}
      <Segmented label="Queue" options={QUEUES} value={s.rankQueue} onchange={(v) => save({ rankQueue: v })} />
    {/if}
    <label class="stack">
      <span class="muted">Status message (empty leaves yours alone)</span>
      <input type="text" maxlength="200" value={status} oninput={(e) => (draft = e.currentTarget.value)} />
    </label>
    <div class="row">
      <span class="muted" role="status">{msg}</span>
      <button type="button" disabled={draft === null} onclick={saveStatus}>Save status</button>
    </div>
  {/if}
</Card>

<style>
  .row { display: flex; gap: .6rem; align-items: end; flex-wrap: wrap; }
  /* Fixed side columns keep the emblem centred and the row inside the card at panel width. */
  .tier { display: grid; grid-template-columns: 2.25rem minmax(0, 1fr) 2.25rem; align-items: center; gap: .5rem; }
  .crest { display: grid; justify-items: center; gap: .35rem; min-width: 0; }
  .crest b { font-size: 1.05rem; letter-spacing: .04em; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; max-width: 100%; }
  .step { min-height: 0; width: 2.25rem; height: 2.25rem; padding: 0; border-radius: 50%; display: grid; place-items: center; }
  .step :global(svg) { width: 1.1rem; height: 1.1rem; }
  .step:disabled { visibility: hidden; }
</style>
