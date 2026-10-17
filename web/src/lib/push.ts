import { act, getJson } from './api'

/** Web Push needs a secure page (HTTPS or localhost), a service worker and the Push API. Plain-HTTP phones get ntfy only. */
export function pushSupported(): boolean {
  return typeof window !== 'undefined' && window.isSecureContext && 'serviceWorker' in navigator && 'PushManager' in window && 'Notification' in window
}

function b64urlToBytes(s: string): Uint8Array<ArrayBuffer> {
  const pad = '='.repeat((4 - (s.length % 4)) % 4)
  const raw = atob((s + pad).replace(/-/g, '+').replace(/_/g, '/'))
  const out = new Uint8Array(new ArrayBuffer(raw.length))
  for (let i = 0; i < raw.length; i++) out[i] = raw.charCodeAt(i)
  return out
}

function bytesToB64url(buf: ArrayBuffer | null): string {
  if (!buf) return ''
  let s = ''
  for (const b of new Uint8Array(buf)) s += String.fromCharCode(b)
  return btoa(s).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '')
}

export interface PushStatus { subscribed: boolean; devices: number }

export const pushStatus = () => getJson<PushStatus>('/push/status')

/** Subscribes this browser and tells the PC. Returns an error message, or null. */
export async function subscribeThisDevice(): Promise<string | null> {
  if (!pushSupported()) return 'Web push needs Glamour installed as an app.'
  try {
    if ((await Notification.requestPermission()) !== 'granted') return 'Notifications are blocked. Allow them in the browser settings.'
    const reg = await navigator.serviceWorker.register('/sw.js')
    await navigator.serviceWorker.ready
    const { key } = await getJson<{ key: string }>('/push/key')
    const existing = await reg.pushManager.getSubscription()
    // A subscription made with another key (the PC's keys were reset) cannot be reused.
    if (existing) await existing.unsubscribe()
    const sub = await reg.pushManager.subscribe({ userVisibleOnly: true, applicationServerKey: b64urlToBytes(key) })
    return await act('/push/subscription', 'PUT', {
      endpoint: sub.endpoint,
      p256dh: bytesToB64url(sub.getKey('p256dh')),
      auth: bytesToB64url(sub.getKey('auth')),
    })
  } catch (e) {
    return e instanceof Error ? e.message : 'Could not subscribe.'
  }
}

/** Forgets this browser's subscription, here and on the PC. */
export async function unsubscribeThisDevice(): Promise<string | null> {
  try {
    const reg = await navigator.serviceWorker.getRegistration('/')
    await (await reg?.pushManager.getSubscription())?.unsubscribe()
  } catch { /* nothing to drop locally */ }
  return act('/push/subscription', 'DELETE')
}
