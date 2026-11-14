import { configOf } from './support/fakeFleet'
// Synthetic answers for every route the dashboard reads, so each page can be drawn with
// something on it. Used by the visual tour (tour.browser.spec.ts). Nothing here is real: no
// address, no name, no key.

const minutesAgo = (m: number) => new Date(Date.now() - m * 60_000).toISOString()

const agent = (over: Record<string, unknown>) => ({
  agentName: 'agent', displayName: null, shortName: null, model: 'claude-opus-5-5', role: 'developer',
  currentTask: null, currentTaskId: null, reportedStatus: 'idle', effectiveStatus: 'idle',
  lastSeen: minutesAgo(0), version: '1.42.0', queuedCount: 0, priorityQueuedCount: 0, queuedMessages: [],
  backgroundTasks: [], containerName: 'fleet-agent', containerStartedAt: minutesAgo(600), hostPort: null,
  journal: null, ...over,
})

export const agents = [
  agent({ agentName: 'acto', displayName: 'Acto', role: 'co-cto', effectiveStatus: 'busy', reportedStatus: 'busy',
    currentTask: 'Reviewing the design for issue #412: split the provisioning service into plan and apply', currentTaskId: 't-1',
    queuedCount: 2, priorityQueuedCount: 1, containerName: 'fleet-acto',
    queuedMessages: [
      { preview: 'Can you look at the failing migration test?', source: 'telegram', queuedAt: minutesAgo(3), priority: true },
      { preview: 'Workflow callback: consensus round 2 finished', source: 'temporal', queuedAt: minutesAgo(1) },
    ] }),
  agent({ agentName: 'adev', displayName: 'Adev', role: 'developer', effectiveStatus: 'busy', reportedStatus: 'busy',
    currentTask: 'Implementing PR #415 — dashboard: redraw the agent list', currentTaskId: 't-2', containerName: 'fleet-adev',
    backgroundTasks: [{ taskId: 'bg-1', taskType: 'bash', description: 'dotnet test tests/Fleet.Orchestrator.Tests', elapsedSeconds: 184, summary: null }] }),
  agent({ agentName: 'adev-codex', displayName: 'Adev Codex', role: 'developer', model: 'gpt-5.5-codex', containerName: 'fleet-adev-codex' }),
  agent({ agentName: 'reviewer', displayName: 'Reviewer', role: 'reviewer', model: 'claude-sonnet-5-5', effectiveStatus: 'stale',
    lastSeen: minutesAgo(4), containerName: 'fleet-reviewer' }),
  agent({ agentName: 'planner', displayName: 'Planner', role: 'product', model: 'claude-opus-5-5', effectiveStatus: 'dead',
    reportedStatus: 'idle', lastSeen: minutesAgo(52), containerName: 'fleet-planner' }),
  agent({ agentName: 'scribe', displayName: 'Scribe', role: 'docs', model: 'ollama/gemma4:27b', effectiveStatus: 'stopped',
    reportedStatus: 'stopped', lastSeen: minutesAgo(900), containerName: 'fleet-scribe', version: null }),
]

const run = (over: Record<string, unknown>) => ({
  workflowId: 'wf', runId: 'run-1', workflowType: 'UwePrImplementationWorkflow', namespace: 'fleet', taskQueue: 'fleet',
  status: 'Running', startTime: minutesAgo(90), closeTime: null, issueNumber: null, prNumber: null, repo: 'example/project',
  docPrs: null, phase: null, gateRef: null, ...over,
})

export const workflows = [
  run({ workflowId: 'pr-impl-415', runId: 'r-415', prNumber: 415, issueNumber: 412, phase: 'merge-approval', gateRef: 'g-415', startTime: minutesAgo(190) }),
  run({ workflowId: 'design-418', runId: 'r-418', workflowType: 'UweDesignWorkflow', issueNumber: 418, phase: 'design-approval', gateRef: 'g-418', startTime: minutesAgo(64) }),
  run({ workflowId: 'pr-impl-409', runId: 'r-409', prNumber: 409, issueNumber: 401, phase: 'implementation', startTime: minutesAgo(31) }),
  run({ workflowId: 'consensus-402', runId: 'r-402', workflowType: 'UweConsensusReviewWorkflow', prNumber: 402, phase: 'review-round-2', startTime: minutesAgo(12) }),
  run({ workflowId: 'human-input-123e4567-e89b-42d3-a456-426614174000', runId: 'r-hi', workflowType: 'UweHumanInputWorkflow', repo: null,
    phase: 'request-input', gateRef: 'r-hi', startTime: minutesAgo(7) }),
]

export const completed = [
  run({ workflowId: 'pr-impl-398', runId: 'r-398', prNumber: 398, issueNumber: 390, status: 'Completed', startTime: minutesAgo(400), closeTime: minutesAgo(250) }),
  run({ workflowId: 'pr-impl-396', runId: 'r-396', prNumber: 396, status: 'Failed', startTime: minutesAgo(700), closeTime: minutesAgo(640) }),
  run({ workflowId: 'design-391', runId: 'r-391', workflowType: 'UweDesignWorkflow', issueNumber: 391, status: 'Canceled', startTime: minutesAgo(900), closeTime: minutesAgo(880) }),
  run({ workflowId: 'doc-review-388', runId: 'r-388', workflowType: 'UweDocReviewWorkflow', prNumber: 388, status: 'Completed', startTime: minutesAgo(1200), closeTime: minutesAgo(1100) }),
]

const approve = (extra: string) => [
  { label: 'Approve' + extra, payload: '{"Decision":"approved"}', requiresComment: false },
  { label: 'Request changes', payload: '{"Decision":"changes_requested"}', requiresComment: true },
]

export const signals = {
  UwePrImplementationWorkflow: [{ name: 'merge-approval', label: 'merge approval', validPhases: ['merge-approval'], buttons: approve(' merge') }],
  UweDesignWorkflow: [{ name: 'design-approval', label: 'design approval', validPhases: ['design-approval'], buttons: approve('') }],
  UweHumanInputWorkflow: [{ name: 'request-input', label: 'human input', validPhases: ['request-input'],
    buttons: [{ label: 'Answer', payload: '{}', requiresComment: false }] }],
}

const versioned = (name: string, agentsFor: string[], bytes: number, versions = 3) =>
  ({ name, currentVersion: versions, isActive: true, totalVersions: versions, agents: agentsFor, currentBytes: bytes })

export const instructions = [
  versioned('base', ['acto', 'adev', 'adev-codex', 'reviewer', 'planner', 'scribe'], 4210, 12),
  versioned('co-cto', ['acto'], 8930, 31),
  versioned('developer', ['adev', 'adev-codex'], 11840, 18),
  versioned('reviewer', ['reviewer'], 5120, 6),
  { ...versioned('legacy-triage', [], 1900, 2), isActive: false },
]

export const projectContexts = [
  versioned('project', ['acto', 'adev', 'adev-codex', 'reviewer'], 7650, 9),
  versioned('ios-client', ['adev'], 3120, 2),
]

export const outputStyles = [
  { name: 'plain', description: 'Short answers, no headings', body: 'Answer in plain sentences.', agents: ['acto', 'reviewer'] },
  { name: 'report', description: 'Findings first, then evidence', body: 'Lead with the finding.', agents: ['planner'] },
]

export const workflowTypes = [
  { name: 'UwePrImplementationWorkflow', description: 'Implement an issue as a pull request', namespace: 'fleet', taskQueue: 'fleet',
    inputSchema: JSON.stringify({ type: 'object', required: ['IssueNumber'], properties: { IssueNumber: { type: 'integer' }, Repo: { type: 'string' } } }) },
  { name: 'UweDesignWorkflow', description: 'Write and review a design', namespace: 'fleet', taskQueue: 'fleet', inputSchema: null },
  { name: 'UweHumanInputWorkflow', description: 'Ask a person one question', namespace: 'fleet', taskQueue: 'fleet', inputSchema: null },
]

export const workflowDefinitions = [
  { name: 'UwePrImplementationWorkflow', namespace: 'fleet', taskQueue: 'fleet', version: 14, isActive: true, description: 'Implement an issue as a pull request', updatedAt: minutesAgo(3000) },
  { name: 'UweDesignWorkflow', namespace: 'fleet', taskQueue: 'fleet', version: 7, isActive: true, description: 'Write and review a design', updatedAt: minutesAgo(9000) },
  { name: 'UweConsensusReviewWorkflow', namespace: 'fleet', taskQueue: 'fleet', version: 5, isActive: true, description: 'Reviewers agree on a verdict', updatedAt: minutesAgo(20000) },
  { name: 'UweOldTriage', namespace: 'fleet', taskQueue: 'fleet', version: 2, isActive: false, description: 'Retired', updatedAt: minutesAgo(90000) },
]

export const schedules = [
  { scheduleId: 'nightly-dependency-check', namespace: 'fleet', workflowType: 'UweDependencyCheckWorkflow', cronExpression: '0 3 * * *', paused: false, memo: 'Nightly' },
  { scheduleId: 'weekly-memory-review', namespace: 'fleet', workflowType: 'UweMemoryReviewWorkflow', cronExpression: '0 9 * * 1', paused: true, memo: 'Paused while memory is being reorganised' },
]

export const repositories = [
  { name: 'project', fullName: 'example/project', isActive: true },
  { name: 'ios-client', fullName: 'example/ios-client', isActive: true },
  { name: 'old-site', fullName: 'example/old-site', isActive: false },
]

export const memories = [
  { id: 'm-1', title: 'How deploys reach the host', project: 'project', type: 'reference', tags: ['deploy', 'ops'], updated_at: minutesAgo(300) },
  { id: 'm-2', title: 'Provider CLI pins and why each one is where it is', project: 'project', type: 'decision', tags: ['providers'], updated_at: minutesAgo(1500) },
  { id: 'm-3', title: 'Review checklist the reviewers agreed on', project: 'project', type: 'learning', tags: ['review'], updated_at: minutesAgo(4000) },
  { id: 'm-4', title: 'iOS client: what CI can and cannot prove', project: 'ios-client', type: 'reference', tags: ['ios', 'ci'], updated_at: minutesAgo(90) },
]

export const memoryDoc = {
  id: 'm-1', title: 'How deploys reach the host', type: 'reference', agent: 'acto', project: 'project', tags: ['deploy', 'ops'],
  source: 'conversation', created_at: minutesAgo(9000), updated_at: minutesAgo(300),
  content: '# How deploys reach the host\n\nA merge to `main` builds the images. The deploy pipeline pulls them and recreates the services **one at a time**.\n\n- the orchestrator first\n- then the bridge\n- agents last, by reprovision\n\n```\n./deploy.sh --service orchestrator\n```\n',
}

export const devices = {
  devices: [
    { deviceId: 'd-1', kind: 'browser', name: 'This browser', registeredAt: minutesAgo(5000), lastSeenAt: minutesAgo(0), revokedAt: null, current: true },
    { deviceId: 'd-2', kind: 'app', name: 'iPhone', registeredAt: minutesAgo(2000), lastSeenAt: minutesAgo(14), revokedAt: null, current: false },
  ],
}

/** The answers that do not depend on a name in the path. */
export const routes: Record<string, unknown> = {
  '/api/auth/session': { authRequired: true, signedIn: true, kind: 'browser', userId: 'owner', displayName: 'Owner', deviceId: 'd-1' },
  '/api/agents': agents,
  '/api/workflows': workflows,
  '/api/workflows/completed': completed,
  '/api/workflows/failures': completed.filter(w => w.status === 'Failed'),
  '/api/signals': signals,
  '/api/namespaces': ['fleet', 'research'],
  '/api/setup/status': { telegram: { configured: true, groupChatEnabled: true }, gitHub: { configured: true } },
  '/api/instructions': instructions,
  '/api/project-contexts': projectContexts,
  '/api/output-styles': outputStyles,
  '/api/workflow-types': workflowTypes,
  '/api/workflow-definitions': workflowDefinitions,
  '/api/schedules': schedules,
  '/api/repositories': repositories,
  '/api/memory': memories,
  '/api/memory/m-1': memoryDoc,
  '/api/memory/stats/reads': { since: minutesAgo(10000), entries: [] },
  '/api/prompt-size-policy': { unit: 'utf8Bytes', instruction: { limitBytes: 10000, limitKey: 'k' }, projectContext: { limitBytes: 10000, limitKey: 'k' } },
  '/api/chat/agents': { available: true, agents: [{ agentName: 'acto', displayName: 'Acto' }, { agentName: 'adev', displayName: 'Adev' }] },
  '/api/auth/devices': devices,
  '/api/epic-grants': [],
  '/api/credential-files': [
    { id: 1, name: 'deploy-key', type: 'ssh', fileName: 'id_ed25519', sizeBytes: 411, createdAt: minutesAgo(9000),
      mounts: [{ id: 1, agentName: 'adev', mountPath: '/root/.ssh/id_ed25519', mode: '0400' }] },
  ],
  '/api/credentials/connections': {
    telegram: { configured: true, groupChatEnabled: true, maskedCtoBotToken: '7•••••••Qx', maskedNotifierBotToken: null,
      groupChatId: '-100200300', userId: '1001', lastValidatedUtc: minutesAgo(300) },
    gitHub: { configured: false, maskedAppId: null, maskedPrivateKey: null, lastValidatedUtc: null },
  },
  '/api/config/all': { TELEGRAM_BOT_TOKEN: '••••', GITHUB_APP_ID: '12345' },
  '/api/secret-bindings': { enabled: false, fault: null, subnet: null, address: null, caPresent: false, bindings: [] },
  '/api/agent-templates': [],
  '/api/push': { enabled: false, devices: 0 },
  '/api/comms/journal/status': { status: 'available', schemaVersion: 7,
    observers: [{ observer: 'acto', messages: 1840, lastIngestAt: minutesAgo(1) }, { observer: 'adev', messages: 912, lastIngestAt: minutesAgo(4) }],
    rejectedSinceStart: {}, gc: { lastRunAt: minutesAgo(50), deletedSinceStart: { messages: 0, conversations: 0 }, failedSinceStart: 0 },
    ingest: { samples: 200, p50Ms: 4, p95Ms: 11 }, media: null },
}

const versionsOf = (name: string, count: number) => Array.from({ length: Math.min(count, 4) }, (_, i) => {
  const n = count - i
  return { versionNumber: n, content: `# ${name}\n\nYou work in this repository. Read the issue before the code.\n\n- Keep changes small (v${n}).\n- Say what you verified.\n`,
    createdAt: new Date(Date.now() - (i + 1) * 86_400_000).toISOString().slice(0, 16).replace('T', ' '), createdBy: i % 2 ? 'acto' : 'owner',
    reason: i === 0 ? 'Tighten the review rule' : i === 1 ? 'Add the verification line' : null }
})

const history = [
  { eventId: 1, eventType: 'WorkflowExecutionStarted', timestamp: minutesAgo(31), inputSummary: '{"IssueNumber":401,"Repo":"example/project"}' },
  { eventId: 5, eventType: 'ActivityTaskScheduled', timestamp: minutesAgo(30), activityType: 'DelegateToAgent', agent: 'adev', inputSummary: 'Implement issue #401' },
  { eventId: 9, eventType: 'ActivityTaskCompleted', timestamp: minutesAgo(12), activityType: 'DelegateToAgent', agent: 'adev', outputSummary: 'Opened PR #409' },
  { eventId: 12, eventType: 'ActivityTaskScheduled', timestamp: minutesAgo(11), activityType: 'DelegateToAgent', agent: 'reviewer', inputSummary: 'Review PR #409' },
]

/** What a GET answers: the fixed routes above, then the ones with a name in the path. */
export function answer(path: string): unknown {
  if (path in routes) return routes[path]
  let m = path.match(/^\/api\/agents\/([^/]+)\/(history|config|project-access)$/)
  if (m) {
    if (m[2] === 'config') return { ...configOf(m[1]), model: 'claude-opus-5-5', projects: ['project'] }
    if (m[2] === 'history') return [
      { taskText: 'Reviewed the design for issue #398 and asked for a smaller first step', startedAt: minutesAgo(190), durationSeconds: 840 },
      { taskText: 'Answered a question about the release checklist', startedAt: minutesAgo(400), durationSeconds: 95 },
    ]
    return { projects: ['project'] }
  }
  m = path.match(/^\/api\/(instructions|project-contexts)\/([^/]+)$/)
  if (m) {
    const item = (m[1] === 'instructions' ? instructions : projectContexts).find(v => v.name === m![2])
    return item && { name: item.name, currentVersion: item.currentVersion, versions: versionsOf(item.name, item.currentVersion) }
  }
  if (/^\/api\/workflows\/[^/]+\/history\//.test(path)) return history
  if (/\/effective-harness$/.test(path)) return undefined
  return []
}

/** Every page of the dashboard, by the hash that opens it. */
export const pages = [
  'agents', 'workflows', 'chat', 'instructions', 'project-contexts', 'output-styles', 'repositories',
  'credentials', 'memory', 'wf-definitions', 'schedules', 'namespaces', 'epic-grants', 'alerts',
] as const

/** A state worth seeing that a page does not open in: a row opened, a dialog up. */
export const scenes = [
  { name: 'agent-open', hash: 'agents' },
  { name: 'agent-config', hash: 'agents' },
  { name: 'new-agent', hash: 'agents' },
  { name: 'logs', hash: 'agents' },
  { name: 'instruction-open', hash: 'instructions' },
  { name: 'new-instruction', hash: 'instructions' },
  { name: 'start-workflow', hash: 'workflows' },
  { name: 'workflow-history', hash: 'workflows' },
  { name: 'signal-comment', hash: 'workflows' },
  { name: 'confirm-bar', hash: 'workflows' },
  { name: 'new-schedule', hash: 'schedules' },
  { name: 'more-sheet', hash: 'agents' },
] as const
