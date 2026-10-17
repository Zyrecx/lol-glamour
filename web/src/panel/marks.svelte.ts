// The marks the tray app draws on the client's own enemy cards (SPEC "Click layer on the client's roster"), and the panel's half of the band
// that runs from the chosen enemy's ring into the counter list. The tray app draws the ring and the band up to the client's edge; the page draws
// the rest, so no window ever sits over the panel. Both halves run one lap of the same period when the enemy I face changes, so the light meets.

/** One turn of the light, in ms (`RosterOverlay.Period`); its first 5% is the crossing from the ring to the client's edge (`BandShare`, in MarksBand's keyframes). */
export const PERIOD = 3000

/** From the tray app: whether the client's enemy cards are marked, and where its portraits sit in this page (css px from the top: first centre, row step). */
export const marks = $state({ on: false, y0: 0, step: 0 })

/** From the champ select section: the client card of the enemy I face (-1: none) and where the counter list is (null: not shown). */
export const band = $state<{ slot: number; target: { left: number; top: number; bottom: number } | null }>({ slot: -1, target: null })

/** Takes one message from the tray app; true when it was about the marks. */
export function onMarksMessage(data: unknown): boolean {
  if (typeof data !== 'string' || !data.startsWith('marks:')) return false
  const rest = data.slice(6)
  if (rest === 'off') { marks.on = false; return true }
  const [y0, step] = rest.split(',').map(Number)
  if (!Number.isFinite(y0) || !Number.isFinite(step)) return true
  marks.on = true
  marks.y0 = y0
  marks.step = step
  return true
}

/**
 * The band inside the panel: from the panel's left edge at the ring's height, over the rail, into the counter list's left edge. It enters the list at
 * the same height when it can (then it is a straight line), else at the nearest point a little inside the list.
 */
export function bandPath(startY: number, target: { left: number; top: number; bottom: number }): { link: string; entry: number } {
  const inset = Math.min(14, (target.bottom - target.top) / 2)
  const entry = Math.min(Math.max(startY, target.top + inset), target.bottom - inset)
  const x = target.left
  if (Math.abs(entry - startY) < 0.5) return { link: `M 0 ${r(startY)} H ${r(x)}`, entry }
  const bend = x * 0.5
  return { link: `M 0 ${r(startY)} C ${r(bend)} ${r(startY)} ${r(x - bend)} ${r(entry)} ${r(x)} ${r(entry)}`, entry }
}

const r = (v: number) => Math.round(v * 10) / 10

/**
 * The "tags:" message: my champion's win rate on each enemy card, by cell (the client's top-to-bottom order). Only enemies my matchups list (picked
 * enemies who play my lane, and the one I face) get one; "~" marks few games. Empty items for the rest, all empty before any enemy is picked.
 */
export function cardTags(theirTeam: { cellId: number; championId: number }[], mine: { picked: boolean; lines: { championId: number; winRate: number; fewGames: boolean }[] } | null | undefined): string {
  const items = [...theirTeam].sort((a, b) => a.cellId - b.cellId).map((p) => {
    const l = mine?.picked && p.championId ? mine.lines.find((x) => x.championId === p.championId) : undefined
    return l ? `${l.winRate.toFixed(1)}${l.fewGames ? '~' : ''}` : ''
  })
  return `tags:${items.join(',')}`
}

/** The enemy's card in the client: its place among the enemies by cell (the client's top-to-bottom order), -1 when not picked. */
export function slotOf(theirTeam: { cellId: number; championId: number }[], championId: number): number {
  if (!championId) return -1
  return [...theirTeam].sort((a, b) => a.cellId - b.cellId).findIndex((p) => p.championId === championId)
}
