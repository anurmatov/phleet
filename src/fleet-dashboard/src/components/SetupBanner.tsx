import { useState } from 'react'
import { apiFetch } from '../utils'

interface SetupStatus {
  telegramConfigured: boolean
  githubConfigured: boolean
}

interface SetupBannerProps {
  status: SetupStatus | null
  agentCount: number
  onConnected: () => void
}

// ── Telegram Modal ────────────────────────────────────────────────────────────

interface TelegramModalProps {
  onClose: () => void
  onConnected: () => void
}

function ConnectTelegramModal({ onClose, onConnected }: TelegramModalProps) {
  const [token, setToken] = useState('')
  const [groupChatId, setGroupChatId] = useState('')
  const [testState, setTestState] = useState<'idle' | 'testing' | 'ok' | 'error'>('idle')
  const [testMsg, setTestMsg] = useState('')
  const [saveState, setSaveState] = useState<'idle' | 'saving' | 'success' | 'error'>('idle')
  const [saveMsg, setSaveMsg] = useState('')

  async function handleTest() {
    setTestState('testing')
    setTestMsg('')
    try {
      const res = await apiFetch('/api/setup/telegram/validate', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ botToken: token, groupChatId: groupChatId || undefined }),
      })
      const data = await res.json()
      if (res.ok && data.valid) {
        setTestState('ok')
        setTestMsg(data.botUsername ? `Connected as @${data.botUsername}` : 'Connection OK')
      } else {
        setTestState('error')
        setTestMsg(data.error ?? 'Validation failed')
      }
    } catch (e) {
      setTestState('error')
      setTestMsg(String(e))
    }
  }

  async function handleSave() {
    setSaveState('saving')
    setSaveMsg('')
    try {
      const res = await apiFetch('/api/setup/telegram', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ botToken: token, groupChatId: groupChatId || undefined }),
      })
      const data = await res.json()
      if (res.ok || res.status === 207) {
        setSaveState('success')
        setSaveMsg(data.restartErrors?.length ? `Saved (some containers could not restart: ${data.restartErrors.join(', ')})` : 'Saved and applied')
        setTimeout(() => { onConnected(); onClose() }, 1200)
      } else {
        setSaveState('error')
        setSaveMsg(data.error ?? `Error ${res.status}`)
      }
    } catch (e) {
      setSaveState('error')
      setSaveMsg(String(e))
    }
  }

  return (
    <div className="config-modal-overlay" onClick={onClose}>
      <div className="config-modal" style={{ maxWidth: 480 }} onClick={e => e.stopPropagation()}>
        <div className="config-modal-header">
          <span className="config-modal-title">Connect Telegram</span>
          <button className="config-modal-close" onClick={onClose}>✕ close</button>
        </div>
        <div className="config-modal-body">
          <div className="config-row">
            <label className="config-label">Bot token <span style={{ color: 'var(--red)' }}>*</span></label>
            <input
              className="config-input"
              type="password"
              placeholder="110201543:AAHdqTcvCH1vGWJxfSeofSAs0K5PALDsaw"
              value={token}
              onChange={e => { setToken(e.target.value); setTestState('idle'); setSaveState('idle') }}
            />
          </div>
          <div className="config-row">
            <label className="config-label">Group chat ID <span style={{ color: 'var(--muted)', fontWeight: 400 }}>(optional)</span></label>
            <input
              className="config-input"
              placeholder="-1001234567890"
              value={groupChatId}
              onChange={e => setGroupChatId(e.target.value)}
            />
          </div>

          {testMsg && (
            <div className="config-feedback" style={{ color: testState === 'ok' ? 'var(--accent)' : 'var(--red)', marginBottom: 8 }}>
              {testMsg}
            </div>
          )}

          <div style={{ display: 'flex', gap: 8, marginTop: 4 }}>
            <button
              className="wfd-cancel-btn"
              disabled={!token || testState === 'testing'}
              onClick={handleTest}
            >
              {testState === 'testing' ? 'Testing…' : 'Test connection'}
            </button>
            <button
              className="config-save-btn"
              disabled={!token || saveState === 'saving'}
              onClick={handleSave}
            >
              {saveState === 'saving' ? 'Saving…' : 'Save & connect'}
            </button>
          </div>

          {saveMsg && (
            <div className="config-feedback" style={{ color: saveState === 'success' ? 'var(--accent)' : 'var(--red)', marginTop: 8 }}>
              {saveMsg}
            </div>
          )}
        </div>
      </div>
    </div>
  )
}

// ── GitHub Modal ──────────────────────────────────────────────────────────────

interface GitHubModalProps {
  onClose: () => void
  onConnected: () => void
}

function ConnectGitHubModal({ onClose, onConnected }: GitHubModalProps) {
  const [appId, setAppId] = useState('')
  const [pem, setPem] = useState('')
  const [testState, setTestState] = useState<'idle' | 'testing' | 'ok' | 'error'>('idle')
  const [testMsg, setTestMsg] = useState('')
  const [saveState, setSaveState] = useState<'idle' | 'saving' | 'success' | 'error'>('idle')
  const [saveMsg, setSaveMsg] = useState('')

  function handlePemFile(e: React.ChangeEvent<HTMLInputElement>) {
    const file = e.target.files?.[0]
    if (!file) return
    const reader = new FileReader()
    reader.onload = ev => setPem((ev.target?.result as string) ?? '')
    reader.readAsText(file)
  }

  async function handleTest() {
    setTestState('testing')
    setTestMsg('')
    try {
      const res = await apiFetch('/api/setup/github/validate', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ appId, privateKeyPem: pem }),
      })
      const data = await res.json()
      if (res.ok && data.valid) {
        setTestState('ok')
        setTestMsg(data.appSlug ? `App: ${data.appSlug}` : 'Credentials OK')
      } else {
        setTestState('error')
        setTestMsg(data.error ?? 'Validation failed')
      }
    } catch (e) {
      setTestState('error')
      setTestMsg(String(e))
    }
  }

  async function handleSave() {
    setSaveState('saving')
    setSaveMsg('')
    try {
      const res = await apiFetch('/api/setup/github', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ appId, privateKeyPem: pem }),
      })
      const data = await res.json()
      if (res.ok || res.status === 207) {
        setSaveState('success')
        setSaveMsg(data.restartErrors?.length ? `Saved (some containers could not restart: ${data.restartErrors.join(', ')})` : 'Saved and applied')
        setTimeout(() => { onConnected(); onClose() }, 1200)
      } else {
        setSaveState('error')
        setSaveMsg(data.error ?? `Error ${res.status}`)
      }
    } catch (e) {
      setSaveState('error')
      setSaveMsg(String(e))
    }
  }

  return (
    <div className="config-modal-overlay" onClick={onClose}>
      <div className="config-modal" style={{ maxWidth: 480 }} onClick={e => e.stopPropagation()}>
        <div className="config-modal-header">
          <span className="config-modal-title">Connect GitHub App</span>
          <button className="config-modal-close" onClick={onClose}>✕ close</button>
        </div>
        <div className="config-modal-body">
          <div className="config-row">
            <label className="config-label">App ID <span style={{ color: 'var(--red)' }}>*</span></label>
            <input
              className="config-input"
              placeholder="123456"
              value={appId}
              onChange={e => { setAppId(e.target.value); setTestState('idle'); setSaveState('idle') }}
            />
          </div>
          <div className="config-row">
            <label className="config-label">Private key (.pem) <span style={{ color: 'var(--red)' }}>*</span></label>
            <input
              type="file"
              accept=".pem,.key"
              className="config-input"
              style={{ padding: '6px 8px' }}
              onChange={handlePemFile}
            />
            {pem && (
              <div style={{ fontSize: 12, color: 'var(--muted)', marginTop: 4 }}>
                {pem.split('\n').length} lines loaded
              </div>
            )}
          </div>

          {testMsg && (
            <div className="config-feedback" style={{ color: testState === 'ok' ? 'var(--accent)' : 'var(--red)', marginBottom: 8 }}>
              {testMsg}
            </div>
          )}

          <div style={{ display: 'flex', gap: 8, marginTop: 4 }}>
            <button
              className="wfd-cancel-btn"
              disabled={!appId || !pem || testState === 'testing'}
              onClick={handleTest}
            >
              {testState === 'testing' ? 'Testing…' : 'Test connection'}
            </button>
            <button
              className="config-save-btn"
              disabled={!appId || !pem || saveState === 'saving'}
              onClick={handleSave}
            >
              {saveState === 'saving' ? 'Saving…' : 'Save & connect'}
            </button>
          </div>

          {saveMsg && (
            <div className="config-feedback" style={{ color: saveState === 'success' ? 'var(--accent)' : 'var(--red)', marginTop: 8 }}>
              {saveMsg}
            </div>
          )}
        </div>
      </div>
    </div>
  )
}

// ── Banner ────────────────────────────────────────────────────────────────────

export default function SetupBanner({ status, agentCount, onConnected }: SetupBannerProps) {
  const [telegramOpen, setTelegramOpen] = useState(false)
  const [githubOpen, setGithubOpen] = useState(false)

  if (!status) return null

  const bothConfigured = status.telegramConfigured && status.githubConfigured

  // Compact chip row once everything is configured and there are agents
  if (bothConfigured && agentCount > 0) {
    return (
      <div className="setup-banner setup-banner--compact">
        <span className="setup-chip setup-chip--ok">✓ Telegram</span>
        <span className="setup-chip setup-chip--ok">✓ GitHub</span>
      </div>
    )
  }

  return (
    <>
      <div className="setup-banner">
        {/* Telegram card */}
        {!status.telegramConfigured ? (
          <div className="setup-card setup-card--pending">
            <div className="setup-card-icon">✈</div>
            <div className="setup-card-body">
              <div className="setup-card-title">Connect Telegram</div>
              <div className="setup-card-desc">Add your bot token so agents can receive and send messages.</div>
            </div>
            <button className="config-save-btn setup-card-btn" onClick={() => setTelegramOpen(true)}>
              Connect
            </button>
          </div>
        ) : (
          <div className="setup-card setup-card--done">
            <div className="setup-card-icon">✓</div>
            <div className="setup-card-body">
              <div className="setup-card-title">Telegram connected</div>
            </div>
            <button className="wfd-cancel-btn setup-card-btn" onClick={() => setTelegramOpen(true)}>
              Update
            </button>
          </div>
        )}

        {/* GitHub card */}
        {!status.githubConfigured ? (
          <div className="setup-card setup-card--pending">
            <div className="setup-card-icon">⌥</div>
            <div className="setup-card-body">
              <div className="setup-card-title">Connect GitHub App</div>
              <div className="setup-card-desc">Add your GitHub App credentials so agents can push code and open PRs.</div>
            </div>
            <button className="config-save-btn setup-card-btn" onClick={() => setGithubOpen(true)}>
              Connect
            </button>
          </div>
        ) : (
          <div className="setup-card setup-card--done">
            <div className="setup-card-icon">✓</div>
            <div className="setup-card-body">
              <div className="setup-card-title">GitHub connected</div>
            </div>
            <button className="wfd-cancel-btn setup-card-btn" onClick={() => setGithubOpen(true)}>
              Update
            </button>
          </div>
        )}

        {/* First agent nudge */}
        {agentCount === 0 && (
          <div className="setup-card setup-card--info">
            <div className="setup-card-icon">+</div>
            <div className="setup-card-body">
              <div className="setup-card-title">Provision your first agent</div>
              <div className="setup-card-desc">Use the "New agent" button above to create and start your first agent container.</div>
            </div>
          </div>
        )}
      </div>

      {telegramOpen && (
        <ConnectTelegramModal
          onClose={() => setTelegramOpen(false)}
          onConnected={onConnected}
        />
      )}
      {githubOpen && (
        <ConnectGitHubModal
          onClose={() => setGithubOpen(false)}
          onConnected={onConnected}
        />
      )}
    </>
  )
}
