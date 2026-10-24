// Shared type definitions for the Fleet Dashboard

export interface QueuedMessageInfo {
  preview: string
  source: string
  queuedAt: string
  priority?: boolean
}

export interface BackgroundTaskSummary {
  taskId: string
  taskType: string
  description: string
  elapsedSeconds: number
  summary: string | null
}

export interface JournalHeartbeat {
  enabled: boolean
  spoolDepth: number
  oldestAgeSeconds: number | null
  dropped: number
  dead: number
  authFailed: number
  bindingFailed?: number
}

export interface AgentState {
  agentName: string
  displayName: string | null
  shortName: string | null
  model: string | null
  role: string | null
  currentTask: string | null
  currentTaskId: string | null
  reportedStatus: string
  effectiveStatus: string
  lastSeen: string
  version: string | null
  queuedCount: number
  priorityQueuedCount?: number
  queuedMessages: QueuedMessageInfo[] | null
  backgroundTasks: BackgroundTaskSummary[] | null
  containerName: string | null
  containerStartedAt: string | null
  hostPort: number | null
  journal: JournalHeartbeat | null
}

export interface TaskRecord {
  agentName: string
  taskText: string
  startedAt: string
  completedAt: string
  durationSeconds: number
}

export interface WorkflowSummary {
  workflowId: string
  runId: string
  workflowType: string
  namespace: string
  taskQueue: string | null
  status: string
  startTime: string
  closeTime: string | null
  issueNumber: number | null
  prNumber: number | null
  repo: string | null
  docPrs: string | null
  phase: string | null
  gateRef: string | null
}

export interface WorkflowEvent {
  eventId: number
  eventType: string
  timestamp: string
  activityType: string | null
  agent: string | null
  inputSummary: string | null
  outputSummary: string | null
  failureMessage: string | null
  signalName: string | null
}

export interface SignalButton {
  label: string
  payload: string
  requiresComment: boolean
}

export interface SignalDef {
  name: string
  label: string
  buttons: SignalButton[]
  validPhases: string[] | null
}

export interface SignalModalState {
  wf: WorkflowSummary
  signalName: string
  button: SignalButton
  comment: string
}

export interface Alert {
  id: string
  type: 'agent-dead' | 'agent-stale' | 'task-stuck' | 'workflow-failed' | 'info'
  message: string
  timestamp: string
  dismissed: boolean
  showToast: boolean
  workflowId?: string
  workflowNamespace?: string
  agentName?: string
}

export interface McpEndpointEntry {
  mcpName: string
  url: string
  transportType: string
}

export interface CreateForm {
  name: string
  displayName: string
  role: string
  model: string
  containerName: string
  memoryLimitMb: string
  isEnabled: boolean
  image: string
  permissionMode: string
  maxTurns: string
  workDir: string
  proactiveIntervalMinutes: string
  groupListenMode: string
  groupDebounceSeconds: string
  warmupTimeoutSeconds: string
  shortName: string
  effort: string
  jsonSchema: string
  agentsJson: string
  autoMemoryEnabled: boolean
  showStats: boolean
  prefixMessages: boolean
  formattingMode: number
  suppressToolMessages: boolean
  telegramSendOnly: boolean
  provider: string
  codexSandboxMode: string
  tools: string
  projects: string
  networks: string
  envRefs: string
  mcpEndpoints: McpEndpointEntry[]
  telegramUsers: string
  telegramGroups: string
  instructions: { name: string; loadOrder: number }[]
}

export interface AgentConfig {
  agentName: string
  model: string
  memoryLimitMb: number
  isEnabled: boolean
  image: string
  permissionMode: string
  maxTurns: number
  workDir: string
  proactiveIntervalMinutes: number
  groupListenMode: string
  groupDebounceSeconds: number
  warmupTimeoutSeconds: number
  shortName: string
  displayName: string
  showStats: boolean
  prefixMessages: boolean
  formattingMode: number
  suppressToolMessages: boolean
  effort: string
  jsonSchema: string
  agentsJson: string
  autoMemoryEnabled: boolean
  journalEnabled: boolean
  journalCrossChatEnabled: boolean
  hostPort: number | null
  telegramSendOnly: boolean
  provider: string
  codexSandboxMode: string | null
  outputStyle: string | null
  /** Origin of the local inference server for Claude or Codex, or null for cloud. */
  localBaseUrl?: string | null
  /** Older name of `localBaseUrl`; an older orchestrator returns only this. Read, never sent. */
  anthropicBaseUrl?: string | null
  /** The local server's context size in tokens, or null. Used only in local mode (#367, #382). */
  contextWindow?: number | null
  /** The one per-agent harness override (#460), verbatim, or null. Claude and Codex only. */
  harnessExtra?: string | null
  tools: { toolName: string; isEnabled: boolean }[]
  projects: string[]
  mcpEndpoints: McpEndpointEntry[]
  networks: string[]
  envRefs: string[]
  telegramUsers: number[]
  telegramGroups: number[]
  canReceiveChatRequests: boolean
  requestReceivedMessage: string | null
  mountDockerSock: boolean
  instructions: { name: string; loadOrder: number }[]
}

export interface ConfigEdits {
  model: string
  memoryLimitMb: string
  isEnabled: boolean
  image: string
  permissionMode: string
  maxTurns: string
  workDir: string
  proactiveIntervalMinutes: string
  groupListenMode: string
  groupDebounceSeconds: string
  warmupTimeoutSeconds: string
  shortName: string
  displayName: string
  showStats: boolean
  prefixMessages: boolean
  formattingMode: number
  suppressToolMessages: boolean
  effort: string
  jsonSchema: string
  agentsJson: string
  autoMemoryEnabled: boolean
  journalEnabled: boolean
  journalCrossChatEnabled: boolean
  hostPort: string
  telegramSendOnly: boolean
  provider?: string
  codexSandboxMode: string
  tools: string
  /** Assigned project names in stored order, edited only through the project table. */
  projects: string[]
  networks: string
  envRefs: string
  mcpEndpoints: McpEndpointEntry[]
  telegramUsers: string
  telegramGroups: string
  canReceiveChatRequests: boolean
  requestReceivedMessage: string
  mountDockerSock: boolean
  /** Name of an output_styles row, or '' for none — the sentinel the API clears on. */
  outputStyle: string
  /** True while editing local-server mode, even before a URL is entered. */
  localMode: boolean
  /** Local server origin, or '' — the sentinel the API clears on. */
  localBaseUrl: string
  /** Codex local server software; the `<server>/` prefix of a Codex local model. */
  localServer: CodexLocalServer
  /** Context window in tokens, or '' for not set (sent as 0, which the API clears on). */
  contextWindow: string
  /** Extra harness settings (#460), '' for none. Sent only when it differs from the loaded value. */
  harnessExtra: string
  instructions: { name: string; loadOrder: number }[]
}

/** GET /api/agents/{name}/effective-harness (#460): the computed plan and the running agent's report. */
export interface EffectiveHarness {
  agent: string
  provider: 'claude' | 'codex'
  mode: 'cloud' | 'local'
  /** `model`: the id a Codex family alias resolves to, or null when the stored model is sent as is. */
  codex: { configToml: string; threadConfig: Record<string, string | number | boolean>; model?: string | null } | null
  claude: { args: string[]; env: { name: string; value: string }[] } | null
  extra: { state: 'none' | 'applied' | 'ignored'; reason: string | null; replaced: string[]; notes?: string[] }
  planSha256: string
  computedPlanSha256: string
  startedPlanSha256: string | null
  pendingReprovision: boolean
}

/** The two local servers codex routes on (`ollama/<tag>`, `lmstudio/<tag>`). */
export type CodexLocalServer = 'ollama' | 'lmstudio'

export type LocalHarness = 'claude' | 'codex'

/** A suggestion chip in local mode: a public registry tag, never a URL or context size. */
export interface LocalModelSuggestion {
  label: string
  tag: string
  harnesses: LocalHarness[]
  /** Effort the chip also sets on Codex, with the note shown while it is in effect. */
  codexEffort?: { value: string; note: string }
  contextHint?: string
}

export interface AgentTemplateSummary {
  name: string
  displayName: string
  description: string
  defaultModel: string
  toolCount: number
  mcpCount: number
}

export interface OutputStyleSummary {
  name: string
  description: string | null
}

/** A style row with its full body and the agents assigned to it — what GET /api/output-styles returns. */
export interface OutputStyleDetail extends OutputStyleSummary {
  body: string
  agents: string[]
}

export interface InstructionSummary {
  name: string
  currentVersion: number
  isActive: boolean
  totalVersions: number
  agents: string[]
  /** UTF-8 bytes of the current version. Absent on an older orchestrator. */
  currentBytes?: number | null
}

export interface InstructionVersion {
  versionNumber: number
  content: string
  createdAt: string
  createdBy: string
  reason: string
}

export interface InstructionDetail {
  name: string
  currentVersion: number
  versions: InstructionVersion[]
}

export type WsStatus = 'connecting' | 'connected' | 'disconnected'
export type RestartState = 'idle' | 'confirming' | 'restarting' | 'success' | 'error'
export type ReprovisionState = 'idle' | 'confirming' | 'provisioning' | 'success' | 'error'
export type StopStartState = 'idle' | 'confirming' | 'pending' | 'success' | 'error'
export type WfActionState = 'idle' | 'confirming-cancel' | 'confirming-restart' | 'confirming-terminate' | 'pending' | 'success' | 'error'
export type CancelState = 'idle' | 'confirming' | 'cancelling' | 'success' | 'error'
export type ConfigSaveState = 'idle' | 'saving' | 'success' | 'error'
export type ActiveView = 'agents' | 'workflows' | 'workflow-failures' | 'instructions' | 'project-contexts' | 'output-styles' | 'wf-definitions' | 'alerts' | 'schedules' | 'namespaces' | 'repositories' | 'credentials' | 'memory' | 'epic-grants'

// ── Memory types ──────────────────────────────────────────────────────────────

export interface MemoryListItem {
  id: string
  title: string
  project: string
  type: string
  tags: string[]
  updated_at: string
}

export interface MemoryDoc {
  id: string
  title: string
  type: string
  agent: string
  project: string
  tags: string[]
  source: string
  created_at: string
  updated_at: string
  content: string
  /** #346 — absent on an older fleet-memory. */
  size_guidance?: MemoryEmbeddingSize
}

/** #346 — `GET /internal/memory/{id}` size guidance; `limit_bytes` is null when the guidance is off. */
export interface MemoryEmbeddingSize {
  unit: 'utf8Bytes'
  bytes: number
  limit_bytes: number | null
  limit_key: string
}

/** #346 — the `size` report on a memory PUT (snake_case, like the rest of the fleet-memory API). */
export interface MemorySizeReport {
  kind: 'memory'
  unit: 'utf8Bytes'
  previous_bytes: number | null
  bytes: number
  limit_bytes: number | null
  limit_key: string
  status: 'under' | 'crossed' | 'stillOver' | 'disabled'
  warning: string | null
}

/** #346 — the `size` report on an instruction or project-context write. */
export interface PromptSizeReport {
  kind: 'instruction' | 'projectContext'
  unit: 'utf8Bytes'
  previousBytes: number | null
  bytes: number
  limitBytes: number | null
  limitKey: string
  status: 'under' | 'crossed' | 'stillOver' | 'disabled'
  warning: string | null
}

/** #346 — `GET /api/prompt-size-policy`; `limitBytes` is null when that check is disabled. */
export interface PromptSizeLimits {
  unit: 'utf8Bytes'
  instruction: { limitBytes: number | null; limitKey: string }
  projectContext: { limitBytes: number | null; limitKey: string }
}

export interface MemorySearchResult {
  id: string
  title: string
  project: string
  type: string
  tags: string[]
  snippet: string
  updated_at: string
}

export interface AgentReadStatsDto {
  count: number
  lastReadAt: string
}

export interface MemoryReadStatsDto {
  memoryId: string
  total: number
  byAgent: Record<string, AgentReadStatsDto>
  lastReadAt: string
}

export interface MemoryStatsResponse {
  since: string
  entries: MemoryReadStatsDto[]
}

export interface ScheduleSummary {
  scheduleId: string
  namespace: string
  workflowType: string | null
  cronExpression: string | null
  paused: boolean
  memo: string | null
}

export interface ScheduleDetail extends ScheduleSummary {
  nextRunTime: string | null
  lastRunTime: string | null
  lastRunWorkflowId: string | null
  input: unknown
}
export type ReprovisionAllState = 'idle' | 'confirming' | 'running' | 'success' | 'error'

export interface WorkflowDefinitionSummary {
  name: string
  namespace: string
  taskQueue: string
  version: number
  isActive: boolean
  description?: string
  updatedAt: string
}

export interface WorkflowDefinitionVersion {
  version: number
  definition: string
  reason?: string
  createdAt: string
  createdBy?: string
}

export interface WorkflowDefinitionDetail extends WorkflowDefinitionSummary {
  definition: string
  versions: WorkflowDefinitionVersion[]
}


export interface ProjectContextSummary {
  name: string
  currentVersion: number
  isActive: boolean
  totalVersions: number
  agents: string[]
  /** UTF-8 bytes of the canonical context (the current version). Absent on an older orchestrator. */
  currentBytes?: number | null
}

export interface ProjectContextVersion {
  versionNumber: number
  content: string
  createdAt: string
  createdBy: string
  reason: string
}

export interface ProjectContextDetail {
  name: string
  currentVersion: number
  versions: ProjectContextVersion[]
}

export interface WorkflowTypeInfo {
  name: string
  description: string
  namespace: string
  taskQueue: string
  inputSchema: string | null
}

// ── Epic grants (#436) — orchestrator `/api/epic-grants`, JSON camelCase ─────────

export type EpicGrantStatus = 'active' | 'revoked'
/** `revoked` if revoked, else `expired` once now ≥ expiresAt (orchestrator clock), else `active`. */
export type EpicGrantEffectiveStatus = 'active' | 'expired' | 'revoked'

export interface EpicGrantView {
  id: string
  status: EpicGrantStatus
  mode?: 'delegated' | 'autonomous'
  effectiveStatus: EpicGrantEffectiveStatus
  scopeSha256: string
  driverNamespace: string
  driverWorkflowId: string
  driverRunId: string
  ctoAgent: string
  createdAt: string
  expiresAt: string
  revokedAt: string | null
  revokeReason: string | null
}

export type EpicGrantDecisionStatus = 'reserved' | 'sent' | 'send_failed'

export interface EpicGrantDecisionView {
  id: number
  grantId: string
  namespace: string
  workflowId: string
  runId: string
  gate: string
  visitId: string
  artifactRef: string
  evidence: string
  caller: string
  status: EpicGrantDecisionStatus
  createdAt: string
  updatedAt: string
}

export interface EpicGrantBudgets {
  decisions: { used: number; max: number }
  childIssues: { used: number; max: number }
  delegatedRuns: { used: number; pendingRunning: number; max: number }
  acceptanceRounds: { used: number; max: number }
}
export interface EpicPreflight {
  items: Record<string, unknown>[]
  unresolved: number
  sha256: string
  visibility: Record<string, 'public' | 'private'>
}
export interface EpicGrantDetail extends EpicGrantView {
  budgets?: EpicGrantBudgets | null
  preflight?: EpicPreflight | null
  bindings?: { repo: string; issue: number; workflowId: string; runId: string; reviewRef: string }[]
  runs?: { workflowId: string; runId: string; maxFixRound: number }[]
  scope: object
  decisions: EpicGrantDecisionView[]
}

export interface ScopeValidationDriver {
  namespace: string
  workflowId: string
  runId: string
  status: string
}

export interface ScopeValidationWorkflow {
  type: string
  version: number
  sha256: string
  hashMatches: boolean
  delegationCapable: boolean
  gates: string[]
}

export interface ScopeValidationTarget {
  repo: string
  issues: number[]
  allowPublic: boolean
  visibility: 'public' | 'private' | 'unknown'
  denied: boolean
}

/** `POST /api/epic-grants/validate` (200), and the 400 body of `POST /api/epic-grants`. */
export interface ScopeValidationReport {
  preflight?: EpicPreflight | null
  valid: boolean
  errors: string[]
  scopeSha256: string | null
  driver: ScopeValidationDriver | null
  workflows: ScopeValidationWorkflow[]
  targets: ScopeValidationTarget[]
  expiresAt: string | null
}

// ── Workflow failure accountability (#476) — orchestrator `/api/workflow-outcomes`, camelCase ──
// Failure items and cause reviews only: there is no success inbox and no success receipt here.

export type WorkflowOutcomeStage =
  | 'detected' | 'stored' | 'enqueued' | 'delivered' | 'acknowledged' | 'decision_made' | 'unresolved' | 'escalated'
export type WorkflowOutcomeDeliveryState = 'none' | 'pending' | 'delivered' | 'acknowledged' | 'unknown'
/** `state` query of the list route. */
export type WorkflowOutcomeListState = 'failures' | 'unresolved_cause' | 'all_attention'
export type WorkflowOutcomeOperation =
  | 'wait' | 'hold' | 'resume' | 'retry' | 'reassign' | 'accept_late' | 'finish' | 'close' | 'cancel'

/** Per-stage commit times; null until that stage is reached. */
export interface WorkflowOutcomeStageTimes {
  storedAt: string | null
  enqueuedAt: string | null
  deliveredAt: string | null
  acknowledgedAt: string | null
  decisionMadeAt: string | null
}

export interface WorkflowOutcomeLastDecision {
  operation: string
  owner: string
  reason: string
  dueAt: string | null
  status: string
  decidedVia: string
  createdAt: string
}

export interface WorkflowOutcomeView {
  id: string
  version: number
  namespace: string
  workflowId: string
  runId: string
  workflowType: string
  status: string
  classification: string
  reasonCode: string | null
  terminalEventType: string | null
  terminalEventId: number | null
  startTime: string | null
  closeTime: string | null
  project: string | null
  accessNeeded: boolean
  coordinator: string | null
  taskOwner: string | null
  evidenceState: string | null
  failureIncidentId: string | null
  parentWorkflowId: string | null
  parentRunId: string | null
  previousRunId: string | null
  nextRunId: string | null
  stage: string
  stages: WorkflowOutcomeStageTimes
  /** Kept as a string: an unrecognised value is shown as unknown, never as delivered. */
  deliveryState: string
  turnAmbiguous: boolean
  lastDecision: WorkflowOutcomeLastDecision | null
  recovery: { operationId: string; state: string } | null
  dueAt: string | null
  attentionOverdue: boolean
}

export interface WorkflowOutcomeCounts {
  failures: number
  unresolvedCauses: number
  coverageGaps: number
}

export interface WorkflowOutcomeListPage {
  items: WorkflowOutcomeView[]
  nextCursor: string | null
  namespaceErrors: { namespace: string; error: string }[]
  counts: WorkflowOutcomeCounts
}

export interface WorkflowOutcomeDecisionView {
  decisionId: string | null
  version: number | null
  operation: string
  owner: string
  reason: string
  dueAt: string | null
  status: string
  decidedVia: string
  createdAt: string
  evidenceRefs: string[]
}

/** One batch this run (at `version`) is a member of. `nonce` is needed to read the receipt. */
export interface WorkflowOutcomeBatchMembership {
  batchId: string
  generation: number
  state: string
  deliveredAt: string | null
  ackedAt: string | null
  nonce: string | null
  version: number | null
}

export interface WorkflowOutcomeObservationView {
  eventId: number | null
  eventType: string
  classification: string | null
  reasonCode: string | null
  observedAt: string | null
}

export interface WorkflowOutcomeDetail extends WorkflowOutcomeView {
  decisions: WorkflowOutcomeDecisionView[]
  batches: WorkflowOutcomeBatchMembership[]
  observations: WorkflowOutcomeObservationView[]
  evidenceRefs: string[]
}

export interface WorkflowOutcomeCoverageGap {
  start: string | null
  end: string | null
  reason: string
  unknownRunCount: number | null
}

export interface WorkflowOutcomeNamespaceCoverage {
  namespace: string
  /** healthy | degraded | pending | disabled; anything else is treated as not healthy. */
  state: string
  retentionSeconds: number | null
  enrolledAt: string | null
  lastRecentPassAt: string | null
  lastFullPassAt: string | null
  knownPending: number
  gaps: WorkflowOutcomeCoverageGap[]
}

export interface WorkflowOutcomeCoverage {
  enabled: boolean
  degraded: boolean
  namespaces: WorkflowOutcomeNamespaceCoverage[]
}

export interface WorkflowOutcomeHealthIncident {
  component: string
  outageKey: string
  state: string
  reason: string
  namespace: string | null
  firstSeenAt: string | null
  lastSeenAt: string | null
}

export interface WorkflowOutcomeHealth {
  state: string
  incidents: WorkflowOutcomeHealthIncident[]
}

export interface WorkflowOutcomeItemRef {
  id: string
  version: number
}

/** `GET /batches/{id}/receipt?nonce=` — exact delivered and acknowledged member versions. */
export interface WorkflowOutcomeReceipt {
  batchId: string
  generation: number
  nonce: string
  itemVersionsHash: string
  items: WorkflowOutcomeItemRef[]
  deliveredVersions: WorkflowOutcomeItemRef[]
  ackedVersions: WorkflowOutcomeItemRef[]
  receiptHash: string | null
  deliveredAt: string | null
  acknowledgedAt: string | null
}

/** `POST /batches/{id}/ack`. Never a wildcard: only the listed exact versions. */
export interface WorkflowOutcomeAckRequest {
  generation: number
  nonce: string
  items: WorkflowOutcomeItemRef[]
  taskId: string | null
}

/** `POST /{id}/decisions`. Record-only: the server never executes it. */
export interface WorkflowOutcomeDecisionRequest {
  expectedVersion: number
  operation: WorkflowOutcomeOperation
  owner: string
  reason: string
  evidenceRefs: string[]
  dueAt: string | null
  taskRef: string | null
  artifactRef: string | null
  batchId: string | null
  generation: number | null
}

/** `POST /{id}/recovery-plan` — a read-only recheck, never a reservation or an approval. */
export interface WorkflowRecoveryPlanResult {
  allowed: boolean
  operation: string | null
  blockedReason: string | null
  missingProofs: string[]
  checkedAt: string | null
}
