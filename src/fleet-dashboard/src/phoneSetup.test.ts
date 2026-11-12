import { test } from 'node:test'
import assert from 'node:assert/strict'
import {
  emptyPhoneSetupForm, normalizeOrigin, phoneSetupCode, phoneSetupProblemMessage,
  type PhoneSetupForm, type PhoneSetupProblem,
} from './phoneSetup.ts'
import qrcode from 'qrcode-generator'
import { QR_MAX_BYTES, qrMatrix } from './qrMatrix.ts'

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
    'service_token_incomplete', 'operator_token_missing', 'code_too_long',
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

test('a credential outside ASCII reaches the phone exactly, in a code that is plain ASCII', () => {
  const secret = 'секрет-ünïcödé-\u{1F511}'
  const code = codeOf(form({ accessClientId: 'клиент', accessClientSecret: secret }), 'тест')

  // Plain ASCII is the same bytes whichever character set a scanner assumes.
  assert.ok([...code].every(c => c.charCodeAt(0) < 0x80), 'every character of the code is ASCII')
  const parsed = JSON.parse(code)
  assert.equal(parsed.operatorToken, 'тест')
  assert.equal(parsed.accessClientId, 'клиент')
  assert.equal(parsed.accessClientSecret, secret)
})

test('different credentials never draw the same QR code', () => {
  // Every character of 'тест' has the same low byte as the matching one of 'B5AB'. Written as
  // low bytes, the two tokens drew identical codes.
  assert.notDeepEqual(qrMatrix(codeOf(form(), 'тест')), qrMatrix(codeOf(form(), 'B5AB')))
  assert.notDeepEqual(qrMatrix('тест'), qrMatrix('B5AB'))
})

test('the QR code carries the UTF-8 bytes of its text', () => {
  // 'é' is C3 A9 in UTF-8. Handed straight to the library, the two characters whose low bytes
  // those are draw the code with exactly those bytes. This checks the bytes written, not a scan.
  const direct = qrcode(0, 'M')
  direct.addData('\u00c3\u00a9', 'Byte')
  direct.make()
  const size = direct.getModuleCount()
  const bytes = Array.from({ length: size }, (_, row) =>
    Array.from({ length: size }, (_, column) => direct.isDark(row, column)))
  assert.deepEqual(qrMatrix('é'), bytes)
})

test('a code too long for one QR code is a problem with the form, not a failure to draw', () => {
  for (const length of [4_000, 10_000]) {
    assert.equal(problemOf(form({ accessClientId: 'client-id', accessClientSecret: 's'.repeat(length) })), 'code_too_long')
  }
  // Escaped characters count at their escaped length, which is what the code carries.
  assert.equal(problemOf(form(), 'т'.repeat(QR_MAX_BYTES / 6 + 1)), 'code_too_long')
})

test('the longest code accepted can be drawn, and one byte more is refused', () => {
  const base = codeOf(form({ accessClientId: 'client-id', accessClientSecret: 's' }))
  const longest = 's'.repeat(1 + QR_MAX_BYTES - base.length)
  const code = codeOf(form({ accessClientId: 'client-id', accessClientSecret: longest }))

  assert.equal(code.length, QR_MAX_BYTES)
  assert.equal(qrMatrix(code).length, 177, 'version 40, the largest QR code')
  assert.equal(problemOf(form({ accessClientId: 'client-id', accessClientSecret: `${longest}s` })), 'code_too_long')
})
