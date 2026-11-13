import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import './index.css'
import App from './App'
import SignInGate from './components/SignInGate'
import ErrorBoundary from './components/ErrorBoundary'

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <SignInGate>
      <ErrorBoundary>
        <App />
      </ErrorBoundary>
    </SignInGate>
  </StrictMode>,
)
