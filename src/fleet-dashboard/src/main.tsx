import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import './index.css'
import App from './App'
import SignInGate from './components/SignInGate'

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <SignInGate>
      <App />
    </SignInGate>
  </StrictMode>,
)
