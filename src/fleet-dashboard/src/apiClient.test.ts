// Run with `npm test` (node --test, Node ≥ 22.18 strips the types). Type-checked by `tsc -b`.
import { test } from 'node:test'
import assert from 'node:assert/strict'
import {
  ApiError, ensureOk, errorMessage, firstFailure, requestJson, requestWrite, serverErrorText,
  type Fetcher, type ResponseLike,
} from './apiClient.ts'

function answer(status: number, body: string): ResponseLike {
  return { ok: status >= 200 && status < 300, status, text: async () => body }
}

/** A fetcher that answers once and records what it was asked. */
function fetcherFor(response: ResponseLike) {
  const calls: { url: string; init?: RequestInit }[] = []
  const fetcher: Fetcher = async (url, init) => { calls.push({ url, init }); return response }
  return { fetcher, calls }
}

async function rejection(promise: Promise<unknown>): Promise<unknown> {
  try { await promise } catch (error) { return error }
  assert.fail('expected the request to throw')
}

test('a read resolves with the parsed body of a 2xx answer', async () => {
  const { fetcher, calls } = fetcherFor(answer(200, '[{"agentName":"a1"}]'))
  assert.deepEqual(await requestJson(fetcher, '/api/agents'), [{ agentName: 'a1' }])
  assert.deepEqual(calls, [{ url: '/api/agents', init: undefined }])
})

test('a refused read throws with the status and the server\'s error, and never yields the error body', async () => {
  // The orchestrator's 401 is `{ error }`. Stored as a list, that is what blanked the page.
  const { fetcher } = fetcherFor(answer(401, '{"error":"Sign in to use this fleet."}'))
  const error = await rejection(requestJson(fetcher, '/api/workflows'))
  assert.ok(error instanceof ApiError)
  assert.equal(error.status, 401)
  assert.equal(error.message, 'Sign in to use this fleet.')
  assert.deepEqual(error.body, { error: 'Sign in to use this fleet.' })
})

test('a refusal with no readable body is named by its status', async () => {
  for (const body of ['', '<html>Bad Gateway</html>', '{"detail":"no error or title here"}']) {
    const { fetcher } = fetcherFor(answer(502, body))
    const error = await rejection(requestJson(fetcher, '/api/agents'))
    assert.ok(error instanceof ApiError)
    assert.equal(error.message, 'Error 502')
  }
})

test('a 2xx read that is not JSON throws: a proxy page is not a list', async () => {
  for (const body of ['', '<!doctype html><html></html>']) {
    const { fetcher } = fetcherFor(answer(200, body))
    const error = await rejection(requestJson(fetcher, '/api/agents'))
    assert.ok(error instanceof ApiError)
    assert.equal(error.status, 200)
    assert.equal(error.body, null)
  }
})

test('a body that cannot be read at all is treated as no body', async () => {
  const broken: ResponseLike = { ok: false, status: 500, text: async () => { throw new Error('stream') } }
  const error = await rejection(requestJson(async () => broken, '/api/agents'))
  assert.ok(error instanceof ApiError)
  assert.equal(error.message, 'Error 500')
})

test('a write sends JSON with its content type, and nothing when there is no body', async () => {
  const withBody = fetcherFor(answer(200, '{"message":"ok"}'))
  assert.deepEqual(await requestWrite(withBody.fetcher, '/api/x', 'POST', { project: '*' }), { message: 'ok' })
  assert.deepEqual(withBody.calls[0].init, {
    method: 'POST', headers: { 'Content-Type': 'application/json' }, body: '{"project":"*"}',
  })

  const bare = fetcherFor(answer(204, ''))
  assert.equal(await requestWrite(bare.fetcher, '/api/x/1', 'DELETE'), null)
  assert.deepEqual(bare.calls[0].init, { method: 'DELETE' })
})

test('a refused write throws, so a caller cannot report success for it', async () => {
  const { fetcher } = fetcherFor(answer(400, '{"error":"Cannot remove the owner\'s Telegram user ID (7) from the allowlist."}'))
  const error = await rejection(requestWrite(fetcher, '/api/agents/a1/telegram-users/7', 'DELETE'))
  assert.ok(error instanceof ApiError)
  assert.equal(error.status, 400)
  assert.equal(error.message, "Cannot remove the owner's Telegram user ID (7) from the allowlist.")
})

test('a write that succeeds with a body that is not JSON resolves with null', async () => {
  const { fetcher } = fetcherFor(answer(200, 'OK'))
  assert.equal(await requestWrite(fetcher, '/api/x', 'POST'), null)
})

test('ensureOk passes a 2xx response through and throws for the rest', async () => {
  const fine = answer(200, 'bytes')
  assert.equal(await ensureOk(fine), fine)
  const error = await rejection(ensureOk(answer(413, '')))
  assert.ok(error instanceof ApiError)
  assert.equal(error.status, 413)
})

test('the server\'s words: `error` first, then a ProblemDetails title, else nothing', () => {
  assert.equal(serverErrorText({ error: 'journal_not_configured', detail: 'no key' }), 'journal_not_configured')
  assert.equal(serverErrorText({ title: 'One or more validation errors occurred.', status: 400 }), 'One or more validation errors occurred.')
  assert.equal(serverErrorText({ error: '', title: 'Bad Request' }), 'Bad Request')
  assert.equal(serverErrorText({ error: 42 }), null)
  for (const body of [null, undefined, 'text', 7, []]) assert.equal(serverErrorText(body), null)
})

test('a failure with no answer is "Request failed"; an answered one keeps the server\'s words', () => {
  assert.equal(errorMessage(new ApiError(409, { error: 'Already exists' })), 'Already exists')
  assert.equal(errorMessage(new TypeError('Failed to fetch')), 'Request failed')
  assert.equal(errorMessage(new TypeError('Failed to fetch'), 'Could not load'), 'Could not load')
  assert.equal(errorMessage('nope'), 'Request failed')
})

test('a batch of writes reports how many failed and the first reason', async () => {
  const results = await Promise.allSettled([
    Promise.resolve(null),
    Promise.reject(new ApiError(400, { error: 'owner' })),
    Promise.reject(new TypeError('Failed to fetch')),
  ])
  assert.deepEqual(firstFailure(results), { failed: 2, message: 'owner' })
  assert.equal(firstFailure(await Promise.allSettled([Promise.resolve(1)])), null)
  assert.equal(firstFailure([]), null)
})
