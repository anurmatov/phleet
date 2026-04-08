import type { WsStatus } from '../types'

declare const __APP_VERSION__: string

interface AppFooterProps {
  wsStatus: WsStatus
  lastUpdated: string
}

export default function AppFooter({ wsStatus, lastUpdated }: AppFooterProps) {
  return (
    <footer className="app-footer">
      <span className="app-footer-version">v{__APP_VERSION__}</span>
      <span className="app-footer-sep">·</span>
      <span className={`app-footer-ws app-ws-${wsStatus}`}>WebSocket: {wsStatus}</span>
      {lastUpdated && (
        <>
          <span className="app-footer-sep">·</span>
          <span className="app-footer-sync">Last sync: {lastUpdated}</span>
        </>
      )}
    </footer>
  )
}
