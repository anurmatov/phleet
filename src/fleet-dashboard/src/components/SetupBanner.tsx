import { useState, useEffect } from 'react'
import { apiFetch } from '../utils'
import type { AgentTemplateSummary } from '../types'

// Matches the nested shape returned by SetupStatusDto
interface SetupStatus {
  telegram: { configured: boolean; groupChatEnabled: boolean }
  gitHub: { configured: boolean }
}

interface SetupBannerProps {
  status: SetupStatus | null
  agentCount: number
  onConnected: () => void
  onNewAgentFromTemplate: (templateName: string) => void
}

let _toastId = 0

// ── Telegram Modal ────────────────────────────────────────────────────────────

interface TelegramModalProps {
  onClose: () => void
  onConnected: (toast: string) => void
}

function ConnectTelegramModal({ onClose, onConnected }: TelegramModalProps) {
  const [ctoBotToken, setCtoBotToken] = useState('')
  const [notifierBotToken, setNotifierBotToken] = useState('')
  const [groupChatId, setGroupChatId] = useState('')
  const [testState, setTestState] = useState<'idle' | 'testing' | 'ok' | 'error'>('idle')
  const [testMsg, setTestMsg] = useState('')
  const [fieldErrors, setFieldErrors] = useState<Record<string, boolean>>({})
  const [saveState, setSaveState] = useState<'idle' | 'saving' | 'error'>('idle')
  const [saveMsg, setSaveMsg] = useState('')
  const [restartErrors, setRestartErrors] = useState<Record<string, string> | null>(null)

  function resetFeedback() {
    setTestState('idle')
    setSaveState('idle')
    setTestMsg('')
    setSaveMsg('')
    setFieldErrors({})
    setRestartErrors(null)
  }

  function buildPayload() {
    return {
      ctoBotToken: ctoBotToken || undefined,
      notifierBotToken: notifierBotToken || undefined,
      groupChatId: groupChatId || undefined,
    }
  }

  function parseFieldErrors(data: Record<string, unknown>, errDetail: string): Record<string, boolean> {
    const fe: Record<string, boolean> = {}
    if (data.errors && typeof data.errors === 'object') {
      const errs = data.errors as Record<string, unknown>
      if (errs.cto_token_invalid) fe.ctoToken = true
      if (errs.notifier_token_invalid) fe.notifierToken = true
      if (errs.group_chat_invalid) fe.groupChat = true
    } else {
      if (errDetail.includes('cto_token_invalid') || /\bcto\b/i.test(errDetail)) fe.ctoToken = true
      if (errDetail.includes('notifier_token_invalid') || /notifier/i.test(errDetail)) fe.notifierToken = true
      if (errDetail.includes('group_chat_invalid') || /group.?chat/i.test(errDetail)) fe.groupChat = true
    }
    return fe
  }

  async function handleTest() {
    setTestState('testing')
    setTestMsg('')
    setFieldErrors({})
    try {
      const res = await apiFetch('/api/setup/telegram/validate', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(buildPayload()),
      })
      const data = await res.json()
      if (res.ok && data.valid) {
        const parts: string[] = []
        if (data.ctoBot?.username) parts.push(`CTO: @${data.ctoBot.username}`)
        if (data.notifierBot?.username) parts.push(`Notifier: @${data.notifierBot.username}`)
        setTestState('ok')
        setTestMsg(parts.length ? parts.join(', ') : 'Connection OK')
      } else {
        setTestState('error')
        const errDetail = String(data.errorDetail ?? data.error ?? 'Validation failed')
        setTestMsg(errDetail)
        setFieldErrors(parseFieldErrors(data, errDetail))
      }
    } catch (e) {
      setTestState('error')
      setTestMsg(String(e))
    }
  }

  async function handleSave() {
    setSaveState('saving')
    setSaveMsg('')
    setRestartErrors(null)
    try {
      const res = await apiFetch('/api/setup/telegram', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(buildPayload()),
      })
      const data = await res.json()
      if (res.status === 207) {
        // Keep modal open; show amber restart-errors block
        setRestartErrors(data.restartErrors ?? {})
        setSaveState('error')
      } else if (res.ok) {
        const errCount = Object.keys(data.restartErrors ?? {}).length
        onConnected(
          errCount > 0
            ? `✓ Telegram connected — ${errCount} container${errCount !== 1 ? 's' : ''} restarted`
            : '✓ Telegram connected'
        )
        onClose()
      } else {
        setSaveState('error')
        setSaveMsg(data.errorDetail ?? data.error ?? `Error ${res.status}`)
      }
    } catch (e) {
      setSaveState('error')
      setSaveMsg(String(e))
    }
  }

  const fieldErrorCount = Object.values(fieldErrors).filter(Boolean).length

  return (
    <div className="config-modal-overlay" onClick={onClose}>
      <div className="config-modal" style={{ maxWidth: 480 }} onClick={e => e.stopPropagation()}>
        <div className="config-modal-header">
          <span className="config-modal-title">Connect Telegram</span>
          <button className="config-modal-close" onClick={onClose}>✕ close</button>
        </div>
        <div className="config-modal-body">

          {/* Intro */}
          <p className="setup-modal-intro">
            Fleet agents talk to you via Telegram. The CTO bot is your main chat; the notifier bot
            is a shared bot used for fleet-wide alerts. Group chat ID scopes agent activity to a
            specific group.
          </p>

          {/* CTO bot token */}
          <div className="config-row">
            <label className="config-label">
              CTO bot token <span style={{ color: 'var(--red)' }}>*</span>
            </label>
            <input
              className={`config-input${fieldErrors.ctoToken ? ' input--error' : ''}`}
              type="password"
              placeholder="110201543:AAHdqTcvCH1vGWJxfSeofSAs0K5PALDsaw"
              value={ctoBotToken}
              onChange={e => { setCtoBotToken(e.target.value); resetFeedback() }}
            />
            <div className="setup-field-hint">
              <a
                href="https://t.me/BotFather"
                target="_blank"
                rel="noopener noreferrer"
                className="setup-helper-link"
              >→ Create a bot (t.me/BotFather)</a>
              {' '}· paste the HTTP API token from BotFather (format: 123456789:ABC-DEF…)
            </div>
          </div>

          {/* Notifier bot token */}
          <div className="config-row">
            <label className="config-label">
              Notifier bot token{' '}
              <span style={{ color: 'var(--muted)', fontWeight: 400 }}>(optional)</span>
            </label>
            <input
              className={`config-input${fieldErrors.notifierToken ? ' input--error' : ''}`}
              type="password"
              placeholder="220301543:BBHdqTcvCH1vGWJxfSeofSAs0K5PALDsaw"
              value={notifierBotToken}
              onChange={e => { setNotifierBotToken(e.target.value); resetFeedback() }}
            />
            <div className="setup-field-hint">
              CTO and Notifier can be the same token — one bot per role is just a convention.
            </div>
          </div>

          {/* Group chat ID */}
          <div className="config-row">
            <label className="config-label">
              Group chat ID{' '}
              <span style={{ color: 'var(--muted)', fontWeight: 400 }}>(optional)</span>
            </label>
            <input
              className={`config-input${fieldErrors.groupChat ? ' input--error' : ''}`}
              placeholder="-1001234567890"
              value={groupChatId}
              onChange={e => setGroupChatId(e.target.value)}
            />
            <div className="setup-field-hint">
              <a
                href="https://t.me/userinfobot"
                target="_blank"
                rel="noopener noreferrer"
                className="setup-helper-link"
              >→ Get your ID (t.me/userinfobot)</a>
              {' '}· add the bot to a group, forward a message from that group to @userinfobot, or use /getchatid
            </div>
          </div>

          {/* Footer note */}
          <div className="setup-modal-footer-note">
            Both bot tokens must be valid before Save &amp; connect enables.
          </div>

          {/* Test result */}
          {testMsg && (
            <div
              className="config-feedback"
              style={{ color: testState === 'ok' ? 'var(--accent)' : 'var(--red)', marginBottom: 8 }}
            >
              {testMsg}
            </div>
          )}

          {/* 207 amber restart-errors block */}
          {restartErrors && (
            <div className="setup-restart-errors">
              <div className="setup-restart-errors__title">Some containers failed to restart:</div>
              {Object.entries(restartErrors).map(([k, v]) => (
                <div key={k} className="setup-restart-errors__row">• {k}: {String(v)}</div>
              ))}
              <div className="setup-restart-errors__footer">
                <a href="#/agents">Restart failed containers from the Agents panel →</a>
              </div>
            </div>
          )}

          {/* Action row */}
          <div style={{ display: 'flex', gap: 8, marginTop: 4, alignItems: 'center', flexWrap: 'wrap' }}>
            <button
              className="wfd-cancel-btn"
              disabled={!ctoBotToken || testState === 'testing'}
              onClick={handleTest}
            >
              {testState === 'testing' ? 'Testing…' : testState === 'ok' ? 'Re-test' : 'Test connection'}
            </button>
            <button
              className="config-save-btn"
              disabled={!ctoBotToken || testState !== 'ok' || saveState === 'saving'}
              onClick={handleSave}
            >
              {saveState === 'saving'
                ? '✓ .env written · restarting containers…'
                : 'Save & connect'}
            </button>
            {testState === 'error' && fieldErrorCount > 0 && (
              <span className="setup-validation-count">
                ✕ validation failed ({fieldErrorCount} error{fieldErrorCount !== 1 ? 's' : ''})
              </span>
            )}
          </div>

          {saveMsg && (
            <div
              className="config-feedback"
              style={{ color: 'var(--red)', marginTop: 8 }}
            >
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
  onConnected: (toast: string) => void
  configured: boolean
}

function ConnectGitHubModal({ onClose, onConnected, configured }: GitHubModalProps) {
  const [appId, setAppId] = useState('')
  const [pem, setPem] = useState('')
  const [appIdRevealed, setAppIdRevealed] = useState(!configured)
  const [pemRevealed, setPemRevealed] = useState(!configured)
  const [testState, setTestState] = useState<'idle' | 'testing' | 'ok' | 'error'>('idle')
  const [testMsg, setTestMsg] = useState('')
  const [fieldErrors, setFieldErrors] = useState<Record<string, boolean>>({})
  const [saveState, setSaveState] = useState<'idle' | 'saving' | 'error'>('idle')
  const [saveMsg, setSaveMsg] = useState('')
  const [restartErrors, setRestartErrors] = useState<Record<string, string> | null>(null)

  function handlePemFile(e: React.ChangeEvent<HTMLInputElement>) {
    const file = e.target.files?.[0]
    if (!file) return
    const reader = new FileReader()
    reader.onload = ev => setPem((ev.target?.result as string) ?? '')
    reader.readAsText(file)
  }

  // At least one field changed (revealed + non-empty)
  const hasChanges = (appIdRevealed && appId !== '') || (pemRevealed && pem !== '')
  // Test can run if: all revealed fields have values, and there's something to test
  const canTest = configured
    ? (!appIdRevealed || !!appId) && (!pemRevealed || !!pem) && (appIdRevealed || pemRevealed)
    : !!appId && !!pem

  async function handleTest() {
    setTestState('testing')
    setTestMsg('')
    setFieldErrors({})
    try {
      const res = await apiFetch('/api/setup/github/validate', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          appId: appId || undefined,
          privateKeyPem: pem || undefined,
        }),
      })
      const data = await res.json()
      if (res.ok && data.valid) {
        setTestState('ok')
        setTestMsg(data.appName ? `App: ${data.appName}` : 'Credentials OK')
        setFieldErrors({})
      } else {
        setTestState('error')
        const errDetail = String(data.errorDetail ?? data.error ?? 'Validation failed')
        setTestMsg(errDetail)
        const fe: Record<string, boolean> = {}
        if (data.errors && typeof data.errors === 'object') {
          const errs = data.errors as Record<string, unknown>
          if (errs.pem_invalid) fe.pem = true
          if (errs.app_id_mismatch) fe.appId = true
        } else {
          if (errDetail.includes('pem_invalid') || /pem|private.?key/i.test(errDetail)) fe.pem = true
          if (errDetail.includes('app_id_mismatch') || /app.?id/i.test(errDetail)) fe.appId = true
        }
        setFieldErrors(fe)
      }
    } catch (e) {
      setTestState('error')
      setTestMsg(String(e))
    }
  }

  async function handleSave() {
    setSaveState('saving')
    setSaveMsg('')
    setRestartErrors(null)
    try {
      const res = await apiFetch('/api/setup/github', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          appId: appId || undefined,
          privateKeyPem: pem || undefined,
        }),
      })
      const data = await res.json()
      if (res.status === 207) {
        // Keep modal open; show amber restart-errors block
        setRestartErrors(data.restartErrors ?? {})
        setSaveState('error')
      } else if (res.ok) {
        onConnected('✓ GitHub connected')
        onClose()
      } else {
        setSaveState('error')
        setSaveMsg(data.errorDetail ?? data.error ?? `Error ${res.status}`)
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

          {/* Intro */}
          <p className="setup-modal-intro">
            Fleet uses a GitHub App to authenticate as itself when cloning repos and pushing PRs —
            not a personal token. You create the app once, install it on your repos, and paste the
            App ID + private key here.
          </p>

          {/* App ID */}
          <div className="config-row">
            <label className="config-label">App ID <span style={{ color: 'var(--red)' }}>*</span></label>
            {configured && !appIdRevealed ? (
              <div className="setup-masked-field">
                <span className="setup-masked-value">••••••• (configured)</span>
                <button
                  className="wfd-cancel-btn"
                  style={{ flexShrink: 0 }}
                  onClick={() => setAppIdRevealed(true)}
                >Change</button>
              </div>
            ) : (
              <input
                className={`config-input${fieldErrors.appId ? ' input--error' : ''}`}
                placeholder="123456"
                value={appId}
                onChange={e => { setAppId(e.target.value); setTestState('idle') }}
              />
            )}
            {fieldErrors.appId && (
              <div className="setup-field-error">
                App ID mismatch — check the App ID in your GitHub App settings.
              </div>
            )}
            <div className="setup-field-hint">
              <a
                href="https://github.com/settings/apps/new"
                target="_blank"
                rel="noopener noreferrer"
                className="setup-helper-link"
              >→ Create a GitHub App (github.com/settings/apps/new)</a>
              {' '}· numeric ID shown at the top of your app's settings page
            </div>
          </div>

          {/* Private key */}
          <div className="config-row">
            <label className="config-label">Private key (.pem) <span style={{ color: 'var(--red)' }}>*</span></label>
            {configured && !pemRevealed ? (
              <div className="setup-masked-field">
                <span className="setup-masked-value">••••••• (configured)</span>
                <button
                  className="wfd-cancel-btn"
                  style={{ flexShrink: 0 }}
                  onClick={() => setPemRevealed(true)}
                >Change</button>
              </div>
            ) : (
              <>
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
              </>
            )}
            {fieldErrors.pem && (
              <div className="setup-field-error">
                PEM invalid — ensure the file is the private key (.pem) from your GitHub App.
              </div>
            )}
            <div className="setup-field-hint">
              <a
                href="https://docs.github.com/en/apps/creating-github-apps/authenticating-with-a-github-app/managing-private-keys-for-github-apps"
                target="_blank"
                rel="noopener noreferrer"
                className="setup-helper-link"
              >→ How to generate a .pem</a>
              {' '}· paste the full PEM contents (including BEGIN/END lines) or base64-encoded
            </div>
          </div>

          {/* Footer note */}
          <div className="setup-modal-footer-note">
            After saving, install the app on the repos you want Fleet to access.
          </div>

          {/* Test result */}
          {testMsg && (
            <div
              className="config-feedback"
              style={{ color: testState === 'ok' ? 'var(--accent)' : 'var(--red)', marginBottom: 8 }}
            >
              {testMsg}
            </div>
          )}

          {/* 207 amber restart-errors block */}
          {restartErrors && (
            <div className="setup-restart-errors">
              <div className="setup-restart-errors__title">Some containers failed to restart:</div>
              {Object.entries(restartErrors).map(([k, v]) => (
                <div key={k} className="setup-restart-errors__row">• {k}: {String(v)}</div>
              ))}
              <div className="setup-restart-errors__footer">
                <a href="#/agents">Restart failed containers from the Agents panel →</a>
              </div>
            </div>
          )}

          {/* Action row */}
          <div style={{ display: 'flex', gap: 8, marginTop: 4 }}>
            <button
              className="wfd-cancel-btn"
              disabled={!canTest || testState === 'testing'}
              onClick={handleTest}
            >
              {testState === 'testing' ? 'Testing…' : testState === 'ok' ? 'Re-test' : 'Test connection'}
            </button>
            <button
              className="config-save-btn"
              disabled={(configured && !hasChanges) || testState !== 'ok' || saveState === 'saving'}
              onClick={handleSave}
            >
              {saveState === 'saving'
                ? '✓ .env written · restarting containers…'
                : 'Save & connect'}
            </button>
          </div>

          {saveMsg && (
            <div
              className="config-feedback"
              style={{ color: 'var(--red)', marginTop: 8 }}
            >
              {saveMsg}
            </div>
          )}
        </div>
      </div>
    </div>
  )
}

// ── Banner ────────────────────────────────────────────────────────────────────

export default function SetupBanner({ status, agentCount, onConnected, onNewAgentFromTemplate }: SetupBannerProps) {
  const [telegramOpen, setTelegramOpen] = useState(false)
  const [githubOpen, setGithubOpen] = useState(false)
  const [toasts, setToasts] = useState<Array<{ id: number; text: string }>>([])
  const [templates, setTemplates] = useState<AgentTemplateSummary[]>([])

  useEffect(() => {
    apiFetch('/api/agent-templates')
      .then(r => r.ok ? r.json() : [])
      .then(data => setTemplates(Array.isArray(data) ? data : []))
      .catch(() => {})
  }, [])

  if (!status) return null

  const telegramOk = status.telegram.configured
  const githubOk = status.gitHub.configured
  const bothConfigured = telegramOk && githubOk

  function addToast(text: string) {
    const id = ++_toastId
    setToasts(prev => [...prev, { id, text }])
    setTimeout(() => setToasts(prev => prev.filter(t => t.id !== id)), 5000)
  }

  function handleModalConnected(toast: string) {
    addToast(toast)
    onConnected()
  }

  // Compact chip row once everything is configured and there are agents
  if (bothConfigured && agentCount > 0) {
    return (
      <>
        <div className="setup-banner setup-banner--compact">
          <span className="setup-chip setup-chip--ok">✓ Telegram</span>
          <span className="setup-chip setup-chip--ok">✓ GitHub</span>
        </div>
        {toasts.map(t => (
          <div key={t.id} className="setup-toast">{t.text}</div>
        ))}
      </>
    )
  }

  return (
    <>
      <div className="setup-banner">
        {/* Telegram card */}
        {!telegramOk ? (
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
        {!githubOk ? (
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

        {/* First agent nudge + template grid */}
        {agentCount === 0 && (
          <div className="setup-card setup-card--info setup-card--templates">
            <div className="setup-card-body" style={{ width: '100%' }}>
              <div className="setup-card-title">Provision your first agent</div>
              <div className="setup-card-desc">Pick a role to get started — fields are pre-filled, just choose a name.</div>
              {templates.length > 0 && (
                <div className="template-card-grid">
                  {templates.map(t => (
                    <button
                      key={t.name}
                      className="template-card"
                      onClick={() => onNewAgentFromTemplate(t.name)}
                    >
                      <div className="template-card-name">{t.displayName}</div>
                      <div className="template-card-desc">{t.description}</div>
                      <div className="template-card-meta">
                        <span className="template-card-model">{t.defaultModel.replace('claude-', '').replace('-4-6', '')}</span>
                        <span className="template-card-counts">{t.toolCount} tools · {t.mcpCount} MCPs</span>
                      </div>
                    </button>
                  ))}
                </div>
              )}
            </div>
          </div>
        )}
      </div>

      {/* Toasts */}
      {toasts.map(t => (
        <div key={t.id} className="setup-toast">{t.text}</div>
      ))}

      {telegramOpen && (
        <ConnectTelegramModal
          onClose={() => setTelegramOpen(false)}
          onConnected={handleModalConnected}
        />
      )}
      {githubOpen && (
        <ConnectGitHubModal
          onClose={() => setGithubOpen(false)}
          onConnected={handleModalConnected}
          configured={githubOk}
        />
      )}
    </>
  )
}
