// Web Push only: show the notification, and open Glamour when it is tapped.
// No fetch handler on purpose: nothing is cached, so "rebuild and reload" keeps working.
self.addEventListener('install', () => self.skipWaiting())
self.addEventListener('activate', (e) => e.waitUntil(self.clients.claim()))

self.addEventListener('push', (event) => {
  let d = {}
  try { d = event.data ? event.data.json() : {} } catch { /* not json */ }
  event.waitUntil(self.registration.showNotification(d.title || 'Glamour', {
    body: d.body || '',
    tag: d.tag || 'glamour',
    renotify: true,
    requireInteraction: !!d.urgent,
    data: { action: d.action || null },
    actions: d.action === 'accept' ? [{ action: 'accept', title: 'Accept' }]
      : d.action === 'cancel-auto-accept' ? [{ action: 'cancel-auto-accept', title: 'Cancel accept' }] : [],
    icon: '/icon-192.png',
    badge: '/icon-192.png',
  }))
})

const CALLS = { 'accept': '/api/ready-check/accept', 'cancel-auto-accept': '/api/ready-check/auto-accept/cancel' }

// A button tap calls the PC with the session cookie and shows the outcome; no window opens. If the PC cannot be reached, a notice says so and Glamour opens.
async function press(path) {
  try {
    const r = await fetch(path, { method: 'POST', credentials: 'same-origin' })
    if (r.ok) return true
  } catch { /* offline or PC asleep */ }
  return false
}

self.addEventListener('notificationclick', (event) => {
  event.notification.close()
  const path = CALLS[event.action]
  event.waitUntil((async () => {
    if (path) {
      if (await press(path)) return
      await self.registration.showNotification('Could not reach Glamour', { body: 'Open the app to answer.', tag: 'glamour-action-failed' })
    }
    const all = await self.clients.matchAll({ type: 'window', includeUncontrolled: true })
    const open = all.find((c) => c.url.startsWith(self.location.origin))
    if (open) return open.focus()
    return self.clients.openWindow('/')
  })())
})
