import { useState } from 'react'
import type { AgentConfig, ConfigEdits, ConfigSaveState, InstructionSummary, McpEndpointEntry } from '../types'
import { ADVANCED_DEFAULTS, countCustomized, PROVIDER_DEFAULT_MODEL, CLAUDE_PERMISSION_MODES, CODEX_SANDBOX_MODES } from '../constants'
import ModelSelector from './ModelSelector'
import FieldHint from './FieldHint'
import InstructionPicker from './InstructionPicker'

const SHOW_ADVANCED_KEY = 'fleet-dashboard-show-advanced'

interface AgentConfigModalProps {
  agentName: string
  configData: AgentConfig | null
  configEdits: ConfigEdits | null
  configSaveState: ConfigSaveState
  configSaveMsg: string
  configLoading: boolean
  configReprovisionConfirm: boolean
  allInstructions: InstructionSummary[]
  onEditsChange: (patch: Partial<ConfigEdits>) => void
  onSave: (andReprovision: boolean) => void
  onReprovisionConfirmToggle: () => void
  onClose: () => void
}

export default function AgentConfigModal({
  agentName,
  configData,
  configEdits,
  configSaveState,
  configSaveMsg,
  configLoading,
  configReprovisionConfirm,
  allInstructions,
  onEditsChange,
  onSave,
  onReprovisionConfirmToggle,
  onClose,
}: AgentConfigModalProps) {
  const [showAdvanced, setShowAdvanced] = useState<boolean>(() => {
    try { return localStorage.getItem(SHOW_ADVANCED_KEY) === 'true' }
    catch { return false }
  })

  function toggleAdvanced() {
    const next = !showAdvanced
    setShowAdvanced(next)
    try { localStorage.setItem(SHOW_ADVANCED_KEY, String(next)) } catch { /* ignore */ }
  }

  const customizedCount = configEdits
    ? countCustomized(configEdits as unknown as Record<string, unknown>)
    : 0

  const provider = configEdits?.provider ?? configData?.provider ?? 'claude'
  const isClaude = provider === 'claude'
  const isCodex = provider === 'codex'

  return (
    <div className="config-modal-overlay" onClick={onClose}>
      <div className="config-modal" onClick={e => e.stopPropagation()}>
        <div className="config-modal-header">
          <span className="config-modal-title">Config — {agentName}</span>
          <button className="instructions-modal-close" onClick={onClose} title="Close">✕ close</button>
        </div>
        <div className="config-modal-body">
          {configLoading && <div className="config-loading">Loading…</div>}
          {!configLoading && configData && configEdits && (
            <>
              <div className="config-section-label">Core</div>
              <div className="config-field">
                <label className="config-label">Provider</label>
                <select
                  className="config-input"
                  value={configEdits.provider ?? configData.provider ?? 'claude'}
                  onChange={e => onEditsChange({ provider: e.target.value, model: PROVIDER_DEFAULT_MODEL[e.target.value] ?? '' })}
                >
                  <option value="claude">Claude (Anthropic)</option>
                  <option value="codex">Codex (OpenAI)</option>
                </select>
              </div>
              <div className="config-field">
                <label className="config-label">Model</label>
                <ModelSelector
                  provider={configEdits.provider ?? configData.provider ?? 'claude'}
                  value={configEdits.model}
                  onChange={model => onEditsChange({ model })}
                />
              </div>
              <div className="config-field">
                <label className="config-label">Memory (MB)</label>
                <input className="config-input config-input-short" type="number" min={128} value={configEdits.memoryLimitMb} onChange={e => onEditsChange({ memoryLimitMb: e.target.value })} />
              </div>
              <label className="config-field config-field-checkbox">
                <input type="checkbox" checked={configEdits.isEnabled} onChange={e => onEditsChange({ isEnabled: e.target.checked })} />
                <span className="config-label">Enabled</span>
              </label>
              <div className="config-field">
                <label className="config-label">Image</label>
                <FieldHint>Docker image for the agent container. Leave blank to use the provisioning default (<code>fleet:agent</code>).</FieldHint>
                <input className="config-input" value={configEdits.image} onChange={e => onEditsChange({ image: e.target.value })} placeholder="e.g. fleet:agent (leave blank for default)" />
              </div>
              {/* ── Advanced toggle ── */}
              <button className="config-advanced-toggle" onClick={toggleAdvanced}>
                {showAdvanced ? 'Hide advanced settings ▴' : 'Show advanced settings ▾'}
                {!showAdvanced && customizedCount > 0 && (
                  <span className="config-advanced-indicator">Advanced ({customizedCount} customized)</span>
                )}
              </button>

              {showAdvanced && (
              <>
              <div className="config-section-label">Behavior</div>
              {isClaude && (
              <div className="config-field">
                <label className="config-label">Permission Mode <span className="config-provider-badge">Claude only</span></label>
                <FieldHint>Controls which file operations Claude can perform without prompting. <code>acceptEdits</code> = auto-accept file edits; <code>bypassPermissions</code> = all operations auto-approved; <code>default</code> = ask for each.</FieldHint>
                <select className="config-input" value={configEdits.permissionMode} onChange={e => onEditsChange({ permissionMode: e.target.value })}>
                  {CLAUDE_PERMISSION_MODES.map(m => <option key={m} value={m}>{m}</option>)}
                </select>
              </div>
              )}
              {isClaude && (
              <div className="config-field">
                <label className="config-label">Max Turns <span className="config-provider-badge">Claude only</span></label>
                <FieldHint>Maximum conversation turns before Claude stops. Prevents runaway tasks.</FieldHint>
                <input className="config-input config-input-short" type="number" min={1} value={configEdits.maxTurns} onChange={e => onEditsChange({ maxTurns: e.target.value })} />
              </div>
              )}
              <div className="config-field">
                <label className="config-label">Work Dir</label>
                <FieldHint>Working directory inside the container. Defaults to <code>/workspace</code>.</FieldHint>
                <input className="config-input" value={configEdits.workDir} onChange={e => onEditsChange({ workDir: e.target.value })} placeholder="/workspace" />
              </div>
              <div className="config-field">
                <label className="config-label">Short Name</label>
                <FieldHint>Short alias shown in group chat references (e.g. <code>Acto</code>). Used as the display handle in Telegram group messages.</FieldHint>
                <input className="config-input config-input-short" value={configEdits.shortName} onChange={e => onEditsChange({ shortName: e.target.value })} placeholder="e.g. my-agent" />
              </div>
              <div className="config-field">
                <label className="config-label">Group Listen Mode</label>
                <FieldHint><code>all</code> = respond to every message; <code>mention</code> = respond only when mentioned; <code>off</code> = ignore group messages entirely.</FieldHint>
                <input className="config-input config-input-short" value={configEdits.groupListenMode} onChange={e => onEditsChange({ groupListenMode: e.target.value })} placeholder="mention" />
              </div>
              <div className="config-field">
                <label className="config-label">Group Debounce (s)</label>
                <FieldHint>Seconds to wait after the last message before responding in a group. Prevents reacting to mid-sentence message splits.</FieldHint>
                <input className="config-input config-input-short" type="number" min={0} value={configEdits.groupDebounceSeconds} onChange={e => onEditsChange({ groupDebounceSeconds: e.target.value })} />
              </div>
              <div className="config-field">
                <label className="config-label">Proactive Interval (min)</label>
                <FieldHint>How often (in minutes) the agent self-initiates a check-in task. <code>0</code> = disabled.</FieldHint>
                <input className="config-input config-input-short" type="number" min={0} value={configEdits.proactiveIntervalMinutes} onChange={e => onEditsChange({ proactiveIntervalMinutes: e.target.value })} />
              </div>
              <div className="config-field">
                <label className="config-field config-field-checkbox">
                  <input type="checkbox" checked={configEdits.showStats} onChange={e => onEditsChange({ showStats: e.target.checked })} />
                  <span className="config-label">Show Stats</span>
                </label>
                <FieldHint>Append token/time stats to each Telegram response.</FieldHint>
              </div>
              <div className="config-field">
                <label className="config-field config-field-checkbox">
                  <input type="checkbox" checked={configEdits.prefixMessages} onChange={e => onEditsChange({ prefixMessages: e.target.checked })} />
                  <span className="config-label">Prefix Messages</span>
                </label>
                <FieldHint>Prepend the agent's short name to each Telegram message (e.g. <code>[Acto]:</code>).</FieldHint>
              </div>
              <div className="config-field">
                <label className="config-field config-field-checkbox">
                  <input type="checkbox" checked={configEdits.suppressToolMessages} onChange={e => onEditsChange({ suppressToolMessages: e.target.checked })} />
                  <span className="config-label">Suppress Tool Messages</span>
                </label>
                <FieldHint>Hide intermediate tool-use progress messages from Telegram — only post the final response. Use for agents serving non-technical users.</FieldHint>
              </div>
              {isClaude && (
              <div className="config-field">
                <label className="config-label">Effort <span className="config-provider-badge">Claude only</span></label>
                <FieldHint>Claude's reasoning effort level. <code>low</code> = faster/cheaper; <code>max</code> = deepest reasoning. Affects latency and cost.</FieldHint>
                <select className="config-input" value={configEdits.effort} onChange={e => onEditsChange({ effort: e.target.value })}>
                  <option value="">default</option>
                  <option value="low">low</option>
                  <option value="medium">medium</option>
                  <option value="high">high</option>
                  <option value="max">max</option>
                </select>
              </div>
              )}
              {isClaude && (
              <div className="config-field">
                <label className="config-label">JSON Schema <span className="config-provider-badge">Claude only</span></label>
                <FieldHint>JSON schema passed via <code>--json-schema</code> flag. Forces Claude to return structured output matching the schema. Leave blank to disable.</FieldHint>
                <textarea className="config-textarea" value={configEdits.jsonSchema} onChange={e => onEditsChange({ jsonSchema: e.target.value })} placeholder='JSON schema for --json-schema flag (leave blank to disable)' rows={3} />
              </div>
              )}
              {isClaude && (
              <div className="config-field">
                <label className="config-label">Agents JSON <span className="config-provider-badge">Claude only</span></label>
                <FieldHint>JSON config passed via <code>--agents</code> flag to enable Claude's built-in subagent capabilities. Leave blank to disable.</FieldHint>
                <textarea className="config-textarea" value={configEdits.agentsJson} onChange={e => onEditsChange({ agentsJson: e.target.value })} placeholder='JSON for --agents flag (leave blank to disable)' rows={3} />
              </div>
              )}
              {isClaude && (
              <div className="config-field">
                <label className="config-label">Auto Memory <span className="config-provider-badge">Claude only</span></label>
                <FieldHint>Enables Claude's built-in auto-memory feature. When off, suppresses Claude's internal memory. Fleet's own fleet-memory MCP is a separate system.</FieldHint>
                <input type="checkbox" checked={configEdits.autoMemoryEnabled} onChange={e => onEditsChange({ autoMemoryEnabled: e.target.checked })} />
              </div>
              )}
              {isCodex && (
              <div className="config-field">
                <label className="config-label">Sandbox Mode <span className="config-provider-badge">Codex only</span></label>
                <FieldHint>Controls file system access. <code>danger-full-access</code> = unrestricted; <code>workspace-write</code> = writes limited to workspace; <code>read-only</code> = no writes.</FieldHint>
                <select className="config-input" value={configEdits.codexSandboxMode ?? ''} onChange={e => onEditsChange({ codexSandboxMode: e.target.value })}>
                  <option value="">default (danger-full-access)</option>
                  {CODEX_SANDBOX_MODES.map(m => <option key={m} value={m}>{m}</option>)}
                </select>
              </div>
              )}
              <div className="config-section-label">Tools &amp; Projects</div>
              <div className="config-field">
                <label className="config-label">Tools</label>
                <FieldHint>Comma-separated list of allowed tool names (e.g. <code>Read,Glob,mcp__fleet-memory__memory_search</code>). Controls exactly which tools Claude can call.</FieldHint>
                <textarea className="config-textarea" value={configEdits.tools} onChange={e => onEditsChange({ tools: e.target.value })} placeholder="comma-separated tool names" rows={3} />
              </div>
              <div className="config-field">
                <label className="config-label">Projects</label>
                <FieldHint>Comma-separated project names whose context files are loaded into the agent's system prompt (e.g. <code>fleet,backend</code>).</FieldHint>
                <input className="config-input" value={configEdits.projects} onChange={e => onEditsChange({ projects: e.target.value })} placeholder="comma-separated project names" />
              </div>
              <div className="config-section-label">MCP Endpoints</div>
              <FieldHint>MCP Transport Type: <code>http</code> = streamable HTTP (preferred); <code>sse</code> = Server-Sent Events (legacy).</FieldHint>
              {configEdits.mcpEndpoints.map((ep, i) => (
                <div key={i} className="config-related-row">
                  <input
                    className="config-input config-input-sm"
                    value={ep.mcpName}
                    onChange={e => {
                      const eps = configEdits.mcpEndpoints.map((x, j) => j === i ? { ...x, mcpName: e.target.value } : x)
                      onEditsChange({ mcpEndpoints: eps })
                    }}
                    placeholder="name"
                  />
                  <input
                    className="config-input config-input-url"
                    value={ep.url}
                    onChange={e => {
                      const eps = configEdits.mcpEndpoints.map((x, j) => j === i ? { ...x, url: e.target.value } : x)
                      onEditsChange({ mcpEndpoints: eps })
                    }}
                    placeholder="url"
                  />
                  <input
                    className="config-input config-input-sm"
                    value={ep.transportType}
                    onChange={e => {
                      const eps = configEdits.mcpEndpoints.map((x, j) => j === i ? { ...x, transportType: e.target.value } : x)
                      onEditsChange({ mcpEndpoints: eps })
                    }}
                    placeholder="sse"
                  />
                  <button className="config-remove-btn" onClick={() => {
                    const eps = configEdits.mcpEndpoints.filter((_, j) => j !== i)
                    onEditsChange({ mcpEndpoints: eps })
                  }}>✕</button>
                </div>
              ))}
              <button className="config-add-btn" onClick={() => {
                const ep: McpEndpointEntry = { mcpName: '', url: '', transportType: 'sse' }
                onEditsChange({ mcpEndpoints: [...configEdits.mcpEndpoints, ep] })
              }}>+ add endpoint</button>
              <div className="config-section-label">Networks</div>
              <div className="config-field">
                <FieldHint>Docker networks the container joins. Agents must share a network to reach each other and internal services (e.g. <code>fleet-net</code>).</FieldHint>
                <input className="config-input" value={configEdits.networks} onChange={e => onEditsChange({ networks: e.target.value })} placeholder="comma-separated network names" />
              </div>
              <div className="config-section-label">Env Refs</div>
              <div className="config-field">
                <FieldHint>Names of <code>.env</code> keys to inject into the container as environment variables. Store key names only — not values (e.g. <code>TELEGRAM_BOT_TOKEN,GITHUB_APP_ID</code>).</FieldHint>
                <input className="config-input" value={configEdits.envRefs} onChange={e => onEditsChange({ envRefs: e.target.value })} placeholder="comma-separated env key names" />
              </div>
              <div className="config-field">
                <label className="config-field config-field-checkbox">
                  <input type="checkbox" checked={configEdits.telegramSendOnly} onChange={e => onEditsChange({ telegramSendOnly: e.target.checked })} />
                  <span className="config-label">Telegram Send-Only (no polling)</span>
                </label>
                <FieldHint>If checked, the agent sends Telegram messages but does not poll for incoming messages. Useful for notification-only agents.</FieldHint>
              </div>
              <div className="config-section-label">Telegram Users</div>
              <div className="config-field">
                <FieldHint>Numeric Telegram user IDs allowed to send tasks to this agent (e.g. <code>123456789</code>). Find your ID via <code>@userinfobot</code>. Comma-separated.</FieldHint>
                <input className="config-input" value={configEdits.telegramUsers} onChange={e => onEditsChange({ telegramUsers: e.target.value })} placeholder="comma-separated user IDs" />
              </div>
              <div className="config-section-label">Telegram Groups</div>
              <div className="config-field">
                <FieldHint>Numeric Telegram group IDs where this agent listens (typically negative, e.g. <code>-1001234567890</code>). Comma-separated.</FieldHint>
                <input className="config-input" value={configEdits.telegramGroups} onChange={e => onEditsChange({ telegramGroups: e.target.value })} placeholder="comma-separated group IDs" />
              </div>
              <div className="config-section-label">Instructions</div>
              <div className="config-field">
                <FieldHint>Role instructions composed into this agent's system prompt. Checked items are included; load order controls concatenation sequence. <code>base</code> is auto-attached and excluded here.</FieldHint>
                <InstructionPicker
                  allInstructions={allInstructions}
                  selected={configEdits.instructions}
                  onChange={instructions => onEditsChange({ instructions })}
                />
              </div>
              </>)}
              <div className="config-actions">
                <button className="config-save-btn" disabled={configSaveState === 'saving'} onClick={() => onSave(false)}>
                  {configSaveState === 'saving' ? '…' : 'Save'}
                </button>
                <button
                  className={`config-save-reprovision-btn${configReprovisionConfirm ? ' confirming' : ''}`}
                  disabled={configSaveState === 'saving'}
                  onClick={() => { if (configReprovisionConfirm) { onSave(true) } else { onReprovisionConfirmToggle() } }}
                >
                  {configReprovisionConfirm ? 'confirm reprovision?' : 'Save & Reprovision'}
                </button>
                {(configSaveState === 'success' || configSaveState === 'error') && (
                  <span className={`config-feedback config-feedback-${configSaveState}`}>{configSaveMsg}</span>
                )}
              </div>
            </>
          )}
          {!configLoading && configSaveState === 'error' && !configData && (
            <div className="config-feedback config-feedback-error">{configSaveMsg}</div>
          )}
        </div>
      </div>
    </div>
  )
}
