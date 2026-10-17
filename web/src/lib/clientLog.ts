/** Sends script errors to the PC's log file, so a broken screen on the phone shows up there too. Never throws, never redirects. */
export function installClientLog(target: Window = window): void {
  let sent = 0
  const send = (kind: string, message: string, stack?: string) => {
    if (++sent > 20) return // a page in an error loop: the first ones say enough
    fetch('/api/client-log', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ kind, message, stack, page: location.pathname + location.search }),
      keepalive: true,
    }).catch(() => { /* the PC is unreachable: nothing to report to */ })
  }
  target.addEventListener('error', (e) => send('error', e.message || String(e.error), e.error instanceof Error ? e.error.stack : `${e.filename}:${e.lineno}`))
  target.addEventListener('unhandledrejection', (e) => {
    const r = e.reason
    if (r instanceof Error && r.message === 'unauthorized') return // api() already went to the sign-in page
    send('unhandled promise', r instanceof Error ? r.message : String(r), r instanceof Error ? r.stack : undefined)
  })
}
