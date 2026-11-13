import { test, expect, type Page, type TestInfo, type WebSocketRoute } from '@playwright/test'
import { execFileSync } from 'node:child_process'
import { mkdir, writeFile } from 'node:fs/promises'
import path from 'node:path'

// Only HTTP/WebSocket IO is substituted. App, AgentsView, handleSignalClick,
// SignalModal and sendSignal are the actual production modules and callbacks.
const modes = ['choice', 'text', 'approve-reject'] as const
type Mode = typeof modes[number]
const question = 'Which report? "Кыргыз"\n<b>literal, not markup</b> \\ {{config.Secret}}'
const context = 'Only this disposable test request.\nDo not execute an action.'
const longLabel = 'Literal long label ' + 'unbroken'.repeat(24)
const workflow = {
  namespace: 'fleet', workflowId: 'human-input-123e4567-e89b-42d3-a456-426614174000',
  runId: '11111111-1111-4111-8111-111111111111', gateRef: '11111111-1111-4111-8111-111111111111',
  workflowType: 'UweHumanInputWorkflow', status: 'Running', phase: 'request-input', taskQueue: 'fleet',
  startTime: '2026-11-12T12:00:00Z', closeTime: null, issueNumber: null, prNumber: null, repo: null, docPrs: null,
}
const head = execFileSync('git', ['rev-parse', 'HEAD'], { encoding: 'utf8' }).trim()
const dirty = !!execFileSync('git', ['status', '--porcelain'], { encoding: 'utf8' }).trim()

async function fixture(page: Page, mode: Mode, historyState: 'held' | 'error' | 'ready' = 'ready', signedIn = true) {
  let channel: WebSocketRoute | undefined
  let releaseHistory!: () => void
  const held = new Promise<void>(resolve => { releaseHistory = resolve })
  const posts: { url: string; body: Record<string, unknown> }[] = []
  const request = { Question: question, Context: context, AnswerMode: mode,
    Origin: mode === 'choice'
      ? { Kind: 'parent', Namespace: 'fleet', WorkflowId: 'disposable-parent', RunId: '22222222-2222-4222-8222-222222222222' }
      : { Kind: 'agent', RequesterAgent: 'agent1' },
    ...(mode === 'choice' ? { Choices: [{ Id: 'first', Label: longLabel }, { Id: 'other', Label: 'Other literal option' }] } : {}),
  }
  await page.route('**/*', route => {
    const host = new URL(route.request().url()).hostname
    return host === '127.0.0.1' || host === 'localhost' ? route.continue() : route.abort()
  })
  await page.routeWebSocket('**/ws', ws => { channel = ws })
  await page.route('**/api/**', async route => {
    const url = new URL(route.request().url())
    if (route.request().method() !== 'GET') {
      posts.push({ url: url.pathname, body: route.request().postDataJSON() })
      await route.fulfill({ json: { message: 'Signal sent' } }); return
    }
    if (url.pathname.includes('/history/')) {
      expect(url.searchParams.get('runId')).toBe(workflow.runId)
      if (historyState === 'held') await held
      await route.fulfill(historyState === 'error' ? { status: 503, json: {} } :
        { json: [{ eventType: 'WorkflowStarted', inputSummary: JSON.stringify(request) }] })
      return
    }
    // The page loads only signed in (#530); a signed-out browser gets the sign-in page instead.
    if (url.pathname === '/api/auth/session') {
      await route.fulfill(signedIn
        ? { json: { authRequired: true, signedIn: true, kind: 'browser', userId: 'owner', displayName: 'Owner', deviceId: 'd_test' } }
        : { status: 401, json: { error: 'Unauthorized', authRequired: true } })
      return
    }
    const data: Record<string, unknown> = {
      '/api/agents': [{ agentName: 'agent1', displayName: 'Test agent', shortName: null, model: null, role: null,
        currentTask: null, currentTaskId: null, reportedStatus: 'idle', effectiveStatus: 'idle', lastSeen: new Date().toISOString(),
        version: null, queuedCount: 0, queuedMessages: [], backgroundTasks: [], containerName: null,
        containerStartedAt: null, hostPort: null, journal: null }],
      '/api/workflows': [workflow],
      '/api/signals': { UweHumanInputWorkflow: [{ name: 'request-input', label: 'human input', validPhases: ['request-input'],
        bindAttribute: 'GateRef', buttons: [{ label: 'Answer', payload: '{}', requiresComment: false }] }] },
      '/api/namespaces': ['fleet'],
      '/api/setup/status': { telegram: { configured: true, groupChatEnabled: false }, gitHub: { configured: true } },
    }
    await route.fulfill({ json: data[url.pathname] ?? [] })
  })
  await page.goto('/#agents')
  if (!signedIn) return { posts, releaseHistory, stale: () => {} }
  await expect(page.locator('.agent-list')).toBeVisible()
  // This is specifically AgentsView's button, not WorkflowsView or a test callback.
  await page.locator('.wf-btn-signal').getByText('Answer', { exact: true }).click()
  await expect(page.getByRole('dialog')).toBeVisible()
  return { posts, releaseHistory, stale: () => {
    expect(channel).toBeTruthy()
    channel!.send(JSON.stringify({ type: 'workflows', data: [{ ...workflow,
      runId: '33333333-3333-4333-8333-333333333333', gateRef: '33333333-3333-4333-8333-333333333333' }] }))
  } }
}

async function select(page: Page, mode: Mode) {
  if (mode === 'text') await page.getByRole('textbox', { name: 'Answer', exact: true }).fill(' literal\n"answer" \\ {{input.Secret}} ')
  else await page.getByRole('radio', { name: mode === 'choice' ? longLabel : 'Approve', exact: true }).check()
}
async function capture(page: Page, info: TestInfo, mode: Mode, state: string) {
  const directory = process.env.HUMAN_INPUT_EVIDENCE_DIR ?? info.outputPath('evidence')
  await mkdir(directory, { recursive: true })
  const name = `${info.project.name}-${mode}-${state}`
  const dialog = page.getByRole('dialog')
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true)
  expect(await dialog.evaluate(el => el.scrollWidth <= el.clientWidth + 1)).toBe(true)
  await page.screenshot({ path: path.join(directory, name + '.png') })
  await writeFile(path.join(directory, name + '.aria.yml'), await dialog.ariaSnapshot())
  await writeFile(path.join(directory, name + '.json'), JSON.stringify({ head, dirty, capturedAt: new Date().toISOString(), browser: page.context().browser()?.version(), viewport: page.viewportSize(), mode, state,
    path: 'App → AgentsView → App.handleSignalClick → SignalModal → App.sendSignal', transport: 'synthetic HTTP/WebSocket only' }, null, 2))
}

// What "read-only" became with sign-in (#530): a browser that is not signed in never reaches the
// decision at all. It gets the sign-in page, and nothing is sent.
test('signed out: the sign-in page shows and no decision can be sent', async ({ page }) => {
  const f = await fixture(page, 'approve-reject', 'ready', false)
  await expect(page.getByLabel('Orchestrator token')).toBeVisible()
  await expect(page.locator('.agent-list')).toHaveCount(0)
  await expect(page.getByRole('dialog')).toHaveCount(0)
  expect(f.posts).toHaveLength(0)
})

for (const mode of modes) {
  test(`${mode}: production AgentsView/App path, loading, no selection and explicit answer`, async ({ page }, info) => {
    const f = await fixture(page, mode, 'held')
    const send = page.getByRole('button', { name: 'Send', exact: true })
    await expect(send).toBeDisabled()
    await capture(page, info, mode, 'loading')
    expect(f.posts).toHaveLength(0)
    f.releaseHistory()
    await expect(page.getByText(question, { exact: true })).toBeVisible()
    await expect(page.getByText(context, { exact: true })).toBeVisible()
    await expect(send).toBeDisabled()
    await capture(page, info, mode, mode === 'choice' ? 'long-label-no-selection' : 'no-selection')
    await select(page, mode)
    await expect(send).toBeEnabled()
    await capture(page, info, mode, 'ready')
    expect(f.posts).toHaveLength(0)
    await send.click()
    await expect.poll(() => f.posts.length).toBe(1)
    const post = f.posts[0]
    expect(post.url).toBe(`/api/workflows/fleet/signal/${workflow.workflowId}`)
    expect(post.body).toMatchObject({ signalName: 'request-input', runId: workflow.runId, expectedGateRef: workflow.gateRef })
    expect(JSON.parse(post.body.payload as string)).toEqual({ Value: mode === 'text' ? ' literal\n"answer" \\ {{input.Secret}} ' : mode === 'choice' ? 'first' : 'approved' })
  })
  test(`${mode}: history error refuses sending`, async ({ page }, info) => {
    const f = await fixture(page, mode, 'error')
    await expect(page.getByText('Cannot load the exact request. Refresh before deciding.')).toBeVisible()
    await expect(page.getByRole('button', { name: 'Send', exact: true })).toBeDisabled()
    await capture(page, info, mode, 'history-error')
    expect(f.posts).toHaveLength(0)
  })
  test(`${mode}: changed run refuses a previously selected answer`, async ({ page }, info) => {
    const f = await fixture(page, mode)
    await expect(page.getByText(question, { exact: true })).toBeVisible()
    await select(page, mode)
    f.stale()
    await expect(page.getByRole('button', { name: 'Send', exact: true })).toBeDisabled()
    await expect(page.getByText('This request changed or closed. Refresh before deciding.')).toBeVisible()
    await capture(page, info, mode, 'stale-selected')
    expect(f.posts).toHaveLength(0)
  })
  test(`${mode}: stale run refuses late history without rebinding`, async ({ page }, info) => {
    const f = await fixture(page, mode, 'held')
    f.stale()
    f.releaseHistory()
    await expect(page.getByRole('button', { name: 'Send', exact: true })).toBeDisabled()
    await expect(page.getByText('This request changed or closed. Refresh before deciding.')).toBeVisible()
    await expect(page.getByText(question, { exact: true })).toHaveCount(0)
    await capture(page, info, mode, 'stale-late-history')
    expect(f.posts).toHaveLength(0)
  })
}
