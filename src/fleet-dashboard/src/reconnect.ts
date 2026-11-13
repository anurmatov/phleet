// Reconnecting, and timers that must not outlive what armed them. Shared by the chat stream and
// the agent/workflow socket. Kept free of JSX and `import.meta.env` so `node --test` can run it.

/** The pause before reconnect attempt `failures` (0-based): doubling from 1 s to 30 s, jittered. */
export function reconnectDelayMs(failures: number, random: number): number {
  const ceiling = Math.min(30_000, 1_000 * 2 ** Math.min(Math.max(failures, 0), 5))
  const fraction = Math.min(Math.max(random, 0), 1)
  return Math.round(ceiling / 2 + (ceiling / 2) * fraction)
}

/** A connection that stayed up this long resets the backoff; one that drops sooner does not. */
export const BACKOFF_RESET_MS = 60_000

/**
 * A socket closed. `failures` is the count carried so far, `openedAt` when this socket opened
 * (null if it never did). Answers the pause before the next attempt and the count to carry on:
 * a connection that held for {@link BACKOFF_RESET_MS} starts the backoff over, one that dropped
 * sooner (or never opened) keeps climbing.
 */
export function afterClose(
  failures: number, openedAt: number | null, now: number, random: number,
): { delayMs: number; failures: number } {
  const carried = openedAt !== null && now - openedAt >= BACKOFF_RESET_MS ? 0 : failures
  return { delayMs: reconnectDelayMs(carried, random), failures: carried + 1 }
}

type TimerHandle = ReturnType<typeof setTimeout>

/**
 * One timer per key. Setting a key again replaces its timer, so the timer a first click left
 * behind cannot fire into the state a second click made.
 *
 * That was the fault in the confirm buttons: each click started an anonymous five-second timer
 * that reset "confirm?" to idle, and a timer from an earlier click, or from an earlier result
 * message, expired a confirmation armed after it.
 */
export class KeyedTimers {
  private readonly pending = new Map<string, TimerHandle>()
  private readonly start: (fn: () => void, ms: number) => TimerHandle
  private readonly stop: (handle: TimerHandle) => void

  constructor(
    start: (fn: () => void, ms: number) => TimerHandle = (fn, ms) => setTimeout(fn, ms),
    stop: (handle: TimerHandle) => void = handle => clearTimeout(handle),
  ) {
    this.start = start
    this.stop = stop
  }

  /** Runs `fn` after `ms`, unless the key is set again or cancelled first. */
  after(key: string, ms: number, fn: () => void): void {
    this.cancel(key)
    const handle = this.start(() => {
      // Only the timer that still owns the key runs; one that was replaced has been stopped, and
      // this also covers a `stop` that could not prevent an already-queued callback.
      if (this.pending.get(key) !== handle) return
      this.pending.delete(key)
      fn()
    }, ms)
    this.pending.set(key, handle)
  }

  /** Drops the key's timer, if it has one. */
  cancel(key: string): void {
    const handle = this.pending.get(key)
    if (handle === undefined) return
    this.pending.delete(key)
    this.stop(handle)
  }

  /** Drops every timer: the owner is going away. */
  cancelAll(): void {
    for (const handle of this.pending.values()) this.stop(handle)
    this.pending.clear()
  }

  has(key: string): boolean {
    return this.pending.has(key)
  }
}
