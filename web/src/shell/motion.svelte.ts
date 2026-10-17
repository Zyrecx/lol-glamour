/** The "reduce motion" switch: a per-device choice, so it lives in this browser only (the OS setting is followed by CSS on its own). */
class Motion {
  reduced = $state(read())

  set(on: boolean) {
    this.reduced = on
    try { localStorage.setItem('glamour.reduceMotion', on ? '1' : '0') } catch { /* private mode: this session only */ }
  }
}

function read(): boolean {
  try { return localStorage.getItem('glamour.reduceMotion') === '1' } catch { return false }
}

export const motion = new Motion()
