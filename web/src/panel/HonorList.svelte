<script lang="ts">
  // Who auto-honor votes for, in order: move up or down, remove, and add from the League friend list (no typing). The caller saves:
  // Now saves into the settings in effect, Auto through its "For" scope.
  import { friends } from './friends.svelte'

  let { list, here = [], busy = false, onsave }: { list: string[]; here?: string[]; busy?: boolean; onsave: (next: string[]) => void } = $props()

  const key = (f: string) => f.toLowerCase()
  // Duplicates (possible in a list typed on the phone) are shown and saved once.
  const items = $derived(list.map((f) => f.trim()).filter((f, i, all) => f && all.findIndex((x) => key(x) === key(f)) === i))
  const inParty = (f: string) => here.some((h) => key(h) === key(f))
  const short = (riotId: string) => riotId.split('#')[0]
  const tag = (riotId: string) => riotId.split('#')[1] ?? ''

  function move(i: number, by: -1 | 1) {
    const next = [...items]
    ;[next[i], next[i + by]] = [next[i + by], next[i]]
    onsave(next)
  }
  const remove = (i: number) => onsave(items.filter((_, n) => n !== i))
  const add = (riotId: string) => { onsave([...items, riotId]); adding = false }

  let adding = $state(false)
  $effect(() => { if (adding) friends.load() })
  const choices = $derived(friends.list.filter((f) => !items.some((i) => key(i) === key(f.riotId))))
</script>

<ol class="hl">
  {#each items as f, i (f)}
    <li class:here={inParty(f)}>
      <span class="n">{i + 1}</span>
      <span class="fname">{short(f)}<span class="tag">#{tag(f)}</span></span>
      {#if inParty(f)}<span class="in">In party</span>{/if}
      <span class="tools">
        <button type="button" class="t" disabled={busy || i === 0} onclick={() => move(i, -1)} aria-label={`Move ${short(f)} up`} title="Up">
          <svg width="10" height="10" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="3" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M6 15l6-6 6 6" /></svg>
        </button>
        <button type="button" class="t" disabled={busy || i === items.length - 1} onclick={() => move(i, 1)} aria-label={`Move ${short(f)} down`} title="Down">
          <svg width="10" height="10" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="3" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M6 9l6 6 6-6" /></svg>
        </button>
        <button type="button" class="t x" disabled={busy} onclick={() => remove(i)} aria-label={`Remove ${short(f)}`} title="Remove">
          <svg width="10" height="10" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="3" stroke-linecap="round" aria-hidden="true"><path d="M6 6l12 12M18 6L6 18" /></svg>
        </button>
      </span>
    </li>
  {:else}
    <li class="empty">Nobody yet</li>
  {/each}
</ol>
<div class="add">
  <button type="button" class="link" aria-expanded={adding} onclick={() => (adding = !adding)}>{adding ? 'Close' : '+ Add a friend'}</button>
  {#if adding}
    <ul class="pick" aria-label="Friends">
      {#if friends.error}<li class="msg">{friends.error}</li>
      {:else if !friends.loaded}<li class="msg">Loading…</li>
      {:else}
        {#each choices as f (f.riotId)}
          <li><button type="button" disabled={busy} onclick={() => add(f.riotId)}><span class="dot" class:on={f.online}></span>{short(f.riotId)}<span class="tag">#{tag(f.riotId)}</span></button></li>
        {:else}
          <li class="msg">Every friend is listed.</li>
        {/each}
      {/if}
    </ul>
  {/if}
</div>

<style>
  .hl { list-style: none; margin: 0; padding: 0; border-left: 1px solid var(--l-frame); }
  .hl li { display: flex; align-items: center; gap: 6px; min-height: 28px; padding-left: 8px; font-size: 12px; color: var(--l-grey1); }
  .n { flex: none; width: 12px; font-size: 10px; color: var(--l-grey2); text-align: right; }
  .fname { flex: 1; min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .tag { color: var(--l-grey2); }
  li.here .fname { color: var(--l-gold1); }
  .in { flex: none; font-size: 9px; font-weight: 700; letter-spacing: .08em; text-transform: uppercase; color: var(--l-blue); }
  .tools { flex: none; display: flex; gap: 2px; }
  .t { width: 20px; height: 20px; min-height: 0 !important; padding: 0 !important; display: grid; place-items: center; border: 1px solid transparent !important; background: none !important; color: var(--l-grey2) !important; cursor: pointer; }
  .t:hover:not(:disabled) { color: var(--l-gold1) !important; border-color: var(--l-grey3) !important; }
  .t:disabled { opacity: .3; cursor: default; }
  .t.x:hover:not(:disabled) { color: #F0B4B4 !important; }
  .empty { color: var(--l-grey2); font-style: italic; }
  .add { padding: 4px 0 2px 8px; }
  .link { min-height: 0 !important; padding: 2px 0 !important; border: 0 !important; background: none !important; color: var(--l-blue) !important; font: 700 11px var(--g-body) !important; letter-spacing: .04em !important; text-transform: none !important; cursor: pointer; }
  .pick { list-style: none; margin: 4px 0 0; padding: 0; max-height: 180px; overflow-y: auto; border: 1px solid var(--l-frame); scrollbar-width: thin; }
  .pick button { width: 100%; display: flex; align-items: center; gap: 6px; min-height: 26px !important; padding: 0 8px !important; border: 0 !important; background: none !important; text-align: left;
    font: 400 12px var(--g-body) !important; letter-spacing: 0 !important; text-transform: none !important; color: var(--l-gold1) !important; cursor: pointer; }
  .pick button:hover { background: rgba(200, 170, 110, .08) !important; }
  .dot { width: 6px; height: 6px; border-radius: 50%; flex: none; background: var(--l-grey3); }
  .dot.on { background: #1EB453; }
  .msg { padding: 6px 8px; font-size: 12px; color: var(--l-grey2); }
</style>
