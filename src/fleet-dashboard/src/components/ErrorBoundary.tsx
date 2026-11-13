import { Component, type ErrorInfo, type ReactNode } from 'react'

interface BoundaryState {
  /** What a render threw, or null while the page is fine. */
  error: Error | null
  /** Bumped by "Try again" so the children mount afresh, without the state that broke them. */
  attempt: number
}

/** A thrown value as an Error, whatever was thrown. */
export function asError(thrown: unknown): Error {
  return thrown instanceof Error ? thrown : new Error(String(thrown))
}

/**
 * Shown in place of the page when rendering it threw. It says so, shows what was thrown (the
 * first thing anyone reporting it will be asked for) and offers the two ways out.
 */
export function CrashScreen({ error, onRetry, onReload }: { error: Error; onRetry: () => void; onReload: () => void }) {
  return (
    <div className="signin-shell">
      <div className="signin-card" role="alert">
        <h1 className="signin-title">Fleet</h1>
        <p className="signin-error">The page hit an error and stopped drawing.</p>
        <p className="signin-note">
          Nothing was changed by this. Try again to redraw the page; reload if it happens again.
        </p>
        <p className="signin-note"><code>{error.message || error.name}</code></p>
        <div style={{ display: 'flex', gap: 8 }}>
          <button className="config-save-btn" onClick={onRetry}>Try again</button>
          <button className="config-save-btn" onClick={onReload}>Reload</button>
        </div>
      </div>
    </div>
  )
}

/**
 * Sits under SignInGate, around the page. A render that throws would otherwise unmount the whole
 * tree and leave a blank window with no way back but a reload nobody is told to do.
 *
 * It is below the gate on purpose: a 401 still reaches the gate through its own event, and the
 * gate replaces this boundary and everything in it with the sign-in form.
 */
export default class ErrorBoundary extends Component<{ children: ReactNode }, BoundaryState> {
  state: BoundaryState = { error: null, attempt: 0 }

  static getDerivedStateFromError(thrown: unknown): Partial<BoundaryState> {
    return { error: asError(thrown) }
  }

  componentDidCatch(error: Error, info: ErrorInfo) {
    console.error('[dashboard] render failed', error, info.componentStack)
  }

  render() {
    const { error, attempt } = this.state
    if (error) {
      return (
        <CrashScreen
          error={error}
          onRetry={() => this.setState({ error: null, attempt: attempt + 1 })}
          onReload={() => window.location.reload()}
        />
      )
    }
    // Keyed so "Try again" remounts the page instead of re-rendering the state that threw.
    return <BoundaryChildren key={attempt}>{this.props.children}</BoundaryChildren>
  }
}

function BoundaryChildren({ children }: { children: ReactNode }) {
  return <>{children}</>
}
