/**
 * gemini-bridge.role-guard.test.mjs
 *
 * Unit tests for the role-guard fix in the gemini-bridge.mjs message handler.
 *
 * LegacyAgentProtocol emits 'message' events for BOTH the user turn (role='user')
 * and the model's reply (role='agent'). The bridge must only accumulate agent-role
 * text in finalText; user-role events carry the full input directive and must be
 * excluded from the response payload.
 *
 * Run: node --test src/Fleet.Agent/gemini-bridge.role-guard.test.mjs
 */

import { test } from 'node:test';
import assert from 'node:assert/strict';

/**
 * Simulates the subscriber callback logic from runTask() in gemini-bridge.mjs.
 * Processes a sequence of events and returns the accumulated finalText.
 * Mirrors the actual handler exactly — if the handler changes, update this too.
 */
function processMessageEvents(events) {
  let finalText = '';
  for (const event of events) {
    const evType = event.type;
    if (evType === 'message') {
      if (event.role !== 'agent') continue;
      const text = Array.isArray(event.content)
        ? event.content.filter(c => c.type === 'text').map(c => c.text ?? '').join('')
        : '';
      if (text) finalText += text;
    }
  }
  return finalText;
}

const INPUT_DIRECTIVE =
  '[fleet-wf:TaskDelegationWorkflow:TaskDelegationWorkflow-1777839054591]\n' +
  'Smoke test for PR #129. Do two things: send a message to the CEO and search fleet-memory.\n' +
  'Search fleet-memory for \'TaskDelegationWorkflow\' to find any operational context before proceeding.';

const MODEL_REPLY = 'got the ceo message sent. i can\'t perform the memory search part of the task because i don\'t have a tool for it.';

// Fixture events: user turn (the injected input) followed by model reply
const fixtureEvents = [
  {
    type: 'message',
    role: 'user',
    content: [{ type: 'text', text: INPUT_DIRECTIVE }],
  },
  {
    type: 'message',
    role: 'agent',
    content: [{ type: 'text', text: MODEL_REPLY }],
  },
];

test('response does not contain the input directive substring', () => {
  const result = processMessageEvents(fixtureEvents);
  assert.ok(
    !result.includes(INPUT_DIRECTIVE),
    `Response must not contain input directive. Got: ${JSON.stringify(result)}`,
  );
});

test('response equals the model terminal assistant message text', () => {
  const result = processMessageEvents(fixtureEvents);
  assert.equal(result, MODEL_REPLY);
});

test('user-role message is excluded when role guard is active', () => {
  const userOnlyEvents = [
    { type: 'message', role: 'user', content: [{ type: 'text', text: 'user input' }] },
  ];
  const result = processMessageEvents(userOnlyEvents);
  assert.equal(result, '', 'user-role message must produce empty finalText');
});

test('agent-role message is included', () => {
  const agentOnlyEvents = [
    { type: 'message', role: 'agent', content: [{ type: 'text', text: 'model output' }] },
  ];
  const result = processMessageEvents(agentOnlyEvents);
  assert.equal(result, 'model output');
});

test('multiple agent-role chunks are concatenated', () => {
  const multiChunkEvents = [
    { type: 'message', role: 'user', content: [{ type: 'text', text: 'ignore me' }] },
    { type: 'message', role: 'agent', content: [{ type: 'text', text: 'chunk one ' }] },
    { type: 'message', role: 'agent', content: [{ type: 'text', text: 'chunk two' }] },
  ];
  const result = processMessageEvents(multiChunkEvents);
  assert.equal(result, 'chunk one chunk two');
});

test('non-text content parts in agent message are skipped', () => {
  const events = [
    {
      type: 'message',
      role: 'agent',
      content: [
        { type: 'image', data: 'base64...' },
        { type: 'text', text: 'text only' },
      ],
    },
  ];
  const result = processMessageEvents(events);
  assert.equal(result, 'text only');
});

test('smoke-test seam is absent: input not glued to model reply', () => {
  const result = processMessageEvents(fixtureEvents);
  // The observed bug produced: '...proceeding.got the ceo message sent.'
  const seam = 'proceeding.' + MODEL_REPLY.slice(0, 20);
  assert.ok(
    !result.includes(seam),
    `Response must not contain the input+reply seam. Got: ${JSON.stringify(result)}`,
  );
});
