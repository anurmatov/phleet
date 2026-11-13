import { test } from 'node:test'
import assert from 'node:assert/strict'
import {
  describeBrowser, deviceTitle, isWrite, readDevices, readSession, signInProblem,
} from './session.ts'

test('a signed-in answer is read with its fields', () => {
  assert.deepEqual(
    readSession(200, { authRequired: true, signedIn: true, kind: 'browser', userId: 'owner', displayName: 'Owner', deviceId: 'd1' }),
    { status: 'signed-in', kind: 'browser', userId: 'owner', displayName: 'Owner', deviceId: 'd1', authRequired: true })
})

test('a 401 is signed out, whatever the body says', () => {
  assert.deepEqual(readSession(401, { error: 'Unauthorized', authRequired: true }), { status: 'signed-out' })
  assert.deepEqual(readSession(401, null), { status: 'signed-out' })
  assert.deepEqual(readSession(401, { signedIn: true }), { status: 'signed-out' })
})

test('an orchestrator that gates nothing is open, not signed out', () => {
  assert.deepEqual(readSession(200, { authRequired: false, signedIn: false }), { status: 'open' })
})

test('an orchestrator from before sign-in is open: it has no such route', () => {
  assert.deepEqual(readSession(404, 'Not Found'), { status: 'open' })
})

test('an answer that cannot be read is never taken for signed in or signed out', () => {
  assert.deepEqual(readSession(502, '<html>Bad gateway</html>'), { status: 'unreachable', httpStatus: 502 })
  assert.deepEqual(readSession(503, { error: 'sign_in_unavailable' }), { status: 'unreachable', httpStatus: 503 })
  assert.deepEqual(readSession(302, null), { status: 'unreachable', httpStatus: 302 })
})

test('a 200 that does not say signed in is signed out unless it says nothing is gated', () => {
  assert.deepEqual(readSession(200, {}), { status: 'signed-out' })
  assert.deepEqual(readSession(200, 'not an object'), { status: 'signed-out' })
  assert.deepEqual(readSession(200, { signedIn: 'yes', authRequired: true }), { status: 'signed-out' })
})

test('missing fields of a signed-in answer read as empty, not as undefined', () => {
  const state = readSession(200, { signedIn: true })
  assert.deepEqual(state, { status: 'signed-in', kind: '', userId: '', displayName: '', deviceId: '', authRequired: true })
})

test('every sign-in refusal has its own words', () => {
  const statuses = [401, 409, 429, 503, 500]
  const messages = statuses.map(signInProblem)
  assert.equal(new Set(messages).size, statuses.length)
  assert.ok(messages.every(m => m.length > 0))
})

test('a browser is named from its user agent', () => {
  assert.equal(describeBrowser(
    'Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36'),
    'Chrome on macOS')
  assert.equal(describeBrowser(
    'Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Safari/605.1.15'),
    'Safari on macOS')
  assert.equal(describeBrowser(
    'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36 Edg/130.0.0.0'),
    'Edge on Windows')
  assert.equal(describeBrowser('Mozilla/5.0 (X11; Linux x86_64; rv:131.0) Gecko/20100101 Firefox/131.0'), 'Firefox on Linux')
  assert.equal(describeBrowser(
    'Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Mobile/15E148 Safari/604.1'),
    'Safari on iOS')
  assert.equal(describeBrowser(''), 'Browser')
  assert.equal(describeBrowser('curl/8.0'), 'Browser')
})

test('only a request that changes something is a write', () => {
  for (const method of [undefined, 'GET', 'get', 'HEAD', 'OPTIONS']) assert.equal(isWrite(method), false)
  for (const method of ['POST', 'post', 'PUT', 'PATCH', 'DELETE']) assert.equal(isWrite(method), true)
})

test('devices are listed with this browser first, then active ones newest first, then revoked', () => {
  const rows = readDevices({
    devices: [
      { deviceId: 'old-phone', kind: 'app', name: 'Old phone', registeredAt: '2026-01-01T00:00:00+00:00', revokedAt: '2026-02-01T00:00:00+00:00' },
      { deviceId: 'tablet', kind: 'app', name: 'Tablet', registeredAt: '2026-03-01T00:00:00+00:00', lastSeenAt: '2026-03-02T00:00:00+00:00' },
      { deviceId: 'this', kind: 'browser', name: 'Chrome on macOS', registeredAt: '2026-02-01T00:00:00+00:00', current: true },
      { deviceId: 'phone', kind: 'app', name: '', registeredAt: '2026-04-01T00:00:00+00:00' },
    ],
  })
  assert.deepEqual(rows.map(r => r.deviceId), ['this', 'phone', 'tablet', 'old-phone'])
  assert.equal(rows[2].lastSeenAt, '2026-03-02T00:00:00+00:00')
  assert.equal(rows[1].lastSeenAt, null)
  assert.equal(rows[3].revokedAt, '2026-02-01T00:00:00+00:00')
})

test('an entry that cannot be addressed is dropped, and a body that is not a list reads as empty', () => {
  assert.deepEqual(readDevices({ devices: [null, 'x', { name: 'no id' }, { deviceId: 'd1' }] }).map(r => r.deviceId), ['d1'])
  assert.deepEqual(readDevices({ devices: 'nope' }), [])
  assert.deepEqual(readDevices(null), [])
})

test('an unknown kind is shown as an app, and a nameless device still has a title', () => {
  const [row] = readDevices({ devices: [{ deviceId: 'd1', kind: 'toaster' }] })
  assert.equal(row.kind, 'app')
  assert.equal(deviceTitle(row), 'App')
  assert.equal(deviceTitle({ name: '', kind: 'browser' }), 'Browser')
  assert.equal(deviceTitle({ name: 'Pocket phone', kind: 'app' }), 'Pocket phone')
})
