import { getJson } from './api'
import type { AuthStatus } from './types'

class Auth {
  status = $state<AuthStatus | null>(null)

  async load() {
    this.status = await getJson<AuthStatus>('/auth/status')
  }

  /** True only on the PC itself. Pairing, password setup and the firewall are PC-side jobs the host also refuses elsewhere. */
  get isPc() { return this.status?.loopback ?? false }
}

export const auth = new Auth()
