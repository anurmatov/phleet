import type { LocalModelSuggestion } from './types'

// ── Providers ─────────────────────────────────────────────────────────────────

/** The provider select of both the config and the create modal, in display order. */
export const PROVIDER_OPTIONS: { value: string; label: string }[] = [
  { value: 'claude', label: 'Claude (Anthropic)' },
  { value: 'codex', label: 'Codex (OpenAI)' },
  { value: 'gemini', label: 'Gemini (Google)' },
]

// ── Provider model lists ──────────────────────────────────────────────────────

// Cloud Claude and Codex list family aliases (#460, owner direction): a dated version goes stale.
// Claude Code resolves opus/sonnet/haiku itself; Codex aliases are mapped to the current model id in
// HarnessDefaults.CodexModelAliases, and the effective settings show the resolved id. A dated, hosted
// (zai/) or other id is entered through the picker's custom entry (#465); a stored one keeps working,
// opens in the custom input and is never rewritten.
export const PROVIDER_MODELS: Record<string, string[]> = {
  claude: ['opus', 'sonnet', 'haiku'],
  codex: ['sol', 'terra', 'luna'],
  // Exactly what `agy models` prints (Antigravity CLI 1.5.14), in its order. The slug is passed to
  // `agy --model` as is and carries the reasoning effort; Gemini has no separate effort.
  gemini: [
    'gemini-4-neon-high',
    'gemini-4-neon-xhigh',
    'gemini-4-neon-medium',
    'gemini-4-neon-low',
    'gemini-4-carbon-high',
    'gemini-4-carbon-medium',
    'gemini-4-carbon-low',
    'gemini-4-hydrogen-high',
    'gemini-4-hydrogen-xhigh',
    'gemini-4-hydrogen-medium',
    'gemini-4-hydrogen-low',
    'gemini-4-argon-high',
    'gemini-4-argon-medium',
    'gemini-4-argon-low',
    'gemini-3.8-flash-high',
    'gemini-3.8-flash-medium',
    'gemini-3.8-flash-low',
    'gemini-3.7-flash-high',
    'gemini-3.7-flash-medium',
    'gemini-3.7-flash-low',
    'gemini-3.6-flash-high',
    'gemini-3.6-flash-medium',
    'gemini-3.6-flash-low',
    'gemini-3.1-pro-high',
    'gemini-3.1-pro-low',
  ],
}

export const PROVIDER_DEFAULT_MODEL: Record<string, string> = {
  claude: 'sonnet',
  codex: 'sol',
  gemini: 'gemini-3.1-pro-low',
}
// Local-mode suggestion chips never fill the server URL or context window.
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
  'acceptance-running': { label: 'Acceptance Running', color: 'blue' },
  'acceptance-classifying': { label: 'Acceptance Classification', color: 'purple' },
  'acceptance-continue': { label: 'Acceptance Continue', color: 'yellow' },
  'acceptance-blocked': { label: 'Acceptance Blocked', color: 'red' },
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

/** How many advanced fields differ from their defaults; only the fields in `keys` when given. */
export function countCustomized(config: Record<string, unknown>, keys?: readonly string[]): number {
  return Object.entries(ADVANCED_DEFAULTS).filter(([key, defaultVal]) => {
    if (keys && !keys.includes(key)) return false
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
