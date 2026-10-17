import type { Board, Turn } from './types'

/** The action a hover or lock goes to: my live turn, else my own unfinished pick (hovering works on it before my turn). Null when there is none. */
export function pickSlot(turn: Turn | null | undefined, board: Board): { actionId: number; type: string } | null {
  return turn ?? (board.myPickActionId ? { actionId: board.myPickActionId, type: 'pick' } : null)
}

/** Champions that cannot be chosen any more, with the reason shown to the user: banned, or locked by someone. */
export function takenMap(board: Board): Map<number, string> {
  const m = new Map<number, string>()
  for (const id of [...board.myBans, ...board.theirBans, ...(board.banList ?? []).map((b) => b.championId)]) if (id > 0) m.set(id, 'Banned')
  for (const p of [...board.myTeam, ...board.theirTeam]) if (p.locked && p.championId > 0 && !m.has(p.championId)) m.set(p.championId, 'Picked')
  return m
}

/** Locking works only on a live BAN_PICK turn of mine. */
export const canLock = (board: Board, turn: Turn | null | undefined) => board.stage === 'BAN_PICK' && !!turn && turn.type === 'pick'

/** What the pick list depends on, so the pickable ids are asked again only when something that changes them changed. */
export function takenKey(board: Board, slot: { actionId: number; type: string } | null): string {
  return `${slot?.actionId}:${slot?.type}:${board.banList?.length ?? board.myBans.length + board.theirBans.length}:${[...board.myTeam, ...board.theirTeam].filter((p) => p.locked).length}`
}
