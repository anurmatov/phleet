import { useEffect, useState } from 'react'

/**
 * Re-renders the calling component every `ms`. For a component that shows time relative to now
 * ("3m ago", an uptime): nothing else re-renders it when only the clock has moved.
 */
export function useTick(ms = 5000): void {
  const [, setTick] = useState(0)
  useEffect(() => {
    const id = setInterval(() => setTick(t => t + 1), ms)
    return () => clearInterval(id)
  }, [ms])
}
