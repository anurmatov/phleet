import { useCallback, useEffect, useMemo, useState } from 'react'
import { apiFetch, heartbeatAge } from '../utils'
import {
  copyPhoneSetupCode, emptyPhoneSetupForm, phoneSetupCode, phoneSetupFormProblem, phoneSetupProblemMessage,
  DEVICE_NAME_MAX_LENGTH, type PhoneSetupForm,
} from '../phoneSetup'
import { qrMatrix } from '../qrMatrix'
import { deviceTitle, readDevices, type DeviceRow } from '../session'

const label: React.CSSProperties = { fontSize: 11, color: 'var(--muted)', display: 'block', marginBottom: 2 }
const note: React.CSSProperties = { fontSize: 12, color: 'var(--muted)', margin: '6px 0' }

/** How long a setup code stays on screen before it hides itself. */
const VISIBLE_SECONDS = 120

/** While a phone is being connected, how often the list is read again so the phone shows up. */
const CONNECTING_REFRESH_MS = 4000

/**
 * Devices: every browser and app signed in to this fleet, with a way to revoke each, and
 * "Connect a phone".
 *
 * Connecting a phone asks the orchestrator for a one-time enrollment code and draws it, with the
 * address the phone will use, as a QR code for the iOS app to scan. The code registers one device,
 * once, within fifteen minutes; nothing long-lived is ever on screen. It still hides itself after
 * two minutes, because until it is used a photograph of it is a way in.
 */
export default function DevicesSection({ onToast }: { onToast?: (message: string, ok: boolean) => void }) {
  const [devices, setDevices] = useState<DeviceRow[] | null>(null)
  const [listError, setListError] = useState('')
  const [form, setForm] = useState<PhoneSetupForm | null>(null)
  const [code, setCode] = useState<string | null>(null)
  const [error, setError] = useState('')
  const [busy, setBusy] = useState(false)
  const [confirming, setConfirming] = useState<string | null>(null)
  // Which code a copy was asked for, and whether the browser allowed it. Kept with the code it
  // was about, so a new code on screen starts as not copied without anything having to reset it.
  const [copyResult, setCopyResult] = useState<{ code: string; allowed: boolean } | null>(null)

  const load = useCallback(async () => {
    try {
      const response = await apiFetch('/api/auth/devices')
      if (!response.ok) { setListError(`Devices could not be read (HTTP ${response.status}).`); return }
      setDevices(readDevices(await response.json()))
      setListError('')
    } catch {
      setListError('Devices could not be read.')
    }
  }, [])

  useEffect(() => { void load() }, [load])

  // While the connect form is open the list is re-read, so the phone appears when it enrolls.
  useEffect(() => {
    if (form === null) return
    const timer = setInterval(() => { void load() }, CONNECTING_REFRESH_MS)
    return () => clearInterval(timer)
  }, [form, load])

  // Hide the code on a timer.
  useEffect(() => {
    if (code === null) return
    const timer = setTimeout(() => setCode(null), VISIBLE_SECONDS * 1000)
    return () => clearTimeout(timer)
  }, [code])

  const copied = copyResult === null || copyResult.code !== code ? 'no' : copyResult.allowed ? 'yes' : 'refused'

  async function copy() {
    if (code === null) return
    const shown = code
    setCopyResult({ code: shown, allowed: await copyPhoneSetupCode(shown, navigator.clipboard) })
  }

  const matrix = useMemo(() => (code === null ? null : qrMatrix(code)), [code])

  function change(patch: Partial<PhoneSetupForm>) {
    // Any edit invalidates the code on screen: it no longer matches the form.
    setForm(current => (current ? { ...current, ...patch } : current))
    setCode(null)
    setError('')
  }

  async function show() {
    if (!form || busy) return
    // Checked first, so a form that cannot make a code never costs an enrollment code.
    const problem = phoneSetupFormProblem(form)
    if (problem) { setError(phoneSetupProblemMessage(problem)); setCode(null); return }

    setBusy(true)
    setError('')
    try {
      const response = await apiFetch('/api/auth/enrollments', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ deviceName: form.deviceName.trim() }),
      })
      if (!response.ok) {
        setError(response.status === 409
          ? 'This fleet already has the most devices it allows. Revoke one first.'
          : `The orchestrator did not issue a code (HTTP ${response.status}).`)
        return
      }
      const issued = await response.json() as { enrollmentCode?: string }
      const result = phoneSetupCode(form, issued.enrollmentCode ?? '')
      if ('problem' in result) { setError(phoneSetupProblemMessage(result.problem)); return }
      setCode(result.code)
    } catch {
      setError('The orchestrator did not answer.')
    } finally {
      setBusy(false)
    }
  }

  function close() {
    setForm(null)
    setCode(null)
    setError('')
  }

  async function revoke(device: DeviceRow) {
    setConfirming(null)
    try {
      const response = await apiFetch(`/api/auth/devices/${encodeURIComponent(device.deviceId)}/revoke`, { method: 'POST' })
      if (!response.ok && response.status !== 404) {
        onToast?.(`${deviceTitle(device)} could not be revoked (HTTP ${response.status}).`, false)
        return
      }
      onToast?.(`${deviceTitle(device)} was revoked.`, true)
      // Revoking this browser signs it out; the page goes back to sign-in.
      if (device.current) { window.location.reload(); return }
      await load()
    } catch {
      onToast?.(`${deviceTitle(device)} could not be revoked.`, false)
    }
  }

  async function signOut() {
    try { await apiFetch('/api/auth/browser-session', { method: 'DELETE' }) } catch { /* reload decides */ }
    window.location.reload()
  }

  const active = devices?.filter(d => !d.revokedAt) ?? []
  const revoked = devices?.filter(d => d.revokedAt) ?? []

  return (
    <section className="cred-section">
      <div className="cred-section-header">
        <div className="cred-section-title">Devices</div>
        <div style={{ display: 'flex', gap: 8 }}>
          {form === null && (
            <button className="cred-action-btn" onClick={() => setForm(emptyPhoneSetupForm(window.location.origin))}>
              Connect a phone
            </button>
          )}
          <button className="cred-action-btn" onClick={signOut}>Sign out of this browser</button>
        </div>
      </div>

      <p style={note}>Every browser and app signed in to this fleet. Revoking one signs it out at once.</p>
      {listError && <p style={{ ...note, color: 'var(--danger, #c0392b)' }} role="alert">{listError}</p>}

      {devices !== null && (
        <div className="device-list">
          {active.length === 0 && <p style={note}>No device is signed in.</p>}
          {[...active, ...revoked].map(device => (
            <div key={device.deviceId} className={`device-row${device.revokedAt ? ' device-row--revoked' : ''}`}>
              <div className="device-row-main">
                <div className="device-row-name">
                  {deviceTitle(device)}
                  <span className="device-badge">{device.kind === 'browser' ? 'browser' : 'app'}</span>
                  {device.current && <span className="device-badge">this browser</span>}
                  {device.revokedAt && <span className="device-badge">revoked</span>}
                </div>
                <div className="device-row-meta">
                  {device.revokedAt
                    ? `revoked ${heartbeatAge(device.revokedAt)}`
                    : device.lastSeenAt ? `last seen ${heartbeatAge(device.lastSeenAt)}` : 'not used yet'}
                </div>
              </div>
              {!device.revokedAt && (confirming === device.deviceId ? (
                <>
                  <button className="cred-action-btn" onClick={() => revoke(device)}>Confirm revoke</button>
                  <button className="cred-action-btn" onClick={() => setConfirming(null)}>Cancel</button>
                </>
              ) : (
                <button className="cred-action-btn" onClick={() => setConfirming(device.deviceId)}>Revoke</button>
              ))}
            </div>
          ))}
        </div>
      )}

      {form !== null && (
        <div style={{ maxWidth: 520 }}>
          <div style={{ marginBottom: 8 }}>
            <label style={label} htmlFor="phone-setup-name">Name for the phone (optional)</label>
            <input id="phone-setup-name" className="config-input" style={{ width: '100%' }}
              value={form.deviceName} maxLength={DEVICE_NAME_MAX_LENGTH}
              onChange={e => change({ deviceName: e.target.value })} autoComplete="off" />
          </div>

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

          {error && <p style={{ ...note, color: 'var(--danger, #c0392b)' }} role="alert">{error}</p>}

          <div style={{ display: 'flex', gap: 8, margin: '8px 0' }}>
            {code === null
              ? <button className="config-save-btn" onClick={show} disabled={busy}>{busy ? 'Asking…' : 'Show code'}</button>
              : <button className="config-save-btn" onClick={() => setCode(null)}>Hide code</button>}
            <button className="cred-action-btn" onClick={close}>Close</button>
          </div>

          {matrix && (
            <div>
              <QrCode matrix={matrix} />
              <p style={note} role="status">
                Scan this with the app. The code connects one phone, once, and stops working after
                fifteen minutes. It hides itself after two.
              </p>
              <button className="cred-action-btn" onClick={() => void copy()}>Copy as text</button>
              <p style={note} role="status">
                {copied === 'yes'
                  ? 'Copied. Paste it in the app with “Paste” on its setup screen. It is the same one-time code as the picture, so treat the clipboard the same way.'
                  : copied === 'refused'
                    ? 'This browser did not allow the copy. Scan the picture instead.'
                    : 'For a phone that cannot scan: copy the same code as text and paste it in the app.'}
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
