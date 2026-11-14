import { useEffect, useRef, useState } from 'react'
import { browserClipboard, copyText } from '../clipboard'
import Icon from './Icon'

/**
 * A small button that puts text on the clipboard and says whether it went. It never claims
 * "Copied" for a copy the browser refused.
 */
export default function CopyButton({ text, label, className }: { text: string; label: string; className?: string }) {
  const [state, setState] = useState<'idle' | 'copied' | 'failed'>('idle')
  const timer = useRef<number | null>(null)

  useEffect(() => () => { if (timer.current !== null) window.clearTimeout(timer.current) }, [])

  async function copy() {
    const went = await copyText(text, browserClipboard())
    setState(went ? 'copied' : 'failed')
    if (timer.current !== null) window.clearTimeout(timer.current)
    timer.current = window.setTimeout(() => setState('idle'), 1800)
  }

  const said = state === 'copied' ? 'Copied' : state === 'failed' ? 'Could not copy' : label
  return (
    <button type="button" className={`copy-btn${state !== 'idle' ? ` copy-btn--${state}` : ''}${className ? ` ${className}` : ''}`}
      onClick={() => void copy()} title={said} aria-label={label}>
      <Icon name={state === 'copied' ? 'check' : state === 'failed' ? 'warning' : 'copy'} size={14} />
      <span className="copy-btn-said" role="status">{state === 'idle' ? '' : said}</span>
    </button>
  )
}
