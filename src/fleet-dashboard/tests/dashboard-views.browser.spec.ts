import { test, expect, type Locator, type Page } from '@playwright/test'
import { FakeFleet, EDITED_DEFINITION } from './support/fakeFleet'
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
  await expect(card.locator('.wf-action-success')).toHaveText('Signal sent')
  const sent = fleet.writes('POST', '/api/workflows/fleet/signal/wf-review-1')
  expect(sent).toHaveLength(1)
  expect(sent[0].body).toMatchObject({ signalName: 'design-approval', payload: '{"Value":"approved"}', runId: 'run-wf-review-1' })
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
  await expect(review.locator('.wf-action-success')).toHaveText('Signal sent')
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

  test(`${kind.hash}: choosing a version shows its diff, and rollback needs a second click`, async ({ page }) => {
    const fleet = await open(page, kind.hash)
    const row = rowByName(page, kind.name)
    await row.locator('.instr-header').click()
    const v1 = row.locator('.instr-version-item', { has: page.locator('.instr-version-num', { hasText: /^v1$/ }) })
    await v1.click()
    await expect(v1).toHaveClass(/selected/)
    await expect(row.locator('.instr-diff')).toContainText('diff: v1 → v2')
    await v1.click()
    await expect(row.locator('.instr-diff')).toHaveCount(0)

    await v1.getByRole('button', { name: 'rollback' }).click()
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
