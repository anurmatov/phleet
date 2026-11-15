import { useMemo, useState } from 'react'
import type { FormEvent, KeyboardEvent } from 'react'
import { useUsage } from '../hooks/useUsage'
import {
  DEFAULT_USAGE_FILTERS, USAGE_METRICS, USAGE_PRESETS, chartSeries, coverageState, coverageText,
  formatValue, groupKey, metricLabel, utcHourLabel, valueLabel,
} from '../usage'
import type { UsageFailure, UsageFilters, UsageGroup, UsageMetric, UsagePreset, UsageSummary } from '../usage'

const FAILURE_TEXT: Record<UsageFailure, string> = {
  disabled: 'Usage collection is off on this deployment.',
  not_configured: 'Usage collection is on but not configured: the receiver address or its token is missing or invalid.',
  unavailable: 'Usage data is unavailable right now.',
}

const time = (d: Date) => d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' })

/**
 * Per agent, model and provider token use and reported cost, by UTC hour. Recorded turns only:
 * a value is what providers reported for turns the feed stored, not a bill.
 */
export default function UsageView() {
  const [filters, setFilters] = useState<UsageFilters>(DEFAULT_USAGE_FILTERS)
  const [draft, setDraft] = useState({ agent: '', provider: '', model: '' })
  const [selected, setSelected] = useState<string | null>(null)
  const { state, reload } = useUsage(filters)
  const { data, failure, loading, fetchedAt } = state

  const groups = useMemo(() => data?.groups ?? [], [data])
  const current = groups.find(g => groupKey(g) === selected) ?? groups[0] ?? null

  const applyFilters = (e: FormEvent) => {
    e.preventDefault()
    setFilters(f => ({ ...f, agent: draft.agent.trim(), provider: draft.provider.trim(), model: draft.model.trim() }))
  }

  return (
    <div className="view-page usage-view">
      <div className="view-page-header">
        <h1 className="view-page-title">
          Usage
          {data && <span className="section-count">{data.matched_groups} group{data.matched_groups === 1 ? '' : 's'}</span>}
        </h1>
        <button className="view-page-action" onClick={reload} disabled={loading}>Refresh</button>
      </div>

      <p className="usage-note">
        Recorded turns only · best-effort · provider-reported estimate, not a bill. Reset turns are left out of every subtotal.
      </p>

      <div className="usage-controls">
        <div className="usage-presets" role="group" aria-label="Time range">
          {USAGE_PRESETS.map(p => (
            <button
              key={p.id}
              type="button"
              className={`usage-preset${filters.preset === p.id ? ' active' : ''}`}
              aria-pressed={filters.preset === p.id}
              onClick={() => setFilters(f => ({ ...f, preset: p.id as UsagePreset }))}
            >
              {p.label}
            </button>
          ))}
        </div>
        <label className="usage-field">
          <span>Metric</span>
          <select
            value={filters.metric}
            onChange={e => setFilters(f => ({ ...f, metric: e.target.value as UsageMetric }))}
          >
            {USAGE_METRICS.map(m => <option key={m.id} value={m.id}>{m.label}</option>)}
          </select>
        </label>
        <form className="usage-filters" onSubmit={applyFilters} aria-label="Exact filters">
          {(['agent', 'provider', 'model'] as const).map(key => (
            <label key={key} className="usage-field">
              <span>{key[0].toUpperCase() + key.slice(1)}</span>
              <input
                type="text"
                value={draft[key]}
                placeholder="any"
                maxLength={key === 'model' ? 128 : key === 'provider' ? 32 : 64}
                onChange={e => setDraft(d => ({ ...d, [key]: e.target.value }))}
              />
            </label>
          ))}
          <button type="submit" className="view-page-action">Apply</button>
        </form>
      </div>

      <UsageStatus failure={failure} data={data} fetchedAt={fetchedAt} loading={loading} onRetry={reload} />

      {data && data.groups.length > 0 && current && (
        <>
          <UsageChart summary={data} group={current} />
          <UsageTable summary={data} current={current} onSelect={g => setSelected(groupKey(g))} />
          {data.truncated && (
            <p className="usage-note">
              Showing the top {data.groups_shown} of {data.matched_groups} groups. Narrow the filters to see the others.
            </p>
          )}
        </>
      )}
    </div>
  )
}

function UsageStatus({ failure, data, fetchedAt, loading, onRetry }: {
  failure: UsageFailure | null
  data: UsageSummary | null
  fetchedAt: Date | null
  loading: boolean
  onRetry: () => void
}) {
  if (failure && data && fetchedAt) {
    return (
      <div className="usage-stale" role="status">
        <span>{FAILURE_TEXT[failure]} Showing the last answer, fetched at {time(fetchedAt)} ({metricLabel(data.metric)}).</span>
        <button type="button" className="view-page-action" onClick={onRetry}>Retry</button>
      </div>
    )
  }
  if (failure) {
    return (
      <div className={`view-empty usage-failure usage-failure--${failure}`} role="status">
        <span>{FAILURE_TEXT[failure]}</span>
        {failure === 'unavailable' && <button type="button" className="view-page-action" onClick={onRetry}>Retry</button>}
      </div>
    )
  }
  if (!data) return loading ? <div className="view-empty">Loading…</div> : null
  if (data.groups.length === 0) return <div className="view-empty">No recorded turns in this range.</div>
  return fetchedAt ? <p className="usage-updated">{loading ? 'Refreshing…' : `Updated ${time(fetchedAt)}`}</p> : null
}

const WIDTH = 720
const HEIGHT = 200
const PAD = { left: 8, right: 8, top: 12, bottom: 8 }

function UsageChart({ summary, group }: { summary: UsageSummary; group: UsageGroup }) {
  const series = useMemo(() => chartSeries(group, summary.from, summary.to), [group, summary.from, summary.to])
  const state = coverageState(group)
  const x = (i: number) => PAD.left + (series.slots <= 1 ? 0 : (i / (series.slots - 1)) * (WIDTH - PAD.left - PAD.right))
  const y = (v: number) => HEIGHT - PAD.bottom - (series.max === 0 ? 0 : (v / series.max) * (HEIGHT - PAD.top - PAD.bottom))
  const title = `${group.agent} · ${group.provider} · ${group.model}`
  const known = series.points.filter(p => p.value !== null).length

  return (
    <section className="card usage-chart" aria-label="Hourly chart">
      <div className="usage-chart-head">
        <h2 className="usage-chart-title">{title}</h2>
        <span className="usage-chart-value" title={group.reported_value ?? 'not reported'}>
          {valueLabel(group, summary.metric)}
        </span>
      </div>
      <p className="usage-chart-meta">{metricLabel(summary.metric)} by UTC hour · {coverageText(group)}</p>
      {state === 'not_reported' || state === 'reset_only' ? (
        <div className="usage-chart-empty">
          {state === 'reset_only'
            ? 'Only reset turns in this range: they are excluded from totals.'
            : 'The provider did not report this metric for these turns.'}
        </div>
      ) : (
        <svg
          className="usage-chart-svg"
          viewBox={`0 0 ${WIDTH} ${HEIGHT}`}
          preserveAspectRatio="none"
          role="img"
          aria-label={`${metricLabel(summary.metric)} for ${title}: ${known} of ${series.slots} hours have a reported value; peak ${formatValue(series.maxRaw, summary.metric)}.`}
        >
          <line className="usage-chart-axis" x1={PAD.left} x2={WIDTH - PAD.right} y1={HEIGHT - PAD.bottom} y2={HEIGHT - PAD.bottom} />
          {series.segments.map(seg => seg.length === 1 ? (
            <circle key={seg[0].index} className="usage-chart-dot" cx={x(seg[0].index)} cy={y(seg[0].value!)} r={3}>
              <title>{`${utcHourLabel(seg[0].start)}: ${formatValue(seg[0].raw, summary.metric)}`}</title>
            </circle>
          ) : (
            <polyline
              key={seg[0].index}
              className="usage-chart-line"
              points={seg.map(p => `${x(p.index)},${y(p.value!)}`).join(' ')}
            />
          ))}
          {series.points.filter(p => p.value !== null && p.bucket).map(p => (
            <circle key={`h${p.index}`} className="usage-chart-hit" cx={x(p.index)} cy={y(p.value!)} r={6}>
              <title>{`${utcHourLabel(p.start)}: ${formatValue(p.raw, summary.metric)} · ${coverageText(p.bucket!)}`}</title>
            </circle>
          ))}
        </svg>
      )}
      <div className="usage-chart-scale">
        <span>{utcHourLabel(summary.from)}</span>
        <span>peak {formatValue(series.maxRaw, summary.metric)}</span>
        <span>{utcHourLabel(summary.to)}</span>
      </div>
    </section>
  )
}

function UsageTable({ summary, current, onSelect }: {
  summary: UsageSummary
  current: UsageGroup
  onSelect: (g: UsageGroup) => void
}) {
  const move = (e: KeyboardEvent<HTMLTableSectionElement>) => {
    if (e.key !== 'ArrowDown' && e.key !== 'ArrowUp') return
    const buttons = Array.from(e.currentTarget.querySelectorAll<HTMLButtonElement>('button[data-group]'))
    const at = buttons.indexOf(document.activeElement as HTMLButtonElement)
    if (at === -1) return
    const next = buttons[at + (e.key === 'ArrowDown' ? 1 : -1)]
    if (!next) return
    e.preventDefault()
    next.focus()
    next.click()
  }

  return (
    <div className="table-scroll">
      <table className="data-table data-table-stack usage-table">
        <caption className="usage-table-caption">Top groups by reported {metricLabel(summary.metric).toLowerCase()}</caption>
        <thead>
          <tr>
            <th scope="col">Agent</th>
            <th scope="col">Provider</th>
            <th scope="col">Model</th>
            <th scope="col">Reported</th>
            <th scope="col">Coverage</th>
          </tr>
        </thead>
        <tbody onKeyDown={move}>
          {summary.groups.map(g => {
            const isCurrent = groupKey(g) === groupKey(current)
            return (
              <tr key={groupKey(g)} className={isCurrent ? 'usage-row selected' : 'usage-row'} onClick={() => onSelect(g)}>
                <td data-label="Agent">
                  <button type="button" className="usage-select" data-group aria-pressed={isCurrent} onClick={e => { e.stopPropagation(); onSelect(g) }}>
                    {g.agent}
                  </button>
                </td>
                <td data-label="Provider">{g.provider}</td>
                <td data-label="Model">{g.model}</td>
                <td data-label="Reported" className={`usage-value usage-value--${coverageState(g)}`} title={g.reported_value ?? 'not reported'}>
                  {valueLabel(g, summary.metric)}
                </td>
                <td data-label="Coverage">{coverageText(g)}</td>
              </tr>
            )
          })}
        </tbody>
      </table>
    </div>
  )
}
