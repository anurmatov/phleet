import type { Page, Route, WebSocketRoute } from '@playwright/test'

// A stand-in for the orchestrator, held in the test process. Only HTTP and WebSocket IO is
// substituted: App, every view and every handler are the production modules. Writes change the
// state the next read returns, so a test can follow a save through to what the page shows after it.

export interface Call { method: string; path: string; query: string; body: unknown }

interface Version { versionNumber: number; content: string; createdAt: string; createdBy: string; reason: string }
interface Versioned { name: string; currentVersion: number; isActive: boolean; agents: string[]; versions: Version[] }
interface DefVersion { version: number; definition: string; reason?: string; createdAt: string; createdBy?: string }
interface Definition {
  name: string; namespace: string; taskQueue: string; version: number; isActive: boolean
  description?: string; updatedAt: string; definition: string; versions: DefVersion[]
}

const T0 = '2026-11-12T12:00:00Z'

function agent(agentName: string, over: Record<string, unknown> = {}) {
  return {
    agentName, displayName: agentName, shortName: agentName, model: 'sonnet', role: 'developer',
    currentTask: null, currentTaskId: null, reportedStatus: 'idle', effectiveStatus: 'idle',
    lastSeen: new Date().toISOString(), version: null, queuedCount: 0, queuedMessages: [], backgroundTasks: [],
    containerName: `fleet-${agentName}`, containerStartedAt: null, hostPort: null, journal: null, ...over,
  }
}

function workflow(workflowId: string, workflowType: string, phase: string | null, over: Record<string, unknown> = {}) {
  return {
    namespace: 'fleet', workflowId, runId: `run-${workflowId}`, gateRef: null, workflowType, status: 'Running', phase,
    taskQueue: 'fleet', startTime: T0, closeTime: null, issueNumber: null, prNumber: null, repo: null, docPrs: null, ...over,
  }
}

function versioned(name: string, contents: string[], agents: string[] = [], isActive = true): Versioned {
  return {
    name, isActive, agents, currentVersion: contents.length,
    versions: contents.map((content, i) => ({
      versionNumber: i + 1, content, createdAt: T0, createdBy: 'owner', reason: i === 0 ? 'initial' : `revision ${i + 1}`,
    })).reverse(),
  }
}

export const REVIEW_DEFINITION = JSON.stringify({ type: 'sequence', steps: [{ type: 'sleep', seconds: 60 }] }, null, 2)
export const EDITED_DEFINITION = JSON.stringify({ type: 'sequence', steps: [{ type: 'sleep', seconds: 120 }] }, null, 2)

export function configOf(name: string) {
  return {
    name, model: 'sonnet', memoryLimitMb: 4096, isEnabled: true, image: '', permissionMode: 'acceptEdits', maxTurns: 50,
    workDir: '/workspace', proactiveIntervalMinutes: 0, groupListenMode: 'mention', groupDebounceSeconds: 15,
    warmupTimeoutSeconds: 60, shortName: name, showStats: false, prefixMessages: false, formattingMode: 0,
    suppressToolMessages: false, effort: '', jsonSchema: '', agentsJson: '', autoMemoryEnabled: true,
    journalEnabled: false, journalCrossChatEnabled: false, chatEnabled: true, hostPort: null, telegramSendOnly: false,
    provider: 'claude', codexSandboxMode: null, outputStyle: null, localBaseUrl: null, contextWindow: null,
    harnessExtra: null, tools: [{ toolName: 'Read', isEnabled: true }, { toolName: 'Bash', isEnabled: true }],
    projects: ['fleet'], mcpEndpoints: [], networks: ['fleet-net'], envRefs: ['GH_TOKEN'], telegramUsers: [1001],
    telegramGroups: [], canReceiveChatRequests: false, requestReceivedMessage: null, mountDockerSock: false,
    instructions: [{ name: 'base', loadOrder: 0 }],
  }
}

export class FakeFleet {
  calls: Call[] = []
  /** `"METHOD /path"` → the refusal that request gets. */
  refuse: Record<string, { status: number; json: unknown }> = {}
  socket: WebSocketRoute | undefined
  logSocket: WebSocketRoute | undefined

  agents = [
    agent('alpha'),
    agent('beta', { currentTask: 'Implement the review step', currentTaskId: 'wf-build-2/implement', reportedStatus: 'busy', effectiveStatus: 'busy' }),
    agent('gamma', { effectiveStatus: 'dead', reportedStatus: 'idle' }),
  ]
  workflows = [
    workflow('wf-review-1', 'ReviewWorkflow', 'design-review'),
    workflow('wf-build-2', 'BuildWorkflow', null),
  ]
  completed = [workflow('wf-done-3', 'BuildWorkflow', null, { status: 'Completed', closeTime: T0 })]
  signals = {
    ReviewWorkflow: [{
      name: 'design-approval', label: 'design', validPhases: ['design-review'],
      buttons: [
        { label: 'Approve', payload: '{"Value":"approved"}', requiresComment: false },
        { label: 'Reject', payload: '{"Value":"rejected"}', requiresComment: true },
      ],
    }],
  }
  instructions: Versioned[] = [
    versioned('base', ['base v1 content', 'base v2 content'], ['alpha']),
    versioned('reviewer', ['reviewer v1 content']),
    versioned('retired', ['retired content'], [], false),
  ]
  contexts: Versioned[] = [
    versioned('fleet', ['fleet context v1', 'fleet context v2'], ['alpha']),
    versioned('docs', ['docs context v1']),
  ]
  definitions: Definition[] = [{
    name: 'ReviewWorkflow', namespace: 'fleet', taskQueue: 'fleet', version: 3, isActive: true, description: 'Reviews a change',
    updatedAt: T0, definition: REVIEW_DEFINITION,
    versions: [{ version: 2, definition: JSON.stringify({ type: 'sequence', steps: [{ type: 'sleep', seconds: 30 }] }, null, 2), reason: 'shorter wait', createdAt: T0, createdBy: 'owner' }],
  }]
  history: Record<string, unknown[]> = {
    alpha: [
      { agentName: 'alpha', taskText: 'Wrote the release notes', startedAt: T0, completedAt: T0, durationSeconds: 42 },
      { agentName: 'alpha', taskText: 'Reviewed the migration', startedAt: T0, completedAt: T0, durationSeconds: 7 },
    ],
  }
  projectAccess: Record<string, string[]> = { alpha: ['fleet'] }

  writes(method: string, pathPrefix: string): Call[] {
    return this.calls.filter(c => c.method === method && c.path.startsWith(pathPrefix))
  }

  /** Pushes an agent heartbeat over the dashboard socket, as the orchestrator does. */
  pushAgent(agentName: string, over: Record<string, unknown>) {
    const current = this.agents.find(a => a.agentName === agentName) ?? agent(agentName)
    const next = { ...current, ...over }
    this.agents = [...this.agents.filter(a => a.agentName !== agentName), next]
    this.socket!.send(JSON.stringify(next))
  }

  pushWorkflows(list: unknown[]) {
    this.workflows = list as typeof this.workflows
    this.socket!.send(JSON.stringify({ type: 'workflows', data: list }))
  }

  async install(page: Page) {
    await page.route('**/*', route => {
      const host = new URL(route.request().url()).hostname
      return host === '127.0.0.1' || host === 'localhost' ? route.continue() : route.abort()
    })
    await page.routeWebSocket(/\/ws$/, ws => { this.socket = ws })
    await page.routeWebSocket(/\/ws\/logs\//, ws => { this.logSocket = ws })
    await page.route('**/v1/**', route => route.fulfill({ status: 404, json: { error: 'not_found' } }))
    await page.route('**/api/**', route => this.handle(route))
  }

  private async handle(route: Route) {
    const request = route.request()
    const url = new URL(request.url())
    const method = request.method()
    const path = decodeURIComponent(url.pathname)
    let body: unknown
    try { body = request.postDataJSON() } catch { body = request.postData() }
    if (method !== 'GET') this.calls.push({ method, path, query: url.search, body })

    const refusal = this.refuse[`${method} ${path}`]
    if (refusal) { await route.fulfill({ status: refusal.status, json: refusal.json }); return }

    const answer = method === 'GET' ? this.read(path) : this.write(method, path, body as Record<string, unknown> | null)
    if (answer === undefined) { await route.fulfill({ status: 404, json: { error: `no route for ${method} ${path}` } }); return }
    await route.fulfill({ json: answer })
  }

  private summaries(list: Versioned[]) {
    return list.map(v => ({
      name: v.name, currentVersion: v.currentVersion, isActive: v.isActive, totalVersions: v.versions.length, agents: v.agents,
      currentBytes: v.versions.find(x => x.versionNumber === v.currentVersion)?.content.length ?? 0,
    }))
  }

  private read(path: string): unknown {
    const fixed: Record<string, unknown> = {
      '/api/auth/session': { authRequired: true, signedIn: true, kind: 'browser', userId: 'owner', displayName: 'Owner', deviceId: 'd_test' },
      '/api/agents': this.agents,
      '/api/workflows': this.workflows,
      '/api/workflows/completed': this.completed,
      '/api/workflows/failures': [],
      '/api/signals': this.signals,
      '/api/workflow-types': [{ name: 'ReviewWorkflow', description: 'Reviews a change', namespace: 'fleet', taskQueue: 'fleet', inputSchema: null }],
      '/api/namespaces': ['fleet', 'ops'],
      '/api/setup/status': { telegram: { configured: true, groupChatEnabled: false }, gitHub: { configured: true } },
      '/api/prompt-size-policy': { unit: 'utf8Bytes', instruction: { limitBytes: 10000, limitKey: 'PromptSizeWarnings__InstructionBytes' }, projectContext: { limitBytes: 10000, limitKey: 'PromptSizeWarnings__ProjectContextBytes' } },
      '/api/output-styles': [{ name: 'terse', description: 'Short answers', body: '---\nname: terse\n---\n', agents: [] }],
      '/api/instructions': this.summaries(this.instructions),
      '/api/project-contexts': this.summaries(this.contexts),
      '/api/workflow-definitions': this.definitions.map(({ definition: _d, versions: _v, ...summary }) => summary),
      '/api/schedules': [{ scheduleId: 'nightly-review', namespace: 'fleet', workflowType: 'ReviewWorkflow', cronExpression: '0 3 * * *', paused: false, memo: null }],
      '/api/repositories': [{ name: 'fleet-repo', fullName: 'example/fleet-repo', isActive: true }],
      '/api/comms/journal/status': { status: 'disabled' },
      '/api/credentials/connections': {
        telegram: { configured: true, groupChatEnabled: false, maskedCtoBotToken: '12…ab', maskedNotifierBotToken: null, groupChatId: null, userId: '1001', lastValidatedUtc: null },
        gitHub: { configured: true, maskedAppId: '12…', maskedPrivateKey: '--…', lastValidatedUtc: null },
      },
      '/api/config/all': { FLEET_CTO_AGENT: 'alpha' },
      '/api/secret-bindings': { enabled: false, fault: null, subnet: null, address: null, caPresent: false, bindings: [] },
      '/api/memory/stats/reads': { since: T0, entries: [] },
      '/api/agent-templates': [],
    }
    if (path in fixed) return fixed[path]

    let m = path.match(/^\/api\/agents\/([^/]+)\/(history|config|project-access)$/)
    if (m) {
      if (m[2] === 'history') return this.history[m[1]] ?? []
      if (m[2] === 'config') return configOf(m[1])
      return { projects: this.projectAccess[m[1]] ?? [] }
    }
    m = path.match(/^\/api\/(instructions|project-contexts)\/([^/]+)$/)
    if (m) {
      const item = this.versionedList(m[1]).find(v => v.name === m![2])
      return item && { name: item.name, currentVersion: item.currentVersion, versions: item.versions }
    }
    m = path.match(/^\/api\/workflow-definitions\/([^/]+)$/)
    if (m) return this.definitions.find(d => d.name === m![1])
    m = path.match(/^\/api\/workflows\/[^/]+\/history\/([^/]+)$/)
    if (m) return [
      { eventId: 1, eventType: 'WorkflowStarted', timestamp: T0, inputSummary: '{"issue":7}' },
      { eventId: 2, eventType: 'ActivityScheduled', timestamp: T0, activityType: 'DelegateToAgent', agent: 'beta', inputSummary: 'implement' },
    ]
    if (/\/effective-harness$/.test(path)) return undefined
    // Every other read is a list some view shows as empty.
    return []
  }

  private versionedList(kind: string) { return kind === 'instructions' ? this.instructions : this.contexts }

  private write(method: string, path: string, body: Record<string, unknown> | null): unknown {
    let m = path.match(/^\/api\/(instructions|project-contexts)(?:\/([^/]+)(?:\/(versions|toggle-active|rollback)(?:\/(\d+))?)?)?$/)
    if (m) {
      const list = this.versionedList(m[1])
      if (!m[2]) {
        list.push(versioned(String(body!.name), [String(body!.content)]))
        return {}
      }
      const item = list.find(v => v.name === m![2])
      if (!item) return undefined
      if (m[3] === 'versions') {
        const versionNumber = Math.max(...item.versions.map(v => v.versionNumber)) + 1
        item.versions.unshift({ versionNumber, content: String(body!.content), createdAt: T0, createdBy: 'owner', reason: String(body!.reason ?? '') })
        item.currentVersion = versionNumber
      } else if (m[3] === 'toggle-active') item.isActive = Boolean(body!.isActive)
      else if (m[3] === 'rollback') item.currentVersion = Number(m[4])
      return {}
    }
    m = path.match(/^\/api\/workflow-definitions(?:\/([^/]+)(?:\/(toggle-active))?)?$/)
    if (m) {
      if (!m[1]) {
        this.definitions.push({ name: String(body!.name), namespace: String(body!.namespace), taskQueue: String(body!.taskQueue),
          version: 1, isActive: true, updatedAt: T0, definition: String(body!.definition), versions: [] })
        return {}
      }
      const def = this.definitions.find(d => d.name === m![1])
      if (!def) return undefined
      if (m[2]) def.isActive = Boolean(body!.isActive)
      else {
        def.versions.unshift({ version: def.version, definition: def.definition, reason: 'previous', createdAt: T0, createdBy: 'owner' })
        def.version += 1
        def.definition = String(body!.definition)
      }
      return {}
    }
    m = path.match(/^\/api\/agents\/([^/]+)$/)
    if (m && method === 'DELETE') {
      this.agents = this.agents.filter(a => a.agentName !== m![1])
      return { message: `Deleted ${m[1]}` }
    }
    if (path === '/api/agents' && method === 'POST') {
      this.agents = [...this.agents, agent(String(body!.name), { effectiveStatus: 'dead' })]
      return null
    }
    m = path.match(/^\/api\/agents\/([^/]+)\/project-access(?:\/(.+))?$/)
    if (m) {
      const current = this.projectAccess[m[1]] ?? []
      this.projectAccess[m[1]] = method === 'DELETE' ? current.filter(p => p !== m![2]) : [...current, String(body!.project)]
      return {}
    }
    if (path.startsWith('/api/workflows/')) return { message: 'ok' }
    return {}
  }
}
