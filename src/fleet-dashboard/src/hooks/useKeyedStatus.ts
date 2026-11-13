import { useCallback, useState } from 'react'
import type { KeyedTimers } from '../reconnect'

// The two state machines the dashboard repeats for every action on every row:
//
//   useKeyedStatus   idle → pending → success | error → idle (by timer)
//   useKeyedConfirm  "click again to confirm", which disarms itself
//
// Both are keyed (one per agent, per instruction, per workflow) and both return to rest on a
// KeyedTimers timer, so a timer left by an earlier click on the same key cannot end a later one.

export interface KeyedStatus<S extends string> {
  states: Record<string, S>
  messages: Record<string, string>
  /** Sets the key's status now and drops its pending return to rest. `message` is kept when omitted. */
  set: (key: string, state: S, message?: string) => void
  /** Sets a result and returns the key to `rest` after `ms`. */
  settle: (key: string, state: S, message: string | undefined, ms: number) => void
}

export function useKeyedStatus<S extends string>(timers: KeyedTimers, scope: string, rest: S): KeyedStatus<S> {
  const [states, setStates] = useState<Record<string, S>>({})
  const [messages, setMessages] = useState<Record<string, string>>({})

  const write = useCallback((key: string, state: S, message?: string) => {
    setStates(prev => ({ ...prev, [key]: state }))
    if (message !== undefined) setMessages(prev => ({ ...prev, [key]: message }))
  }, [])

  const set = useCallback((key: string, state: S, message?: string) => {
    timers.cancel(`${scope}:${key}`)
    write(key, state, message)
  }, [timers, scope, write])

  const settle = useCallback((key: string, state: S, message: string | undefined, ms: number) => {
    write(key, state, message)
    timers.after(`${scope}:${key}`, ms, () => setStates(prev => ({ ...prev, [key]: rest })))
  }, [timers, scope, rest, write])

  return { states, messages, set, settle }
}

export interface KeyedConfirm {
  armed: Record<string, boolean>
  /** Arms the key; it disarms itself after `ms`. */
  arm: (key: string) => void
  disarm: (key: string) => void
}

export function useKeyedConfirm(timers: KeyedTimers, scope: string, ms = 5000): KeyedConfirm {
  const [armed, setArmed] = useState<Record<string, boolean>>({})

  const disarm = useCallback((key: string) => {
    timers.cancel(`${scope}:${key}`)
    setArmed(prev => prev[key] ? { ...prev, [key]: false } : prev)
  }, [timers, scope])

  const arm = useCallback((key: string) => {
    setArmed(prev => ({ ...prev, [key]: true }))
    timers.after(`${scope}:${key}`, ms, () => setArmed(prev => prev[key] ? { ...prev, [key]: false } : prev))
  }, [timers, scope, ms])

  return { armed, arm, disarm }
}
