/**
 * gemini-bridge.role-guard.test.mjs
 *
 * Unit tests for the role-guard logic in gemini-message-handler.mjs.
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
import {
  extractAgentMessageText,
  parseRetryDelayMs,
  DEFAULT_RETRY_DELAY_MS,
  MAX_RETRY_DELAY_MS,
} from './gemini-message-handler.mjs';

/**
 * Drives the actual extractAgentMessageText handler over a sequence of events
 * and returns the accumulated finalText — same logic as the bridge's runTask loop.
 */
function processMessageEvents(events) {
  let finalText = '';
  for (const event of events) {
    const text = extractAgentMessageText(event);
    if (text) finalText += text;
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

// ── parseRetryDelayMs ─────────────────────────────────────────────────────────

test('parseRetryDelayMs: "23s" parses to 23000', () => {
  assert.equal(parseRetryDelayMs('23s'), 23_000);
});

test('parseRetryDelayMs: "1m30s" parses to 90000', () => {
  assert.equal(parseRetryDelayMs('1m30s'), 90_000);
});

test('parseRetryDelayMs: "1m" parses to 60000', () => {
  assert.equal(parseRetryDelayMs('1m'), 60_000);
});

test('parseRetryDelayMs: "0s" is malformed — returns default', () => {
  assert.equal(parseRetryDelayMs('0s'), DEFAULT_RETRY_DELAY_MS);
});

test('parseRetryDelayMs: null returns default', () => {
  assert.equal(parseRetryDelayMs(null), DEFAULT_RETRY_DELAY_MS);
});

test('parseRetryDelayMs: undefined returns default', () => {
  assert.equal(parseRetryDelayMs(undefined), DEFAULT_RETRY_DELAY_MS);
});

test('parseRetryDelayMs: empty string returns default', () => {
  assert.equal(parseRetryDelayMs(''), DEFAULT_RETRY_DELAY_MS);
});

test('parseRetryDelayMs: malformed string returns default', () => {
  assert.equal(parseRetryDelayMs('abc'), DEFAULT_RETRY_DELAY_MS);
  assert.equal(parseRetryDelayMs('30'), DEFAULT_RETRY_DELAY_MS);
  assert.equal(parseRetryDelayMs('1h30m'), DEFAULT_RETRY_DELAY_MS);
});

test('parseRetryDelayMs: result is capped at MAX_RETRY_DELAY_MS', () => {
  // 10m = 600000ms, well over the 90s cap
  const result = parseRetryDelayMs('10m');
  assert.equal(result, MAX_RETRY_DELAY_MS);
});

test('parseRetryDelayMs: exact cap boundary "1m30s" equals MAX_RETRY_DELAY_MS', () => {
  assert.equal(parseRetryDelayMs('1m30s'), MAX_RETRY_DELAY_MS);
});
