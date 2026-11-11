import { test } from 'node:test'
import assert from 'node:assert/strict'
import {
  emptyPhoneSetupForm, normalizeOrigin, phoneSetupCode, phoneSetupProblemMessage,
  type PhoneSetupForm, type PhoneSetupProblem,
} from './phoneSetup.ts'
import { qrMatrix } from './qrMatrix.ts'

const form = (over: Partial<PhoneSetupForm> = {}): PhoneSetupForm => ({
  origin: 'https://fleet.example.test', includeOperatorToken: true,
  accessClientId: '', accessClientSecret: '', ...over,
})

const codeOf = (f: PhoneSetupForm, token = 'synthetic-operator-token') => {
  const result = phoneSetupCode(f, token)
  assert.ok('code' in result, `expected a code, got ${JSON.stringify(result)}`)
  return result.code
}

const problemOf = (f: PhoneSetupForm, token = 'synthetic-operator-token') => {
  const result = phoneSetupCode(f, token)
  assert.ok('problem' in result, 'expected a problem')
  return result.problem
}

test('the form starts from the address this page was opened at, with the operator token included', () => {
  assert.deepEqual(emptyPhoneSetupForm('https://fleet.example.test'), form())
})

test('a full code carries the kind, the version and every part, in a fixed order', () => {
  const code = codeOf(form({ accessClientId: ' client-id ', accessClientSecret: ' client-secret ' }), ' synthetic-operator-token ')
  assert.equal(code,
    '{"phleet":"control-setup","v":1,"origin":"https://fleet.example.test",' +
    '"accessClientId":"client-id","accessClientSecret":"client-secret","operatorToken":"synthetic-operator-token"}')
})

test('a part that is not given is left out, never sent empty', () => {
  const code = JSON.parse(codeOf(form({ includeOperatorToken: false })))
  assert.deepEqual(Object.keys(code), ['phleet', 'v', 'origin'])
})

test('a read-only code never contains the operator token, even though the page holds one', () => {
  const code = codeOf(form({ includeOperatorToken: false }), 'synthetic-operator-token')
  assert.ok(!code.includes('synthetic-operator-token'))
})

test('half a service token is refused', () => {
  assert.equal(problemOf(form({ accessClientId: 'client-id' })), 'service_token_incomplete')
  assert.equal(problemOf(form({ accessClientSecret: 'client-secret' })), 'service_token_incomplete')
})

test('including an operator token the page does not have is refused rather than sent empty', () => {
  assert.equal(problemOf(form(), '   '), 'operator_token_missing')
  assert.ok('code' in phoneSetupCode(form({ includeOperatorToken: false }), ''))
})

test('an address is normalised to what the app stores', () => {
  assert.deepEqual(normalizeOrigin('  fleet.example.test  '), { origin: 'https://fleet.example.test' })
  assert.deepEqual(normalizeOrigin('https://fleet.example.test/'), { origin: 'https://fleet.example.test' })
  assert.deepEqual(normalizeOrigin('https://fleet.example.test:8443/console/'), { origin: 'https://fleet.example.test:8443/console' })
})

test('an insecure address is refused, not upgraded', () => {
  assert.deepEqual(normalizeOrigin('http://fleet.example.test'), { problem: 'origin_insecure' })
  assert.deepEqual(normalizeOrigin('http://localhost:3700'), { problem: 'origin_insecure' })
})

test('other address problems are named', () => {
  assert.deepEqual(normalizeOrigin('   '), { problem: 'origin_empty' })
  assert.deepEqual(normalizeOrigin('https://user:secret@fleet.example.test'), { problem: 'origin_userinfo' })
  assert.deepEqual(normalizeOrigin('https://'), { problem: 'origin_invalid' })
})

test('every problem has its own words', () => {
  const problems: PhoneSetupProblem[] = [
    'origin_empty', 'origin_invalid', 'origin_insecure', 'origin_userinfo',
    'service_token_incomplete', 'operator_token_missing',
  ]
  const messages = problems.map(phoneSetupProblemMessage)
  assert.equal(new Set(messages).size, problems.length)
  assert.ok(messages.every(m => m.length > 0))
})

test('a QR matrix is square, has the three finder patterns, and is the same for the same text', () => {
  const text = codeOf(form({ accessClientId: 'client-id', accessClientSecret: 'client-secret' }))
  const matrix = qrMatrix(text)
  const size = matrix.length

  assert.ok(size >= 21, 'a QR code is at least 21 modules wide')
  assert.equal((size - 17) % 4, 0, 'a QR code is 17 + 4n modules wide')
  assert.ok(matrix.every(row => row.length === size))

  // The outer ring of each finder pattern is dark: top-left, top-right, bottom-left.
  for (const [row, column] of [[0, 0], [0, size - 7], [size - 7, 0]]) {
    for (let i = 0; i < 7; i++) {
      assert.ok(matrix[row][column + i], 'finder top edge')
      assert.ok(matrix[row + 6][column + i], 'finder bottom edge')
      assert.ok(matrix[row + i][column], 'finder left edge')
      assert.ok(matrix[row + i][column + 6], 'finder right edge')
    }
  }
  // And the bottom-right corner has none, which is what tells a reader which way up it is.
  assert.ok(!matrix.slice(size - 7).every(row => row.slice(size - 7).every(Boolean)))

  assert.deepEqual(qrMatrix(text), matrix)
})

test('a longer code makes a larger matrix rather than failing', () => {
  const short = qrMatrix(codeOf(form({ includeOperatorToken: false })))
  const long = qrMatrix(codeOf(form({ accessClientId: 'c'.repeat(64), accessClientSecret: 's'.repeat(96) }), 't'.repeat(96)))
  assert.ok(long.length > short.length)
})
