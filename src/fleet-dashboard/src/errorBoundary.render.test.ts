// Run with `npm test` (node --test, Node ≥ 22.18 strips the types). Type-checked by `tsc -b`.
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { createElement } from 'react'
import { renderToStaticMarkup } from 'react-dom/server'
import { createServer } from 'vite'
import { fileURLToPath } from 'node:url'

// The server renderer has no error boundaries (a throw there rejects the whole render), so the
// catch itself is not exercised here. What is: the state a caught error produces, the screen that
// state draws, and that a healthy page passes through untouched.
test('a render error becomes a screen that names it and offers a way back', async () => {
  const server = await createServer({ root: fileURLToPath(new URL('..', import.meta.url)), configFile: false,
    server: { middlewareMode: true, hmr: false, watch: null }, appType: 'custom' })
  try {
    const { default: ErrorBoundary, CrashScreen, asError } =
      await server.ssrLoadModule('/src/components/ErrorBoundary.tsx')

    // What React hands the boundary is whatever was thrown, not always an Error.
    const fromError = ErrorBoundary.getDerivedStateFromError(new TypeError('workflows.map is not a function'))
    assert.ok(fromError.error instanceof TypeError)
    assert.equal(fromError.error.message, 'workflows.map is not a function')
    assert.equal(ErrorBoundary.getDerivedStateFromError('plain string').error.message, 'plain string')
    assert.equal(asError(undefined).message, 'undefined')
    // The retry counter is not part of what a caught error sets: a second crash must not reset it.
    assert.equal('attempt' in fromError, false)

    const html = renderToStaticMarkup(createElement(CrashScreen, {
      error: fromError.error, onRetry: () => {}, onReload: () => {},
    }))
    assert.match(html, /role="alert"/)
    assert.match(html, /The page hit an error and stopped drawing\./)
    assert.match(html, /<code>workflows\.map is not a function<\/code>/)
    assert.match(html, /<button[^>]*>Try again<\/button>/)
    assert.match(html, /<button[^>]*>Reload<\/button>/)

    // An error with no message still shows something to report.
    const nameless = renderToStaticMarkup(createElement(CrashScreen, {
      error: new RangeError(''), onRetry: () => {}, onReload: () => {},
    }))
    assert.match(nameless, /<code>RangeError<\/code>/)

    // Markup in a message is text, never HTML.
    const hostile = renderToStaticMarkup(createElement(CrashScreen, {
      error: new Error('<img src=x onerror=alert(1)>'), onRetry: () => {}, onReload: () => {},
    }))
    assert.equal(hostile.includes('<img'), false)

    const healthy = renderToStaticMarkup(createElement(ErrorBoundary, null, createElement('p', null, 'the page')))
    assert.equal(healthy, '<p>the page</p>')
  } finally {
    await server.close()
  }
})
