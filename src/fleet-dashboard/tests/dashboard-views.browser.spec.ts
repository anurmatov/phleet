import { test, expect, type Locator, type Page } from '@playwright/test'
import { FakeFleet, EDITED_DEFINITION, configOf, versioned } from './support/fakeFleet'
import { HANDBOOK_VERSIONS, RELEASE_VERSIONS, largeDefinition } from '../src/fixtures/versionFixtures'
import { FakeChat, PIXEL } from './support/fakeChat'

// What the dashboard's views do, driven through the production App against a substituted
// orchestrator (tests/support/fakeFleet.ts). These pin behaviour, not structure: where the state
// lives is free to change, what a click sends and what the page then shows is not.

async function open(page: Page, hash: string, prepare?: (fleet: FakeFleet) => void, search = '') {
  const fleet = new FakeFleet()
  prepare?.(fleet)
  await fleet.install(page)
  await page.goto(`/${search}#${hash}`)
  await expect(page.locator('.sidenav-item-label', { hasText: 'Agents' })).toBeVisible()
  return fleet
}

const agentRow = (page: Page, name: string): Locator =>
  page.locator('.agent-row', { has: page.locator('.agent-row-name', { hasText: new RegExp(`^${name}$`) }) })
const rowByName = (page: Page, name: string): Locator =>
  page.locator('.instr-row', { has: page.locator('.instr-name', { hasText: new RegExp(`^${name}$`) }) })
const wfRow = (page: Page, type: string): Locator =>
  page.locator('.wf-row', { has: page.locator('.wf-type', { hasText: type }) })
const overflow = (row: Locator): Locator => row.locator('.agent-row-actions').getByTitle('More actions')
const navBadge = (page: Page, label: string): Locator =>
  page.locator('.sidenav-item', { has: page.locator('.sidenav-item-label', { hasText: new RegExp(`^${label}$`) }) }).locator('.sidenav-badge')

// ── Every view, from its hash route ─────────────────────────────────────────────────────────────

const VIEWS: { hash: string; shows: (page: Page) => Locator }[] = [
  { hash: 'agents', shows: p => agentRow(p, 'alpha') },
  { hash: 'workflows', shows: p => p.locator('.attention-card', { hasText: 'ReviewWorkflow' }) },
  { hash: 'chat', shows: p => p.locator('.app-main--chat') },
  { hash: 'instructions', shows: p => rowByName(p, 'base') },
  { hash: 'project-contexts', shows: p => rowByName(p, 'fleet') },
  { hash: 'output-styles', shows: p => p.locator('.view-page-title', { hasText: 'Output Styles' }) },
  { hash: 'wf-definitions', shows: p => rowByName(p, 'ReviewWorkflow') },
  { hash: 'schedules', shows: p => p.getByText('nightly-review') },
  { hash: 'namespaces', shows: p => p.locator('.view-page-title', { hasText: 'Namespaces' }) },
  { hash: 'repositories', shows: p => p.getByText('fleet-repo').first() },
  { hash: 'credentials', shows: p => p.locator('.cred-view .view-title', { hasText: 'Credentials' }) },
  { hash: 'memory', shows: p => p.locator('.app-main--memory') },
  { hash: 'epic-grants', shows: p => p.locator('.view-page-title', { hasText: 'Epic grants' }) },
  { hash: 'alerts', shows: p => p.getByText('No active alerts.') },
  { hash: 'usage', shows: p => p.locator('.view-page-title', { hasText: 'Usage' }) },
]

for (const view of VIEWS) {
  test(`#${view.hash} loads its view`, async ({ page }) => {
    await open(page, view.hash)
    await expect(view.shows(page)).toBeVisible()
    // No view may fall through to the error boundary on the data it is given.
    await expect(page.getByText('The page hit an error and stopped drawing.')).toHaveCount(0)
  })
}

test('the sidenav switches views, keeps the hash in step and counts what was loaded', async ({ page }) => {
  await open(page, 'agents')
  await expect(navBadge(page, 'Agents')).toHaveText('3')
  await expect(navBadge(page, 'Instructions')).toHaveText('3')
  await expect(navBadge(page, 'Project Contexts')).toHaveText('2')
  await expect(navBadge(page, 'Output Styles')).toHaveText('1')
  await expect(navBadge(page, 'Namespaces')).toHaveText('2')
  // One workflow waits on a signal, so the badge counts attention, not the two that run.
  await expect(navBadge(page, 'Workflows')).toHaveText('1')
  await page.locator('.sidenav-item-label', { hasText: 'Instructions' }).click()
  await expect(rowByName(page, 'base')).toBeVisible()
  expect(new URL(page.url()).hash).toBe('#instructions')
  await page.evaluate(() => { window.location.hash = '#project-contexts' })
  await expect(rowByName(page, 'fleet')).toBeVisible()
})

// ── Agents ──────────────────────────────────────────────────────────────────────────────────────

test('agents: expanding a row loads its task history', async ({ page }) => {
  await open(page, 'agents')
  const alpha = agentRow(page, 'alpha')
  await alpha.locator('.agent-row-summary').click()
  await expect(alpha.getByText('Recent Tasks')).toBeVisible()
  await expect(alpha.getByText('Wrote the release notes')).toBeVisible()
  await alpha.getByText('+1 more').click()
  await expect(alpha.getByText('Reviewed the migration')).toBeVisible()
  await alpha.locator('.agent-row-summary').click()
  await expect(alpha.getByText('Recent Tasks')).toHaveCount(0)
})

test('agents: restart asks first, cancel sends nothing, confirm sends one restart', async ({ page }) => {
  const fleet = await open(page, 'agents')
  const alpha = agentRow(page, 'alpha')
  await alpha.locator('.agent-row-actions').getByTitle('Restart').click()
  await expect(alpha.locator('.row-confirm')).toContainText('Restart alpha? This will interrupt any running task.')
  await alpha.locator('.row-confirm-cancel').click()
  await expect(alpha.locator('.row-confirm')).toHaveCount(0)
  expect(fleet.writes('POST', '/api/agents/alpha/restart')).toHaveLength(0)

  await alpha.locator('.agent-row-actions').getByTitle('Restart').click()
  await alpha.locator('.row-confirm-ok').click()
  await expect(alpha.locator('.row-feedback-success')).toHaveText('Restart triggered')
  expect(fleet.writes('POST', '/api/agents/alpha/restart')).toHaveLength(1)
})

test('agents: reprovision confirm sends it, and a refusal shows the server\'s reason', async ({ page }) => {
  const fleet = await open(page, 'agents')
  const alpha = agentRow(page, 'alpha')
  await alpha.locator('.agent-row-actions').getByTitle('Reprovision').click()
  await expect(alpha.locator('.row-confirm')).toContainText('Reprovision alpha? Container will be recreated.')
  await alpha.locator('.row-confirm-ok').click()
  await expect(alpha.locator('.row-feedback-success')).toHaveText('Reprovisioning...')
  expect(fleet.writes('POST', '/api/agents/alpha/reprovision')).toHaveLength(1)

  fleet.refuse['POST /api/agents/beta/reprovision'] = { status: 409, json: { error: 'secret_proxy_disabled' } }
  const beta = agentRow(page, 'beta')
  await beta.locator('.agent-row-actions').getByTitle('Reprovision').click()
  await beta.locator('.row-confirm-ok').click()
  await expect(beta.locator('.row-feedback-error')).toContainText('secret_proxy_disabled')
})

test('agents: reprovision resumes the session unless a new one is ticked, and asks afresh each time', async ({ page }) => {
  const fleet = await open(page, 'agents')
  const alpha = agentRow(page, 'alpha')
  const choice = alpha.locator('.row-confirm').getByRole('checkbox', { name: 'New session' })

  // The default: the question offers the choice unticked, and the request is the one it always was.
  await alpha.locator('.agent-row-actions').getByTitle('Reprovision').click()
  await expect(choice).not.toBeChecked()
  await expect(alpha.locator('.row-confirm .field-hint'))
    .toHaveText('A new session forgets the conversation. Changed instructions always start a new session.')
  await alpha.locator('.row-confirm-ok').click()
  await expect(alpha.locator('.row-feedback-success')).toHaveText('Reprovisioning...')
  expect(fleet.writes('POST', '/api/agents/alpha/reprovision').map(c => [c.query, c.body])).toEqual([['', null]])

  // Ticked and then cancelled sends nothing, and the next question is back at resume.
  const beta = agentRow(page, 'beta')
  const betaChoice = beta.locator('.row-confirm').getByRole('checkbox', { name: 'New session' })
  await beta.locator('.agent-row-actions').getByTitle('Reprovision').click()
  await betaChoice.check()
  await beta.locator('.row-confirm-cancel').click()
  expect(fleet.writes('POST', '/api/agents/beta/reprovision')).toHaveLength(0)
  await beta.locator('.agent-row-actions').getByTitle('Reprovision').click()
  await expect(betaChoice).not.toBeChecked()

  // Ticked and confirmed asks for a new session.
  await betaChoice.check()
  await beta.locator('.row-confirm-ok').click()
  await expect(beta.locator('.row-feedback-success')).toHaveText('Reprovisioning...')
  expect(fleet.writes('POST', '/api/agents/beta/reprovision').map(c => [c.query, c.body])).toEqual([['', { session: 'fresh' }]])
})

test('agents: cancelling a running task asks first', async ({ page }) => {
  const fleet = await open(page, 'agents')
  const beta = agentRow(page, 'beta')
  await expect(agentRow(page, 'alpha').locator('.agent-row-actions').getByTitle('Cancel task')).toBeDisabled()
  await beta.locator('.agent-row-actions').getByTitle('Cancel task').click()
  await expect(beta.locator('.row-confirm')).toContainText('Cancel all tasks on beta?')
  await beta.getByRole('button', { name: 'Cancel Tasks' }).click()
  await expect(beta.locator('.row-feedback-success')).toHaveText('Cancelled')
  expect(fleet.writes('POST', '/api/agents/beta/cancel')).toHaveLength(1)
})

test('agents: stop needs a second click, start does not', async ({ page }) => {
  const fleet = await open(page, 'agents')
  const alpha = agentRow(page, 'alpha')
  await overflow(alpha).click()
  await alpha.locator('.agent-row-overflow').getByRole('button', { name: 'Stop', exact: true }).click()
  expect(fleet.writes('POST', '/api/agents/alpha/stop')).toHaveLength(0)
  await overflow(alpha).click()
  await alpha.locator('.agent-row-overflow').getByRole('button', { name: 'confirm stop?' }).click()
  await expect(alpha.locator('.row-feedback-success')).toHaveText('Stopped')
  expect(fleet.writes('POST', '/api/agents/alpha/stop')).toHaveLength(1)

  const gamma = agentRow(page, 'gamma')
  await overflow(gamma).click()
  await gamma.locator('.agent-row-overflow').getByRole('button', { name: 'Start', exact: true }).click()
  await expect(gamma.locator('.row-feedback-success')).toHaveText('Started')
  expect(fleet.writes('POST', '/api/agents/gamma/start')).toHaveLength(1)
})

test('agents: delete asks first and the row goes once the server has dropped it', async ({ page }) => {
  const fleet = await open(page, 'agents')
  const alpha = agentRow(page, 'alpha')
  await overflow(alpha).click()
  await alpha.locator('.agent-row-overflow').getByRole('button', { name: 'Delete', exact: true }).click()
  await expect(alpha.locator('.row-confirm')).toContainText('Delete alpha? Container and DB record will be removed.')
  await alpha.locator('.row-confirm-delete-ok').click()
  await expect(alpha).toHaveCount(0)
  expect(fleet.writes('DELETE', '/api/agents/alpha')).toHaveLength(1)
  await expect(navBadge(page, 'Agents')).toHaveText('2')
})

test('agents: a ?delete= link arms the confirmation and ?name= opens a filled create dialog', async ({ page }) => {
  const fleet = await open(page, 'agents', undefined, '?delete=alpha')
  await expect(agentRow(page, 'alpha').locator('.row-confirm')).toContainText('Delete alpha?')
  expect(fleet.writes('DELETE', '/api/agents')).toHaveLength(0)

  await open(page, 'agents', undefined, '?name=zeta&role=qa')
  const dialog = page.locator('.create-agent-modal')
  await expect(dialog.getByPlaceholder('e.g. anew', { exact: true })).toHaveValue('zeta')
  await expect(dialog.getByPlaceholder('e.g. developer')).toHaveValue('qa')
})

test('agents: the config dialog loads, saves what was edited and reports it', async ({ page }) => {
  const fleet = await open(page, 'agents')
  const alpha = agentRow(page, 'alpha')
  await overflow(alpha).click()
  await alpha.locator('.agent-row-overflow').getByRole('button', { name: 'edit config' }).click()
  const dialog = page.locator('.config-modal-sectioned')
  await expect(dialog.locator('.config-modal-title')).toHaveText('Config — alpha')
  await expect(alpha).toHaveClass(/config-editing/)

  await dialog.getByRole('tab', { name: /Advanced/ }).click()
  const memory = dialog.locator('.config-field', { has: page.locator('label', { hasText: 'Memory (MB)' }) }).locator('input')
  await expect(memory).toHaveValue('4096')
  await memory.fill('8192')
  await dialog.getByRole('tab', { name: /Telegram/ }).click()
  await dialog.getByPlaceholder('comma-separated user IDs').fill('1001, 2002')
  await dialog.getByRole('button', { name: 'Save', exact: true }).click()
  await expect(dialog.locator('.config-feedback-success')).toHaveText('Saved')

  const put = fleet.writes('PUT', '/api/agents/alpha/config')
  expect(put).toHaveLength(1)
  expect(put[0].body).toMatchObject({ memoryLimitMb: 8192, model: 'sonnet', projects: ['fleet'], networks: ['fleet-net'] })
  // The allowlist is its own write: only the id that was added is sent.
  expect(fleet.writes('POST', '/api/agents/alpha/telegram-users').map(c => c.body)).toEqual([{ userId: 2002 }])
  expect(fleet.writes('POST', '/api/agents/alpha/reprovision')).toHaveLength(0)

  await dialog.getByTitle('Close').click()
  await expect(dialog).toHaveCount(0)
})

test('agents: save and reprovision needs a second click; a refused save says why and reprovisions nothing', async ({ page }) => {
  const fleet = await open(page, 'agents')
  const alpha = agentRow(page, 'alpha')
  await overflow(alpha).click()
  await alpha.locator('.agent-row-overflow').getByRole('button', { name: 'edit config' }).click()
  const dialog = page.locator('.config-modal-sectioned')
  await dialog.getByRole('button', { name: 'Save & Reprovision' }).click()
  expect(fleet.writes('PUT', '/api/agents/alpha/config')).toHaveLength(0)
  await dialog.getByRole('button', { name: 'confirm reprovision?' }).click()
  await expect(dialog.locator('.config-feedback-success')).toHaveText('Saved & reprovisioning…')
  expect(fleet.writes('PUT', '/api/agents/alpha/config')).toHaveLength(1)
  expect(fleet.writes('POST', '/api/agents/alpha/reprovision')).toHaveLength(1)

  fleet.refuse['PUT /api/agents/alpha/config'] = { status: 400, json: { error: 'Model is required' } }
  await dialog.getByRole('button', { name: 'confirm reprovision?' }).click()
  await expect(dialog.locator('.config-feedback-error')).toContainText('Model is required')
  expect(fleet.writes('POST', '/api/agents/alpha/reprovision')).toHaveLength(1)
})

test('agents: memory project access is its own write and re-reads the list', async ({ page }) => {
  const fleet = await open(page, 'agents')
  const alpha = agentRow(page, 'alpha')
  await overflow(alpha).click()
  await alpha.locator('.agent-row-overflow').getByRole('button', { name: 'edit config' }).click()
  const dialog = page.locator('.config-modal-sectioned')
  await dialog.getByRole('tab', { name: /Instructions/ }).click()
  const access = dialog.locator('.config-section-panel[data-section="instructions"]')
  await expect(access.getByText('Memory Project Access')).toBeVisible()
  await access.getByPlaceholder(/project/i).last().fill('docs')
  await access.getByRole('button', { name: /^\+?\s*add$/i }).last().click()
  await expect.poll(() => fleet.writes('POST', '/api/agents/alpha/project-access').map(c => c.body)).toEqual([{ project: 'docs' }])
  expect(fleet.projectAccess.alpha).toEqual(['fleet', 'docs'])
  expect(fleet.writes('PUT', '/api/agents/alpha/config')).toHaveLength(0)
})

test('agents: a new agent can be copied from an existing one', async ({ page }) => {
  const fleet = await open(page, 'agents')
  await page.getByRole('button', { name: 'New agent' }).click()
  const dialog = page.locator('.create-agent-modal')
  await expect(dialog.locator('.config-modal-title')).toHaveText('New Agent')
  await dialog.locator('select').first().selectOption('alpha')
  await expect(dialog.getByPlaceholder('e.g. developer')).toHaveValue('developer')
  await dialog.getByPlaceholder('e.g. anew', { exact: true }).fill('delta')
  await dialog.getByRole('button', { name: 'Create', exact: true }).click()
  await expect(dialog.locator('.config-feedback-success')).toHaveText('Agent created — configure and provision when ready')

  const post = fleet.calls.filter(c => c.method === 'POST' && c.path === '/api/agents')
  expect(post).toHaveLength(1)
  expect(post[0].body).toMatchObject({ name: 'delta', role: 'developer', model: 'sonnet', memoryLimitMb: 4096 })
  // The copy carries the source's tools, projects and networks; instruction assignments are not copied.
  expect(JSON.stringify(post[0].body)).toContain('fleet-net')
  expect(fleet.writes('PUT', '/api/agents/delta/config')).toHaveLength(0)
})

test('agents: a create with no name is refused in the dialog and nothing is sent', async ({ page }) => {
  const fleet = await open(page, 'agents')
  await page.getByRole('button', { name: 'New agent' }).click()
  const dialog = page.locator('.create-agent-modal')
  await dialog.getByRole('button', { name: 'Create', exact: true }).click()
  await expect(dialog.locator('.config-feedback-error')).toBeVisible()
  expect(fleet.calls.filter(c => c.method === 'POST' && c.path === '/api/agents')).toHaveLength(0)
})

test('agents: reprovision all asks in the sidenav, then says so and raises an alert', async ({ page }) => {
  const fleet = await open(page, 'agents')
  await page.getByRole('button', { name: 'Reprovision all' }).click()
  await page.locator('.sidenav-confirm-no').click()
  expect(fleet.writes('POST', '/api/agents/reprovision-running')).toHaveLength(0)
  await page.getByRole('button', { name: 'Reprovision all' }).click()
  await page.locator('.sidenav-confirm-yes').click()
  await expect(page.locator('.sidenav-action.success')).toHaveText('All agents reprovisioning…')
  await expect(page.locator('.toast')).toContainText('Reprovision all triggered — agents restarting')
  expect(fleet.writes('POST', '/api/agents/reprovision-running')).toHaveLength(1)
})

test('agents: reprovision all resumes sessions by default and sends nothing more', async ({ page }) => {
  const fleet = await open(page, 'agents')
  await page.getByRole('button', { name: 'Reprovision all' }).click()
  const confirm = page.locator('.sidenav-action-confirm')
  await expect(confirm.getByRole('checkbox', { name: 'New session' })).not.toBeChecked()
  await expect(confirm.locator('.field-hint'))
    .toHaveText('A new session forgets the conversation. Changed instructions always start a new session.')
  await confirm.locator('.sidenav-confirm-yes').click()
  await expect(page.locator('.sidenav-action.success')).toHaveText('All agents reprovisioning…')
  expect(fleet.writes('POST', '/api/agents/reprovision-running').map(c => [c.query, c.body])).toEqual([['', null]])
})

test('agents: reprovision all with a new session ticked asks every agent for one', async ({ page }) => {
  const fleet = await open(page, 'agents')
  const confirm = page.locator('.sidenav-action-confirm')
  const choice = confirm.getByRole('checkbox', { name: 'New session' })

  // Ticked and then refused sends nothing, and the next question is back at resume.
  await page.getByRole('button', { name: 'Reprovision all' }).click()
  await choice.check()
  await confirm.locator('.sidenav-confirm-no').click()
  expect(fleet.writes('POST', '/api/agents/reprovision-running')).toHaveLength(0)
  await page.getByRole('button', { name: 'Reprovision all' }).click()
  await expect(choice).not.toBeChecked()

  await choice.check()
  await confirm.locator('.sidenav-confirm-yes').click()
  await expect(page.locator('.sidenav-action.success')).toHaveText('All agents reprovisioning…')
  expect(fleet.writes('POST', '/api/agents/reprovision-running').map(c => [c.query, c.body])).toEqual([['', { session: 'fresh' }]])
})

test('agents: the log viewer follows the agent\'s stream and closes', async ({ page }) => {
  const fleet = await open(page, 'agents')
  const alpha = agentRow(page, 'alpha')
  await alpha.locator('.agent-row-actions').getByTitle('Logs').click()
  const viewer = page.locator('.log-fullscreen-overlay')
  await expect(viewer.locator('.log-panel-agent')).toHaveText('alpha')
  await expect.poll(() => fleet.logSocket).toBeTruthy()
  fleet.logSocket!.send('2026-11-12T12:00:01Z info: turn started')
  fleet.logSocket!.send('2026-11-12T12:00:02Z error: provider refused')
  await expect(viewer.locator('.log-line')).toHaveCount(2)
  await viewer.locator('.log-filter-btn', { hasText: 'error' }).click()
  await expect(viewer.locator('.log-line')).toHaveCount(1)
  await viewer.getByTitle('Close').click()
  await expect(viewer).toHaveCount(0)
})

// ── Live updates ────────────────────────────────────────────────────────────────────────────────

test('live: a heartbeat on the socket changes the agent\'s card, and a dead agent raises an alert', async ({ page }) => {
  const fleet = await open(page, 'agents')
  await expect.poll(() => fleet.socket).toBeTruthy()
  const alpha = agentRow(page, 'alpha')
  await expect(alpha.locator('.task-idle')).toBeVisible()
  fleet.pushAgent('alpha', { currentTask: 'Answering a live question', reportedStatus: 'busy', effectiveStatus: 'busy' })
  await expect(alpha.locator('.task-active')).toHaveText('Answering a live question')
  await expect(alpha).toHaveClass(/status-busy/)

  fleet.pushAgent('alpha', { currentTask: null, effectiveStatus: 'dead' })
  await expect(page.locator('.toast')).toContainText('alpha is down')
  await page.locator('.sidenav-item-label', { hasText: 'Alerts' }).click()
  await expect(page.locator('.alert-item')).toContainText('alpha is down')
  await page.getByRole('button', { name: 'Dismiss all' }).click()
  await expect(page.getByText('No active alerts.')).toBeVisible()

  // A socket message for an agent the page has not seen adds its row.
  await page.locator('.sidenav-item-label', { hasText: 'Agents' }).click()
  fleet.pushAgent('epsilon', {})
  await expect(agentRow(page, 'epsilon')).toBeVisible()
  await expect(navBadge(page, 'Agents')).toHaveText('4')
})

test('live: a workflows message replaces the list', async ({ page }) => {
  const fleet = await open(page, 'workflows')
  await expect.poll(() => fleet.socket).toBeTruthy()
  await expect(wfRow(page, 'BuildWorkflow')).toBeVisible()
  fleet.pushWorkflows([fleet.workflows[0]])
  await expect(wfRow(page, 'BuildWorkflow')).toHaveCount(0)
  await expect(page.locator('.attention-card')).toHaveCount(1)
})

test('live: a late workflow refresh cannot restore rows removed by the socket', async ({ page }) => {
  const fleet = await open(page, 'workflows')
  // Wait for the fixture socket, not the removed connected-state UI.
  await expect.poll(() => fleet.socket).toBeTruthy()
  await expect(wfRow(page, 'BuildWorkflow')).toBeVisible()
  const stale = [...fleet.workflows]
  let releaseSnapshot!: () => void
  const held = new Promise<void>(resolve => { releaseSnapshot = resolve })
  await page.route('**/api/workflows', async route => {
    await held
    await route.fulfill({ json: stale })
  })
  try {
    const refresh = page.getByTitle('Refresh workflows', { exact: true })
    await refresh.click()
    await expect(refresh).toBeDisabled()
    fleet.pushWorkflows([fleet.workflows[0]])
    await expect(wfRow(page, 'BuildWorkflow')).toHaveCount(0)
    releaseSnapshot()
    await expect(refresh).toBeEnabled()
    await expect(wfRow(page, 'BuildWorkflow')).toHaveCount(0)
    await expect(page.locator('.attention-card')).toHaveCount(1)

    // A refresh begun after the live update is still allowed to replace the list.
    await page.unroute('**/api/workflows')
    fleet.workflows = stale
    await refresh.click()
    await expect(wfRow(page, 'BuildWorkflow')).toBeVisible()
  } finally {
    releaseSnapshot()
  }
})

// ── Workflows ───────────────────────────────────────────────────────────────────────────────────

test('workflows: a signal goes through the confirm bar and is then reported as sent', async ({ page }) => {
  const fleet = await open(page, 'workflows')
  const card = page.locator('.attention-card', { hasText: 'ReviewWorkflow' })
  await card.getByRole('button', { name: 'Approve' }).click()
  const bar = page.locator('.confirm-bar')
  await expect(bar).toContainText('Approve for ReviewWorkflow')
  await bar.getByRole('button', { name: 'Cancel' }).click()
  await expect(bar).toHaveCount(0)
  expect(fleet.writes('POST', '/api/workflows/fleet/signal/')).toHaveLength(0)

  await card.getByRole('button', { name: 'Approve' }).click()
  await page.locator('.confirm-bar').getByRole('button', { name: 'Confirm' }).click()
  // The run is not waiting for anyone now: it leaves "Waiting for you" for the list, saying what was sent.
  await expect(wfRow(page, 'ReviewWorkflow').locator('.wf-action-success')).toHaveText('Approve sent')
  await expect(card).toHaveCount(0)
  const sent = fleet.writes('POST', '/api/workflows/fleet/signal/wf-review-1')
  expect(sent).toHaveLength(1)
  expect(sent[0].body).toMatchObject({ signalName: 'design-approval', payload: '{"Value":"approved"}', runId: 'run-wf-review-1' })
})

// ── A decision that was sent ────────────────────────────────────────────────────────────────────
// The run keeps its phase until the workflow takes the signal up. Until then its buttons stay away
// on both pages, it is not counted as waiting, and the note that replaces them offers them back.

test('agents view: after a decision the buttons stay away, past the moment the result is shown', async ({ page }) => {
  const fleet = await open(page, 'agents')
  const review = wfRow(page, 'ReviewWorkflow')
  await expect(navBadge(page, 'Workflows')).toHaveText('1')
  await review.getByRole('button', { name: 'Approve' }).click()
  await review.getByRole('button', { name: 'confirm approve?' }).click()
  await expect(review.locator('.wf-decision-sent')).toContainText('Approve sent')
  await expect(review.locator('.wf-decision-sent')).toContainText('waiting for the workflow')
  await expect(review).not.toHaveClass(/wf-row-signal-waiting/)
  // Nothing waits for a person, so the Workflows badge counts the two that run.
  await expect(navBadge(page, 'Workflows')).toHaveText('2')

  // The result of a signal is shown for four seconds. The buttons used to come back after it.
  await page.waitForTimeout(4500)
  await expect(review.locator('.wf-decision-sent')).toContainText('Approve sent')
  await expect(review.getByRole('button', { name: 'Approve' })).toHaveCount(0)
  await expect(review.getByRole('button', { name: 'Reject' })).toHaveCount(0)

  // The same phase reported again changes nothing.
  fleet.pushWorkflows([...fleet.workflows])
  await expect(review.locator('.wf-decision-sent')).toBeVisible()
  await expect(review.getByRole('button', { name: 'Approve' })).toHaveCount(0)

  // The workflows page agrees: the run is in the list, not under "Waiting for you".
  await page.evaluate(() => { window.location.hash = '#workflows' })
  await expect(wfRow(page, 'ReviewWorkflow').locator('.wf-decision-sent')).toContainText('Approve sent')
  await expect(page.locator('.attention-card')).toHaveCount(0)
})

test('agents view: a sent decision ends when the run moves on, and the phase asks again when it returns', async ({ page }) => {
  const fleet = await open(page, 'agents')
  const review = wfRow(page, 'ReviewWorkflow')
  await review.getByRole('button', { name: 'Approve' }).click()
  await review.getByRole('button', { name: 'confirm approve?' }).click()
  await expect(review.locator('.wf-decision-sent')).toBeVisible()

  const [waiting, other] = fleet.workflows as Array<Record<string, unknown>>
  fleet.pushWorkflows([{ ...waiting, phase: 'building' }, other])
  await expect(review.locator('.wf-decision-sent')).toHaveCount(0)
  await expect(review.getByRole('button', { name: 'Approve' })).toHaveCount(0)

  fleet.pushWorkflows([waiting, other])
  await expect(review.getByRole('button', { name: 'Approve' })).toBeVisible()
  await expect(review.locator('.wf-decision-sent')).toHaveCount(0)
  await expect(navBadge(page, 'Workflows')).toHaveText('1')
})

test('agents view: "Send again" brings the buttons back, and a second decision is sent', async ({ page }) => {
  const fleet = await open(page, 'agents')
  const review = wfRow(page, 'ReviewWorkflow')
  await review.getByRole('button', { name: 'Approve' }).click()
  await review.getByRole('button', { name: 'confirm approve?' }).click()
  await review.locator('.wf-decision-sent').getByRole('button', { name: 'Send again' }).click()
  await expect(review.locator('.wf-decision-sent')).toHaveCount(0)
  await expect(navBadge(page, 'Workflows')).toHaveText('1')

  await review.getByRole('button', { name: 'Approve' }).click()
  await review.getByRole('button', { name: 'confirm approve?' }).click()
  await expect(review.locator('.wf-decision-sent')).toContainText('Approve sent')
  expect(fleet.writes('POST', '/api/workflows/fleet/signal/wf-review-1')).toHaveLength(2)
})

test('a decision sent with a comment is named by its button, and one that fails keeps the buttons', async ({ page }) => {
  const refused = 'POST /api/workflows/fleet/signal/wf-review-1'
  const fleet = await open(page, 'workflows', f => { f.refuse[refused] = { status: 500, json: { error: 'temporal unavailable' } } })
  const card = page.locator('.attention-card', { hasText: 'ReviewWorkflow' })
  await card.getByRole('button', { name: 'Approve' }).click()
  await page.locator('.confirm-bar').getByRole('button', { name: 'Confirm' }).click()
  await expect(card.locator('.wf-action-error')).toBeVisible()
  await expect(wfRow(page, 'ReviewWorkflow')).toHaveCount(0)
  await expect(card.getByRole('button', { name: 'Reject' })).toBeVisible({ timeout: 6000 })
  delete fleet.refuse[refused]

  await card.getByRole('button', { name: 'Reject' }).click()
  const dialog = page.getByRole('dialog')
  await dialog.getByPlaceholder('Comment (optional)').fill('needs a rollback plan')
  await dialog.getByRole('button', { name: 'Send', exact: true }).click()
  await expect(wfRow(page, 'ReviewWorkflow').locator('.wf-decision-sent')).toContainText('Reject sent')
  await expect(card).toHaveCount(0)
})

test('workflows: a signal that needs a comment opens the dialog and sends the comment', async ({ page }) => {
  const fleet = await open(page, 'workflows')
  await page.locator('.attention-card', { hasText: 'ReviewWorkflow' }).getByRole('button', { name: 'Reject' }).click()
  const dialog = page.getByRole('dialog')
  await expect(dialog).toBeVisible()
  await dialog.getByPlaceholder('Comment (optional)').fill('needs a rollback plan')
  await dialog.getByRole('button', { name: 'Send', exact: true }).click()
  await expect(dialog).toHaveCount(0)
  await expect.poll(() => fleet.writes('POST', '/api/workflows/fleet/signal/wf-review-1').length).toBe(1)
  const sent = fleet.writes('POST', '/api/workflows/fleet/signal/wf-review-1')[0].body as { signalName: string; payload: string }
  expect(sent.signalName).toBe('design-approval')
  // The comment travels inside the payload, beside the button's own value.
  expect(JSON.parse(sent.payload)).toEqual({ Value: 'rejected', Comment: 'needs a rollback plan' })
})

test('workflows: cancel and kill go through the confirm bar with the run id', async ({ page }) => {
  const fleet = await open(page, 'workflows')
  const row = wfRow(page, 'BuildWorkflow')
  await row.locator('.wf-actions-desktop').getByTitle('More actions').click()
  await row.locator('.wf-more-menu').getByRole('button', { name: 'cancel' }).click()
  await expect(page.locator('.confirm-bar')).toContainText('Cancel workflow "BuildWorkflow"?')
  await page.locator('.confirm-bar').getByRole('button', { name: 'Cancel' }).click()
  expect(fleet.writes('POST', '/api/workflows/fleet/cancel/')).toHaveLength(0)

  await row.locator('.wf-actions-desktop').getByTitle('More actions').click()
  await row.locator('.wf-more-menu').getByRole('button', { name: 'cancel' }).click()
  await page.locator('.confirm-bar').getByRole('button', { name: 'Confirm' }).click()
  await expect(row.locator('.wf-action-success')).toHaveText('Cancel sent')
  const cancel = fleet.writes('POST', '/api/workflows/fleet/cancel/wf-build-2')
  expect(cancel).toHaveLength(1)
  expect(cancel[0].query).toBe('?runId=run-wf-build-2')
})

test('workflows: a refused kill shows the reason on the row', async ({ page }) => {
  const fleet = await open(page, 'workflows', f => {
    f.refuse['POST /api/workflows/fleet/terminate/wf-build-2'] = { status: 500, json: { error: 'temporal unavailable' } }
  })
  const row = wfRow(page, 'BuildWorkflow')
  await row.locator('.wf-actions-desktop').getByTitle('More actions').click()
  await row.locator('.wf-more-menu').getByRole('button', { name: 'kill' }).click()
  await expect(page.locator('.confirm-bar')).toContainText('Terminate workflow "BuildWorkflow"?')
  await page.locator('.confirm-bar').getByRole('button', { name: 'Confirm' }).click()
  await expect(row.locator('.wf-action-error')).toContainText('temporal unavailable')
  expect(fleet.writes('POST', '/api/workflows/fleet/terminate/wf-build-2')).toHaveLength(1)
})

test('workflows: clicking a type opens its history, and completed runs list on demand', async ({ page }) => {
  await open(page, 'workflows')
  await wfRow(page, 'BuildWorkflow').locator('.wf-type').click()
  const detail = page.locator('.wf-detail-modal')
  await expect(detail.locator('.wf-detail-id')).toHaveText('wf-build-2')
  await expect(detail.locator('.wf-ev')).toHaveCount(2)
  await expect(detail.locator('.wf-ev-agent')).toHaveText('→ beta')
  await detail.locator('.wf-detail-close').click()
  await expect(detail).toHaveCount(0)

  await page.locator('.completed-header').click()
  await expect(page.locator('.wf-row-muted')).toContainText('BuildWorkflow')
})

test('agents view: its workflow rows confirm on the second click', async ({ page }) => {
  const fleet = await open(page, 'agents')
  const review = wfRow(page, 'ReviewWorkflow')
  await review.getByRole('button', { name: 'Approve' }).click()
  expect(fleet.writes('POST', '/api/workflows/fleet/signal/')).toHaveLength(0)
  await review.getByRole('button', { name: 'confirm approve?' }).click()
  await expect(review.locator('.wf-action-success')).toHaveText('Approve sent')
  expect(fleet.writes('POST', '/api/workflows/fleet/signal/wf-review-1')).toHaveLength(1)

  const build = wfRow(page, 'BuildWorkflow')
  await expect(build.locator('.wf-agent-badge')).toHaveText('beta')
  await build.getByTitle('More actions').click()
  await build.locator('.wf-more-menu').getByRole('button', { name: 'restart' }).click()
  expect(fleet.writes('POST', '/api/workflows/fleet/restart/')).toHaveLength(0)
  await build.locator('.wf-more-menu').getByRole('button', { name: 'confirm restart?' }).click()
  await expect(build.locator('.wf-action-success')).toHaveText('Restarted')
  const restart = fleet.writes('POST', '/api/workflows/fleet/restart/wf-build-2')
  expect(restart).toHaveLength(1)
  expect(restart[0].query).toBe('?runId=run-wf-build-2&terminateExisting=true')
})

test('dialogs are announced as dialogs, take focus, and close on Escape', async ({ page }) => {
  await open(page, 'agents')
  await page.getByRole('button', { name: 'New agent' }).click()
  const create = page.getByRole('dialog', { name: 'New Agent' })
  await expect(create).toBeVisible()
  await expect(create).toBeFocused()
  await page.keyboard.press('Escape')
  await expect(create).toHaveCount(0)

  await wfRow(page, 'BuildWorkflow').locator('.wf-type').click()
  const history = page.getByRole('dialog', { name: 'Workflow history' })
  await expect(history.locator('.wf-ev')).toHaveCount(2)
  await page.keyboard.press('Escape')
  await expect(history).toHaveCount(0)

  const alpha = agentRow(page, 'alpha')
  await overflow(alpha).click()
  await alpha.locator('.agent-row-overflow').getByRole('button', { name: 'edit config' }).click()
  const config = page.getByRole('dialog', { name: 'Config — alpha' })
  await expect(config).toBeVisible()
  // A click on the backdrop still closes it, as before.
  await page.locator('.config-modal-overlay').click({ position: { x: 5, y: 5 } })
  await expect(config).toHaveCount(0)
})

// ── Instructions and project contexts: the same versioned editor ────────────────────────────────

const VERSIONED = [
  { hash: 'instructions', api: '/api/instructions', name: 'base', current: 'base v2 content', added: 'New instruction',
    newName: 'e.g. my-instruction', newContent: 'Instruction content…', other: 'reviewer' },
  { hash: 'project-contexts', api: '/api/project-contexts', name: 'fleet', current: 'fleet context v2', added: 'New project context',
    newName: 'e.g. my-project', newContent: 'Project context content…', other: 'docs' },
] as const

for (const kind of VERSIONED) {
  test(`${kind.hash}: open, edit, save a new version`, async ({ page }) => {
    const fleet = await open(page, kind.hash)
    const row = rowByName(page, kind.name)
    await row.locator('.instr-header').click()
    const editor = row.locator('textarea.instr-editor')
    await expect(editor).toHaveValue(kind.current)
    const save = row.getByRole('button', { name: 'Save', exact: true })
    await expect(save).toBeDisabled()
    await editor.fill('rewritten content')
    await row.getByPlaceholder('reason (optional)').fill('tighten the wording')
    await save.click()
    await expect(row.locator('.instr-save-row .config-feedback-success')).toHaveText('Saved')

    expect(fleet.writes('POST', `${kind.api}/${kind.name}/versions`).map(c => c.body))
      .toEqual([{ content: 'rewritten content', reason: 'tighten the wording' }])
    // The row re-reads itself: the new version is current, and the reason field is cleared.
    await expect(row.locator('.instr-version-item.current .instr-version-num')).toHaveText('v3')
    await expect(row.locator('.instr-header .instr-version')).toHaveText('v3')
    await expect(row.getByPlaceholder('reason (optional)')).toHaveValue('')
    await expect(save).toBeDisabled()
  })

  test(`${kind.hash}: choosing a version shows its comparison, and rollback needs a second click`, async ({ page }) => {
    const fleet = await open(page, kind.hash)
    const row = rowByName(page, kind.name)
    await row.locator('.instr-header').click()
    const v2 = row.locator('.instr-version-item', { has: page.locator('.instr-version-num', { hasText: /^v2$/ }) })
    const v1 = row.locator('.instr-version-item', { has: page.locator('.instr-version-num', { hasText: /^v1$/ }) })
    // A press on the row opens the comparison on what that version changed.
    await v2.locator('.instr-version-meta').click()
    const comparison = page.getByRole('dialog', { name: `Compare versions of ${kind.name}` })
    await expect(comparison.getByRole('status')).toContainText('v1 → v2')
    await expect(comparison.locator('del.vc-row')).toHaveText(/-.*\bv1\b/)
    await expect(comparison.locator('ins.vc-row')).toHaveText(/\+.*\bv2\b/)
    await page.keyboard.press('Escape')
    await expect(comparison).toHaveCount(0)

    // The row's rollback is still its own button: it does not open the comparison.
    await v1.getByRole('button', { name: 'rollback' }).click()
    await expect(comparison).toHaveCount(0)
    expect(fleet.writes('POST', `${kind.api}/${kind.name}/rollback/`)).toHaveLength(0)
    await v1.getByRole('button', { name: 'confirm rollback?' }).click()
    await expect(row.locator('.instr-save-row .config-feedback-success')).toHaveText('Rolled back to v1')
    expect(fleet.writes('POST', `${kind.api}/${kind.name}/rollback/1`)).toHaveLength(1)
    await expect(row.locator('.instr-version-item.current .instr-version-num')).toHaveText('v1')
  })

  test(`${kind.hash}: disable needs a second click, and a new one is created from the form`, async ({ page }) => {
    const fleet = await open(page, kind.hash)
    const row = rowByName(page, kind.other)
    await row.getByRole('button', { name: 'disable', exact: true }).click()
    expect(fleet.writes('POST', `${kind.api}/${kind.other}/toggle-active`)).toHaveLength(0)
    await row.getByRole('button', { name: 'confirm disable?' }).click()
    await expect.poll(() => fleet.writes('POST', `${kind.api}/${kind.other}/toggle-active`).map(c => c.body)).toEqual([{ isActive: false }])
    // Inactive rows leave the default list.
    await expect(row).toHaveCount(0)

    await page.getByRole('button', { name: kind.added }).click()
    const form = page.locator('.wfd-new-form')
    await form.getByPlaceholder(kind.newName).fill('brand-new')
    await form.getByPlaceholder(kind.newContent).fill('first content')
    await form.getByRole('button', { name: 'Create', exact: true }).click()
    await expect(form).toHaveCount(0)
    await expect(rowByName(page, 'brand-new')).toBeVisible()
    const created = fleet.calls.filter(c => c.method === 'POST' && c.path === kind.api)
    expect(created).toHaveLength(1)
    expect(created[0].body).toMatchObject({ name: 'brand-new', content: 'first content' })
  })

  test(`${kind.hash}: a refused save keeps the draft and shows the reason`, async ({ page }) => {
    const fleet = await open(page, kind.hash, f => {
      f.refuse[`POST ${kind.api}/${kind.name}/versions`] = { status: 400, json: { error: 'content is required' } }
    })
    const row = rowByName(page, kind.name)
    await row.locator('.instr-header').click()
    await row.locator('textarea.instr-editor').fill('a draft the server refuses')
    await row.getByRole('button', { name: 'Save', exact: true }).click()
    await expect(row.locator('.instr-save-row .config-feedback-error')).toContainText('content is required')
    await expect(row.locator('textarea.instr-editor')).toHaveValue('a draft the server refuses')
    expect(fleet.writes('POST', `${kind.api}/${kind.name}/versions`)).toHaveLength(1)
  })

  test(`${kind.hash}: an unsaved draft and the open row survive a visit to another view`, async ({ page }) => {
    await open(page, kind.hash)
    const row = rowByName(page, kind.name)
    await row.locator('.instr-header').click()
    await row.locator('textarea.instr-editor').fill('half-written draft')
    await page.locator('.sidenav-item-label', { hasText: /^Agents$/ }).click()
    await expect(agentRow(page, 'alpha')).toBeVisible()
    await page.evaluate(h => { window.location.hash = h }, `#${kind.hash}`)
    await expect(rowByName(page, kind.name).locator('textarea.instr-editor')).toHaveValue('half-written draft')
  })
}

test('instructions: a version deploys to its agent on the second click', async ({ page }) => {
  const fleet = await open(page, 'instructions')
  const row = rowByName(page, 'base')
  await row.locator('.instr-header').click()
  const v1 = row.locator('.instr-version-item', { has: page.locator('.instr-version-num', { hasText: /^v1$/ }) })
  await v1.getByRole('button', { name: 'deploy to alpha', exact: true }).click()
  // A press on the deploy button is not a press on the row: no comparison opens.
  await expect(page.getByRole('dialog')).toHaveCount(0)
  expect(fleet.writes('POST', '/api/agents/alpha/restart-with-version')).toHaveLength(0)
  await v1.getByRole('button', { name: 'confirm deploy to alpha?' }).click()
  await expect(v1.locator('.instr-deploy-success')).toHaveText('Deployed to alpha')
  expect(fleet.writes('POST', '/api/agents/alpha/restart-with-version').map(c => c.body))
    .toEqual([{ instructionName: 'base', versionNumber: 1 }])
})

test('instructions: archived rows show on request', async ({ page }) => {
  await open(page, 'instructions')
  await expect(rowByName(page, 'retired')).toHaveCount(0)
  await page.getByRole('button', { name: 'Show archived (1)' }).click()
  await expect(rowByName(page, 'retired')).toBeVisible()
})

// ── Workflow definitions ────────────────────────────────────────────────────────────────────────

test('wf-definitions: open, edit, save the next version', async ({ page }) => {
  const fleet = await open(page, 'wf-definitions')
  const row = rowByName(page, 'ReviewWorkflow')
  await row.locator('.instr-header').click()
  const editor = row.locator('textarea.instr-editor')
  await expect(editor).toHaveValue(/"seconds": 60/)
  const save = row.getByRole('button', { name: 'Save v4' })
  await expect(save).toBeDisabled()
  await editor.fill(EDITED_DEFINITION)
  await row.getByPlaceholder('reason (optional)').fill('wait longer')
  await expect(save).toBeEnabled()
  await save.click()
  await expect(row.locator('.instr-save-row .config-feedback-success')).toHaveText('Saved')
  expect(fleet.writes('PUT', '/api/workflow-definitions/ReviewWorkflow').map(c => c.body))
    .toEqual([{ definition: EDITED_DEFINITION, reason: 'wait longer' }])
  await expect(row.locator('.instr-header .instr-version')).toHaveText('v4')
  await expect(row.getByRole('button', { name: 'Save v5' })).toBeDisabled()
  // A reason stands on the version it was given for: the one this save made, not the one it replaced.
  await expect(versionRow(row, 4).locator('.instr-version-reason')).toHaveText('wait longer')
  await expect(versionRow(row, 3).locator('.instr-version-reason')).toHaveText('shorter wait')
  await expect(versionRow(row, 2).locator('.instr-version-reason')).toHaveCount(0)
})

test('wf-definitions: invalid JSON blocks the save', async ({ page }) => {
  const fleet = await open(page, 'wf-definitions')
  const row = rowByName(page, 'ReviewWorkflow')
  await row.locator('.instr-header').click()
  await row.locator('textarea.instr-editor').fill('{ not json')
  await expect(row.locator('.wfd-validation-item.blocking')).toContainText('JSON parse error')
  await expect(row.getByRole('button', { name: 'Save v4' })).toBeDisabled()
  expect(fleet.writes('PUT', '/api/workflow-definitions/')).toHaveLength(0)
})

test('wf-definitions: rollback and disable each need a second click', async ({ page }) => {
  const fleet = await open(page, 'wf-definitions')
  const row = rowByName(page, 'ReviewWorkflow')
  await row.locator('.instr-header').click()
  const v2 = row.locator('.instr-version-item', { has: page.locator('.instr-version-num', { hasText: /^v2$/ }) })
  await v2.getByRole('button', { name: 'rollback' }).click()
  await expect(page.getByRole('dialog')).toHaveCount(0)
  expect(fleet.writes('PUT', '/api/workflow-definitions/ReviewWorkflow')).toHaveLength(0)
  await v2.getByRole('button', { name: /confirm/ }).click()
  await expect(row.locator('.instr-save-row .config-feedback-success')).toHaveText('Rolled back to v2')
  const rollback = fleet.writes('PUT', '/api/workflow-definitions/ReviewWorkflow')
  expect(rollback).toHaveLength(1)
  expect(rollback[0].body).toMatchObject({ reason: 'Rollback to v2' })
  expect(String((rollback[0].body as { definition: string }).definition)).toContain('"seconds": 30')

  await row.locator('.wfd-toggle-btn').click()
  expect(fleet.writes('POST', '/api/workflow-definitions/ReviewWorkflow/toggle-active')).toHaveLength(0)
  await row.locator('.wfd-toggle-btn').click()
  await expect.poll(() => fleet.writes('POST', '/api/workflow-definitions/ReviewWorkflow/toggle-active').map(c => c.body))
    .toEqual([{ isActive: false }])
})

test('wf-definitions: search filters the list, and the count reaches the sidenav once loaded', async ({ page }) => {
  await open(page, 'wf-definitions')
  await expect(navBadge(page, 'Definitions')).toHaveText('1')
  await page.getByPlaceholder('Search definitions…').fill('nothing-like-it')
  await expect(rowByName(page, 'ReviewWorkflow')).toHaveCount(0)
  await page.getByPlaceholder('Search definitions…').fill('review')
  await expect(rowByName(page, 'ReviewWorkflow')).toBeVisible()
})

// ── Comparing versions ──────────────────────────────────────────────────────────────────────────
// One comparison for the three pages that keep versions. It opens over the page from a history
// row, on what that version changed, and either side can then be any version.

const versionRow = (row: Locator, version: number): Locator =>
  row.locator('.instr-version-item', { has: row.page().locator('.instr-version-num', { hasText: new RegExp(`^v${version}$`) }) })
/** How many comparisons the page has computed: each one is recorded on its performance timeline. */
const comparisonsComputed = (page: Page) => page.evaluate(() => performance.getEntriesByName('version-compare').length)

const TEXT_COMPARED = [
  { hash: 'instructions', name: 'base', seed: (f: FakeFleet) => { f.instructions[0] = versioned('base', HANDBOOK_VERSIONS, ['alpha']) } },
  { hash: 'project-contexts', name: 'fleet', seed: (f: FakeFleet) => { f.contexts[0] = versioned('fleet', HANDBOOK_VERSIONS, ['alpha']) } },
] as const

for (const kind of TEXT_COMPARED) {
  test(`${kind.hash}: a version is compared with the one before it, with the current one, or with any other`, async ({ page }) => {
    const fleet = await open(page, kind.hash, kind.seed)
    const row = rowByName(page, kind.name)
    await row.locator('.instr-header').click()
    await versionRow(row, 3).getByRole('button', { name: 'Compare v3' }).click()

    // It opens on what v3 changed: v2 against v3, each with its date, author and reason.
    const comparison = page.getByRole('dialog', { name: `Compare versions of ${kind.name}` })
    const older = comparison.getByLabel('Older version')
    const newer = comparison.getByLabel('Newer version')
    const summary = comparison.getByRole('status')
    await expect(older).toHaveValue('2')
    await expect(newer).toHaveValue('3')
    await expect(summary).toHaveText(/v2 → v3\s*\+4\s*-4\s*2 changes/)
    await expect(comparison.getByRole('button', { name: 'What v3 changed' })).toHaveAttribute('aria-pressed', 'true')
    await expect(comparison.locator('.vc-side').first()).toContainText('owner')
    await expect(comparison.locator('.vc-side').first()).toContainText('revision 2')
    await expect(comparison.locator('.vc-side').last()).toContainText('revision 3')

    // Only the changes are drawn. Three replaced lines are one change: removed, then added, each
    // marked by a sign as well as a colour, with the words that changed picked out.
    await expect(comparison.locator('del.vc-row')).toHaveCount(4)
    await expect(comparison.locator('ins.vc-row')).toHaveCount(4)
    await expect(comparison.locator('del.vc-row').first()).toHaveText(/^20\s*-\s*A review answers one question/)
    await expect(comparison.locator('ins.vc-row').first()).toHaveText(/^20\s*\+\s*A review answers two questions/)
    await expect(comparison.locator('del.vc-row').first().locator('mark')).toHaveText(['one question'])
    await expect(comparison.locator('ins.vc-row').first().locator('mark')).toHaveText(['two questions', 'correct, and is it '])
    // Three lines of context around a change, and the rest folded until it is asked for.
    await expect(comparison.getByText('## Reviews', { exact: true })).toBeVisible()
    await expect(comparison.getByText('# Working agreement')).toHaveCount(0)
    await expect(comparison.locator('.vc-fold').first()).toContainText('16 unchanged lines')
    await comparison.getByRole('button', { name: 'Show all 16 unchanged lines' }).click()
    await expect(comparison.getByText('# Working agreement')).toBeVisible()
    await expect(comparison.locator('.vc-fold')).toHaveCount(2)

    // The steps go from change to change, bring it into view and mark it.
    const next = comparison.getByRole('button', { name: 'Next change' })
    await expect(comparison.getByRole('button', { name: 'Previous change' })).toBeDisabled()
    await next.click()
    await expect(summary).toContainText('Change 1 of 2')
    await expect(comparison.locator('.vc-at')).toHaveCount(6)
    await next.click()
    await expect(summary).toContainText('Change 2 of 2')
    await expect(comparison.locator('.vc-at')).toHaveCount(2)
    await expect(comparison.locator('.vc-at').first()).toBeInViewport()
    await expect(next).toBeDisabled()
    await comparison.getByRole('button', { name: 'Previous change' }).click()
    await expect(summary).toContainText('Change 1 of 2')

    // One press compares the same version with the one in use.
    await comparison.getByRole('button', { name: 'Against current' }).click()
    await expect(older).toHaveValue('3')
    await expect(newer).toHaveValue('4')
    await expect(summary).toHaveText(/v3 → v4\s*\+0\s*-1\s*1 change$/)
    await expect(comparison.locator('del.vc-row')).toHaveText(/No headings in short answers/)

    // Any two versions: each side is chosen freely, and a pair chosen backwards is put in order.
    await newer.selectOption('2')
    await expect(older).toHaveValue('2')
    await expect(newer).toHaveValue('3')
    await older.selectOption('1')
    await expect(summary).toContainText('v1 → v3')
    await older.selectOption('4')
    await expect(older).toHaveValue('3')
    await expect(newer).toHaveValue('4')
    await expect(summary).toContainText('v3 → v4')
    // Against nothing, every line is an added one.
    await older.selectOption({ label: 'nothing' })
    await expect(summary).toHaveText(/nothing → v4\s*\+39\s*-0\s*1 change$/)
    await expect(comparison.locator('.vc-side').first()).toContainText('Before the first version')
    await expect(comparison.locator('del.vc-row')).toHaveCount(0)
    await expect(comparison.locator('ins.vc-row')).toHaveCount(39)
    await comparison.getByRole('button', { name: 'What v3 changed' }).click()
    await expect(summary).toContainText('v2 → v3')

    // Closing puts the focus back on the row it was opened from, and nothing was written.
    const computed = await comparisonsComputed(page)
    await comparison.getByRole('button', { name: 'Close' }).click()
    await expect(comparison).toHaveCount(0)
    await expect(versionRow(row, 3).getByRole('button', { name: 'Compare v3' })).toBeFocused()
    expect(fleet.calls).toHaveLength(0)

    // With the versions still loaded, the editor beside them is typed in and nothing is compared.
    await row.locator('textarea.instr-editor').pressSequentially(' and a few more words')
    await expect(row.getByRole('button', { name: 'Save', exact: true })).toBeEnabled()
    expect(await comparisonsComputed(page)).toBe(computed)
  })

  test(`${kind.hash}: the oldest version is compared with nothing, and a press on the row opens it too`, async ({ page }) => {
    await open(page, kind.hash, kind.seed)
    const row = rowByName(page, kind.name)
    await row.locator('.instr-header').click()
    await versionRow(row, 1).locator('.instr-version-meta').click()
    const comparison = page.getByRole('dialog', { name: `Compare versions of ${kind.name}` })
    await expect(comparison.getByLabel('Older version')).toHaveValue('')
    await expect(comparison.getByRole('status')).toHaveText(/nothing → v1\s*\+39\s*-0/)
    await expect(comparison.locator('.vc-note')).toHaveCount(0)
    // The dialog has the focus, so Escape reaches it; it goes back to the row's own button.
    await expect(comparison).toBeFocused()
    await page.keyboard.press('Escape')
    await expect(comparison).toHaveCount(0)
    await expect(versionRow(row, 1).getByRole('button', { name: 'Compare v1' })).toBeFocused()

    // The current version is compared with the one before it, and has no "against current".
    await versionRow(row, 4).getByRole('button', { name: 'Compare v4' }).click()
    await expect(comparison.getByRole('status')).toContainText('v3 → v4')
    await expect(comparison.getByLabel('Newer version').locator('option:checked')).toHaveText('v4 (current)')
    await expect(comparison.getByRole('button', { name: 'Against current' })).toBeDisabled()
    // The same version on both sides is said, not drawn as an empty comparison.
    await comparison.getByLabel('Older version').selectOption('4')
    await expect(comparison.locator('.vc-note')).toHaveText('v4 is on both sides: there is nothing to compare.')
  })

  test(`${kind.hash}: the oldest version kept, when earlier ones are gone, does not claim to have written every line`, async ({ page }) => {
    // Only the newest versions are kept: here v1 and v2 have been dropped.
    await open(page, kind.hash, f => {
      kind.seed(f)
      const kept = kind.hash === 'instructions' ? f.instructions[0] : f.contexts[0]
      kept.versions = kept.versions.filter(v => v.versionNumber > 2)
    })
    const row = rowByName(page, kind.name)
    await row.locator('.instr-header').click()
    await versionRow(row, 3).getByRole('button', { name: 'Compare v3' }).click()
    const comparison = page.getByRole('dialog', { name: `Compare versions of ${kind.name}` })
    await expect(comparison.getByRole('status')).toContainText('nothing → v3')
    await expect(comparison.locator('.vc-note')).toHaveText('Versions before v3 are not kept, so every line of v3 shows as added, not only what it changed.')
    // With the one before it kept, the note goes.
    await comparison.getByLabel('Newer version').selectOption('4')
    await comparison.getByLabel('Older version').selectOption({ label: 'nothing' })
    await expect(comparison.getByRole('status')).toContainText('nothing → v4')
    await expect(comparison.locator('.vc-note')).toHaveCount(0)
  })
}

/** A definition as the orchestrator sends it: every version one line of JSON, the history newest first. */
function releaseDefinition(fleet: FakeFleet, versions: string[]) {
  const [current, ...history] = versions.map((definition, i) => ({ version: i + 1, definition })).reverse()
  fleet.definitions[0] = {
    ...fleet.definitions[0], version: current.version, definition: current.definition,
    createdAt: RELEASE_CREATED, createdBy: 'creator',
    // A history row is stamped with the write that replaced its version.
    versions: history.map(v => ({ ...v, reason: `reason for v${v.version + 1}`, createdAt: releaseWritten(v.version + 1), createdBy: `author-of-v${v.version + 1}` })),
  }
}
const RELEASE_CREATED = '2026-11-01T08:30:00Z'
/** When the write that made `version` happened: a different day for each. */
const releaseWritten = (version: number) => `2026-11-${10 + version}T12:00:00Z`

test('wf-definitions: a one-line definition is laid out and compared line by line', async ({ page }) => {
  const fleet = await open(page, 'wf-definitions', f => releaseDefinition(f, RELEASE_VERSIONS))
  const row = rowByName(page, 'ReviewWorkflow')
  await row.locator('.instr-header').click()
  // The version in use is a row of the history like the others.
  await versionRow(row, 3).getByRole('button', { name: 'Compare v3' }).click()

  const comparison = page.getByRole('dialog', { name: 'Compare versions of ReviewWorkflow' })
  const older = comparison.getByLabel('Older version')
  const newer = comparison.getByLabel('Newer version')
  const summary = comparison.getByRole('status')
  await expect(older).toHaveValue('2')
  await expect(newer.locator('option:checked')).toHaveText('v3 (current)')
  // v3 moved two steps into a loop: the loop's own six lines are the change, not the steps inside it.
  await expect(summary).toHaveText(/v2 → v3\s*\+6\s*-0\s*2 changes/)
  await expect(comparison.locator('ins.vc-row')).toHaveText([/\+\s*\{$/, /"type": "loop",$/, /"maxIterations": 3,$/, /"steps": \[$/, /\+\s*\]$/, /\+\s*\},$/])
  await expect(comparison.locator('del.vc-row')).toHaveCount(0)
  await expect(comparison.locator('.vc-note')).toHaveCount(0)
  // Each version carries the reason and the author of the write that made it.
  await expect(comparison.locator('.vc-side').last()).toContainText('reason for v3')
  await expect(comparison.locator('.vc-side').last()).toContainText('author-of-v3')
  await expect(comparison.locator('.vc-side').first()).toContainText('reason for v2')
  await expect(comparison.getByRole('button', { name: 'Against current' })).toBeDisabled()

  // A long fold opens twenty lines at a time from either end, or all at once.
  const fold = comparison.locator('.vc-fold').first()
  await expect(fold).toContainText('25 unchanged lines')
  await expect(comparison.getByText('"type": "sequence",')).toHaveCount(0)
  await fold.getByRole('button', { name: 'Show the last 20 of 25 unchanged lines' }).click()
  await expect(fold).toContainText('5 unchanged lines')
  await expect(comparison.getByText('"type": "sequence",')).toHaveCount(0)
  await fold.getByRole('button', { name: 'Show all 5 unchanged lines' }).click()
  await expect(comparison.getByText('"type": "sequence",')).toBeVisible()

  await comparison.getByRole('button', { name: 'Next change' }).click()
  await expect(summary).toContainText('Change 1 of 2')
  await expect(comparison.locator('.vc-at')).toHaveCount(4)

  // Another pair: v1 against v3 also has the longer review, with the number that changed picked out.
  await older.selectOption('1')
  await expect(summary).toContainText('v1 → v3')
  await expect(comparison.locator('del.vc-row')).toHaveText(/-\s*"timeoutMinutes": 45,/)
  await expect(comparison.locator('del.vc-row mark')).toHaveText('45')
  await expect(comparison.locator('ins.vc-row mark').first()).toHaveText('60')

  await page.keyboard.press('Escape')
  await expect(comparison).toHaveCount(0)
  await expect(versionRow(row, 3).getByRole('button', { name: 'Compare v3' })).toBeFocused()

  // From an older row: what it changed, then the same version against the current one.
  await versionRow(row, 2).locator('.instr-version-meta').click()
  await expect(summary).toContainText('v1 → v2')
  await comparison.getByRole('button', { name: 'Against current' }).click()
  await expect(summary).toContainText('v2 → v3')
  await comparison.getByRole('button', { name: 'Close' }).click()
  await expect(versionRow(row, 2).getByRole('button', { name: 'Compare v2' })).toBeFocused()
  expect(fleet.calls).toHaveLength(0)
})

test('wf-definitions: a history row and the comparison’s header say the same of a version', async ({ page }) => {
  await open(page, 'wf-definitions', f => releaseDefinition(f, RELEASE_VERSIONS))
  const row = rowByName(page, 'ReviewWorkflow')
  await row.locator('.instr-header').click()
  const comparison = page.getByRole('dialog', { name: 'Compare versions of ReviewWorkflow' })
  // The date as this browser prints it, whatever its time zone.
  const printed = (iso: string) => page.evaluate(d => new Date(d).toLocaleString([], { dateStyle: 'medium', timeStyle: 'short' }), iso)

  // Each version is described by the write that made it; the first, by the definition's own creation, and it has no reason.
  const versions = [
    { version: 3, written: releaseWritten(3), author: 'author-of-v3', reasons: ['reason for v3'] },
    { version: 2, written: releaseWritten(2), author: 'author-of-v2', reasons: ['reason for v2'] },
    { version: 1, written: RELEASE_CREATED, author: 'creator', reasons: [] },
  ]
  for (const { version, written, author, reasons } of versions) {
    const item = versionRow(row, version)
    const date = await item.locator('.instr-version-meta span').first().getAttribute('title')
    expect(date).toBe(await printed(written))
    await expect(item.locator('.instr-version-meta span')).toHaveText([/ago$/, author])
    await expect(item.locator('.instr-version-reason')).toHaveText(reasons)

    // The comparison opens with that version as its newer side, and says of it what the row says.
    await item.getByRole('button', { name: `Compare v${version}` }).click()
    await expect(comparison.getByLabel('Newer version')).toHaveValue(String(version))
    const side = comparison.locator('.vc-side').last()
    await expect(side.locator('.vc-side-facts')).toHaveText(`${date} · ${author}`)
    await expect(side.locator('.vc-side-reason')).toHaveText(reasons)
    await page.keyboard.press('Escape')
    await expect(comparison).toHaveCount(0)
  }
})

test('wf-definitions: a version that is not JSON is compared as plain text, and the comparison says so', async ({ page }) => {
  await open(page, 'wf-definitions', f => releaseDefinition(f, ['{ "type": "sequence", not json', RELEASE_VERSIONS[0]]))
  const row = rowByName(page, 'ReviewWorkflow')
  await row.locator('.instr-header').click()
  await versionRow(row, 2).getByRole('button', { name: 'Compare v2' }).click()
  const comparison = page.getByRole('dialog', { name: 'Compare versions of ReviewWorkflow' })
  await expect(comparison.locator('.vc-note-warn')).toHaveText('v1 is not valid JSON, so the two versions are compared as plain text.')
  await expect(comparison.getByRole('status')).toHaveText(/v1 → v2\s*\+1\s*-1/)
})

test('wf-definitions: a definition of 240,000 characters compares at once, and redrawing the page behind computes nothing', async ({ page }) => {
  const fleet = await open(page, 'wf-definitions', f => releaseDefinition(f, [largeDefinition(), largeDefinition(true)]))
  const row = rowByName(page, 'ReviewWorkflow')
  await row.locator('.instr-header').click()
  const editor = row.locator('textarea.instr-editor')
  await expect(editor).toHaveValue(/release-approval/)
  await versionRow(row, 2).getByRole('button', { name: 'Compare v2' }).click()

  const comparison = page.getByRole('dialog', { name: 'Compare versions of ReviewWorkflow' })
  await expect(comparison.getByRole('status')).toHaveText(/v1 → v2\s*\+13\s*-30\s*6 changes/)
  // Seven thousand lines, of which the changes and their context are drawn.
  expect(await comparison.locator('.vc-row').count()).toBeLessThan(200)
  await expect(comparison.locator('ins.vc-row mark', { hasText: 'every' })).toHaveCount(1)
  const times = await page.evaluate(() => performance.getEntriesByName('version-compare').map(e => e.duration))
  expect(times.length).toBeGreaterThan(0)
  for (const time of times) expect(time).toBeLessThan(1000)

  // The page behind the dialog redraws: its editor changes, heartbeats arrive. None of it compares again.
  const computed = times.length
  for (const draft of ['{ not json', '{ "type": "noop" }', '{ still not json']) {
    await editor.fill(draft)
    await expect(editor).toHaveValue(draft)
  }
  await expect(row.locator('.wfd-validation-item.blocking')).toContainText('JSON parse error')
  for (let beat = 0; beat < 5; beat++) fleet.pushAgent('alpha', { currentTask: `task ${beat}` })
  await page.waitForTimeout(700)
  expect(await comparisonsComputed(page)).toBe(computed)

  // A new pair is a new comparison; the same pair again is not.
  await comparison.getByLabel('Older version').selectOption({ label: 'nothing' })
  await expect(comparison.getByRole('status')).toContainText('nothing → v2')
  // Every one of its seven thousand lines is drawn as added, and the page is still answering.
  await expect(comparison.locator('del.vc-row, div.vc-row')).toHaveCount(0)
  expect(await comparison.locator('ins.vc-row').count()).toBeGreaterThan(5000)
  const afterNewPair = await comparisonsComputed(page)
  expect(afterNewPair).toBeGreaterThan(computed)
  await comparison.getByRole('button', { name: 'Next change' }).click()
  await expect(comparison.getByRole('status')).toContainText('Change 1 of 1')
  expect(await comparisonsComputed(page)).toBe(afterNewPair)

  // Closed, there is no comparison at all: typing in the editor computes none.
  await page.keyboard.press('Escape')
  await expect(comparison).toHaveCount(0)
  await editor.pressSequentially('abc')
  await expect(editor).toHaveValue('{ still not jsonabc')
  expect(await comparisonsComputed(page)).toBe(afterNewPair)
})

test.describe('comparing versions on a phone', () => {
  test.use({ viewport: { width: 390, height: 844 } })

  test('the comparison takes the screen, and its lines wrap instead of scrolling sideways', async ({ page }) => {
    const long = 'This rule is one very long line of prose, as real instructions have, and it goes on well past any screen. '.repeat(7)
    const fleet = new FakeFleet()
    fleet.instructions[0] = versioned('base', [`# Rules\n\n${long}\n`, `# Rules\n\n${long.replace('goes on', 'runs on')}\n`], ['alpha'])
    await fleet.install(page)
    await page.goto('/#instructions')
    const row = rowByName(page, 'base')
    await row.locator('.instr-header').click()
    await versionRow(row, 2).getByRole('button', { name: 'Compare v2' }).click()
    const comparison = page.getByRole('dialog', { name: 'Compare versions of base' })
    await expect(comparison.locator('ins.vc-row mark').first()).toHaveText('runs')

    // A sheet from the bottom edge, as wide as the screen.
    const box = (await comparison.boundingBox())!
    expect([box.x, box.width, box.y + box.height]).toEqual([0, 390, 844])
    const widths = await page.evaluate(() => {
      const body = document.querySelector('.vc-body')!
      return { page: document.documentElement.scrollWidth, screen: window.innerWidth, body: body.scrollWidth, bodyBox: body.clientWidth }
    })
    expect(widths.page).toBeLessThanOrEqual(widths.screen)
    expect(widths.body).toBeLessThanOrEqual(widths.bodyBox)
    // The long line is drawn on many rows of the screen, not one.
    expect((await comparison.locator('ins.vc-row').first().boundingBox())!.height).toBeGreaterThan(100)
    // Every control is still there to press.
    for (const name of ['What v2 changed', 'Against current', 'Previous change', 'Next change', 'Close']) {
      await expect(comparison.getByRole('button', { name })).toBeInViewport()
    }
  })
})

// ── Chat ────────────────────────────────────────────────────────────────────────────────────────

async function openChat(page: Page, hash: string, prepare?: (chat: FakeChat) => void, seen?: Record<string, number>) {
  const fleet = new FakeFleet()
  const chat = new FakeChat()
  prepare?.(chat)
  await fleet.install(page)
  await chat.install(page)
  if (seen) await page.addInitScript(kept => { window.localStorage.setItem('fleet.chat.seen', JSON.stringify(kept)) }, seen)
  await page.goto(`/#${hash}`)
  // The frame, not the sidebar: on a phone the sidebar is not on screen.
  await expect(page.locator('.app-main')).toBeVisible()
  return { fleet, chat }
}

const chatAgent = (page: Page, name: string): Locator =>
  page.locator('.chat-agent', { has: page.locator('.chat-agent-name', { hasText: new RegExp(`^${name}$`) }) })

/** A finished exchange: a message and its answer. */
function exchange(chat: FakeChat, agent: string, id: string, question: string, answer: string) {
  chat.push(agent, 'submission.text', id, { text: question })
  chat.push(agent, 'turn.started', id)
  chat.push(agent, 'turn.final', id, { text: answer, completion: 'completed' })
}

test('chat: a thread that moved on is marked in the navigation and the list; opening it clears the mark', async ({ page }) => {
  await openChat(page, 'agents', chat => {
    exchange(chat, 'alpha', 's1', 'first', 'answer one')
    exchange(chat, 'beta', 's1', 'hello', 'answer two')
  }, { alpha: 3, beta: 1 })

  // Beta's thread stands at 3 and this browser showed 1 of it: one thread with news.
  await expect(navBadge(page, 'Chat')).toHaveText('1')
  await expect(page).toHaveTitle(/^\(1\) /)

  await page.locator('.sidenav-item', { hasText: 'Chat' }).click()
  await expect(chatAgent(page, 'Beta').locator('.chat-agent-new')).toBeVisible()
  await expect(chatAgent(page, 'Alpha').locator('.chat-agent-new')).toHaveCount(0)
  // A line of each thread, and how each agent is doing, from the fleet, one hover away.
  await expect(chatAgent(page, 'Beta').locator('.chat-agent-caption')).toHaveText('answer two')
  await expect(chatAgent(page, 'Alpha').locator('.chat-agent-caption')).toHaveText('answer one')
  await expect(chatAgent(page, 'Beta')).toHaveAttribute('title', 'Working: Implement the review step')
  await expect(chatAgent(page, 'Alpha')).toHaveAttribute('title', 'Idle')

  await chatAgent(page, 'Beta').click()
  await expect(page.locator('.chat-bubble--agent')).toHaveText(/answer two/)
  await expect(navBadge(page, 'Chat')).toHaveCount(0)
  await expect(page).not.toHaveTitle(/^\(/)
  expect(await page.evaluate(() => JSON.parse(window.localStorage.getItem('fleet.chat.seen')!))).toMatchObject({ beta: 3 })
})

test('chat: a browser that never opened chat does not start with every old thread marked', async ({ page }) => {
  await openChat(page, 'agents', chat => exchange(chat, 'alpha', 's1', 'old', 'older'))
  await expect(page.locator('.sidenav-item', { hasText: 'Chat' })).toBeVisible()
  await expect.poll(() => page.evaluate(() => window.localStorage.getItem('fleet.chat.seen'))).toBe('{"alpha":3}')
  await expect(navBadge(page, 'Chat')).toHaveCount(0)
})

test('chat: Enter sends; a running turn shows its tools and can be stopped; the answer replaces it', async ({ page }) => {
  const { chat } = await openChat(page, 'chat/alpha')
  const input = page.getByLabel('Message Alpha')
  await input.fill('count the tests')
  await input.press('Shift+Enter')
  await input.pressSequentially('and the files')
  await input.press('Enter')

  await expect(page.locator('.chat-bubble--you')).toHaveText('You: count the tests\nand the files')
  await expect(input).toHaveValue('')
  const sent = chat.writes('POST', /\/submissions$/)
  expect(sent).toHaveLength(1)
  expect(sent[0].body).toMatchObject({ type: 'create', text: 'count the tests\nand the files' })
  expect(sent[0].body).not.toHaveProperty('attachments')
  const id = (sent[0].body as { submissionId: string }).submissionId

  chat.push('alpha', 'turn.started', id)
  chat.push('alpha', 'turn.progress', id, { activity: 'tool', toolName: 'Grep' })
  chat.push('alpha', 'turn.progress', id, { activity: 'tool', toolName: 'Read' })
  chat.push('alpha', 'turn.progress', id, { activity: 'tool', toolName: 'Read' })
  await expect(page.locator('.chat-working-line')).toContainText('Alpha is using Read…')
  // What the agent says between tools shows while the turn still runs, apart from the answer.
  chat.push('alpha', 'turn.message', id, { text: 'Counting them now.' })
  await expect(page.locator('.chat-bubble--interim')).toHaveText('Agent: Counting them now.')
  await expect(page.locator('.chat-working')).toBeVisible()
  await expect(page.locator('.chat-trail-step')).toHaveText(['Grep', 'Read ×2'])

  await page.getByRole('button', { name: 'Stop' }).click()
  await expect.poll(() => chat.writes('POST', /:cancel$/).length).toBe(1)

  chat.push('alpha', 'turn.final', id, { text: 'There are **330** tests.', completion: 'completed' })
  await expect(page.locator('.chat-working')).toHaveCount(0)
  await expect(page.locator('.chat-bubble--agent strong')).toHaveText('330')
  await expect(page.getByRole('button', { name: 'Stop' })).toHaveCount(0)
})

test('chat: a picture is reserved, sent up with its own token, named by the message, and shown from the thread', async ({ page }) => {
  const { chat } = await openChat(page, 'chat/alpha')
  await page.locator('#chat-file-picker').setInputFiles({ name: 'shot.png', mimeType: 'image/png', buffer: PIXEL })
  await expect(page.locator('.chat-draft-file img')).toBeVisible()
  // A picture alone is a message.
  await page.getByRole('button', { name: 'Send' }).click()

  await expect(page.locator('.chat-row--you .chat-picture img')).toHaveAttribute('src', '/v1/attachments/att-1/content')
  await expect(page.locator('.chat-draft-file')).toHaveCount(0)

  const reserve = chat.writes('POST', /\/attachments$/)
  expect(reserve).toHaveLength(1)
  expect(reserve[0].body).toMatchObject({ kind: 'image', contentType: 'image/png', byteSize: PIXEL.length, fileName: 'shot.png' })
  expect((reserve[0].body as { sha256: string }).sha256).toMatch(/^[0-9a-f]{64}$/)
  const upload = chat.writes('PUT', /\/content$/)
  expect(upload.map(u => [u.path, u.headers.authorization, u.headers['content-type'], u.body])).toEqual([
    ['/v1/attachments/att-1/content', 'Bearer token-att-1', 'image/png', PIXEL.length],
  ])
  expect(chat.writes('POST', /\/submissions$/)[0].body).toMatchObject({ text: '', attachments: [{ attachmentId: 'att-1', kind: 'image' }] })
})

test('chat: a file that is not a picture goes up as a file and is shown as a link that saves it', async ({ page }) => {
  const { chat } = await openChat(page, 'chat/alpha')
  await page.locator('#chat-file-picker').setInputFiles({ name: 'notes.pdf', mimeType: 'application/pdf', buffer: Buffer.from('%PDF-1.7 notes') })
  // Named, not drawn, before it is sent.
  await expect(page.locator('.chat-draft-file--named')).toContainText('notes.pdf')
  await page.getByLabel('Message Alpha').fill('read this')
  await page.getByRole('button', { name: 'Send' }).click()

  const link = page.locator('.chat-row--you a.chat-file')
  await expect(link).toContainText('notes.pdf')
  expect(chat.writes('POST', /\/attachments$/)[0].body).toMatchObject({ kind: 'file', fileName: 'notes.pdf', byteSize: 14 })
  expect(chat.writes('POST', /\/submissions$/)[0].body).toMatchObject({ text: 'read this', attachments: [{ attachmentId: 'att-1', kind: 'file' }] })
  await expect(link).toHaveAttribute('href', '/v1/attachments/att-1/content')
  await expect(link).toHaveAttribute('download', 'notes.pdf')
  // It is never drawn in the page.
  await expect(page.locator('.chat-row--you .chat-picture')).toHaveCount(0)
})

test('chat: a deployment with files off says so once and stops offering them; the words are kept', async ({ page }) => {
  const { chat } = await openChat(page, 'chat/alpha', c => { c.filesOn = false })
  await page.getByLabel('Message Alpha').fill('look at this')
  await page.locator('#chat-file-picker').setInputFiles({ name: 'shot.png', mimeType: 'image/png', buffer: PIXEL })
  await page.getByRole('button', { name: 'Send' }).click()

  await expect(page.locator('.chat-meta--error')).toContainText('files in chat switched off')
  await expect(page.getByRole('button', { name: 'Attach files' })).toHaveCount(0)
  // Nothing was sent without its picture, and the failed message can be taken back out.
  expect(chat.writes('POST', /\/submissions$/)).toHaveLength(0)
  await expect(page.locator('.chat-bubble--failed')).toHaveText('You: look at this')
})

test('chat: replying to an answer names it to the chat service and shows what was answered above the reply', async ({ page }) => {
  const { chat } = await openChat(page, 'chat/alpha', c => exchange(c, 'alpha', 's1', 'is it broken?', 'Yes. Want me to open a PR?'))

  await page.locator('.chat-row--agent').hover()
  await page.locator('.chat-row--agent').getByRole('button', { name: 'Reply', exact: true }).click()
  await expect(page.locator('.chat-replying')).toContainText('Replying to Alpha')
  await expect(page.locator('.chat-replying-quote')).toHaveText('Yes. Want me to open a PR?')
  // The cursor is where the answer goes.
  await expect(page.getByLabel('Message Alpha')).toBeFocused()

  await page.keyboard.type('yes please')
  await page.keyboard.press('Enter')

  const reply = page.locator('.chat-row--you').last()
  await expect(reply.locator('.chat-quote-who')).toHaveText('Alpha')
  await expect(reply.locator('.chat-quote-text')).toHaveText('Yes. Want me to open a PR?')
  await expect(page.locator('.chat-replying')).toHaveCount(0)
  // The answer's event id (the third event of the thread), not its text, is what is sent.
  expect(chat.writes('POST', /\/submissions$/)[0].body).toMatchObject({ text: 'yes please', replyToEventId: 'c-alpha-e3' })

  // Escape takes a started reply back.
  await page.locator('.chat-row--agent').hover()
  await page.locator('.chat-row--agent').getByRole('button', { name: 'Reply', exact: true }).click()
  await page.keyboard.press('Escape')
  await expect(page.locator('.chat-replying')).toHaveCount(0)
})

test('chat: an incompatible runtime has no Reply action but still accepts a new message', async ({ page }) => {
  const { chat } = await openChat(page, 'chat/alpha', c => {
    c.agents[0].replySupported = false
    exchange(c, 'alpha', 's1', 'an earlier question', 'an earlier answer')
  })
  await expect(page.locator('.chat-row--agent')).toBeVisible()
  await expect(page.getByRole('button', { name: 'Reply', exact: true })).toHaveCount(0)
  await page.locator('.chat-composer textarea').fill('a new message')
  await page.getByRole('button', { name: 'Send', exact: true }).click()
  await expect.poll(() => chat.writes('POST', /\/submissions$/).length).toBe(1)
  expect((chat.writes('POST', /\/submissions$/)[0].body as { replyToEventId?: string }).replyToEventId ?? null).toBeNull()
})

test('chat: what was typed and not sent is still there after a reload', async ({ page }) => {
  await openChat(page, 'chat/alpha')
  await page.getByLabel('Message Alpha').fill('half a thought')
  await page.reload()
  await expect(page.getByLabel('Message Alpha')).toHaveValue('half a thought')
})

test('chat: an agent’s row on the Agents page opens its thread; an agent without chat has no such button', async ({ page }) => {
  await openChat(page, 'agents')
  await expect(agentRow(page, 'gamma').getByTitle('Chat')).toHaveCount(0)
  await agentRow(page, 'beta').locator('.agent-row-actions').getByTitle('Chat').click()
  await expect(page).toHaveURL(/#chat\/beta$/)
  await expect(page.locator('.chat-thread-title')).toHaveText('Beta')
  // And back: the thread's header opens the agent.
  await page.getByRole('button', { name: 'Beta on the Agents page' }).click()
  await expect(page).toHaveURL(/#agents$/)
})

test.describe('chat on a phone', () => {
  test.use({ viewport: { width: 390, height: 844 } })

  test('the list and the thread take turns on the screen', async ({ page }) => {
    await openChat(page, 'chat', chat => exchange(chat, 'alpha', 's1', 'first', 'answer one'))
    await expect(chatAgent(page, 'Alpha')).toBeVisible()
    await expect(page.locator('.chat-thread')).toBeHidden()

    await chatAgent(page, 'Alpha').click()
    await expect(page.locator('.chat-bubble--agent')).toHaveText(/answer one/)
    await expect(page.locator('.chat-agents')).toBeHidden()
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth + 1)).toBe(true)

    await page.getByRole('button', { name: 'All chats' }).click()
    await expect(chatAgent(page, 'Alpha')).toBeVisible()
    await expect(page.locator('.chat-thread')).toBeHidden()
  })
})

// ── Usage ───────────────────────────────────────────────────────────────────────────────────────

type UsageAnswer = { status: number; json: unknown; delayMs?: number }

const usageGroup = (agent: string, over: Record<string, unknown> = {}) => ({
  agent, provider: 'claude', model: 'model-x',
  reported_value: '150', eligible_turns: 3, measured_turns: 2, reset_excluded: 1,
  buckets: [
    { start: '2026-01-05T08:00:00Z', reported_value: '100', eligible_turns: 2, measured_turns: 2, reset_excluded: 0 },
    { start: '2026-01-05T09:00:00Z', reported_value: '50', eligible_turns: 1, measured_turns: 0, reset_excluded: 1 },
  ],
  ...over,
})

const usageAnswer = (groups: unknown[], over: Record<string, unknown> = {}): UsageAnswer => ({
  status: 200,
  json: {
    from: '2026-01-04T10:00:00.000Z', to: '2026-01-05T10:00:00.000Z', metric: 'output_tokens', bucket_seconds: 3600,
    matched_groups: groups.length, groups_shown: groups.length, truncated: false, groups, ...over,
  },
})

/** Opens #usage with `/api/usage` answered by `respond`, called with each request's query and its number. */
async function openUsage(page: Page, respond: (query: URLSearchParams, n: number) => UsageAnswer) {
  const fleet = new FakeFleet()
  await fleet.install(page)
  const requests: URLSearchParams[] = []
  await page.route(url => url.pathname === '/api/usage', async route => {
    const query = new URL(route.request().url()).searchParams
    requests.push(query)
    const answer = respond(query, requests.length)
    if (answer.delayMs) await new Promise(r => setTimeout(r, answer.delayMs))
    await route.fulfill({ status: answer.status, json: answer.json }).catch(() => { /* aborted by a newer request */ })
  })
  await page.goto('/#usage')
  await expect(page.locator('.view-page-title', { hasText: 'Usage' })).toBeVisible()
  return requests
}

const usageRow = (page: Page, agent: string) => page.locator('.usage-row', { has: page.locator('.usage-select', { hasText: agent }) })

test('#usage loads once with the last 24 hours of output tokens, and rows select the chart by click and keyboard', async ({ page }) => {
  const requests = await openUsage(page, () => usageAnswer([usageGroup('agent-a'), usageGroup('agent-b', { reported_value: '90', measured_turns: 3, reset_excluded: 0 })]))

  await expect(page.locator('.usage-chart-title')).toHaveText('agent-a · claude · model-x')
  await expect(usageRow(page, 'agent-a').locator('.usage-value')).toHaveText('150 (subtotal)')
  await expect(usageRow(page, 'agent-a')).toContainText('2 of 3 turns reported · 1 turn reset, excluded')
  await expect(page.locator('.usage-note').first()).toContainText('not a bill')

  expect(requests).toHaveLength(1)
  const [first] = requests
  expect(first.get('metric')).toBe('output_tokens')
  expect(first.get('from')).toMatch(/^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{3}Z$/)
  expect(Date.parse(first.get('to')!) - Date.parse(first.get('from')!)).toBe(24 * 3_600_000)

  await usageRow(page, 'agent-b').click()
  await expect(page.locator('.usage-chart-title')).toHaveText('agent-b · claude · model-x')

  await page.locator('.usage-select', { hasText: 'agent-a' }).focus()
  await page.keyboard.press('ArrowDown')
  await expect(page.locator('.usage-select', { hasText: 'agent-b' })).toBeFocused()
  await expect(page.locator('.usage-select', { hasText: 'agent-b' })).toHaveAttribute('aria-pressed', 'true')
  await page.keyboard.press('ArrowUp')
  await expect(page.locator('.usage-chart-title')).toHaveText('agent-a · claude · model-x')

  // Nothing polls: only the explicit Refresh fetches again.
  await page.waitForTimeout(1200)
  expect(requests).toHaveLength(1)
  await page.getByRole('button', { name: 'Refresh' }).click()
  await expect.poll(() => requests.length).toBe(2)
})

test('#usage survives a reload and sends exact filters and the week preset only when asked', async ({ page }) => {
  const requests = await openUsage(page, () => usageAnswer([usageGroup('agent-a')]))
  await page.reload()
  await expect(page.locator('.usage-chart-title')).toHaveText('agent-a · claude · model-x')
  const afterReload = requests.length

  const filters = page.locator('.usage-filters')
  await filters.getByLabel('Agent').fill('agent-a')
  await filters.getByLabel('Model').fill('model-x')
  expect(requests).toHaveLength(afterReload)   // typing alone sends nothing
  await page.getByRole('button', { name: 'Apply' }).click()
  await expect.poll(() => requests.length).toBe(afterReload + 1)
  expect(requests.at(-1)!.get('agent')).toBe('agent-a')
  expect(requests.at(-1)!.get('model')).toBe('model-x')
  expect(requests.at(-1)!.has('provider')).toBe(false)

  await page.getByRole('button', { name: 'Last 7 days' }).click()
  await expect.poll(() => requests.length).toBe(afterReload + 2)
  const week = requests.at(-1)!
  expect(Date.parse(week.get('to')!) - Date.parse(week.get('from')!)).toBe(168 * 3_600_000)
})

test('an older answer never replaces a newer filter', async ({ page }) => {
  await openUsage(page, query => query.get('metric') === 'output_tokens'
    ? { ...usageAnswer([usageGroup('slow-agent')]), delayMs: 900 }
    : usageAnswer([usageGroup('fast-agent')], { metric: 'cost_usd' }))

  await page.getByLabel('Metric').selectOption('cost_usd')
  await expect(page.locator('.usage-chart-title')).toHaveText('fast-agent · claude · model-x')
  await page.waitForTimeout(1300)
  await expect(page.locator('.usage-chart-title')).toHaveText('fast-agent · claude · model-x')
  await expect(page.getByText('slow-agent')).toHaveCount(0)
})

for (const [error, text] of [
  ['usage_disabled', 'Usage collection is off on this deployment.'],
  ['usage_not_configured', 'Usage collection is on but not configured'],
] as const) {
  test(`#usage names ${error} and offers no fake data`, async ({ page }) => {
    await openUsage(page, () => ({ status: 503, json: { error } }))
    await expect(page.locator('.usage-failure')).toContainText(text)
    await expect(page.locator('.usage-table')).toHaveCount(0)
  })
}

test('#usage unavailable offers Retry for the current filters, and a failed refresh keeps the last answer as stale', async ({ page }) => {
  const requests = await openUsage(page, (_, n) => n === 2 ? usageAnswer([usageGroup('agent-a')]) : { status: 503, json: { error: 'usage_unavailable' } })

  await expect(page.locator('.usage-failure')).toContainText('Usage data is unavailable right now.')
  await page.locator('.usage-failure').getByRole('button', { name: 'Retry' }).click()
  await expect(page.locator('.usage-chart-title')).toHaveText('agent-a · claude · model-x')
  expect(requests[1].get('metric')).toBe(requests[0].get('metric'))

  await page.getByRole('button', { name: 'Refresh' }).click()
  await expect(page.locator('.usage-stale')).toContainText(/Showing the last answer, fetched at/)
  await expect(page.locator('.usage-chart-title')).toHaveText('agent-a · claude · model-x')
  await expect(page.locator('.usage-stale').getByRole('button', { name: 'Retry' })).toBeVisible()
})

test('#usage shows not reported, reset-only and empty as states, never as 0', async ({ page }) => {
  await openUsage(page, query => query.get('metric') === 'output_tokens'
    ? usageAnswer([
      usageGroup('agent-none', { reported_value: null, measured_turns: 0, reset_excluded: 0, buckets: [] }),
      usageGroup('agent-reset', { reported_value: null, eligible_turns: 0, measured_turns: 0, reset_excluded: 2, buckets: [] }),
    ])
    : usageAnswer([]))

  await expect(usageRow(page, 'agent-none').locator('.usage-value')).toHaveText('not reported')
  await expect(usageRow(page, 'agent-reset').locator('.usage-value')).toHaveText('reset turns only')
  await expect(page.locator('.usage-chart-empty')).toContainText('did not report')
  await usageRow(page, 'agent-reset').click()
  await expect(page.locator('.usage-chart-empty')).toContainText('Only reset turns')
  await expect(page.locator('.usage-table td.usage-value', { hasText: /^0$/ })).toHaveCount(0)

  await page.getByLabel('Metric').selectOption('input_tokens')
  await expect(page.getByText('No recorded turns in this range.')).toBeVisible()
})

for (const colorScheme of ['light', 'dark'] as const) {
  test.describe(`usage on a 360px phone, ${colorScheme}`, () => {
    test.use({ viewport: { width: 360, height: 800 }, colorScheme })

    test('is reachable under More and never scrolls sideways', async ({ page }, testInfo) => {
      await openUsage(page, () => usageAnswer([usageGroup('agent-with-a-long-name-for-wrapping'), usageGroup('agent-b')], { truncated: true, matched_groups: 23 }))
      await expect(page.locator('.usage-chart-title')).toContainText('agent-with-a-long-name-for-wrapping')
      await expect(page.getByText('Showing the top 2 of 23 groups.')).toBeVisible()
      expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth + 1)).toBe(true)

      await page.screenshot({ path: testInfo.outputPath(`usage-360-${colorScheme}.png`), fullPage: true })
      const aria = await page.locator('.usage-view').ariaSnapshot()
      await testInfo.attach(`usage-360-${colorScheme}.aria.yml`, { body: aria, contentType: 'text/yaml' })
      expect(aria).toContain('table')
      expect(aria).toContain('button "Refresh"')
      expect(aria).toContain('img "Output tokens for agent-with-a-long-name-for-wrapping')

      await page.goto('/#agents')
      await page.locator('.tabbar').getByRole('button', { name: 'More' }).click()
      await page.locator('.sidenav-item', { hasText: 'Usage' }).click()
      await expect(page).toHaveURL(/#usage$/)
    })
  })
}

test.describe('usage in a browser east of UTC', () => {
  test.use({ timezoneId: 'Asia/Bishkek' })   // UTC+6: a local formatter would show 16:00 for 10:00Z

  test('chart axis labels and hour tooltips stay in UTC', async ({ page }) => {
    await openUsage(page, () => usageAnswer([usageGroup('agent-a', {
      buckets: [{ start: '2026-01-05T09:00:00Z', reported_value: '42', eligible_turns: 1, measured_turns: 1, reset_excluded: 0 }],
    })]))

    const scale = page.locator('.usage-chart-scale span')
    await expect(scale.first()).toContainText('10:00')   // from 2026-01-04T10:00Z
    await expect(scale.last()).toContainText('10:00')    // to 2026-01-05T10:00Z
    await expect(page.locator('.usage-chart-scale')).not.toContainText('16:00')

    const tooltips = await page.locator('.usage-chart-svg title').allTextContents()
    expect(tooltips.length).toBeGreaterThan(0)
    for (const tip of tooltips) {
      expect(tip).toContain('09:00')
      expect(tip).not.toContain('15:00')
    }
  })
})

// ── MCP header mappings (#593) ──────────────────────────────────────────────────────────────────

const MCP_URL = 'https://mcp.example.test/mcp'

function withMappings(fleet: FakeFleet, headerEnvRefs: { headerName: string; envRefName: string }[]) {
  fleet.configs.alpha = {
    ...configOf('alpha'),
    envRefs: ['EXAMPLE_KEY', 'EXAMPLE_SECRET'],
    mcpEndpoints: [{ mcpName: 'example', url: MCP_URL, transportType: 'http', headerEnvRefs }],
  }
}

async function openToolsTab(page: Page, phone = false) {
  const alpha = agentRow(page, 'alpha')
  if (phone) {
    // A phone has no row icons: the opened row lists its actions by name.
    await alpha.locator('.agent-row-summary').click()
    await alpha.locator('.agent-row-detail-actions').getByRole('button', { name: 'Edit config' }).click()
  } else {
    await overflow(alpha).click()
    await alpha.locator('.agent-row-overflow').getByRole('button', { name: 'edit config' }).click()
  }
  const dialog = page.locator('.config-modal-sectioned')
  await dialog.getByRole('tab', { name: /Tools/ }).click()
  return dialog.getByRole('group', { name: 'Headers of example' })
}

test('MCP header mappings: add, edit, save, reopen and remove send and show names only', async ({ page }, testInfo) => {
  const fleet = await open(page, 'agents', f => withMappings(f, []))
  let headers = await openToolsTab(page)
  const dialog = page.locator('.config-modal-sectioned')

  await headers.getByRole('button', { name: 'Add header' }).click()
  await headers.getByLabel('Header name 1 of example').fill('X-Example-Key')
  await headers.getByLabel('Env Ref of header 1 of example').selectOption('EXAMPLE_KEY')
  await headers.getByRole('button', { name: 'Add header' }).click()
  await headers.getByLabel('Header name 2 of example').fill('X-Example-Secret')
  await headers.getByLabel('Env Ref of header 2 of example').selectOption('EXAMPLE_SECRET')
  // The reference is picked from the agent's own Env Refs; there is no field for a value.
  await expect(headers.getByLabel('Env Ref of header 1 of example').locator('option')).toHaveText(['choose an Env Ref…', 'EXAMPLE_KEY', 'EXAMPLE_SECRET'])
  await page.screenshot({ path: testInfo.outputPath('mcp-headers-desktop.png'), fullPage: true })

  await dialog.getByRole('button', { name: 'Save', exact: true }).click()
  await expect(dialog.locator('.config-modal-footer .config-feedback-success')).toHaveText('Saved')
  const sent = fleet.writes('PUT', '/api/agents/alpha/config')[0].body as { mcpEndpoints: unknown[] }
  expect(sent.mcpEndpoints).toEqual([{ mcpName: 'example', url: MCP_URL, transportType: 'http', headerEnvRefs: [
    { headerName: 'X-Example-Key', envRefName: 'EXAMPLE_KEY' },
    { headerName: 'X-Example-Secret', envRefName: 'EXAMPLE_SECRET' },
  ] }])

  await dialog.getByTitle('Close').click()
  headers = await openToolsTab(page)
  await expect(headers.getByLabel('Header name 2 of example')).toHaveValue('X-Example-Secret')
  await expect(headers.getByLabel('Env Ref of header 2 of example')).toHaveValue('EXAMPLE_SECRET')

  await headers.getByRole('button', { name: 'Remove header 1 of example' }).click()
  await headers.getByRole('button', { name: 'Remove header 1 of example' }).click()
  await dialog.getByRole('button', { name: 'Save', exact: true }).click()
  await expect(dialog.locator('.config-modal-footer .config-feedback-success')).toHaveText('Saved')
  // Removing every row sends [], which clears the stored mappings.
  expect((fleet.writes('PUT', '/api/agents/alpha/config')[1].body as { mcpEndpoints: { headerEnvRefs: unknown }[] }).mcpEndpoints[0].headerEnvRefs).toEqual([])
})

test('MCP header mappings: a dangling reference stays visible, and a refused save keeps the draft', async ({ page }) => {
  const fleet = await open(page, 'agents', f => withMappings(f, [{ headerName: 'X-Example-Key', envRefName: 'REMOVED_KEY' }]))
  const headers = await openToolsTab(page)
  const dialog = page.locator('.config-modal-sectioned')

  await expect(headers.getByLabel('Env Ref of header 1 of example')).toHaveValue('REMOVED_KEY')
  await expect(headers.getByLabel('Env Ref of header 1 of example').locator('option:checked')).toHaveText('REMOVED_KEY (not an Env Ref of this agent)')
  await expect(headers.getByText('REMOVED_KEY is not an Env Ref of this agent')).toBeVisible()

  fleet.refuse['PUT /api/agents/alpha/config'] = { status: 400, json: { error: "mcp_header_ref_missing: MCP endpoint 'example' maps X-Other to OTHER_KEY, which is not an Env Ref of this agent." } }
  await headers.getByLabel('Header name 1 of example').fill('X-Other')
  await dialog.getByRole('tab', { name: /General/ }).click()
  await dialog.getByRole('button', { name: 'Save', exact: true }).click()
  // The refusal brings the endpoints back into view, beside the draft it refused.
  const panel = dialog.locator('.config-section-panel[data-section="tools"]')
  await expect(panel.locator('.config-feedback-error', { hasText: 'mcp_header_ref_missing' })).toBeVisible()
  await expect(headers.getByLabel('Header name 1 of example')).toHaveValue('X-Other')
})

for (const colorScheme of ['light', 'dark'] as const) {
  test.describe(`MCP header editor on a 360px phone, ${colorScheme}`, () => {
    test.use({ viewport: { width: 360, height: 800 }, colorScheme })

    test('fits without sideways scroll and names every control', async ({ page }, testInfo) => {
      // The phone layout has no side navigation to wait for: wait for the agent instead.
      const fleet = new FakeFleet()
      withMappings(fleet, [
        { headerName: 'X-Example-Key', envRefName: 'EXAMPLE_KEY' },
        { headerName: 'X-Example-Secret-With-A-Long-Name', envRefName: 'EXAMPLE_SECRET' },
      ])
      await fleet.install(page)
      await page.goto('/#agents')
      await expect(agentRow(page, 'alpha')).toBeVisible()
      const headers = await openToolsTab(page, true)
      await expect(headers.getByLabel('Header name 2 of example')).toBeVisible()
      expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth + 1)).toBe(true)
      for (const control of [headers.getByLabel('Header name 2 of example'), headers.getByLabel('Env Ref of header 2 of example'),
        headers.getByRole('button', { name: 'Remove header 2 of example' })]) {
        const box = (await control.boundingBox())!
        expect(box.x).toBeGreaterThanOrEqual(0)
        expect(box.x + box.width).toBeLessThanOrEqual(360)
      }

      await page.screenshot({ path: testInfo.outputPath(`mcp-headers-360-${colorScheme}.png`), fullPage: true })
      const aria = await headers.ariaSnapshot()
      await testInfo.attach(`mcp-headers-360-${colorScheme}.aria.yml`, { body: aria, contentType: 'text/yaml' })
      expect(aria).toContain('textbox "Header name 1 of example"')
      expect(aria).toContain('combobox "Env Ref of header 2 of example"')
      expect(aria).toContain('button "Add header"')
    })
  })
}

// ── Picking tools from each MCP server's own list ───────────────────────────────────────────────

const ALWAYS_ON = ['Read', 'Write', 'Edit', 'Bash', 'Glob', 'Grep']

/** alpha with two servers: one that lists its tools, and one that cannot be asked for them. */
function withServers(fleet: FakeFleet) {
  fleet.configs.alpha = {
    ...configOf('alpha'),
    envRefs: ['EXAMPLE_KEY'],
    tools: ['Read', 'WebFetch', 'mcp__example__zeta_lookup', 'mcp__guarded__orders_get', 'mcp__removed__thing']
      .map(toolName => ({ toolName, isEnabled: true })),
    mcpEndpoints: [
      { mcpName: 'example', url: MCP_URL, transportType: 'http', headerEnvRefs: [] },
      { mcpName: 'guarded', url: 'https://guarded.example.test/mcp', transportType: 'http', headerEnvRefs: [{ headerName: 'X-Example-Key', envRefName: 'EXAMPLE_KEY' }] },
    ],
  }
  fleet.mcpTools.alpha = {
    agent: 'alpha',
    alwaysOn: ALWAYS_ON,
    servers: [
      { name: 'example', status: 'ok', reason: null, truncated: false, tools: [
        { name: 'alpha_list', description: 'Lists every thing the account holds.' },
        { name: 'zeta_lookup', description: 'Looks one thing up by its id.\nReturns null when there is none.' },
      ] },
      { name: 'fleet-memory', status: 'ok', reason: null, truncated: false, tools: [{ name: 'memory_search', description: 'Searches shared memory.' }] },
      { name: 'guarded', status: 'needs_agent_secret', reason: 'Header X-Example-Key carries EXAMPLE_KEY, a GitHub App secret: only the forwarder can turn it into a token.', tools: [], truncated: false },
    ],
  }
}

async function openTools(page: Page, phone = false) {
  await openToolsTab(page, phone)
  return page.locator('.config-modal-sectioned .config-section-panel[data-section="tools"]')
}

function sentTools(fleet: FakeFleet, index = 0): string[] {
  return (fleet.writes('PUT', '/api/agents/alpha/config')[index].body as { tools: string[] }).tools
}

test('tools: each server lists its tools with descriptions, and a tick is what gets saved', async ({ page }, testInfo) => {
  const fleet = await open(page, 'agents', withServers)
  const panel = await openTools(page)
  const dialog = page.locator('.config-modal-sectioned')

  const asked = () => fleet.writes('POST', '/api/agents/alpha/mcp-tools').length

  const example = panel.getByRole('region', { name: 'MCP server example' })
  const table = example.getByRole('table', { name: 'Tools of example' })
  await expect(table.getByRole('row')).toHaveCount(3)
  await expect(table.getByRole('row', { name: /alpha_list/ })).toContainText('Lists every thing the account holds.')
  // A row shows the description's first line.
  await expect(table.getByRole('row', { name: /zeta_lookup/ })).toContainText('Looks one thing up by its id.')
  await expect(table.getByRole('row', { name: /zeta_lookup/ })).not.toContainText('Returns null')
  await expect(example.getByText('1 of 2 granted')).toBeVisible()
  await expect(panel.getByRole('checkbox', { name: 'mcp__example__zeta_lookup' })).toBeChecked()
  await expect(panel.getByRole('checkbox', { name: 'mcp__example__alpha_list' })).not.toBeChecked()

  const askedBeforeSave = asked()
  expect(askedBeforeSave).toBeGreaterThan(0)

  // The six tools every agent has are shown as that, not as boxes to untick.
  const builtIn = panel.getByRole('region', { name: 'Built-in tools' })
  await expect(builtIn.locator('.tool-chip')).toHaveText(ALWAYS_ON)
  await expect(builtIn.getByRole('checkbox', { name: 'WebFetch' })).toBeChecked()
  await expect(builtIn.getByRole('checkbox', { name: 'Read' })).toHaveCount(0)

  // A server every agent is given is listed although alpha has no endpoint for it.
  await expect(panel.getByRole('region', { name: 'MCP server fleet-memory' }).getByRole('row', { name: /memory_search/ })).toBeVisible()
  await page.screenshot({ path: testInfo.outputPath('tools-desktop.png'), fullPage: true })

  await panel.getByRole('checkbox', { name: 'mcp__example__alpha_list' }).check()
  await panel.getByRole('checkbox', { name: 'mcp__fleet-memory__memory_search' }).check()
  await builtIn.getByRole('checkbox', { name: 'WebFetch' }).uncheck()
  await expect(example.getByText('2 of 2 granted')).toBeVisible()
  await dialog.getByRole('button', { name: 'Save', exact: true }).click()
  await expect(dialog.locator('.config-modal-footer .config-feedback-success')).toHaveText('Saved')

  // The saved list is the old one with exactly those changes: nothing typed, nothing lost.
  expect(sentTools(fleet)).toEqual([
    'Read', 'mcp__example__zeta_lookup', 'mcp__guarded__orders_get', 'mcp__removed__thing',
    'mcp__example__alpha_list', 'mcp__fleet-memory__memory_search',
  ])
  // A save asks the servers again: the saved endpoints may have changed.
  await expect.poll(asked).toBeGreaterThan(askedBeforeSave)
})

test('tools: a server that cannot be listed says why, keeps what is granted, and takes a name', async ({ page }) => {
  const fleet = await open(page, 'agents', withServers)
  const panel = await openTools(page)
  const dialog = page.locator('.config-modal-sectioned')

  const guarded = panel.getByRole('region', { name: 'MCP server guarded' })
  await expect(guarded.getByRole('status')).toContainText('Its tools cannot be listed from here.')
  await expect(guarded.getByRole('status')).toContainText('EXAMPLE_KEY, a GitHub App secret')
  // What the agent is granted there is still shown. It is not called missing: nobody asked the server.
  const granted = guarded.getByRole('row', { name: /orders_get/ })
  await expect(granted.getByRole('checkbox')).toBeChecked()
  await expect(granted).not.toContainText('not listed')

  await guarded.getByLabel('Tool name to grant on guarded').fill('quotes_get')
  await guarded.getByRole('button', { name: 'Add', exact: true }).click()
  await expect(panel.getByRole('checkbox', { name: 'mcp__guarded__quotes_get' })).toBeChecked()

  // A granted tool of a server the agent no longer has is kept apart, not dropped.
  const other = panel.getByRole('region', { name: 'Granted tools of no server' })
  await expect(other.getByRole('checkbox', { name: 'mcp__removed__thing' })).toBeChecked()
  await other.getByRole('checkbox', { name: 'mcp__removed__thing' }).uncheck()

  await dialog.getByRole('button', { name: 'Save', exact: true }).click()
  await expect(dialog.locator('.config-modal-footer .config-feedback-success')).toHaveText('Saved')
  expect(sentTools(fleet)).toEqual(['Read', 'WebFetch', 'mcp__example__zeta_lookup', 'mcp__guarded__orders_get', 'mcp__guarded__quotes_get'])
})

test('tools: the filter narrows every list, All and None act on what is shown, and the text field is the same list', async ({ page }) => {
  await open(page, 'agents', withServers)
  const panel = await openTools(page)

  await panel.getByLabel('Filter tools').fill('lookup')
  const example = panel.getByRole('region', { name: 'MCP server example' })
  await expect(example.getByRole('table').getByRole('row')).toHaveCount(2)
  await expect(panel.getByRole('region', { name: 'MCP server fleet-memory' })).toContainText('No tool here matches the filter.')
  await panel.getByLabel('Filter tools').fill('')

  await example.getByRole('button', { name: 'All', exact: true }).click()
  await expect(example.getByText('2 of 2 granted')).toBeVisible()
  await example.getByRole('button', { name: 'None', exact: true }).click()
  await expect(example.getByText('0 of 2 granted')).toBeVisible()

  // A granted tool a server answered without is marked as that, and stays.
  await panel.getByRole('button', { name: 'Edit as text' }).click()
  await panel.getByLabel('Tools', { exact: true }).fill('Read, WebFetch, mcp__example__gone, mcp__guarded__orders_get, mcp__removed__thing')
  await panel.getByRole('button', { name: 'Pick from lists' }).click()
  await expect(example.getByRole('row', { name: /gone/ })).toContainText('not listed by its server')
  await example.getByRole('checkbox', { name: 'mcp__example__gone' }).uncheck()

  // The same list as text, for pasting: what was unticked is gone from it, the rest is as it was.
  await panel.getByRole('button', { name: 'Edit as text' }).click()
  const field = panel.getByLabel('Tools', { exact: true })
  await expect(field).toHaveValue('Read, WebFetch, mcp__guarded__orders_get, mcp__removed__thing')
  await expect(panel.getByRole('table')).toHaveCount(0)
  await field.fill('Read, mcp__example__alpha_list')
  await panel.getByRole('button', { name: 'Pick from lists' }).click()
  await expect(panel.getByRole('checkbox', { name: 'mcp__example__alpha_list' })).toBeChecked()
  await expect(panel.getByRole('checkbox', { name: 'mcp__example__zeta_lookup' })).not.toBeChecked()
})

test('tools: a server not saved yet is not asked, and an older orchestrator leaves the page usable', async ({ page }) => {
  // No `mcpTools` entry: the route answers 404, as an orchestrator from before it does.
  const fleet = await open(page, 'agents', f => withMappings(f, []))
  const panel = await openTools(page)
  const dialog = page.locator('.config-modal-sectioned')

  await expect(panel.getByText('This orchestrator does not list a server’s tools yet.')).toBeVisible()
  await panel.getByRole('button', { name: '+ add server' }).click()
  await panel.getByLabel('MCP endpoint name').last().fill('fresh')
  const fresh = panel.getByRole('region', { name: 'MCP server fresh' })
  await expect(fresh.getByRole('status')).toContainText('Save to list this server’s tools.')
  // A new server is http unless chosen otherwise, and its transport is a choice, not free text.
  await expect(fresh.getByLabel('Transport of fresh')).toHaveValue('http')
  await expect(fresh.getByLabel('Transport of fresh').locator('option')).toHaveText(['http', 'sse'])

  await fresh.getByLabel('Tool name to grant on fresh').fill('do_thing')
  await fresh.getByLabel('Tool name to grant on fresh').press('Enter')
  await panel.getByLabel('Built-in tool name to grant').fill('WebSearch')
  await panel.getByRole('region', { name: 'Built-in tools' }).getByRole('button', { name: 'Add', exact: true }).click()
  await fresh.getByLabel('URL of fresh').fill('https://fresh.example.test/mcp')
  await dialog.getByRole('button', { name: 'Save', exact: true }).click()
  await expect(dialog.locator('.config-modal-footer .config-feedback-success')).toHaveText('Saved')
  expect(sentTools(fleet)).toEqual(['Read', 'Bash', 'mcp__fresh__do_thing', 'WebSearch'])
})

for (const colorScheme of ['light', 'dark'] as const) {
  test.describe(`tool lists on a 360px phone, ${colorScheme}`, () => {
    test.use({ viewport: { width: 360, height: 800 }, colorScheme })

    test('fit without sideways scroll, with each description under its tool', async ({ page }, testInfo) => {
      const fleet = new FakeFleet()
      withServers(fleet)
      await fleet.install(page)
      await page.goto('/#agents')
      await expect(agentRow(page, 'alpha')).toBeVisible()
      const panel = await openTools(page, true)

      const row = panel.getByRole('row', { name: /alpha_list/ })
      await expect(row).toContainText('Lists every thing the account holds.')
      expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth + 1)).toBe(true)
      for (const control of [panel.getByLabel('Filter tools'), panel.getByRole('checkbox', { name: 'mcp__example__alpha_list' }),
        panel.getByLabel('URL of example'), panel.getByLabel('Transport of guarded'), panel.getByRole('button', { name: 'Remove guarded' })]) {
        const box = (await control.boundingBox())!
        expect(box.x).toBeGreaterThanOrEqual(0)
        expect(box.x + box.width).toBeLessThanOrEqual(360)
      }
      await page.screenshot({ path: testInfo.outputPath(`tools-360-${colorScheme}.png`), fullPage: true })
    })
  })
}

// ── Runs that belong together ───────────────────────────────────────────────────────────────────

test('workflows: a driver, the run it started and that run\u2019s review are shown as a tree', async ({ page }, testInfo) => {
  const fleet = await open(page, 'workflows', f => {
    const base = f.workflows[1]
    f.workflows = [
      // Newest first, as the orchestrator sends them: the review, then its owners.
      { ...base, workflowId: 'review-9', runId: 'run-review-9', workflowType: 'ConsensusReviewWorkflow', phase: null,
        parentWorkflowId: 'wf-build-2', parentRunId: 'run-wf-build-2' },
      { ...base, workflowId: 'driver-7', runId: 'run-driver-7', workflowType: 'DriverWorkflow', phase: 'driving-epic' },
      { ...base, waiterWorkflowId: 'driver-7' },
      { ...base, workflowId: 'solo-3', runId: 'run-solo-3', workflowType: 'SoloWorkflow' },
      // Its owner waits in "Needs your attention", which is another list: said in words.
      { ...base, workflowId: 'helper-4', runId: 'run-helper-4', workflowType: 'HelperWorkflow', parentWorkflowId: 'wf-review-1', parentRunId: 'run-wf-review-1' },
      f.workflows[0],
    ] as typeof f.workflows
  })
  const rows = page.locator('.wf-table').first().locator('.wf-row')
  await expect(rows.locator('.wf-type')).toHaveText(['DriverWorkflow', 'BuildWorkflow', 'ConsensusReviewWorkflow', 'SoloWorkflow', 'HelperWorkflow'])
  await expect(rows.nth(0)).toHaveAttribute('data-depth', '0')
  await expect(rows.nth(1)).toHaveAttribute('data-depth', '1')
  await expect(rows.nth(2)).toHaveAttribute('data-depth', '2')
  await expect(rows.nth(3)).toHaveAttribute('data-depth', '0')
  await expect(rows.nth(1).getByRole('img', { name: 'started by the run above' })).toBeVisible()
  await expect(rows.nth(2).getByRole('img', { name: 'child of the run above' })).toBeVisible()
  // Each step in really is further right.
  const left = async (i: number) => (await rows.nth(i).locator('.wf-type').boundingBox())!.x
  expect(await left(1)).toBeGreaterThan(await left(0))
  expect(await left(2)).toBeGreaterThan(await left(1))
  expect(await left(3)).toBe(await left(0))

  await expect(rows.nth(4)).toHaveAttribute('data-depth', '0')
  await expect(rows.nth(4).locator('.wf-owner')).toHaveText('child of ReviewWorkflow')
  await page.screenshot({ path: testInfo.outputPath('workflow-tree.png'), fullPage: true })

  // A run that ends leaves its children where they are, now saying whose they were.
  fleet.pushWorkflows((fleet.workflows as Array<{ workflowId: string }>).filter(w => w.workflowId !== 'driver-7'))
  await expect(rows.locator('.wf-type')).toHaveText(['BuildWorkflow', 'ConsensusReviewWorkflow', 'SoloWorkflow', 'HelperWorkflow'])
  await expect(rows.nth(0)).toHaveAttribute('data-depth', '0')
  await expect(rows.nth(0).locator('.wf-owner')).toHaveText('started by driver-7')
  await expect(rows.nth(1)).toHaveAttribute('data-depth', '1')
})

test('agents: the page\u2019s own list of running workflows is the same tree', async ({ page }, testInfo) => {
  // The Agents page lists running workflows too, in code of its own. It was left flat once.
  const fleet = await open(page, 'agents', f => {
    const base = f.workflows[1]
    f.workflows = [
      { ...base, workflowId: 'review-9', runId: 'run-review-9', workflowType: 'ConsensusReviewWorkflow', phase: null,
        parentWorkflowId: 'wf-build-2', parentRunId: 'run-wf-build-2' },
      { ...base, workflowId: 'driver-7', runId: 'run-driver-7', workflowType: 'DriverWorkflow', phase: 'driving-epic' },
      { ...base, waiterWorkflowId: 'driver-7' },
      // Waiting for a decision: this page keeps it in the same list, first, with its buttons.
      f.workflows[0],
      { ...base, workflowId: 'helper-4', runId: 'run-helper-4', workflowType: 'HelperWorkflow', parentWorkflowId: 'wf-review-1', parentRunId: 'run-wf-review-1' },
      { ...base, workflowId: 'orphan-5', runId: 'run-orphan-5', workflowType: 'OrphanWorkflow', waiterWorkflowId: 'driver-gone' },
    ] as typeof f.workflows
  })
  const rows = page.locator('.wf-table').first().locator('.wf-row')
  await expect(rows.locator('.wf-type')).toHaveText([
    'ReviewWorkflow', 'HelperWorkflow', 'DriverWorkflow', 'BuildWorkflow', 'ConsensusReviewWorkflow', 'OrphanWorkflow',
  ])
  const depths = await rows.evaluateAll(els => els.map(el => el.getAttribute('data-depth')))
  expect(depths).toEqual(['0', '1', '0', '1', '2', '0'])
  await expect(rows.nth(1).getByRole('img', { name: 'child of the run above' })).toBeVisible()
  await expect(rows.nth(3).getByRole('img', { name: 'started by the run above' })).toBeVisible()
  // The run that waits for a decision keeps its buttons.
  await expect(rows.nth(0).getByRole('button', { name: 'Approve' })).toBeVisible()
  // An owner that is over is named by its id.
  await expect(rows.nth(5).locator('.wf-owner')).toHaveText('started by driver-gone')
  await page.screenshot({ path: testInfo.outputPath('agents-workflow-tree.png'), fullPage: true })

  fleet.pushWorkflows((fleet.workflows as Array<{ workflowId: string }>).filter(w => w.workflowId !== 'driver-7'))
  await expect(rows.locator('.wf-type')).toHaveText(['ReviewWorkflow', 'HelperWorkflow', 'BuildWorkflow', 'ConsensusReviewWorkflow', 'OrphanWorkflow'])
  await expect(rows.nth(2).locator('.wf-owner')).toHaveText('started by driver-7')
})

test('agents: a run waiting for a decision under a run that is not still goes first', async ({ page }) => {
  // It nests under its owner, so the owner's whole tree moves up with it.
  await open(page, 'agents', f => {
    const [waiting, base] = f.workflows
    f.workflows = [
      { ...base, workflowId: 'other-3', runId: 'run-other-3', workflowType: 'OtherWorkflow' },
      { ...base, workflowId: 'driver-7', runId: 'run-driver-7', workflowType: 'DriverWorkflow', phase: 'driving-epic' },
      { ...waiting, waiterWorkflowId: 'driver-7' },
    ] as typeof f.workflows
  })
  const rows = page.locator('.wf-table').first().locator('.wf-row')
  await expect(rows.locator('.wf-type')).toHaveText(['DriverWorkflow', 'ReviewWorkflow', 'OtherWorkflow'])
  const depths = await rows.evaluateAll(els => els.map(el => el.getAttribute('data-depth')))
  expect(depths).toEqual(['0', '1', '0'])
  await expect(rows.nth(1).getByRole('button', { name: 'Approve' })).toBeVisible()
})

test.describe('workflow tree on a 360px phone', () => {
  test.use({ viewport: { width: 360, height: 800 } })

  test('nests without sideways scroll', async ({ page }, testInfo) => {
    const fleet = new FakeFleet()
    const base = fleet.workflows[1]
    fleet.workflows = [
      { ...base, workflowId: 'review-9', runId: 'run-review-9', workflowType: 'ConsensusReviewWorkflow', parentWorkflowId: 'wf-build-2', parentRunId: 'run-wf-build-2' },
      { ...base, workflowId: 'driver-7', runId: 'run-driver-7', workflowType: 'UweCarteBlancheWorkflow', phase: 'driving-epic' },
      { ...base, workflowType: 'UwePrImplementationWorkflow', waiterWorkflowId: 'driver-7' },
    ] as typeof fleet.workflows
    await fleet.install(page)
    await page.goto('/#workflows')
    const rows = page.locator('.wf-table').first().locator('.wf-row')
    await expect(rows.nth(2)).toHaveAttribute('data-depth', '2')
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth + 1)).toBe(true)
    const box = (await rows.nth(2).locator('.wf-type').boundingBox())!
    expect(box.x + box.width).toBeLessThanOrEqual(360)
    await page.screenshot({ path: testInfo.outputPath('workflow-tree-360.png'), fullPage: true })
  })
})

// ── Memory search ───────────────────────────────────────────────────────────────────────────────

type MemoryAnswer = { status?: number; json: unknown; delayMs?: number }

const MEMORY_ID = '5e1f9a3c-3c1f-4b7e-9d52-0a1b2c3d4e5f'
const MEMORY_TWIN = '5e1f9a3c-9e0d-4c3b-8a7f-6e5d4c3b2a19'

const memoryItem = (id: string, title: string, project: string, type: string, tags: string[]) =>
  ({ id, title, project, type, tags, updated_at: '2026-01-05T10:00:00Z' })

const MEMORY_LIST = [
  memoryItem(MEMORY_ID, 'How a deploy reaches the host', 'atlas', 'reference', ['deploy', 'ops']),
  memoryItem(MEMORY_TWIN, 'Rollback checklist', 'atlas', 'decision', ['deploy']),
  memoryItem('1b9d6bcd-bbfd-4b2d-9b5d-ab8dfbbd4bed', 'Заметки о выкладке', 'borealis', 'learning', ['заметки', 'ops']),
  memoryItem('9c858901-8a57-4791-81fe-4c455b099bc9', 'Why reviews need two readers', 'borealis', 'decision', ['review']),
  memoryItem('6ec0bd7f-11c0-43da-975e-2a8ad9ebae0b', 'Staging hosts and what runs on each', 'atlas', 'reference', ['ops']),
]

/** A search hit for a listed memory, as the memory service answers since it says how a hit matched. */
const memoryHit = (id: string, match: string | undefined, snippet: string, highlights?: unknown, score: number | null = null) => {
  const { updated_at, ...item } = MEMORY_LIST.find(m => m.id === id)!
  return match === undefined
    ? { ...item, snippet, updated_at }
    : { ...item, snippet, highlights: highlights ?? [], match, score, updated_at }
}

/** Opens #memory with `/api/memory/search` answered by `respond`, called with each request's query and its number. */
async function openMemory(page: Page, respond: (query: URLSearchParams, n: number) => MemoryAnswer) {
  const fleet = new FakeFleet()
  await fleet.install(page)
  const searches: URLSearchParams[] = []
  await page.route(url => url.pathname.startsWith('/api/memory'), async route => {
    const url = new URL(route.request().url())
    if (url.pathname === '/api/memory') { await route.fulfill({ json: MEMORY_LIST }); return }
    if (url.pathname === '/api/memory/ids') { await route.fulfill({ json: MEMORY_LIST.map(m => m.id) }); return }
    if (url.pathname === '/api/memory/stats/reads') { await route.fulfill({ json: { since: '2026-01-01T00:00:00Z', entries: [] } }); return }
    if (url.pathname === '/api/memory/search') {
      searches.push(url.searchParams)
      const answer = respond(url.searchParams, searches.length)
      if (answer.delayMs) await new Promise(r => setTimeout(r, answer.delayMs))
      await route.fulfill({ status: answer.status ?? 200, json: answer.json }).catch(() => { /* the page moved on */ })
      return
    }
    const item = MEMORY_LIST.find(m => m.id === decodeURIComponent(url.pathname.slice('/api/memory/'.length)))
    if (!item) { await route.fulfill({ status: 404, json: { error: 'Memory not found' } }); return }
    await route.fulfill({ json: { ...item, agent: 'agent1', source: 'conversation', created_at: item.updated_at, content: `Body of ${item.title}` } })
  })
  await page.goto('/#memory')
  await expect(memorySearchBox(page)).toBeVisible()
  await expect(page.locator('.memory-tree-project-btn').first()).toBeVisible()
  return searches
}

const memorySearchBox = (page: Page) => page.getByRole('searchbox', { name: 'Search memories' })
const memoryGroup = (page: Page, name: string) => page.getByRole('region', { name: new RegExp(`^${name}`) })
const memoryResult = (page: Page, title: string) =>
  page.locator('.memory-result-item', { has: page.locator('.memory-result-title', { hasText: new RegExp(`^${title}$`) }) })
/** What a search sent, name by name, in the order it was sent. */
const sent = (query: URLSearchParams) => [...query.entries()].map(([k, v]) => `${k}=${v}`)

const BOTH_GROUPS = [
  memoryHit(MEMORY_ID, 'title', 'A merge builds the images and the pipeline recreates the services one at a time.'),
  memoryHit(MEMORY_TWIN, 'tag', 'Before a rollback, check which image the host is running.'),
  memoryHit('1b9d6bcd-bbfd-4b2d-9b5d-ab8dfbbd4bed', 'keyword', '…перед тем как deploy дойдёт до сервера, проверь выкладку на стенде…', [[15, 6], [49, 8]]),
  memoryHit('6ec0bd7f-11c0-43da-975e-2a8ad9ebae0b', 'meaning', 'Two staging hosts; the second one runs the nightly jobs.', [], 0.71),
  memoryHit('9c858901-8a57-4791-81fe-4c455b099bc9', 'meaning', 'One reader misses what the author also missed. <img src=x onerror="window.__marked=1">', [[400, 9]], 0.44),
]

test('memory search: what matched the words comes first, what is only related comes apart, and each hit says how it matched', async ({ page }, testInfo) => {
  const searches = await openMemory(page, () => ({ json: BOTH_GROUPS }))
  await memorySearchBox(page).fill('deploy')

  const matches = memoryGroup(page, 'Matches')
  const related = memoryGroup(page, 'Related by meaning')
  await expect(matches.locator('.memory-result-title')).toHaveText(['How a deploy reaches the host', 'Rollback checklist', 'Заметки о выкладке'])
  await expect(matches.locator('.memory-result-match')).toHaveText(['Title', 'Tag', 'Keyword'])
  await expect(related.locator('.memory-result-title')).toHaveText(['Staging hosts and what runs on each', 'Why reviews need two readers'])
  await expect(related.locator('.memory-result-match')).toHaveText(['Meaning', 'Meaning'])
  await expect(matches.getByRole('heading')).toHaveText('Matches3')
  await expect(related.getByRole('heading')).toHaveText('Related by meaning2')
  // The matches are above the related ones on the page, not only in the markup.
  const lastMatch = (await matches.locator('.memory-result-item').last().boundingBox())!
  const firstRelated = (await related.locator('.memory-result-item').first().boundingBox())!
  expect(lastMatch.y + lastMatch.height).toBeLessThanOrEqual(firstRelated.y)
  await expect(page.locator('.memory-results-notice')).toHaveCount(0)

  // The matched words are marked where the service said, and the snippet is otherwise untouched.
  const keyword = memoryResult(page, 'Заметки о выкладке')
  await expect(keyword.locator('.memory-result-snippet mark')).toHaveText(['deploy', 'выкладку'])
  await expect(keyword.locator('.memory-result-snippet')).toHaveText('…перед тем как deploy дойдёт до сервера, проверь выкладку на стенде…')
  // A tag hit names the tag; a hit with no highlights has no mark.
  await expect(memoryResult(page, 'Rollback checklist').locator('.memory-result-detail')).toHaveText('#deploy')
  await expect(memoryResult(page, 'How a deploy reaches the host').locator('mark')).toHaveCount(0)
  // A range outside the snippet marks nothing, and a snippet is text, whatever it holds.
  const hostile = memoryResult(page, 'Why reviews need two readers')
  await expect(hostile.locator('mark')).toHaveCount(0)
  await expect(hostile.locator('.memory-result-snippet')).toContainText('<img src=x onerror="window.__marked=1">')
  await expect(hostile.locator('img')).toHaveCount(0)
  expect(await page.evaluate(() => (window as unknown as { __marked?: number }).__marked)).toBeUndefined()

  expect(sent(searches.at(-1)!)).toEqual(['q=deploy'])
  await page.screenshot({ path: testInfo.outputPath('memory-search-groups.png') })
})

test('memory search: when nothing matched the words the page says so, and when nothing is related either it says nothing was found', async ({ page }) => {
  await openMemory(page, query => ({
    json: query.get('q') === 'nightly' ? BOTH_GROUPS.filter(h => h.match === 'meaning') : [],
  }))

  await memorySearchBox(page).fill('nightly')
  await expect(page.locator('.memory-results-notice')).toHaveText(
    'Nothing matched the words “nightly”. The memories below are related by meaning only.')
  await expect(memoryGroup(page, 'Matches')).toHaveCount(0)
  await expect(memoryGroup(page, 'Related by meaning').locator('.memory-result-item')).toHaveCount(2)

  await memorySearchBox(page).fill('zzzz')
  await expect(page.getByText('Nothing found for “zzzz”.')).toBeVisible()
  await expect(page.locator('.memory-result-item')).toHaveCount(0)
  await expect(page.locator('.memory-results-notice')).toHaveCount(0)
  await expect(page.getByRole('heading', { name: /Related by meaning|Matches/ })).toHaveCount(0)
})

test('memory search: project, type and tag are filled from the loaded list, each is sent as its parameter, and clearing sends none', async ({ page }) => {
  const searches = await openMemory(page, () => ({ json: BOTH_GROUPS }))
  const filters = page.getByRole('group', { name: 'Filters' })
  const options = (label: string) => filters.getByLabel(label).locator('option').allTextContents()
  expect(await options('Project')).toEqual(['All projects', 'atlas', 'borealis'])
  expect(await options('Type')).toEqual(['All types', 'decision', 'learning', 'reference'])
  expect(await options('Tag')).toEqual(['All tags', 'deploy', 'ops', 'review', 'заметки'])

  // With nothing typed a filter asks nothing and narrows the tree.
  await filters.getByLabel('Tag').selectOption('review')
  await expect(page.locator('.memory-tree-project-name')).toHaveText(['borealis'])
  await expect(page.locator('.memory-tree-project-btn .memory-tree-count')).toHaveText('1')
  await filters.getByLabel('Project').selectOption('atlas')
  await expect(page.getByText('No memories match these filters.')).toBeVisible()
  await filters.getByRole('button', { name: 'Clear filters' }).click()
  await expect(page.locator('.memory-tree-project-name')).toHaveText(['atlas', 'borealis'])
  await expect(filters.getByRole('button', { name: 'Clear filters' })).toHaveCount(0)
  expect(searches).toHaveLength(0)

  await memorySearchBox(page).fill('deploy')
  await expect.poll(() => searches.length).toBe(1)
  expect(sent(searches[0])).toEqual(['q=deploy'])

  await filters.getByLabel('Project').selectOption('atlas')
  await expect.poll(() => searches.length).toBe(2)
  expect(sent(searches[1])).toEqual(['q=deploy', 'project=atlas'])

  await filters.getByLabel('Type').selectOption('decision')
  await expect.poll(() => searches.length).toBe(3)
  expect(sent(searches[2])).toEqual(['q=deploy', 'project=atlas', 'type=decision'])

  await filters.getByLabel('Project').selectOption('')
  await filters.getByLabel('Type').selectOption('')
  await filters.getByLabel('Tag').selectOption('заметки')
  await expect.poll(() => sent(searches.at(-1)!)).toEqual(['q=deploy', 'tag=заметки'])

  await filters.getByRole('button', { name: 'Clear filters' }).click()
  await expect.poll(() => sent(searches.at(-1)!)).toEqual(['q=deploy'])
  await expect(filters.getByLabel('Tag')).toHaveValue('')
})

test('memory search: a filtered search that finds nothing says the filters were part of it', async ({ page }) => {
  await openMemory(page, () => ({ json: [] }))
  await page.getByRole('group', { name: 'Filters' }).getByLabel('Type').selectOption('learning')
  await memorySearchBox(page).fill('deploy')
  await expect(page.getByText('Nothing found for “deploy” with these filters.')).toBeVisible()
})

test('memory search: a memory id comes first, and Enter opens the one memory it names', async ({ page }) => {
  const searches = await openMemory(page, query => ({
    json: query.get('q') === '5e1f9a3c'
      // Two memories start with these eight characters.
      ? [memoryHit(MEMORY_ID, 'id', 'A merge builds the images.'), memoryHit(MEMORY_TWIN, 'id', 'Before a rollback.')]
      : [memoryHit(MEMORY_ID, 'id', 'A merge builds the images.'),
        memoryHit('6ec0bd7f-11c0-43da-975e-2a8ad9ebae0b', 'meaning', 'Two staging hosts.', [], 0.3)],
  }))

  await memorySearchBox(page).fill('5e1f9a3c-3c1f')
  const first = page.locator('.memory-result-item').first()
  await expect(first.locator('.memory-result-title')).toHaveText('How a deploy reaches the host')
  await expect(first.locator('.memory-result-match')).toHaveText('ID')
  await expect(first.locator('.memory-result-detail')).toHaveText('5e1f9a3c')
  await expect(page.locator('.memory-cv-title')).toHaveCount(0)

  await memorySearchBox(page).press('Enter')
  await expect(page.locator('.memory-cv-title')).toHaveText('How a deploy reaches the host')
  await expect(first).toHaveClass(/memory-selected/)
  // The results stay; Enter did not ask again.
  expect(searches).toHaveLength(1)

  // An id that names two memories opens neither: both are listed to choose from.
  await memorySearchBox(page).fill('5e1f9a3c')
  await expect(page.locator('.memory-result-match')).toHaveText(['ID', 'ID'])
  await memorySearchBox(page).press('Enter')
  await page.waitForTimeout(300)
  await expect(page.locator('.memory-cv-title')).toHaveText('How a deploy reaches the host')
  await expect(page.locator('.memory-result-item.memory-selected')).toHaveCount(1)
})

test('memory search: Enter pressed before the answer is in opens the id hit when it arrives, and only for what is typed now', async ({ page }) => {
  const searches = await openMemory(page, query => query.get('q') === '5e1f9a3c-3c1f'
    ? { json: [memoryHit(MEMORY_ID, 'id', 'A merge builds the images.')], delayMs: 100 }
    : { json: [memoryHit(MEMORY_TWIN, 'keyword', 'Before a rollback.')] })

  // Pasted and entered at once: no wait for the pause after typing.
  await memorySearchBox(page).fill('5e1f9a3c-3c1f')
  await memorySearchBox(page).press('Enter')
  await expect(page.locator('.memory-cv-title')).toHaveText('How a deploy reaches the host')
  expect(searches).toHaveLength(1)

  // Entered, then changed before the answer: the id hit of the abandoned query opens nothing.
  await page.reload()
  await expect(page.locator('.memory-tree-project-btn').first()).toBeVisible()
  await memorySearchBox(page).fill('5e1f9a3c-3c1f')
  await memorySearchBox(page).press('Enter')
  await memorySearchBox(page).fill('rollback')
  await expect(page.locator('.memory-result-title')).toHaveText(['Rollback checklist'])
  await page.waitForTimeout(800)
  await expect(page.locator('.memory-cv-title')).toHaveCount(0)
  await expect(page.locator('.memory-result-title')).toHaveText(['Rollback checklist'])
})

test('memory search: an older answer never replaces a newer one', async ({ page }) => {
  await openMemory(page, query => query.get('q') === 'slow'
    ? { json: [memoryHit(MEMORY_ID, 'keyword', 'The slow answer.')], delayMs: 1100 }
    : { json: [memoryHit(MEMORY_TWIN, 'keyword', 'The fast answer.')] })

  await memorySearchBox(page).fill('slow')
  await page.waitForTimeout(450)   // past the pause after typing: the slow request is out
  await memorySearchBox(page).fill('fast')
  await expect(page.locator('.memory-result-title')).toHaveText(['Rollback checklist'])
  await page.waitForTimeout(1300)
  await expect(page.locator('.memory-result-title')).toHaveText(['Rollback checklist'])
  await expect(page.getByText('Searching…')).toHaveCount(0)
})

test('memory search: an older memory service, which does not say how a hit matched, gets one plain list', async ({ page }) => {
  await openMemory(page, () => ({
    json: [
      memoryHit('6ec0bd7f-11c0-43da-975e-2a8ad9ebae0b', undefined, 'Two staging hosts; the second one runs the nightly jobs.'),
      memoryHit(MEMORY_ID, undefined, 'A merge builds the images and the pipeline recreates the services.'),
    ],
  }))
  await memorySearchBox(page).fill('deploy')

  await expect(page.locator('.memory-result-title')).toHaveText(['Staging hosts and what runs on each', 'How a deploy reaches the host'])
  await expect(page.locator('.memory-result-snippet').first()).toHaveText('Two staging hosts; the second one runs the nightly jobs.')
  await expect(page.locator('.memory-result-meta').first()).toHaveText('atlas · reference')
  // Nothing is claimed that the answer did not say.
  await expect(page.locator('.memory-result-match')).toHaveCount(0)
  await expect(page.locator('.memory-result-item mark')).toHaveCount(0)
  await expect(page.locator('.memory-results-heading')).toHaveCount(0)
  await expect(page.locator('.memory-results-notice')).toHaveCount(0)
  // Enter has no id hit to open.
  await memorySearchBox(page).press('Enter')
  await page.waitForTimeout(300)
  await expect(page.locator('.memory-cv-title')).toHaveCount(0)
  await page.locator('.memory-result-item').first().click()
  await expect(page.locator('.memory-cv-title')).toHaveText('Staging hosts and what runs on each')
})

test('memory search: a search the memory service refuses shows its words, and Retry asks again', async ({ page }) => {
  const searches = await openMemory(page, (_, n) => n === 1
    ? { status: 400, json: { error: 'Invalid \'limit\': expected a number from 1 to 50' } }
    : { json: BOTH_GROUPS })
  await memorySearchBox(page).fill('deploy')
  await expect(page.locator('.memory-tree-error')).toContainText('Search failed: Invalid \'limit\': expected a number from 1 to 50')
  await expect(page.locator('.memory-result-item')).toHaveCount(0)
  await page.locator('.memory-tree-error').getByRole('button', { name: 'Retry' }).click()
  await expect(memoryGroup(page, 'Matches').locator('.memory-result-item')).toHaveCount(3)
  expect(sent(searches[1])).toEqual(['q=deploy'])
})

test.describe('memory search on a 360px phone', () => {
  test.use({ viewport: { width: 360, height: 800 } })

  test('filters, labels and marked snippets fit the screen, and a hit opens over the list', async ({ page }, testInfo) => {
    await openMemory(page, () => ({ json: BOTH_GROUPS }))
    const filters = page.getByRole('group', { name: 'Filters' })
    await filters.getByLabel('Project').selectOption('borealis')
    await filters.getByLabel('Tag').selectOption('заметки')
    await memorySearchBox(page).fill('deploy')
    await expect(memoryGroup(page, 'Related by meaning').locator('.memory-result-item')).toHaveCount(2)

    expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth + 1)).toBe(true)
    for (const part of [filters, page.locator('.memory-result-match').first(), page.locator('.memory-result-snippet mark').last()]) {
      const box = (await part.boundingBox())!
      expect(box.x).toBeGreaterThanOrEqual(0)
      expect(box.x + box.width).toBeLessThanOrEqual(360)
    }
    await page.screenshot({ path: testInfo.outputPath('memory-search-360.png'), fullPage: true })

    await memoryResult(page, 'Rollback checklist').click()
    await expect(page.locator('.memory-cv-title')).toHaveText('Rollback checklist')
    await expect(memorySearchBox(page)).toBeHidden()
    await page.getByRole('button', { name: 'All memories' }).click()
    await expect(memorySearchBox(page)).toHaveValue('deploy')
    await expect(filters.getByLabel('Tag')).toHaveValue('заметки')
  })
})
