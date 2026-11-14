import { test, expect, type Locator, type Page } from '@playwright/test'
import { FakeFleet, EDITED_DEFINITION } from './support/fakeFleet'

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
  await alpha.locator('.agent-row-overflow').getByRole('button', { name: 'stop', exact: true }).click()
  expect(fleet.writes('POST', '/api/agents/alpha/stop')).toHaveLength(0)
  await overflow(alpha).click()
  await alpha.locator('.agent-row-overflow').getByRole('button', { name: 'confirm stop?' }).click()
  await expect(alpha.locator('.row-feedback-success')).toHaveText('Stopped')
  expect(fleet.writes('POST', '/api/agents/alpha/stop')).toHaveLength(1)

  const gamma = agentRow(page, 'gamma')
  await overflow(gamma).click()
  await gamma.locator('.agent-row-overflow').getByRole('button', { name: 'start', exact: true }).click()
  await expect(gamma.locator('.row-feedback-success')).toHaveText('Started')
  expect(fleet.writes('POST', '/api/agents/gamma/start')).toHaveLength(1)
})

test('agents: delete asks first and the row goes once the server has dropped it', async ({ page }) => {
  const fleet = await open(page, 'agents')
  const alpha = agentRow(page, 'alpha')
  await overflow(alpha).click()
  await alpha.locator('.agent-row-overflow').getByRole('button', { name: 'delete', exact: true }).click()
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
  await page.getByRole('button', { name: '+ New Agent' }).click()
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
  await page.getByRole('button', { name: '+ New Agent' }).click()
  const dialog = page.locator('.create-agent-modal')
  await dialog.getByRole('button', { name: 'Create', exact: true }).click()
  await expect(dialog.locator('.config-feedback-error')).toBeVisible()
  expect(fleet.calls.filter(c => c.method === 'POST' && c.path === '/api/agents')).toHaveLength(0)
})

test('agents: reprovision all asks in the sidenav, then says so and raises an alert', async ({ page }) => {
  const fleet = await open(page, 'agents')
  await page.getByRole('button', { name: '⟳ Reprovision All' }).click()
  await page.locator('.sidenav-confirm-no').click()
  expect(fleet.writes('POST', '/api/agents/reprovision-running')).toHaveLength(0)
  await page.getByRole('button', { name: '⟳ Reprovision All' }).click()
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
  await page.getByRole('button', { name: '+ New Agent' }).click()
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
  { hash: 'instructions', api: '/api/instructions', name: 'base', current: 'base v2 content', added: '+ New Instruction',
    newName: 'e.g. my-instruction', newContent: 'Instruction content…', other: 'reviewer' },
  { hash: 'project-contexts', api: '/api/project-contexts', name: 'fleet', current: 'fleet context v2', added: '+ New Project Context',
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
  await expect(navBadge(page, 'WF Definitions')).toHaveText('1')
  await page.getByPlaceholder('Search definitions…').fill('nothing-like-it')
  await expect(rowByName(page, 'ReviewWorkflow')).toHaveCount(0)
  await page.getByPlaceholder('Search definitions…').fill('review')
  await expect(rowByName(page, 'ReviewWorkflow')).toBeVisible()
})
