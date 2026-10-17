/** One shared clock for every countdown and counter on screen; the host only sends elapsed times, the phone counts. */
class Tick {
  now = $state(Date.now())
  #timer: ReturnType<typeof setInterval> | null = null
  #users = 0

  /** Call from an $effect; returns the stop function, so the clock runs only while something shows a time. */
  use(): () => void {
    if (this.#users++ === 0) this.#timer = setInterval(() => (this.now = Date.now()), 250)
    this.now = Date.now()
    return () => {
      if (--this.#users === 0 && this.#timer) { clearInterval(this.#timer); this.#timer = null }
    }
  }
}

export const tick = new Tick()
