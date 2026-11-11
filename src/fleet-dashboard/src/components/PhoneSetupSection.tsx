import { useEffect, useMemo, useState } from 'react'
import { AUTH_TOKEN } from '../utils'
import {
  emptyPhoneSetupForm, phoneSetupCode, phoneSetupProblemMessage, type PhoneSetupForm,
} from '../phoneSetup'
import { qrMatrix } from '../qrMatrix'

const label: React.CSSProperties = { fontSize: 11, color: 'var(--muted)', display: 'block', marginBottom: 2 }
const note: React.CSSProperties = { fontSize: 12, color: 'var(--muted)', margin: '6px 0' }

/** How long a code stays on screen before it hides itself. */
const VISIBLE_SECONDS = 120

/**
 * Connect a phone: a QR code the iOS app scans to set up its control-plane connection.
 *
 * Nothing here is sent anywhere or stored. The code is built in the page from values the page
 * already holds (its address and the operator token it was built with) plus an access-proxy service
 * token the operator pastes in, and it is drawn straight to the screen. It hides itself after two
 * minutes because, with the operator token in it, a photograph of it is full control of the fleet.
 */
export default function PhoneSetupSection() {
  const [form, setForm] = useState<PhoneSetupForm | null>(null)
  const [code, setCode] = useState<string | null>(null)
  const [error, setError] = useState('')

  // Hide on a timer, and whenever the form closes.
  useEffect(() => {
    if (code === null) return
    const timer = setTimeout(() => setCode(null), VISIBLE_SECONDS * 1000)
    return () => clearTimeout(timer)
  }, [code])

  const matrix = useMemo(() => (code === null ? null : qrMatrix(code)), [code])

  function change(patch: Partial<PhoneSetupForm>) {
    // Any edit invalidates the code on screen: it no longer matches the form.
    setForm(current => (current ? { ...current, ...patch } : current))
    setCode(null)
    setError('')
  }

  function show() {
    if (!form) return
    const result = phoneSetupCode(form, AUTH_TOKEN)
    if ('problem' in result) { setError(phoneSetupProblemMessage(result.problem)); setCode(null); return }
    setError('')
    setCode(result.code)
  }

  function close() {
    setForm(null)
    setCode(null)
    setError('')
  }

  return (
    <section className="cred-section">
      <div className="cred-section-header">
        <div className="cred-section-title">Phone</div>
        {form === null && (
          <button className="cred-action-btn" onClick={() => setForm(emptyPhoneSetupForm(window.location.origin))}>
            Connect a phone
          </button>
        )}
      </div>

      {form === null ? (
        <p style={note}>Show a setup code for the iOS app to scan.</p>
      ) : (
        <div style={{ maxWidth: 520 }}>
          <div style={{ marginBottom: 8 }}>
            <label style={label} htmlFor="phone-setup-origin">Address the phone will use</label>
            <input id="phone-setup-origin" className="config-input" style={{ width: '100%' }}
              value={form.origin} onChange={e => change({ origin: e.target.value })}
              autoComplete="off" spellCheck={false} />
          </div>

          <div style={{ display: 'flex', gap: 8, marginBottom: 8 }}>
            <div style={{ flex: 1 }}>
              <label style={label} htmlFor="phone-setup-client-id">Access client ID (optional)</label>
              <input id="phone-setup-client-id" className="config-input" style={{ width: '100%' }}
                value={form.accessClientId} onChange={e => change({ accessClientId: e.target.value })}
                autoComplete="off" spellCheck={false} />
            </div>
            <div style={{ flex: 1 }}>
              <label style={label} htmlFor="phone-setup-client-secret">Access client secret (optional)</label>
              <input id="phone-setup-client-secret" className="config-input" style={{ width: '100%' }} type="password"
                value={form.accessClientSecret} onChange={e => change({ accessClientSecret: e.target.value })}
                autoComplete="off" spellCheck={false} />
            </div>
          </div>
          <p style={note}>
            Only if this dashboard sits behind an access proxy. Use a service token created for that
            phone alone, so it can be revoked on its own.
          </p>

          <label style={{ fontSize: 12, display: 'flex', gap: 6, alignItems: 'center', margin: '8px 0' }}>
            <input type="checkbox" checked={form.includeOperatorToken}
              onChange={e => change({ includeOperatorToken: e.target.checked })} />
            Include the operator token (without it the phone can only read)
          </label>

          {error && <p style={{ ...note, color: 'var(--danger, #c0392b)' }} role="alert">{error}</p>}

          <div style={{ display: 'flex', gap: 8, margin: '8px 0' }}>
            {code === null
              ? <button className="config-save-btn" onClick={show}>Show code</button>
              : <button className="config-save-btn" onClick={() => setCode(null)}>Hide code</button>}
            <button className="cred-action-btn" onClick={close}>Close</button>
          </div>

          {matrix && (
            <div>
              <QrCode matrix={matrix} />
              <p style={note} role="status">
                {form.includeOperatorToken
                  ? 'This code contains the operator token. Anyone who photographs it can control the fleet.'
                  : 'This code lets a phone read the fleet. It contains no operator token.'}
                {' '}It hides itself after two minutes.
              </p>
            </div>
          )}
        </div>
      )}
    </section>
  )
}

/** Drawn as one path of squares on white, with the four-module quiet zone a reader needs. */
function QrCode({ matrix }: { matrix: boolean[][] }) {
  const quiet = 4
  const size = matrix.length + quiet * 2
  const path = matrix
    .flatMap((row, y) => row.map((dark, x) => (dark ? `M${x + quiet} ${y + quiet}h1v1h-1z` : '')))
    .join('')

  return (
    <svg viewBox={`0 0 ${size} ${size}`} width={320} height={320} role="img"
      aria-label="Setup code for the phone" shapeRendering="crispEdges"
      style={{ background: '#fff', borderRadius: 6, display: 'block' }}>
      <rect width={size} height={size} fill="#fff" />
      <path d={path} fill="#000" />
    </svg>
  )
}
