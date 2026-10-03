import assert from 'node:assert/strict'
import { test } from 'node:test'
import { readFileSync } from 'node:fs'
import { crossChatDisabledReason, hasSendGrant, editedTools } from './journalCrossChat.ts'
const fixture = JSON.parse(readFileSync(new URL('../../../tests/fixtures/journal-send-grant-names.json', import.meta.url), 'utf8'))
for (const row of fixture) {
  test(`canonical send grant: ${row.name}, enabled=${row.enabled}`, () => {
    assert.equal(hasSendGrant([row]), row.present)
  })
}
test('edited tools and capture determine eligibility', () => {
  const grant = [{ name: 'mcp__fleet-journal-files__send_attachment', enabled: true }]
  assert.equal(crossChatDisabledReason(false, grant), 'Needs journal capture.')
  assert.equal(crossChatDisabledReason(true, []), 'Needs the send_attachment grant.')
  assert.equal(crossChatDisabledReason(true, grant), null)
})

test('edited list preserves disabled stored rows and recognizes new grants', () => {
  const grant = 'mcp__fleet-journal-files__send_attachment'
  assert.equal(hasSendGrant(editedTools(grant, [{ toolName: grant, isEnabled: false }])), false)
  assert.equal(hasSendGrant(editedTools(grant, [])), true)
  assert.equal(hasSendGrant(editedTools('', [{ toolName: grant, isEnabled: true }])), false)
})
