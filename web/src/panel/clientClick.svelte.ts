// Clicks on the League client's own champ select roster, passed on by the tray app (SPEC "Click layer on the client's roster").
// `seq` counts clicks on enemy cards, `ownSeq` on my own card, so a second click on the same card still reads as new.
export const clientClick = $state({ enemySlot: -1, seq: 0, ownSeq: 0 })

/** Takes one message from the tray app; true when it was a roster click. */
export function onClientMessage(data: unknown): boolean {
  if (data === 'own') { clientClick.ownSeq++; return true }
  if (typeof data !== 'string' || !data.startsWith('face:')) return false
  const n = Number(data.slice(5))
  if (!Number.isInteger(n) || n < 0) return false
  clientClick.enemySlot = n
  clientClick.seq++
  return true
}
