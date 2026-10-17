<script lang="ts">
  import { live } from '../live.svelte'
  import { overriddenKeys, SETTING_LABELS } from '../scope'
  import { label } from '../profileLabel'
  import Card from '../components/Card.svelte'

  const profiles = $derived(live.profiles?.profiles ?? [])
  const chosen = $derived(live.scopeProfile)
  const keys = $derived(overriddenKeys(chosen?.overrides))
  const active = $derived(profiles.find((p) => p.id === live.profiles?.activeId && live.profiles?.adding === false))
  const activeKeys = $derived(overriddenKeys(active?.overrides))
  let err = $state('')

  async function reset(list: string[]) {
    err = (await live.resetOverrides(list)) ?? ''
  }
</script>

{#if profiles.length > 0}
  <Card title="Settings for" hint="Shared by all accounts unless you pick one.">
    <label class="stack">
      <span class="sr-only">Whose settings to edit</span>
      <select value={chosen?.id ?? 'all'} onchange={(e) => (live.scope = e.currentTarget.value)}>
        <option value="all">All accounts (shared defaults)</option>
        {#each profiles as p (p.id)}<option value={p.id}>{label(p)} only</option>{/each}
      </select>
    </label>

    {#if chosen}
      {#if keys.length === 0}
        <p class="muted">{label(chosen)} has no settings of its own. A change below applies to {label(chosen)} only; the other accounts keep the defaults.</p>
      {:else}
        <p class="muted">Set for {label(chosen)} only. Reset one to follow the shared default again:</p>
        <ul>
          {#each keys as k (k)}
            <li><span>{SETTING_LABELS[k] ?? k}</span><button type="button" class="quiet" onclick={() => reset([k])}>Reset</button></li>
          {/each}
        </ul>
        {#if keys.length > 1}<button type="button" class="quiet" onclick={() => reset(keys)}>Reset all</button>{/if}
      {/if}
    {:else if active && activeKeys.length > 0}
      <p class="muted">{label(active)} (signed in now) has {activeKeys.length} {activeKeys.length === 1 ? 'setting' : 'settings'} of its own that these defaults do not change:
        {activeKeys.map((k) => SETTING_LABELS[k] ?? k).join(', ')}.</p>
    {/if}
    {#if err}<p class="err" role="alert">{err}</p>{/if}
  </Card>
{/if}

<style>
  ul { list-style: none; margin: 0; padding: 0; display: grid; gap: .4rem; }
  li { display: flex; justify-content: space-between; align-items: center; gap: .75rem; }
  li button { min-height: 2.25rem; padding: .3rem .8rem; }
</style>
