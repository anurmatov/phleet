import { useEffect, useState } from 'react'
import type {
  AgentConfig, ConfigEdits, ConfigSaveState, InstructionSummary, McpEndpointEntry, OutputStyleSummary,
  ProjectCardState, ProjectContextDetail, ProjectContextMode, ProjectContextSummary,
} from '../types'
import { ADVANCED_DEFAULTS, countCustomized, PROVIDER_DEFAULT_MODEL, CLAUDE_PERMISSION_MODES, CODEX_SANDBOX_MODES } from '../constants'
import { apiFetch, projectModeFor } from '../utils'
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
  outputStyles: OutputStyleSummary[]
  /** Which projects exist and which have a card — drives the per-assignment mode select. */
  projectContexts: ProjectContextSummary[]
  projectAccess: string[] | null
  projectAccessLoading: boolean
  onEditsChange: (patch: Partial<ConfigEdits>) => void
  onSave: (andReprovision: boolean) => void
  onReprovisionConfirmToggle: () => void
  onProjectAccessAdd: (project: string) => void
  onProjectAccessRemove: (project: string) => void
  onProjectAccessToggleWildcard: (enable: boolean) => void
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
  outputStyles,
  projectContexts,
  projectAccess,
  projectAccessLoading,
  onEditsChange,
  onSave,
  onReprovisionConfirmToggle,
  onProjectAccessAdd,
  onProjectAccessRemove,
  onProjectAccessToggleWildcard,
  onClose,
}: AgentConfigModalProps) {
  const [showAdvanced, setShowAdvanced] = useState<boolean>(() => {
    try { return localStorage.getItem(SHOW_ADVANCED_KEY) === 'true' }
    catch { return false }
  })
  const [newProject, setNewProject] = useState('')

  function toggleAdvanced() {
    const next = !showAdvanced
    setShowAdvanced(next)
    try { localStorage.setItem(SHOW_ADVANCED_KEY, String(next)) } catch { /* ignore */ }
  }

  const customizedCount = configEdits
    ? countCustomized(configEdits as unknown as Record<string, unknown>)
    : 0

  // The row behind the current selection, when there is one — the link and the description below
  // the select both describe the style the operator is actually about to assign.
  const selectedStyle = outputStyles.find(s => s.name === configEdits?.outputStyle) ?? null

  const provider = configEdits?.provider ?? configData?.provider ?? 'claude'
  const isClaude = provider === 'claude'
  const isCodex = provider === 'codex'

  // ── Per-assignment context mode ──
  // The assignments are whatever the Projects field says right now, deduplicated the way the
  // orchestrator compares names (case-insensitively), so the selects follow the operator's typing.
  const assignedProjects = (configEdits?.projects ?? '')
    .split(',').map(p => p.trim()).filter(Boolean)
    .filter((p, i, all) => all.findIndex(q => q.toLowerCase() === p.toLowerCase()) === i)
  const summaryFor = (project: string) =>
    projectContexts.find(c => c.name.toLowerCase() === project.toLowerCase()) ?? null

  // Stale state is on the list row, but the keep markers a card is missing are only in the detail,
  // so read the detail of each assigned project that has a card.
  const [cardStates, setCardStates] = useState<Record<string, ProjectCardState | null>>({})
  const cardProjectNames = assignedProjects
    .map(p => summaryFor(p))
    .filter((c): c is ProjectContextSummary => c !== null && c.cardVersion != null)
    .map(c => c.name)
  const cardProjectsKey = [...cardProjectNames].sort().join('\n')
  useEffect(() => {
    for (const name of cardProjectNames) {
      apiFetch(`/api/project-contexts/${encodeURIComponent(name)}`)
        .then(r => r.ok ? r.json() : Promise.reject(r.status))
        .then((d: ProjectContextDetail) => setCardStates(prev => ({ ...prev, [name.toLowerCase()]: d.card ?? null })))
        .catch(() => { /* the list row's stale flag still shows */ })
    }
    // Keyed on the set of names, not the array identity, so typing elsewhere does not refetch.
  }, [cardProjectsKey])

  function setProjectMode(project: string, mode: ProjectContextMode) {
    if (!configEdits) return
    const next = Object.fromEntries(
      Object.entries(configEdits.projectModes).filter(([k]) => k.toLowerCase() !== project.toLowerCase()))
    next[project] = mode
    onEditsChange({ projectModes: next })
  }

  const modeChangePending = !!configData && assignedProjects.some(p =>
    projectModeFor(configEdits?.projectModes, p) !== projectModeFor(configData.projectModes, p))

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
                  // Leaving claude clears the local-server origin too: the field is hidden for other
                  // providers, and the server rejects it on anything but claude.
                  onChange={e => onEditsChange({
                    provider: e.target.value,
                    model: PROVIDER_DEFAULT_MODEL[e.target.value] ?? '',
                    ...(e.target.value !== 'claude' ? { anthropicBaseUrl: '' } : {}),
                  })}
                >
                  <option value="claude">Claude (Anthropic)</option>
                  <option value="codex">Codex (OpenAI)</option>
                  <option value="gemini">Gemini (Google)</option>
                </select>
              </div>
              <div className="config-field">
                <label className="config-label">Model</label>
                <FieldHint>Codex only: a <code>zai/</code> model needs <code>ZAI_CODING_PLAN_API_KEY</code> in Env Refs; subscriber-only use.</FieldHint>
                <ModelSelector
                  provider={configEdits.provider ?? configData.provider ?? 'claude'}
                  value={configEdits.model}
                  onChange={model => onEditsChange({ model })}
                />
              </div>
              {isClaude && (
              <div className="config-field">
                <label className="config-label">Anthropic-compatible base URL <span className="config-provider-badge">Claude only</span></label>
                <FieldHint>Empty = Anthropic with your Claude subscription. Set = this agent runs on a local Anthropic-compatible server (e.g. Ollama). Origin only, e.g. <code>http://&lt;server-lan-address&gt;:11434</code>, never <code>…/v1</code>; <code>localhost</code> is the container itself. Enter the server's model tag as a custom Model; Effort is off/low/medium/xhigh, or empty = model default (sent as xhigh). Claude credentials are not mounted. <strong>Takes effect on reprovision.</strong></FieldHint>
                <input className="config-input" value={configEdits.anthropicBaseUrl} onChange={e => onEditsChange({ anthropicBaseUrl: e.target.value })} placeholder="http://<server-lan-address>:11434" />
              </div>
              )}
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
                <FieldHint>Prepend the agent's short name to each Telegram message (e.g. <code>[Agent]:</code>).</FieldHint>
              </div>
              <div className="config-field">
                <label className="config-label">Formatting Mode</label>
                <FieldHint>PlainText: no formatting. LegacyHtml: Markdown-like syntax → Telegram HTML. Rich: sendRichMessage with LegacyHtml → PlainText fallback.</FieldHint>
                <select
                  value={configEdits.formattingMode}
                  onChange={e => onEditsChange({ formattingMode: parseInt(e.target.value, 10) })}
                  className="config-input"
                >
                  <option value={0}>PlainText</option>
                  <option value={1}>LegacyHtml</option>
                  <option value={2}>Rich</option>
                </select>
              </div>
              <div className="config-field">
                <label className="config-label">
                  Output Style
                  {/* Opens in a new tab on purpose: reading what you are about to impose on every
                      message this agent sends should not cost the unsaved edits in this modal. */}
                  <a
                    className="setup-helper-link"
                    // Naming the style in the hash is what makes "read <name>" land on that style
                    // open rather than on a list with it collapsed.
                    href={selectedStyle
                      ? `#output-styles/${encodeURIComponent(selectedStyle.name)}`
                      : '#output-styles'}
                    target="_blank"
                    rel="noreferrer"
                    title="Open the Output Styles page to read or edit the style body"
                  >
                    {selectedStyle
                      ? `read "${selectedStyle.name}" ↗`
                      : 'browse styles ↗'}
                  </a>
                </label>
                <FieldHint>Chat tone and register. Claude resolves it as a style file; Codex and Gemini get the same text inlined into their prompt. <strong>Needs a reprovision, not a restart</strong> — the value lands in the generated <code>settings.json</code> at provision time.</FieldHint>
                {selectedStyle?.description && (
                  <FieldHint>{selectedStyle.description}</FieldHint>
                )}
                <select
                  value={configEdits.outputStyle}
                  onChange={e => onEditsChange({ outputStyle: e.target.value })}
                  className="config-input"
                >
                  <option value="">none</option>
                  {outputStyles.map(s => (
                    <option key={s.name} value={s.name}>{s.name}</option>
                  ))}
                  {configEdits.outputStyle !== '' && !outputStyles.some(s => s.name === configEdits.outputStyle) && (
                    <option value={configEdits.outputStyle}>{configEdits.outputStyle} (not found)</option>
                  )}
                </select>
              </div>
              <div className="config-field">
                <label className="config-field config-field-checkbox">
                  <input type="checkbox" checked={configEdits.suppressToolMessages} onChange={e => onEditsChange({ suppressToolMessages: e.target.checked })} />
                  <span className="config-label">Suppress Tool Messages</span>
                </label>
                <FieldHint>Hide intermediate tool-use progress messages from Telegram — only post the final response. Use for agents serving non-technical users.</FieldHint>
              </div>
              {(isClaude || isCodex) && (
              <div className="config-field">
                <label className="config-label">Effort <span className="config-provider-badge">Claude + Codex</span></label>
                <FieldHint>Reasoning effort level. <code>low</code> = faster/cheaper; <code>max</code> = deepest reasoning (Codex maps max → xhigh). Affects latency and cost. Local Claude models use off/low/medium/xhigh.</FieldHint>
                {(() => {
                  // #349: local mode owns its own vocabulary. A stored value invalid for the
                  // current mode still renders (with a marker) — nothing is auto-cleared, and
                  // Save surfaces the server fault.
                  const isLocalClaude = (configEdits.provider ?? configData?.provider ?? 'claude') === 'claude'
                    && (configEdits.anthropicBaseUrl ?? configData?.anthropicBaseUrl ?? '').trim() !== ''
                  const current = configEdits.effort ?? ''
                  const localValues = ['', 'off', 'low', 'medium', 'xhigh']
                  const invalid = isLocalClaude && current !== '' && !localValues.includes(current)
                  return (
                    <select className="config-input" value={current} onChange={e => onEditsChange({ effort: e.target.value })}>
                      {isLocalClaude ? (
                        <>
                          <option value="">default (xhigh)</option>
                          <option value="off">off</option>
                          <option value="low">low</option>
                          <option value="medium">medium</option>
                          <option value="xhigh">xhigh</option>
                          {invalid && <option value={current}>{current} (not valid in this mode)</option>}
                        </>
                      ) : (
                        <>
                          <option value="">default</option>
                          <option value="low">low</option>
                          <option value="medium">medium</option>
                          <option value="high">high</option>
                          <option value="xhigh">xhigh</option>
                          <option value="max">max</option>
                          {current === 'off' && <option value="off">off (not valid in this mode)</option>}
                        </>
                      )}
                    </select>
                  )
                })()}
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
              {assignedProjects.length > 0 && (
              <div className="config-field">
                <label className="config-label">Project context mode</label>
                <FieldHint><code>full</code> = the full project context is resident in the system prompt. <code>card</code> = the project's compact card is resident instead, and the full context is attached to turns routed to the project (write the card and its routes under Project Contexts). <strong>Takes effect on reprovision.</strong></FieldHint>
                {assignedProjects.map(p => {
                  const summary = summaryFor(p)
                  const hasCard = summary?.cardVersion != null
                  const mode = projectModeFor(configEdits.projectModes, p)
                  const card = summary ? cardStates[summary.name.toLowerCase()] : undefined
                  const stale = card?.stale ?? summary?.cardStale ?? false
                  const missingKeeps = card?.missingKeeps ?? []
                  return (
                    <div key={p.toLowerCase()} className="config-related-row">
                      <span className="config-project-chip">{p}</span>
                      <select
                        className="config-input config-input-sm"
                        value={mode}
                        onChange={e => setProjectMode(p, e.target.value as ProjectContextMode)}
                      >
                        <option value="full">full</option>
                        {/* Kept selectable when already chosen, so a stored card assignment
                            still shows as card while the project list catches up. */}
                        <option value="card" disabled={!hasCard && mode !== 'card'}>
                          {hasCard ? `card (v${summary!.cardVersion})` : 'card'}
                        </option>
                      </select>
                      {!hasCard && (
                        <span className="config-hint-text">
                          {summary ? 'no card yet — write one under Project Contexts' : 'no project context with this name'}
                        </span>
                      )}
                      {hasCard && (stale || missingKeeps.length > 0) && (
                        <span
                          className="ctx-warn"
                          title={missingKeeps.length > 0
                            ? 'Provisioning renders the full context for this project until the card carries every keep marker of the current full context.'
                            : 'The card was written for an older full version. It is still used; update it under Project Contexts.'}
                        >
                          ⚠ {stale && card ? `stale (written for full v${card.basedOnFullVersion})` : stale ? 'stale' : ''}
                          {missingKeeps.length > 0 && `${stale ? ' · ' : ''}missing keeps: ${missingKeeps.join(', ')}`}
                        </span>
                      )}
                    </div>
                  )
                })}
                {modeChangePending && (
                  <div className="ctx-warn">Context mode changed — save, then reprovision this agent to apply it.</div>
                )}
              </div>
              )}
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
              <div className="config-field">
                <label className="config-field config-field-checkbox">
                  <input type="checkbox" checked={configEdits.mountDockerSock} onChange={e => onEditsChange({ mountDockerSock: e.target.checked })} />
                  <span className="config-label">Mount Docker socket</span>
                </label>
                <FieldHint>Grants the agent access to the host Docker daemon — required for agents that manage containers. Leave off unless the agent needs Docker access (grants host-root).</FieldHint>
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
              <div className="config-section-label">Chat Requests</div>
              <div className="config-field">
                <label className="config-field config-field-checkbox">
                  <input type="checkbox" checked={configEdits.canReceiveChatRequests} onChange={e => onEditsChange({ canReceiveChatRequests: e.target.checked })} />
                  <span className="config-label">Accept Chat Requests</span>
                </label>
                <FieldHint>When enabled, messages from users not on the allowlist are forwarded to the CTO agent for access-request review instead of being silently dropped.</FieldHint>
              </div>
              <div className="config-field">
                <label className="config-label">Request Received Message</label>
                <FieldHint>Reply sent to a user after their access request is forwarded. Leave blank to send no reply. Max 500 characters.</FieldHint>
                <textarea
                  className="config-textarea"
                  value={configEdits.requestReceivedMessage}
                  onChange={e => onEditsChange({ requestReceivedMessage: e.target.value })}
                  placeholder="e.g. Your request has been forwarded for review."
                  rows={3}
                  maxLength={500}
                />
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

              <div className="config-section-label">Memory Project Access</div>
              <div className="config-field">
                <FieldHint>Projects this agent can read from fleet-memory. Add project names explicitly, or grant wildcard (*) for full access. Changes take effect immediately (no reprovision needed).</FieldHint>
                {projectAccessLoading && <div className="config-loading">Loading…</div>}
                {!projectAccessLoading && projectAccess !== null && (
                  <>
                    {projectAccess.length === 0 && (
                      <div className="config-hint-text">No project access configured.</div>
                    )}
                    {projectAccess.map(p => (
                      <div key={p} className="config-related-row">
                        <span className="config-project-chip">{p === '*' ? '* (wildcard — all projects)' : p}</span>
                        <button className="config-remove-btn" onClick={() => p === '*' ? onProjectAccessToggleWildcard(false) : onProjectAccessRemove(p)}>✕</button>
                      </div>
                    ))}
                    <div className="config-related-row">
                      <input
                        className="config-input config-input-sm"
                        value={newProject}
                        onChange={e => setNewProject(e.target.value)}
                        onKeyDown={e => { if (e.key === 'Enter' && newProject.trim()) { onProjectAccessAdd(newProject.trim()); setNewProject('') } }}
                        placeholder="project name"
                      />
                      <button
                        className="config-add-btn"
                        onClick={() => { if (newProject.trim()) { onProjectAccessAdd(newProject.trim()); setNewProject('') } }}
                      >+ add</button>
                      {!projectAccess.includes('*') && (
                        <button className="config-add-btn" onClick={() => onProjectAccessToggleWildcard(true)}>set wildcard (*)</button>
                      )}
                    </div>
                  </>
                )}
              </div>

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
