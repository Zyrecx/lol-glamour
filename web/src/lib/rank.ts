import type { RankInfo } from './types'

export const TIERS = ['IRON', 'BRONZE', 'SILVER', 'GOLD', 'PLATINUM', 'EMERALD', 'DIAMOND', 'MASTER', 'GRANDMASTER', 'CHALLENGER']
/** Master and up have no divisions. */
export const isApex = (tier: string) => TIERS.indexOf(tier) >= TIERS.indexOf('MASTER')
export const tierColor: Record<string, string> = {
  IRON: '#7b6f68', BRONZE: '#b07a4f', SILVER: '#aeb8c4', GOLD: '#e0b44f', PLATINUM: '#3fc5b3',
  EMERALD: '#2ec27e', DIAMOND: '#6aa7ff', MASTER: '#c070ff', GRANDMASTER: '#ff6a6a', CHALLENGER: '#7fe8ff',
}
export const tierTitle = (t: string) => t.charAt(0) + t.slice(1).toLowerCase()
export const tierName = (r: RankInfo) => tierTitle(r.tier) + (r.division && r.division !== 'NA' ? ` ${r.division}` : '')
export const rankText = (r: RankInfo | null | undefined) => (r ? `${tierName(r)} · ${r.lp} LP` : null)
