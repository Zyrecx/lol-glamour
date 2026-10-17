import type { Profile } from './types'

/** What an account is called on screen: its Riot ID (Name#Tag), else the saved name. */
export const label = (p: Pick<Profile, 'name' | 'riotId'>) => p.riotId || p.name

// League platform ids as players name their server.
const SERVERS: Record<string, string> = {
  BR1: 'BR', EUN1: 'EUNE', EUW1: 'EUW', JP1: 'JP', KR: 'KR', LA1: 'LAN', LA2: 'LAS', ME1: 'ME', NA1: 'NA', OC1: 'OCE',
  PH2: 'PH', RU: 'RU', SG2: 'SG', TH2: 'TH', TR1: 'TR', TW2: 'TW', VN2: 'VN',
}

/** An account's server as players say it (EUN1 -> EUNE); unknown ids as they are, none as null. */
export const server = (region: string | null | undefined) => (region ? (SERVERS[region.toUpperCase()] ?? region) : null)
