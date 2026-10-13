import { test } from 'node:test'
import assert from 'node:assert/strict'
import { createElement } from 'react'
import { renderToStaticMarkup } from 'react-dom/server'
import { createServer } from 'vite'
import { fileURLToPath } from 'node:url'
import { autonomyCharterDraft, adoptPreflightRun } from './epicGrants.ts'

// Uses the installed JSX compiler and actual components. No HTTP listener or API requests.
test('charter view renders verdicts, exact adoption checkbox and excluded actions; legacy Create stays disabled initially', async () => {
  const server = await createServer({ root: fileURLToPath(new URL('..', import.meta.url)), configFile: false,
    server: { middlewareMode: true, hmr: false, watch: null }, appType: 'custom' })
  try {
    const { default: View, ScopeReport, AutonomyNotice } = await server.ssrLoadModule('/src/components/EpicGrantsView.tsx')
    const legacy = renderToStaticMarkup(createElement(View))
    assert.match(legacy, /disabled=""[^>]*>Create<\/button>/)
    const item = { source: 'linked_run', workflowId: 'child-example', runId: 'run-example', repo: 'example-org/example-repo', issue: 12, verdict: 'needs_adoption' }
    const scope = adoptPreflightRun(autonomyCharterDraft({ driver: { namespace: 'default' } }), item, true)
    const report = { valid: false, errors: ['preflight has unresolved items'], targets: [], workflows: [], driver: null,
      preflight: { items: [item], unresolved: 1, sha256: 'a'.repeat(64), visibility: {} } }
    const markup = renderToStaticMarkup(createElement(ScopeReport, { report, stale: false, text: JSON.stringify(scope), onAdopt: () => {} }))
    assert.match(markup, /Charter preflight: 1 unresolved/)
    assert.match(markup, /needs_adoption/)
    assert.match(markup, /type="checkbox" checked=""/)
    assert.match(markup, /child-example \/ run-example/)
    assert.match(markup, /preflight has unresolved items/)
    const notice = renderToStaticMarkup(createElement(AutonomyNotice))
    for (const action of ['deployments', 'credentials', 'money', 'production tests', 'instructions', 'live configuration']) assert.ok(notice.includes(action))
  } finally { await server.close() }
})
