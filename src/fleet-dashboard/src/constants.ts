import type { LocalModelSuggestion } from './types'

// ── Provider model lists ──────────────────────────────────────────────────────

export const PROVIDER_MODELS: Record<string, string[]> = {
  claude: [
    'claude-fable-5',
    'claude-opus-4-8',
    'claude-sonnet-4-6',
    'claude-opus-4-7',
    'claude-opus-4-6',
    'claude-haiku-4-5-20251001',
  ],
  codex: [
    'gpt-5',
    'gpt-5.4',
    'gpt-5.4-mini',
    'gpt-5.3-codex',
    'codex-mini-latest',
    // Local models are not listed here: "Runs on: Local server" edits them (LOCAL_MODEL_SUGGESTIONS).
    // A `zai/` prefix runs GLM on the Z.ai GLM Coding Plan through the agent's loopback
    // forwarder. Needs ZAI_CODING_PLAN_API_KEY as an Env Ref; subscriber-only use —
    // see docs/providers/codex-hosted-models.md.
    'zai/glm-5.3',
  ],
  gemini: [
    'gemini-3-pro-preview',
    'gemini-3-flash-preview',
    'gemini-2.5-pro',
    'gemini-2.5-flash',
    'gemini-2.5-flash-lite',
    'gemini-2.0-flash',
  ],
}

export const PROVIDER_DEFAULT_MODEL: Record<string, string> = {
  claude: 'claude-sonnet-4-6',
  codex: 'gpt-5',
  gemini: 'gemini-2.5-flash',
}

// Local-mode suggestion chips (#382). Public registry tags only: a chip never fills the server
// URL or the context window, which are deployment-specific operator input.
const QWEN_CODEX_EFFORT = { value: 'none', note: 'Effort set to none — recommended for Qwen3.8 on Codex' }
const QWEN_CONTEXT_HINT = 'Qwen3.8 supports up to 262144; enter the context your server is configured with.'

export const LOCAL_MODEL_SUGGESTIONS: LocalModelSuggestion[] = [
  { label: 'Qwen3.8 27B', tag: 'qwen3.8:27b', harnesses: ['claude', 'codex'], codexEffort: QWEN_CODEX_EFFORT, contextHint: QWEN_CONTEXT_HINT },
  { label: 'Qwen3.8 Flash Next', tag: 'qwen3.8-flash-next:125b-a6b-q4_K_M', harnesses: ['claude', 'codex'], codexEffort: QWEN_CODEX_EFFORT, contextHint: QWEN_CONTEXT_HINT },
  { label: 'gpt-oss 20B', tag: 'gpt-oss:20b', harnesses: ['codex'] },
]

export const CLAUDE_PERMISSION_MODES: string[] = [
  'default',
  'acceptEdits',
  'bypassPermissions',
  'plan',
  'dontAsk',
  'auto',
]

export const CODEX_SANDBOX_MODES: string[] = [
  'danger-full-access',
  'workspace-write',
  'read-only',
]

// ── Phase label mapping ───────────────────────────────────────────────────────

export const PHASE_LABELS: Record<string, { label: string; color: string }> = {
  'human-review':    { label: 'Code Review',     color: 'yellow' },
  'merge-approval':  { label: 'Merge Approval',  color: 'blue'   },
  'design-approval': { label: 'Design Approval', color: 'purple' },
  'doc-review':      { label: 'Doc Review',       color: 'green'  },
  'escalation':      { label: 'Escalation',       color: 'red'    },
  'advisory-review': { label: 'Advisory Review', color: 'teal'   },
}

// ── Advanced field defaults (used for progressive disclosure in AgentConfigModal) ──

export const ADVANCED_DEFAULTS: Record<string, string | number | boolean | null> = {
  permissionMode: 'acceptEdits',
  maxTurns: 50,
  workDir: '/workspace',
  proactiveIntervalMinutes: 0,
  groupListenMode: 'mention',
  groupDebounceSeconds: 15,
  warmupTimeoutSeconds: 60,
  shortName: '',
  showStats: true,
  prefixMessages: false,
  formattingMode: 0,
  suppressToolMessages: false,
  effort: null,
  jsonSchema: null,
  agentsJson: null,
  autoMemoryEnabled: true,
  outputStyle: null,
}

export function countCustomized(config: Record<string, unknown>): number {
  return Object.entries(ADVANCED_DEFAULTS).filter(([key, defaultVal]) => {
    const current = config[key]
    // Treat empty string and null as equivalent for nullable fields
    if (defaultVal === null) return current !== null && current !== '' && current !== undefined
    // Booleans: compare directly (checkboxes stay as booleans in ConfigEdits)
    if (typeof defaultVal === 'boolean') return current !== defaultVal
    // Numbers/strings: ConfigEdits stores numeric fields as strings in form state
    // so normalize both sides to string for comparison
    return String(current) !== String(defaultVal)
  }).length
}
