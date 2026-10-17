<script lang="ts">
  // A bottom sheet over a dimmed screen. Closes on the scrim, Escape, a downward drag (from the handle, or from anywhere once the content is
  // scrolled to its top), or its own controls.
  import type { Snippet } from 'svelte'
  let { label, onclose, children, tall = false }: { label: string; onclose: () => void; children: Snippet; tall?: boolean } = $props()

  let dy = $state(0)
  let dragging = $state(false)

  const CLOSE_PX = 110, FLICK_PX_MS = 0.6

  // Touch events (not pointer events): the browser cancels pointer events the moment it starts scrolling the content.
  function drag(el: HTMLElement) {
    let startY = 0, startT = 0, active = false, armed = false
    const onstart = (e: TouchEvent) => {
      const t = e.target as HTMLElement
      armed = el.scrollTop <= 0 || !!t.closest('.grab-zone')
      startY = e.touches[0].clientY; startT = e.timeStamp; active = false
    }
    const onmove = (e: TouchEvent) => {
      if (!armed) return
      const d = e.touches[0].clientY - startY
      if (!active) {
        if (d <= 6) { if (d < -6) armed = false; return }
        active = true; dragging = true
      }
      e.preventDefault()
      dy = Math.max(0, d)
    }
    const onend = (e: TouchEvent) => {
      if (!active) { armed = false; return }
      const fast = dy / Math.max(1, e.timeStamp - startT) > FLICK_PX_MS
      active = false; armed = false; dragging = false
      if (dy > CLOSE_PX || fast) onclose(); else dy = 0
    }
    el.addEventListener('touchstart', onstart, { passive: true })
    el.addEventListener('touchmove', onmove, { passive: false })
    el.addEventListener('touchend', onend)
    el.addEventListener('touchcancel', onend)
    return { destroy() {
      el.removeEventListener('touchstart', onstart); el.removeEventListener('touchmove', onmove)
      el.removeEventListener('touchend', onend); el.removeEventListener('touchcancel', onend)
    } }
  }

  // Fixed overlays inside an animated tab are trapped under the tab bar; move to the shell root while open.
  function portal(node: HTMLElement) {
    const root = node.closest('.glam') ?? document.body
    root.appendChild(node)
    return { destroy() { node.remove() } }
  }
</script>

<svelte:window onkeydown={(e) => { if (e.key === 'Escape') onclose() }} />

<div class="layer" use:portal>
<button type="button" class="scrim" aria-label="Close" onclick={onclose} style:opacity={dy ? Math.max(0.3, 1 - dy / 400) : undefined}></button>
<div class="sheet rise" class:tall class:dragging use:drag style:translate={dy ? `0 ${dy}px` : undefined} role="dialog" aria-modal="true" aria-label={label}>
  <div class="grab-zone"><span class="grab" aria-hidden="true"></span></div>
  {@render children()}
</div>
</div>

<style>
  .scrim { position: fixed; inset: 0; z-index: var(--z-sheet); background: rgba(3, 6, 12, .72); border: 0; padding: 0; min-height: 0; border-radius: 0; cursor: pointer; }
  .scrim:hover { filter: none; }
  .sheet {
    position: fixed; left: 0; right: 0; bottom: 0; z-index: calc(var(--z-sheet) + 1); max-width: 34rem; margin-inline: auto; max-height: 86dvh; overflow-y: auto; overscroll-behavior: contain;
    background: var(--g-panel); border-top: 1px solid rgba(200, 170, 110, .45); border-radius: 18px 18px 0 0;
    padding: 0 16px calc(20px + env(safe-area-inset-bottom)); display: flex; flex-direction: column; gap: 10px; transition: translate .2s ease-out;
  }
  .sheet.dragging { transition: none; }
  .sheet.tall { top: 8dvh; max-height: none; }
  /* A tall touch target around the thin handle; it stays at the top while the content scrolls. */
  .grab-zone { position: sticky; top: 0; z-index: 1; display: flex; justify-content: center; padding: 12px 0 8px; margin: 0 -16px; background: var(--g-panel); flex: none; touch-action: none; cursor: grab; }
  .grab { width: 44px; height: 5px; border-radius: 3px; background: rgba(200, 170, 110, .6); }
</style>
