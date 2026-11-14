import { test, expect, type Page } from '@playwright/test'
import { mkdir } from 'node:fs/promises'
import path from 'node:path'
import { answer, pages, scenes } from './tourFixtures'

// A visual tour: every page, at a phone's width and a desktop's, drawn from synthetic data and
// saved as a picture. It asserts two things a picture cannot be trusted to show at a glance —
// that the page drew something, and that nothing is wider than the screen — and is otherwise
// there to be looked at. Only HTTP and the socket are substituted; the app is the real one.

const out = process.env.TOUR_DIR ?? path.join('test-results', 'tour')
const theme = process.env.TOUR_THEME === 'dark' ? '-dark' : ''

async function open(page: Page, hash: string, override?: Record<string, unknown>) {
  await page.route('**/*', route => {
    const host = new URL(route.request().url()).hostname
    return host === '127.0.0.1' || host === 'localhost' ? route.continue() : route.abort()
  })
  await page.routeWebSocket('**/ws**', () => { /* held open, silent */ })
  await page.routeWebSocket('**/v1/**', () => { /* the chat stream: held open, silent */ })
  await page.route('**/v1/**', route => route.fulfill({ status: 503, json: {} }))
  await page.route('**/api/**', async route => {
    const url = new URL(route.request().url())
    if (route.request().method() !== 'GET') { await route.fulfill({ json: {} }); return }
    const body = answer(url.pathname, override)
    if (body === undefined) await route.fulfill({ status: 404, json: {} })
    else await route.fulfill({ json: body })
  })
  await page.goto('/#' + hash)
  await expect(page.locator('.app-layout, .signin-shell')).toBeVisible()
}

async function settle(page: Page) {
  // Lists arrive after the first paint; give them a moment rather than naming a selector per page.
  await page.waitForLoadState('networkidle')
  await page.waitForTimeout(300)
}

/** The whole page in one picture, with the bars that are fixed to the screen where they belong:
 *  the screen is made as tall as the page rather than the page stitched from scrolls. */
async function picture(page: Page, name: string, whole = true) {
  await mkdir(out, { recursive: true })
  const viewport = page.viewportSize()!
  if (whole) {
    const height = await page.evaluate(() => document.documentElement.scrollHeight)
    await page.setViewportSize({ width: viewport.width, height: Math.max(viewport.height, Math.min(height, 6000)) })
  }
  await page.screenshot({ path: path.join(out, `${name}.png`) })
  await page.setViewportSize(viewport)
}

async function notWider(page: Page) {
  expect(
    await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth + 1),
    'the page is wider than the screen',
  ).toBe(true)
}

for (const name of pages) {
  test(`tour: ${name}`, async ({ page }, info) => {
    await open(page, name)
    await settle(page)
    await picture(page, `${info.project.name}${theme}-${name}`)
    expect(await page.locator('main').innerText()).not.toBe('')
    await notWider(page)
  })
}

// How each scene is reached. A phone has no row of icons beside an agent: its actions are in the
// opened row, by name.
const reach: Record<(typeof scenes)[number]['name'], (page: Page, phone: boolean) => Promise<void>> = {
  'agent-open': async page => { await page.locator('.agent-row-summary').first().click() },
  'agent-config': async (page, phone) => {
    const row = page.locator('.agent-row').first()
    if (phone) {
      await row.locator('.agent-row-summary').click()
      await row.getByRole('button', { name: 'Edit config' }).click()
    } else {
      await row.getByTitle('More actions').click()
      await row.getByRole('button', { name: 'Edit config' }).click()
    }
    await expect(page.getByRole('dialog')).toBeVisible()
  },
  'new-agent': async page => {
    await page.getByRole('button', { name: 'New agent' }).click()
    await expect(page.getByRole('dialog')).toBeVisible()
  },
  logs: async (page, phone) => {
    const row = page.locator('.agent-row').first()
    if (phone) await row.locator('.agent-row-summary').click()
    await (phone ? row.locator('.agent-row-detail-actions') : row.locator('.agent-row-actions')).getByTitle('Logs').click()
  },
  'instruction-open': async page => {
    await page.locator('.instr-header').first().click()
    await expect(page.locator('textarea.instr-editor')).toBeVisible()
    await page.locator('.instr-version-item').nth(1).click()
  },
  'new-instruction': async page => { await page.getByRole('button', { name: 'New instruction' }).click() },
  'start-workflow': async page => {
    await page.getByRole('button', { name: 'Start' }).click()
    await expect(page.getByRole('dialog')).toBeVisible()
  },
  'workflow-history': async page => {
    await page.locator('.wf-type-clickable').first().click()
    await expect(page.getByRole('dialog')).toBeVisible()
  },
  'signal-comment': async page => { await page.getByRole('button', { name: 'Request changes' }).first().click() },
  'confirm-bar': async page => { await page.getByRole('button', { name: 'Approve merge' }).click() },
  'new-schedule': async page => { await page.getByRole('button', { name: /new schedule/i }).click() },
  'more-sheet': async (page, phone) => { if (phone) await page.locator('.tabbar-item').last().click() },
  'visual-editor': async page => {
    await page.getByRole('button', { name: 'Visual editor' }).click()
    await expect(page.locator('.wfe-overlay')).toBeVisible()
  },
  'memory-open': async page => {
    // The tree opens as far as it was left; press only what is still closed.
    const memory = page.locator('.memory-tree-mem').first()
    if (await memory.count() === 0) await page.locator('.memory-tree-project-btn').first().click()
    if (await memory.count() === 0) await page.locator('.memory-tree-type-btn').first().click()
    await memory.click()
  },
  'chat-open': async (page, phone) => {
    if (phone) await page.locator('.chat-agent-select').selectOption({ index: 1 })
    else await page.locator('.chat-agent').first().click()
  },
  'sign-in': async () => {},
  'first-run': async () => {},
}

for (const scene of scenes) {
  test(`scene: ${scene.name}`, async ({ page }, info) => {
    const phone = info.project.name === 'phone'
    await open(page, scene.hash, 'override' in scene ? scene.override : undefined)
    await settle(page)
    await reach[scene.name](page, phone)
    await page.waitForTimeout(350)
    // A dialog is seen as the screen shows it; an opened row as the page now reads.
    const overlay = await page.locator('[role="dialog"], .log-fullscreen-overlay, .sidenav-open, .signal-modal, .wfe-overlay').count()
    await picture(page, `${info.project.name}${theme}-scene-${scene.name}`, overlay === 0)
    await notWider(page)
  })
}
