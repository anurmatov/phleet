import { test } from 'node:test'
import assert from 'node:assert/strict'
import { createHash, randomBytes } from 'node:crypto'
import { sha256Hex } from './sha256.ts'

const utf8 = (text: string) => new TextEncoder().encode(text)

test('the published vectors', () => {
  assert.equal(sha256Hex(new Uint8Array(0)), 'e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855')
  assert.equal(sha256Hex(utf8('abc')), 'ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad')
})

test('agrees with the platform at every length around a block boundary, and on a large file', () => {
  for (const length of [1, 54, 55, 56, 57, 63, 64, 65, 119, 120, 128, 1000]) {
    const bytes = new Uint8Array(randomBytes(length))
    assert.equal(sha256Hex(bytes), createHash('sha256').update(bytes).digest('hex'), `length ${length}`)
  }
  const large = new Uint8Array(randomBytes(3 * 1024 * 1024 + 17))
  assert.equal(sha256Hex(large), createHash('sha256').update(large).digest('hex'))
})

test('a view into a larger buffer hashes only what it shows', () => {
  const whole = new Uint8Array(randomBytes(300))
  const part = whole.subarray(100, 200)
  assert.equal(sha256Hex(part), createHash('sha256').update(part).digest('hex'))
})
