// Run with `npm test` (node --test, Node ≥ 22.18 strips the types). Type-checked by `tsc -b`.
import { test } from 'node:test'
import assert from 'node:assert/strict'
import {
  DEFAULT_USAGE_FILTERS, chartSeries, coverageState, coverageText, formatValue, parseUsageSummary,
  usageFailure, usageQuery, usageRange, valueLabel,
} from './usage.ts'
import type { UsageGroup, UsageSummary } from './usage.ts'

const NOW = new Date('2026-01-05T10:30:15.123Z')

function group(over: Partial<UsageGroup> = {}): UsageGroup {
  return {
    agent: 'agent-a', provider: 'claude', model: 'model-x',
    reported_value: '150', eligible_turns: 3, measured_turns: 2, reset_excluded: 1,
    buckets: [], ...over,
  }
}

function summary(over: Partial<UsageSummary> = {}): UsageSummary {
  return {
    from: '2026-01-04T10:30:15.123Z', to: '2026-01-05T10:30:15.123Z', metric: 'output_tokens', bucket_seconds: 3600,
    matched_groups: 1, groups_shown: 1, truncated: false, groups: [group()], ...over,
  }
}

test('the request is UTC with milliseconds, a day or a week, and only sends filters that are set', () => {
  assert.deepEqual(usageRange('24h', NOW), { from: '2026-01-04T10:30:15.123Z', to: '2026-01-05T10:30:15.123Z' })
  assert.equal(usageRange('7d', NOW).from, '2025-12-29T10:30:15.123Z')

  const query = new URLSearchParams(usageQuery({ ...DEFAULT_USAGE_FILTERS, agent: '  agent-a ', model: '' }, NOW))
  assert.deepEqual([...query.keys()], ['from', 'to', 'metric', 'agent'])
  assert.equal(query.get('metric'), 'output_tokens')
  assert.equal(query.get('agent'), 'agent-a')
})

test('a response must have the contract shape; anything else is not data', () => {
  assert.ok(parseUsageSummary(summary()))
  assert.equal(parseUsageSummary(null), null)
  assert.equal(parseUsageSummary({ ...summary(), groups: 'x' }), null)
  assert.equal(parseUsageSummary(summary({ groups: [group({ reported_value: 12 as unknown as string })] })), null)
  assert.equal(parseUsageSummary(summary({ groups: [group({ reported_value: '-1' })] })), null)
  assert.equal(parseUsageSummary(summary({ groups: [group({ measured_turns: 1.5 })] })), null)
  assert.equal(parseUsageSummary(summary({ groups: [group({ buckets: [{ reported_value: '1', eligible_turns: 1, measured_turns: 1, reset_excluded: 0 } as never] })] })), null)
})

test('the orchestrator names disabled and misconfigured; every other failure is unavailable', () => {
  assert.equal(usageFailure(503, { error: 'usage_disabled' }), 'disabled')
  assert.equal(usageFailure(503, { error: 'usage_not_configured' }), 'not_configured')
  assert.equal(usageFailure(503, { error: 'usage_unavailable' }), 'unavailable')
  assert.equal(usageFailure(502, null), 'unavailable')
  assert.equal(usageFailure(401, { error: 'usage_disabled' }), 'unavailable')
})

test('null is not reported, a reported zero stays zero, and a partial value is labelled a subtotal', () => {
  assert.equal(formatValue(null, 'output_tokens'), 'not reported')
  assert.equal(formatValue('0', 'output_tokens'), '0')
  assert.equal(formatValue('1234567', 'output_tokens'), '1,234,567')
  assert.equal(formatValue('1234.30000001', 'cost_usd'), '$1,234.30000001')

  assert.equal(valueLabel(group({ reported_value: '0', eligible_turns: 1, measured_turns: 1 }), 'output_tokens'), '0')
  assert.equal(valueLabel(group(), 'output_tokens'), '150 (subtotal)')
  assert.equal(valueLabel(group({ reported_value: null, measured_turns: 0 }), 'output_tokens'), 'not reported')
  assert.equal(valueLabel(group({ reported_value: null, eligible_turns: 0, measured_turns: 0 }), 'output_tokens'), 'reset turns only')
})

test('coverage names measured, eligible and reset turns', () => {
  assert.equal(coverageState(group()), 'partial')
  assert.equal(coverageState(group({ measured_turns: 3 })), 'measured')
  assert.equal(coverageState(group({ measured_turns: 0 })), 'not_reported')
  assert.equal(coverageState(group({ eligible_turns: 0, measured_turns: 0 })), 'reset_only')
  assert.equal(coverageText(group()), '2 of 3 turns reported · 1 turn reset, excluded')
  assert.equal(coverageText(group({ eligible_turns: 1, measured_turns: 1, reset_excluded: 0 })), '1 of 1 turn reported')
})

test('the chart has one slot per hour and breaks the line at unknown or empty hours', () => {
  const g = group({
    buckets: [
      { start: '2026-01-05T08:00:00Z', reported_value: '10', eligible_turns: 1, measured_turns: 1, reset_excluded: 0 },
      { start: '2026-01-05T09:00:00Z', reported_value: '30', eligible_turns: 1, measured_turns: 1, reset_excluded: 0 },
      { start: '2026-01-05T10:00:00Z', reported_value: null, eligible_turns: 1, measured_turns: 0, reset_excluded: 0 },
    ],
  })

  const series = chartSeries(g, '2026-01-05T06:30:00.000Z', '2026-01-05T10:30:00.000Z')

  assert.equal(series.slots, 5)   // 06:00 … 10:00
  assert.deepEqual(series.points.map(p => p.value), [null, null, 10, 30, null])
  assert.equal(series.segments.length, 1)
  assert.deepEqual(series.segments[0].map(p => p.raw), ['10', '30'])
  assert.equal(series.maxRaw, '30')
})

test('a week starting mid-hour is 169 slots, never more', () => {
  const series = chartSeries(group(), '2026-01-05T10:30:00.000Z', '2026-01-12T10:30:00.000Z')
  assert.equal(series.slots, 169)
  assert.equal(series.maxRaw, null)
})
