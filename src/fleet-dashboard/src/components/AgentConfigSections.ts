import { countCustomized } from '../constants.ts'

// The agent config dialog's sections (#460 Part 2): which field belongs where, which providers see
// it, and each section's "N customized" count. Layout only — every field keeps its component, hint,
// validation and save behaviour. Kept free of JSX so `node --test` can run it.

export type SectionId = 'general' | 'model' | 'instructions' | 'tools' | 'telegram' | 'advanced'

/** One field, by its `ConfigEdits` key (or a dialog-only id), and the providers that show it (all when absent). */
export interface SectionField {
  key: string
  only?: readonly string[]
}

export interface ConfigSection {
  id: SectionId
  label: string
  fields: readonly SectionField[]
}

const CLAUDE = ['claude'] as const
const CODEX = ['codex'] as const
const LOCAL_HARNESSES = ['claude', 'codex'] as const
const HARNESS_EXTRA = ['claude', 'codex'] as const

export const CONFIG_SECTIONS: readonly ConfigSection[] = [
  { id: 'general', label: 'General', fields: [{ key: 'isEnabled' }, { key: 'shortName' }] },
  {
    id: 'model', label: 'Model & runtime', fields: [
      { key: 'provider' },
      { key: 'model' },
      // LocalModelFields: runs on, server URL or server, model tag, context window.
      { key: 'localMode', only: LOCAL_HARNESSES },
      { key: 'localBaseUrl', only: LOCAL_HARNESSES },
      { key: 'localServer', only: CODEX },
      { key: 'contextWindow', only: LOCAL_HARNESSES },
      { key: 'effort', only: LOCAL_HARNESSES },
      { key: 'codexSandboxMode', only: CODEX },
      { key: 'autoMemoryEnabled', only: CLAUDE },
      { key: 'effectiveHarness', only: HARNESS_EXTRA },
    ],
  },
  {
    id: 'instructions', label: 'Instructions & projects', fields: [
      { key: 'instructions' }, { key: 'projects' }, { key: 'outputStyle' }, { key: 'memoryProjectAccess' },
    ],
  },
  { id: 'tools', label: 'Tools & MCP', fields: [{ key: 'tools' }, { key: 'mcpEndpoints' }] },
  {
    id: 'telegram', label: 'Telegram', fields: [
      { key: 'telegramUsers' }, { key: 'telegramGroups' }, { key: 'groupListenMode' },
      { key: 'groupDebounceSeconds' }, { key: 'proactiveIntervalMinutes' }, { key: 'showStats' },
      { key: 'prefixMessages' }, { key: 'formattingMode' }, { key: 'suppressToolMessages' },
      { key: 'telegramSendOnly' }, { key: 'journalEnabled' }, { key: 'journalCrossChatEnabled' },
      { key: 'canReceiveChatRequests' }, { key: 'requestReceivedMessage' },
    ],
  },
  {
    id: 'advanced', label: 'Advanced', fields: [
      { key: 'image' }, { key: 'memoryLimitMb' }, { key: 'workDir' },
      { key: 'permissionMode', only: CLAUDE }, { key: 'maxTurns', only: CLAUDE },
      { key: 'warmupTimeoutSeconds' }, { key: 'jsonSchema', only: CLAUDE }, { key: 'agentsJson', only: CLAUDE },
      { key: 'networks' }, { key: 'envRefs' }, { key: 'mountDockerSock' },
      { key: 'harnessExtra', only: HARNESS_EXTRA },
    ],
  },
]

/** The section the dialog opens on: the one the operator edits most. */
export const DEFAULT_SECTION: SectionId = 'model'

/** The fields of `section` that `provider` shows. */
export function visibleFields(section: ConfigSection, provider: string): SectionField[] {
  return section.fields.filter(f => !f.only || f.only.includes(provider))
}

/** Today's "N customized" count (`countCustomized`) over this section's fields only. */
export function sectionCustomizedCount(section: ConfigSection, edits: Record<string, unknown>): number {
  return countCustomized(edits, section.fields.map(f => f.key))
}
