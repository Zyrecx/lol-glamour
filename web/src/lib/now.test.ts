import { describe as suite, expect, it } from 'vitest'
import { applyEvent, clientStatus, initialNow, type NowState } from './now'

const snapshot = (over: Record<string, unknown> = {}, state: Record<string, unknown> = {}) => ({
  connected: true, phase: 'None',
  state: {
    queue: { state: 'None' }, readyCheck: { state: 'Invalid' },
    champSelect: { stage: '', turn: null, swaps: [] }, dodge: { state: 'Invalid' }, ...state,
  },
  ...over,
})
const feed = (s: NowState, ...events: [string, unknown][]) => events.reduce((acc, [t, d]) => applyEvent(acc, t, d), s)

suite('clientStatus', () => {
  it('tells apart nothing, Riot Client only, League starting and connected', () => {
    const at = (clientStage: 'none' | 'riot-client' | 'league-starting' | 'connected', connected = false) => clientStatus({ connected, clientStage }).text
    expect(at('none')).toBe('Riot Client not running')
    expect(at('riot-client')).toBe('Riot Client open, League not started')
    expect(at('league-starting')).toBe('League client starting')
    expect(at('connected', true)).toBe('League connected')
  })
  it('follows connection events', () => {
    const s = applyEvent(initialNow(), 'connection', { connected: false, stage: 'riot-client' })
    expect(s.clientStage).toBe('riot-client')
    expect(applyEvent(s, 'connection', { connected: true, stage: 'connected' }).connected).toBe(true)
  })
})

suite('applyEvent', () => {
  it('keeps who accepted, and counts from elapsed times on this clock', () => {
    const s = applyEvent(initialNow(), 'snapshot', snapshot({ phase: 'ReadyCheck' }, {
      readyCheck: { state: 'InProgress', playerResponse: 'None', elapsedMs: 3000, windowMs: 12000 },
    }), 100_000)
    expect(s.readySince).toBe(97_000)
    expect(s.readyWindowMs).toBe(12000)
    const accepted = applyEvent(s, 'ready-check', { state: 'InProgress', playerResponse: 'Accepted', elapsedMs: 4000, windowMs: 12000 }, 101_000)
    expect(accepted.readyResponse).toBe('Accepted')
    const q = applyEvent(initialNow(), 'queue', { state: 'Searching', queueId: 420, estimatedQueueTime: 90, elapsedMs: 40_000 }, 100_000)
    expect(q.queueSince).toBe(60_000)
    expect(applyEvent(q, 'queue', { state: 'None', elapsedMs: 0 }).queueSince).toBeNull()
  })

  it('turns the game clock into a local start time', () => {
    const s = applyEvent(initialNow(), 'game', { inGame: true, gameTimeMs: 393_000, gameMode: 'CLASSIC' }, 1_000_000)
    expect(s).toMatchObject({ gameStart: 607_000, gameMode: 'CLASSIC' })
    expect(applyEvent(s, 'game', { inGame: false, gameTimeMs: 0, gameMode: '' }).gameStart).toBeNull()
  })

  it('follows the pause and a waiting auto-accept', () => {
    const s = applyEvent(initialNow(), 'automation', { paused: true, acceptPending: { timeLeftMs: 3000 } }, 50_000)
    expect(s).toMatchObject({ paused: true, acceptAt: 53_000 })
    expect(applyEvent(s, 'automation', { paused: false, acceptPending: null })).toMatchObject({ paused: false, acceptAt: null })
  })

  it('follows a waiting ban or lock and what was skipped', () => {
    const s = applyEvent(initialNow(), 'automation', { draftPending: { type: 'ban', championId: 238, timeLeftMs: 12_000 }, draftSkipped: [] }, 50_000)
    expect(s.draftAt).toEqual({ type: 'ban', championId: 238, at: 62_000 })
    expect(applyEvent(s, 'automation', { draftPending: null, draftSkipped: ['ban'] })).toMatchObject({ draftAt: null, draftSkipped: ['ban'] })
  })

  it('follows the automatic loadout from its countdown to done or cancelled', () => {
    const s = applyEvent(initialNow(), 'automation', { loadout: { state: 'waiting', enemyId: 238, timeLeftMs: 5000 } }, 50_000)
    expect(s.loadout).toEqual({ state: 'waiting', enemyId: 238, at: 55_000 })
    expect(applyEvent(s, 'automation', { loadout: { state: 'cancelled', enemyId: 238, timeLeftMs: null } }).loadout).toEqual({ state: 'cancelled', enemyId: 238, at: null })
    expect(applyEvent(s, 'automation', {}).loadout).toBeNull()
  })

  it('lists the swaps automation made, none when the host sends none', () => {
    expect(applyEvent(initialNow(), 'automation', { swapNotes: ['Asked Mid for their lane'] }).swapNotes).toEqual(['Asked Mid for their lane'])
    expect(applyEvent(initialNow(), 'automation', {}).swapNotes).toEqual([])
  })

  it('keeps the ballot while there is one and closes it on gameId 0', () => {
    const open = applyEvent(initialNow(), 'honor', { gameId: 7, votes: 1, staged: 0, candidates: [] })
    expect(open.honor?.gameId).toBe(7)
    expect(applyEvent(open, 'honor', { gameId: 0, votes: 0, staged: 0, candidates: [] }).honor).toBeNull()
  })

  it('keeps the phase and the search state from the snapshot', () => {
    expect(feed(initialNow(), ['snapshot', snapshot({ phase: 'Matchmaking' })]).phase).toBe('Matchmaking')
    expect(feed(initialNow(), ['snapshot', snapshot({}, { queue: { state: 'Searching' } })]).queue).toBe('Searching')
  })

  it('tracks my turn and clears it when the turn ends', () => {
    const base = feed(initialNow(), ['snapshot', snapshot({ phase: 'ChampSelect' }, { champSelect: { stage: 'BAN_PICK', turn: null, swaps: [] } })])
    expect(base).toMatchObject({ stage: 'BAN_PICK', turn: null })
    const mine = feed(base, ['myturn', { actionId: 3, type: 'ban', stage: 'BAN_PICK' }])
    expect(mine.turn).toMatchObject({ actionId: 3, type: 'ban' })
    expect(feed(mine, ['myturn-end', null]).turn).toBeNull()
  })

  it('keeps received swaps of any kind', () => {
    let s = feed(initialNow(), ['snapshot', snapshot({ phase: 'ChampSelect' })])
    s = feed(s, ['swap', { kind: 'position', id: 7, cellId: 2, state: 'RECEIVED' }], ['swap', { kind: 'trade', id: 9, cellId: 4, state: 'RECEIVED' }])
    expect(s.swaps.map((w) => `${w.kind}:${w.id}`)).toEqual(['position:7', 'trade:9'])
  })

  it('drops a swap once it is accepted', () => {
    let s = feed(initialNow(), ['snapshot', snapshot({ phase: 'ChampSelect' })])
    s = feed(s, ['swap', { kind: 'position', id: 7, cellId: 2, state: 'RECEIVED' }], ['swap', { kind: 'position', id: 7, cellId: 2, state: 'ACCEPTED' }])
    expect(s.swaps).toHaveLength(0)
  })

  it('keeps a dodge and forgets it when the next champ select starts', () => {
    let s = feed(initialNow(), ['snapshot', snapshot()])
    s = feed(s, ['dodge', { state: 'PartyDodged', byMe: false }])
    expect(s.dodge).toMatchObject({ state: 'PartyDodged', byMe: false })
    s = feed(s, ['phase', { phase: 'ChampSelect' }])
    expect(s.dodge).toBeNull()
  })

  it('ignores an Invalid dodge in the snapshot', () => {
    expect(feed(initialNow(), ['snapshot', snapshot()]).dodge).toBeNull()
  })

  it('keeps how champ select ended', () => {
    const s = feed(initialNow(), ['snapshot', snapshot()], ['champselect-end', { outcome: 'game-start' }])
    expect(s.outcome).toBe('game-start')
  })

  it('keeps the game phase when the League client goes away at game start', () => {
    const s = feed(initialNow(), ['snapshot', snapshot({ phase: 'InProgress' })], ['connection', { connected: false, stage: 'league-starting' }])
    expect(s).toMatchObject({ connected: false, phase: 'InProgress' })
  })

  it('ignores unknown events', () => {
    const s = initialNow()
    expect(applyEvent(s, 'settings', {})).toBe(s)
  })
})

suite('invitations', () => {
  const inv = { invitationId: 'a', fromName: 'Pellucid', queueId: 440, canAccept: true }
  it('come from the snapshot and later events', () => {
    const s = feed(initialNow(), ['snapshot', snapshot({}, { invitations: [inv] })])
    expect(s.invitations).toEqual([inv])
    expect(feed(s, ['invitations', []]).invitations).toEqual([])
  })
  it('default to none for a host that predates them', () => {
    expect(feed(initialNow(), ['snapshot', snapshot()]).invitations).toEqual([])
  })
  it('track the lobby: who may start, which queue, my lanes', () => {
    const lobby = { canStart: false, queueId: 440, showPositions: true, first: 'TOP', second: 'MIDDLE', members: 3 }
    const s = feed(initialNow(), ['snapshot', snapshot({}, { lobby })])
    expect(s.lobby.canStart).toBe(false)
    expect(s.lobby.first).toBe('TOP')
    expect(feed(s, ['lobby', { ...lobby, canStart: true }]).lobby.canStart).toBe(true)
  })
})

suite('board', () => {
  const board = { stage: 'BAN_PICK', myCellId: 0, myTeam: [], theirTeam: [], myBans: [], theirBans: [238], acting: [], timeLeftMs: 9000 }

  it('is empty before champ select and comes from the snapshot when joining mid-draft', () => {
    expect(initialNow().board).toBeNull()
    expect(feed(initialNow(), ['snapshot', snapshot({}, { champSelect: { stage: 'BAN_PICK', turn: null, swaps: [], board } })]).board).toEqual(board)
  })

  it('follows board events and is cleared when champ select starts or ends', () => {
    const s = feed(initialNow(), ['snapshot', snapshot()], ['board', board])
    expect(s.board?.theirBans).toEqual([238])
    expect(feed(s, ['champselect-end', { outcome: 'ended' }]).board).toBeNull()
    expect(feed(s, ['champselect-start', { queueId: 400 }]).board).toBeNull()
  })
})

suite('swap requests', () => {
  const req = { kind: 'pick-order', id: 3, cellId: 1, state: 'RECEIVED' }

  it('drops the request when it ends, however it ends', () => {
    for (const end of ['AVAILABLE', 'INVALID', 'ACCEPTED', 'CANCELLED', 'GONE']) {
      const s = feed(initialNow(), ['swap', req], ['swap', { ...req, state: end }])
      expect(s.swaps).toEqual([])
    }
  })

  it('keeps several live requests and replaces one by kind and id', () => {
    const s = feed(initialNow(), ['swap', req], ['swap', { ...req, id: 4 }], ['swap', { ...req, id: 3, state: 'AVAILABLE' }])
    expect(s.swaps.map((x) => x.id)).toEqual([4])
  })
})
