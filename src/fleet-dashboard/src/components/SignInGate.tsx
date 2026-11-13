import { useCallback, useEffect, useState, type FormEvent, type ReactNode } from 'react'
import { SIGNED_OUT_EVENT, apiFetch } from '../utils'
import { describeBrowser, readSession, signInProblem, type SessionState } from '../session'

type GateState = SessionState | { status: 'checking' }

async function fetchSession(): Promise<SessionState> {
  try {
    const response = await fetch('/api/auth/session', { credentials: 'same-origin' })
    let body: unknown = null
    try { body = await response.json() } catch { /* not JSON: readSession decides on the status */ }
    return readSession(response.status, body)
  } catch {
    return { status: 'unreachable', httpStatus: 0 }
  }
}

/**
 * Stands in front of the whole page. Nothing behind it is rendered, and so nothing behind it is
 * fetched, until the orchestrator says this browser is signed in or that it gates nothing.
 */
export default function SignInGate({ children }: { children: ReactNode }) {
  const [state, setState] = useState<GateState>({ status: 'checking' })

  const check = useCallback(async () => {
    const session = await fetchSession()
    if (session.status === 'open') {
      // Nothing is gated, but the chat service still wants a credential from this page. Asking
      // costs nothing and needs no token; a refusal changes nothing about what the page may do.
      try {
        await apiFetch('/api/auth/browser-session', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ deviceName: describeBrowser(navigator.userAgent) }),
        })
      } catch { /* the page loads either way */ }
    }
    setState(session)
  }, [])

  useEffect(() => { void check() }, [check])

  // A 401 anywhere means this browser may have been signed out — revoked from another device, or
  // its session lapsed. The session route says which; only its answer changes what is shown.
  useEffect(() => {
    let pending = false
    const onUnauthorized = async () => {
      if (pending) return
      pending = true
      try {
        const session = await fetchSession()
        if (session.status === 'signed-out') setState(session)
      } finally { pending = false }
    }
    window.addEventListener(SIGNED_OUT_EVENT, onUnauthorized)
    return () => window.removeEventListener(SIGNED_OUT_EVENT, onUnauthorized)
  }, [])

  if (state.status === 'signed-in' || state.status === 'open') return <>{children}</>

  return (
    <div className="signin-shell">
      <div className="signin-card">
        <h1 className="signin-title">Fleet</h1>
        {state.status === 'checking' && <p className="signin-note" role="status">Checking sign-in…</p>}
        {state.status === 'unreachable' && (
          <>
            <p className="signin-error" role="alert">
              {state.httpStatus === 0
                ? 'The orchestrator did not answer.'
                : `The orchestrator answered HTTP ${state.httpStatus}.`}
            </p>
            <button className="config-save-btn" onClick={() => { setState({ status: 'checking' }); void check() }}>
              Try again
            </button>
          </>
        )}
        {state.status === 'signed-out' && <SignInForm onSignedIn={() => { setState({ status: 'checking' }); void check() }} />}
      </div>
    </div>
  )
}

function SignInForm({ onSignedIn }: { onSignedIn: () => void }) {
  const [token, setToken] = useState('')
  const [deviceName, setDeviceName] = useState(() => describeBrowser(navigator.userAgent))
  const [error, setError] = useState('')
  const [busy, setBusy] = useState(false)

  async function submit(event: FormEvent) {
    event.preventDefault()
    if (busy || !token.trim()) return
    setBusy(true)
    setError('')
    try {
      // Plain fetch: a refusal here is an answer about the token, not "this browser was signed out".
      const response = await fetch('/api/auth/browser-session', {
        method: 'POST',
        credentials: 'same-origin',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ token: token.trim(), deviceName }),
      })
      if (!response.ok) { setError(signInProblem(response.status)); return }
      // The token has done its one job. It is not kept anywhere on this page.
      setToken('')
      onSignedIn()
    } catch {
      setError('The orchestrator did not answer.')
    } finally {
      setBusy(false)
    }
  }

  return (
    <form onSubmit={submit}>
      <p className="signin-note">
        Sign this browser in with the orchestrator token (<code>ORCHESTRATOR_AUTH_TOKEN</code> in the
        deployment&rsquo;s <code>.env</code>). It is asked for once per browser.
      </p>
      <label className="signin-label" htmlFor="signin-token">Orchestrator token</label>
      <input id="signin-token" className="config-input signin-input" type="password" value={token}
        onChange={e => setToken(e.target.value)} autoComplete="off" spellCheck={false} autoFocus />
      <label className="signin-label" htmlFor="signin-device">Name for this browser</label>
      <input id="signin-device" className="config-input signin-input" value={deviceName} maxLength={64}
        onChange={e => setDeviceName(e.target.value)} autoComplete="off" />
      {error && <p className="signin-error" role="alert">{error}</p>}
      <button className="config-save-btn" type="submit" disabled={busy || !token.trim()}>
        {busy ? 'Signing in…' : 'Sign in'}
      </button>
    </form>
  )
}
