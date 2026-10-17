/** Every call to the host. A 401 means this browser is not paired: go to the sign-in page. */
export async function api(path: string, method = 'GET', body?: unknown): Promise<Response> {
  const r = await fetch('/api' + path, {
    method,
    headers: body === undefined ? {} : { 'Content-Type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body),
  })
  if (r.status === 401) {
    location.replace('/login.html')
    throw new Error('unauthorized')
  }
  return r
}

export async function getJson<T>(path: string): Promise<T> {
  const r = await api(path)
  if (!r.ok) throw new Error(await errorText(r))
  return r.json() as Promise<T>
}

/** Best readable message from a failed response: the host's {error}, or an LCU body, or the status. */
export async function errorText(r: Response): Promise<string> {
  try {
    const b = await r.json()
    if (typeof b?.error === 'string') return b.error
    if (typeof b?.message === 'string') return b.message
  } catch { /* not json */ }
  return `Request failed (${r.status})`
}

/** Runs an action; returns null on success or the error text. */
export async function act(path: string, method = 'POST', body?: unknown): Promise<string | null> {
  try {
    const r = await api(path, method, body)
    return r.ok ? null : await errorText(r)
  } catch (e) {
    return e instanceof Error && e.message === 'unauthorized' ? null : 'Cannot reach the PC.'
  }
}
