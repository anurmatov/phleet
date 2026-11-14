import { useEffect, useRef, type ReactNode } from 'react'

interface ModalProps {
  /** Names the dialog to assistive technology. */
  label: string
  /**
   * The backdrop's and the panel's classes. Each dialog keeps the look it had: this component adds
   * behaviour, not styling.
   */
  overlayClassName: string
  className: string
  onClose: () => void
  children: ReactNode
}

/**
 * A dialog over the page: announced as one (`role="dialog"`), closed by Escape or a click on the
 * backdrop, and given focus on opening, which goes back where it was on closing.
 */
export default function Modal({ label, overlayClassName, className, onClose, children }: ModalProps) {
  const panel = useRef<HTMLDivElement>(null)
  // The listener below is attached once; it reads the latest `onClose` through this.
  const close = useRef(onClose)
  useEffect(() => { close.current = onClose })

  useEffect(() => {
    const previous = document.activeElement instanceof HTMLElement ? document.activeElement : null
    // A field inside that asked for focus keeps it.
    if (!panel.current?.contains(document.activeElement)) panel.current?.focus({ preventScroll: true })
    function onKey(e: KeyboardEvent) { if (e.key === 'Escape') close.current() }
    document.addEventListener('keydown', onKey)
    return () => {
      document.removeEventListener('keydown', onKey)
      previous?.focus({ preventScroll: true })
    }
  }, [])

  return (
    <div className={overlayClassName} onClick={onClose}>
      <div
        ref={panel}
        className={className}
        role="dialog"
        aria-modal="true"
        aria-label={label}
        tabIndex={-1}
        // The panel takes focus only so keys reach the dialog; it is not a control and shows no ring.
        style={{ outline: 'none' }}
        onClick={e => e.stopPropagation()}
      >
        {children}
      </div>
    </div>
  )
}
