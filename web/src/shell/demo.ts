import type { NowState } from '../lib/now'
import type { Board, BoardPlayer } from '../lib/types'

/**
 * `?demo=<screen>`: shows one screen with made-up client state, for reviewing the design without a game running.
 * Only the client state is faked; settings, accounts and every button still talk to the real host (which refuses what makes no sense).
 */
export const DEMOS = ['idle', 'lobby', 'queue', 'match', 'match-auto', 'champ', 'champ-turn', 'champ-auto', 'champ-final', 'aram', 'mayhem', 'game', 'reconnect', 'honor'] as const
export type Demo = (typeof DEMOS)[number]

export function demoFromUrl(search: string): Demo | null {
  const d = new URLSearchParams(search).get('demo')
  return DEMOS.includes(d as Demo) ? (d as Demo) : null
}

const player = (cellId: number, championId: number, lane: string, pickTurn: number, over: Partial<BoardPlayer> = {}): BoardPlayer => ({
  cellId, championId, locked: true, lane, spell1Id: 4, spell2Id: 14, skinId: championId * 1000, name: '', isMe: false, pickTurn, ...over,
})

// In champ select the host sends the groups and their queue positions only: no names, no champions.
const anon = (puuid: string, lane: string, isMe = false) => ({ puuid, championId: 0, name: '', isMe, lane })
const champParties = { gameId: 1, parties: [{ ally: true, players: [anon('a', 'SUPPORT', true), anon('b', 'BOTTOM')] }, { ally: false, players: [anon('c', 'TOP'), anon('d', 'MIDDLE'), anon('e', 'SUPPORT')] }] }

const MY_BANS = [238, 777, 55, 360, 63], THEIR_BANS = [11, 84, 25, 67, 54]

function board(myTurn: boolean): Board {
  return {
    stage: 'BAN_PICK', myCellId: 4, queueId: 420, mode: 'draft', timeLeftMs: 24_000, pickTurnNow: myTurn ? 6 : 4,
    myTeam: [
      player(0, 86, 'TOP', 1, { name: 'Garen main#EUW' }), player(1, 64, 'JUNGLE', 3), player(2, 103, 'MIDDLE', 3),
      player(3, myTurn ? 222 : 0, 'BOTTOM', 6, { locked: myTurn }),
      player(4, 518, 'SUPPORT', 6, { isMe: true, locked: false, spell1Id: 4, spell2Id: 3, skinId: 518001 }),
    ],
    theirTeam: [player(5, 122, '', 2), player(6, 157, '', 2), player(7, myTurn ? 51 : 0, '', 4, { locked: myTurn }), player(8, myTurn ? 412 : 0, '', 4), player(9, 0, '', 5, { locked: false })],
    // A full ranked ban phase, one ban per player; mine (cell 4) is Brand, as in the support setup.
    myBans: MY_BANS, theirBans: THEIR_BANS, myPickActionId: 20,
    banList: [...MY_BANS.map((championId, cellId) => ({ cellId, championId, mine: true })), ...THEIR_BANS.map((championId, i) => ({ cellId: 5 + i, championId, mine: false }))],
    acting: myTurn ? [{ cellId: 3, type: 'pick' }, { cellId: 4, type: 'pick' }] : [{ cellId: 7, type: 'pick' }, { cellId: 8, type: 'pick' }],
    next: myTurn ? [] : [{ cellId: 9, type: 'pick' }],
    bench: [], rerolls: 0, allowRerolling: false,
    swaps: [
      { kind: 'position', id: 1, cellId: 0, state: 'RECEIVED' }, { kind: 'position', id: 2, cellId: 2, state: 'AVAILABLE' },
      { kind: 'pick-order', id: 3, cellId: 2, state: 'AVAILABLE' },
    ],
  }
}

/** The faked client state laid over what the host sent (connection and settings stay real). */
export function demoNow(d: Demo, s: NowState, at: number): NowState {
  const base: NowState = { ...s, connected: true, clientStage: 'connected', ready: null, readyResponse: 'None', readySince: null, honor: null, board: null, turn: null, swaps: [], parties: null,
    acceptAt: null, draftAt: null, loadout: null, myPick: null, gameStart: null, gameMode: '', dodge: null, outcome: '' }
  const lobby = { canStart: true, queueId: 420, showPositions: true, first: 'UTILITY', second: 'MIDDLE', members: 2,
    roster: [
      { puuid: 'me', summonerId: 1, isMe: true, isLeader: true, isBot: false, ready: true, iconId: 29, level: 212, first: 'UTILITY', second: 'MIDDLE', name: 'You#EUW' },
      { puuid: 'b', summonerId: 2, isMe: false, isLeader: false, isBot: false, ready: true, iconId: 4, level: 88, first: 'BOTTOM', second: 'FILL', name: 'Lee friend#EUW' },
    ] }
  switch (d) {
    case 'idle': return { ...base, phase: 'None', queue: 'None' }
    case 'lobby': return { ...base, phase: 'Lobby', lobby }
    case 'queue': return { ...base, phase: 'Matchmaking', lobby, queue: 'Searching', queueSince: at - 98_000, queueEstimate: 116 }
    case 'match': return { ...base, phase: 'ReadyCheck', lobby, ready: 'InProgress', readySince: at - 2_000, readyWindowMs: 12_000 }
    case 'match-auto': return { ...base, phase: 'ReadyCheck', lobby, ready: 'InProgress', readySince: at - 1_000, readyWindowMs: 12_000, acceptAt: at + 9_000 }
    case 'champ': {
      const b = board(false)
      return { ...base, phase: 'ChampSelect', lobby, stage: 'BAN_PICK', board: b, parties: champParties, swapNotes: ['Asked Mid for their lane'], swaps: [...b.swaps.filter((w) => w.state === 'RECEIVED'), { kind: 'pick-order', id: 5, cellId: 2, state: 'SENT' }] }
    }
    case 'champ-final': {
      const b = board(true)
      const all = (t: BoardPlayer[]) => t.map((p) => ({ ...p, locked: true, championId: p.championId || 266 }))
      // Locked: the automatic loadout counts down with Cancel (shown when the real matchupLoadout setting is on).
      return { ...base, phase: 'ChampSelect', lobby, stage: 'FINALIZATION', loadout: { state: 'waiting', enemyId: all(b.theirTeam)[0].championId, at: at + 5_000 }, board: { ...b, stage: 'FINALIZATION', myPickActionId: 0, acting: [], next: [], myTeam: all(b.myTeam), theirTeam: all(b.theirTeam),
        swaps: [{ kind: 'trade', id: 4, cellId: 2, state: 'AVAILABLE' }, { kind: 'pick-order', id: 3, cellId: 2, state: 'AVAILABLE' }, { kind: 'position', id: 2, cellId: 2, state: 'AVAILABLE' }] } }
    }
    case 'champ-turn': return { ...base, phase: 'ChampSelect', lobby, stage: 'BAN_PICK', board: board(true), turn: { actionId: 14, type: 'pick', stage: 'BAN_PICK' } }
    // Automation waiting to lock the champion it hovered: the countdown with Skip (the panel's row shows it only when the real lock setting is this champion).
    case 'champ-auto': return { ...base, phase: 'ChampSelect', lobby, stage: 'BAN_PICK', board: board(true), turn: { actionId: 14, type: 'pick', stage: 'BAN_PICK' }, draftAt: { type: 'pick', championId: 518, at: at + 15_000 } }
    case 'aram': {
      const b: Board = { ...board(false), mode: 'aram', queueId: 450, bench: [99, 412, 51, 157], rerolls: 1, allowRerolling: true, acting: [], next: [], swaps: [],
        myTeam: board(false).myTeam.map((p) => ({ ...p, lane: '', pickTurn: 1 })), theirTeam: [] }
      return { ...base, phase: 'ChampSelect', stage: 'BAN_PICK', board: b }
    }
    // Mayhem before my pick: the offered pair and a bench; `&tiers` gives u.gg-style tiers instead of win rates.
    case 'mayhem': {
      const b: Board = { ...board(false), mode: 'mayhem', queueId: 2400, bench: [99, 412, 157], offered: [267, 69], rerolls: 0, allowRerolling: false, acting: [], next: [], swaps: [],
        myTeam: board(false).myTeam.map((p) => ({ ...p, lane: '', pickTurn: 1, championId: p.isMe ? 0 : p.championId })), theirTeam: [] }
      return { ...base, phase: 'ChampSelect', stage: 'BAN_PICK', board: b, turn: { actionId: 6, type: 'pick', stage: 'BAN_PICK' } }
    }
    // As in a real game: the League client is gone, the phase and the game's own clock remain.
    case 'game': return { ...base, connected: false, clientStage: 'league-starting', phase: 'InProgress', lobby, myPick: { championId: 518, skinId: 518001, lane: 'SUPPORT', queueId: 420 }, gameStart: at - 1_122_000, gameMode: 'CLASSIC',
      parties: { gameId: 1, parties: [
        { ally: true, players: [{ puuid: 'a', championId: 518, name: 'Tidewren#EUW', isMe: true }, { puuid: 'b', championId: 222, name: 'Pellucid#EUW', isMe: false }] },
        { ally: false, players: [{ puuid: 'c', championId: 122, name: 'Red side#EUW', isMe: false }, { puuid: 'd', championId: 157, name: 'Their duo#EUW', isMe: false }, { puuid: 'e', championId: 412, name: 'Third#EUW', isMe: false }] },
      ] } }
    // After a crash: the client is back and waits on Reconnect.
    case 'reconnect': return { ...base, phase: 'Reconnect', lobby, myPick: { championId: 518, skinId: 518001, lane: 'SUPPORT', queueId: 420 }, gameStart: at - 1_122_000, gameMode: 'CLASSIC' }
    case 'honor': return {
      ...base, phase: 'PreEndOfGame', honor: {
        gameId: 1, votes: 3, staged: 1, candidates: [
          { puuid: 'a', summonerId: 1, name: 'Garen main#EUW', championId: 86, ally: true, isFriend: false, honor: null },
          { puuid: 'b', summonerId: 2, name: 'Lee friend#EUW', championId: 64, ally: true, isFriend: true, honor: 'COOL' },
          { puuid: 'c', summonerId: 3, name: '', championId: 103, ally: true, isFriend: false, honor: null },
          { puuid: 'd', summonerId: 4, name: 'Jinx friend#EUW', championId: 222, ally: true, isFriend: true, honor: null },
          { puuid: 'e', summonerId: 5, name: '', championId: 122, ally: false, isFriend: false, honor: null },
          { puuid: 'f', summonerId: 6, name: '', championId: 99, ally: false, isFriend: false, honor: null },
        ],
      },
    }
  }
}
