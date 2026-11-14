import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import './index.css'
import App from './App'
import { applyTheme, readTheme } from './theme'
import SignInGate from './components/SignInGate'
import ErrorBoundary from './components/ErrorBoundary'

// Before the first paint, so a chosen theme never flashes the other one.
applyTheme(readTheme(window.localStorage), document.documentElement, window.localStorage)

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <SignInGate>
      <ErrorBoundary>
        <App />
      </ErrorBoundary>
    </SignInGate>
  </StrictMode>,
)
