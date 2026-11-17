import { memo, useEffect, useMemo, useRef, useState, type CSSProperties, type MouseEvent } from 'react'
import {
  compareTexts, expand, layOut, EXPAND_STEP,
  type CompareFormat, type CompareResult, type DiffRow, type Expansion, type Section,
} from '../versionDiff'
import { againstCurrent, changesOf, samePair, withSide, type ComparedVersion, type VersionPair } from '../versionHistory'
import Modal from './Modal'
import Icon from './Icon'

interface VersionCompareProps {
  /** Whose versions these are: the instruction's, the project context's or the definition's name. */
  name: string
  /** Every version there is, in any order. */
  versions: ComparedVersion[]
  /** The number of the version in use. */
  current: number
  /** The version chosen in the history: the comparison opens on what that version changed. */
  chosen: number
  /** `json` lays both sides out as JSON before comparing them. */
  format: CompareFormat
  onClose: () => void
}

/**
 * Runs one comparison and records it on the page's performance timeline as `version-compare`, so
 * how long it took, and how often it ran, can be read in the browser's tools (and by the tests).
 */
function timed(compare: () => CompareResult): CompareResult {
  const start = performance.now()
  const result = compare()
  try { performance.measure('version-compare', { start, end: performance.now() }) } catch { /* a browser without it */ }
  return result
}

const versionLabel = (version: number | null) => (version === null ? 'nothing' : `v${version}`)

/**
 * Two versions of one text, compared: what was added and removed, folded to the changes with a
 * few lines around each. It opens over the page on what the chosen version changed, and either
 * side can then be any version.
 */
export default function VersionCompare({ name, versions, current, chosen, format, onClose }: VersionCompareProps) {
  const numbers = versions.map(v => v.number).sort((a, b) => b - a)
  const opening = changesOf(numbers, chosen)
  const versusCurrent = againstCurrent(chosen, current)
  const [pair, setPair] = useState<VersionPair>(opening)
  const [opened, setOpened] = useState<Record<number, Expansion>>({})
  /** The change the steps have reached, or -1 before the first step. */
  const [at, setAt] = useState(-1)
  const scroller = useRef<HTMLDivElement>(null)

  const older = versions.find(v => v.number === pair.older) ?? null
  const newer = versions.find(v => v.number === pair.newer) ?? null
  const olderContent = older?.content ?? null
  const newerContent = newer?.content ?? ''
  // Keyed on the two texts themselves: a redraw of the page behind (a keystroke in its editor, a
  // heartbeat, the clock) hands this component new arrays and the same strings, and computes nothing.
  const result = useMemo(() => timed(() => compareTexts(olderContent, newerContent, format)), [olderContent, newerContent, format])
  const sections = useMemo(() => (result.ok ? layOut(result, opened) : []), [result, opened])
  /** For each row, the change it belongs to; -1 for an unchanged row. */
  const changeOf = useMemo(() => {
    const changes = new Int32Array(result.ok ? result.rows.length : 0).fill(-1)
    if (result.ok) result.blocks.forEach((block, b) => changes.fill(b, block.start, block.end))
    return changes
  }, [result])

  useEffect(() => {
    if (at >= 0) scroller.current?.querySelector(`[data-change="${at}"]`)?.scrollIntoView({ block: 'start' })
  }, [at])

  if (!newer) return null

  function show(next: VersionPair) {
    setPair(next)
    setOpened({})
    setAt(-1)
    scroller.current?.scrollTo({ top: 0 })
  }

  const changes = result.ok ? result.blocks.length : 0
  const digits = result.ok ? String(result.rows.length).length : 1

  return (
    <Modal label={`Compare versions of ${name}`} overlayClassName="wf-detail-overlay" className="wf-detail-modal vc-modal" onClose={onClose}>
      <div className="wf-detail-header">
        <span className="wf-detail-title">
          Compare versions
          <span className="wf-detail-id">{name}</span>
        </span>
        <div className="wf-detail-header-actions">
          <button className="icon-btn" onClick={onClose} title="Close" aria-label="Close"><Icon name="close" /></button>
        </div>
      </div>

      <div className="vc-head">
        <div className="vc-pair">
          <Side
            label="Older" version={older} value={pair.older} numbers={numbers} current={current} nothing
            onChange={version => show(withSide(pair, 'older', version))}
          />
          <span className="vc-arrow" aria-hidden="true"><Icon name="chevronRight" size={16} /></span>
          <Side
            label="Newer" version={newer} value={pair.newer} numbers={numbers} current={current}
            onChange={version => show(withSide(pair, 'newer', version))}
          />
        </div>

        <div className="vc-bar">
          <div className="seg vc-presets" role="group" aria-label={`Compare v${chosen}`}>
            <button className={`seg-btn${samePair(pair, opening) ? ' active' : ''}`} aria-pressed={samePair(pair, opening)} onClick={() => show(opening)}>
              What v{chosen} changed
            </button>
            <button
              className={`seg-btn${samePair(pair, versusCurrent) ? ' active' : ''}`}
              aria-pressed={samePair(pair, versusCurrent)}
              disabled={!versusCurrent}
              title={versusCurrent ? undefined : `v${chosen} is the current version`}
              onClick={() => versusCurrent && show(versusCurrent)}
            >
              Against current
            </button>
          </div>

          <div className="vc-summary" role="status">
            <span className="vc-summary-pair">{versionLabel(pair.older)} → {versionLabel(pair.newer)}</span>
            {result.ok && (
              <>
                <span className="vc-count-add" title={`${result.added} line${result.added === 1 ? '' : 's'} added`}>+{result.added}</span>
                <span className="vc-count-remove" title={`${result.removed} line${result.removed === 1 ? '' : 's'} removed`}>-{result.removed}</span>
                <span className="vc-summary-changes">
                  {at >= 0 ? `Change ${at + 1} of ${changes}` : `${changes} change${changes === 1 ? '' : 's'}`}
                </span>
              </>
            )}
          </div>

          <div className="vc-steps">
            <button className="icon-btn icon-btn-s" disabled={at <= 0} onClick={() => setAt(at - 1)} title="Previous change" aria-label="Previous change">
              <Icon name="chevronUp" size={16} />
            </button>
            <button className="icon-btn icon-btn-s" disabled={at >= changes - 1} onClick={() => setAt(at + 1)} title="Next change" aria-label="Next change">
              <Icon name="chevronDown" size={16} />
            </button>
          </div>
        </div>
      </div>

      <div
        className={`vc-body${result.ok && result.layout === 'json' ? ' vc-body-json' : ''}`}
        ref={scroller} tabIndex={0} role="region" aria-label="Changes"
        style={{ '--vc-digits': digits } as CSSProperties}
      >
        {!result.ok && (
          <div className="vc-note vc-note-warn">
            These two versions are too large or too different to compare here. Choose a closer pair of versions.
          </div>
        )}
        {result.ok && result.notJson.length > 0 && (
          <div className="vc-note vc-note-warn">
            {result.notJson.map(side => versionLabel(side === 'older' ? pair.older : pair.newer)).join(' and ')}
            {result.notJson.length === 1 ? ' is' : ' are'} not valid JSON, so the two versions are compared as plain text.
          </div>
        )}
        {result.ok && changes === 0 && (
          <div className="vc-note">
            {pair.older === pair.newer
              ? `${versionLabel(pair.newer)} is on both sides: there is nothing to compare.`
              : result.onlySpacing
                ? 'No line differs. The two versions differ only in spacing or line endings.'
                : `No differences: ${versionLabel(pair.older)} and ${versionLabel(pair.newer)} have the same content.`}
          </div>
        )}
        {result.ok && sections.flatMap(section => {
          if (section.kind === 'fold') {
            return [<Fold key={`fold-${section.id}`} section={section} onExpand={where => setOpened(o => expand(o, section.id, where))} />]
          }
          return result.rows.slice(section.start, section.end).map((row, offset) => {
            const index = section.start + offset
            const change = changeOf[index]
            return (
              <Row
                key={index} row={row}
                change={change >= 0 && result.blocks[change].start === index ? change : undefined}
                marked={change >= 0 && change === at}
              />
            )
          })
        })}
      </div>
    </Modal>
  )
}

interface SideProps {
  label: 'Older' | 'Newer'
  version: ComparedVersion | null
  value: number | null
  /** Every version's number, newest first. */
  numbers: number[]
  current: number
  /** Whether this side can be "nothing". */
  nothing?: boolean
  onChange: (version: number | null) => void
}

/** One side of the comparison: which version it is, and when, by whom and why that version was written. */
function Side({ label, version, value, numbers, current, nothing, onChange }: SideProps) {
  const facts = [version?.when, version?.by].filter(Boolean).join(' · ')
  return (
    <div className="vc-side">
      <label className="vc-side-pick">
        <span className="vc-side-label">{label}</span>
        <select
          className="config-input vc-select"
          aria-label={`${label} version`}
          value={value === null ? '' : String(value)}
          onChange={e => onChange(e.target.value === '' ? null : Number(e.target.value))}
        >
          {nothing && <option value="">nothing</option>}
          {numbers.map(n => <option key={n} value={n}>v{n}{n === current ? ' (current)' : ''}</option>)}
        </select>
      </label>
      <div className="vc-side-facts">
        {version ? facts : 'Before the first version: every line shows as added.'}
      </div>
      {version?.reason && <div className="vc-side-reason" title={version.reason}>{version.reason}</div>}
    </div>
  )
}

/** A stretch of unchanged lines, hidden: how many, and the ways to show more of them. */
function Fold({ section, onExpand }: { section: Extract<Section, { kind: 'fold' }>; onExpand: (where: 'head' | 'tail' | 'all') => void }) {
  const hidden = section.end - section.start
  // A step that would show everything is the same press as "show all".
  const step = hidden > EXPAND_STEP ? EXPAND_STEP : 0
  return (
    <div className="vc-fold">
      <span className="vc-fold-count">{hidden} unchanged line{hidden === 1 ? '' : 's'}</span>
      {step > 0 && section.canHead && (
        <button className="vc-fold-btn" onClick={() => onExpand('head')} aria-label={`Show the first ${step} of ${hidden} unchanged lines`}>
          First {step}
        </button>
      )}
      {step > 0 && section.canTail && (
        <button className="vc-fold-btn" onClick={() => onExpand('tail')} aria-label={`Show the last ${step} of ${hidden} unchanged lines`}>
          Last {step}
        </button>
      )}
      <button className="vc-fold-btn" onClick={() => onExpand('all')} aria-label={`Show all ${hidden} unchanged lines`}>
        Show all
      </button>
    </div>
  )
}

interface RowProps {
  row: DiffRow
  /** Set on the first row of a change: which change, for the steps to find. */
  change: number | undefined
  /** Whether the steps stand on this row's change. */
  marked: boolean
}

const MARKS = { same: ' ', add: '+', remove: '-' } as const
const TAGS = { same: 'div', add: 'ins', remove: 'del' } as const

/** One line. Added and removed lines carry a `+` or `-` as well as their colour, and are `ins` and `del` to a screen reader. */
const Row = memo(function Row({ row, change, marked }: RowProps) {
  const Tag = TAGS[row.kind]
  const lead = row.text.slice(0, row.indent)
  // A tab is four columns here, as the stylesheet draws it.
  const columns = row.indent + 3 * (lead.split('\t').length - 1)
  return (
    <Tag className={`vc-row vc-${row.kind}${marked ? ' vc-at' : ''}`} data-change={change}>
      <span className="vc-no" aria-hidden="true">{row.oldNo ?? ''}</span>
      <span className="vc-no" aria-hidden="true">{row.newNo ?? ''}</span>
      <span className="vc-mark">{MARKS[row.kind]}</span>
      <span className="vc-text" style={{ '--vc-lead': columns } as CSSProperties}>
        {row.indent > 0 && <span className="vc-lead">{lead}</span>}
        {row.parts
          ? row.parts.map((part, p) => (part.changed ? <mark key={p} className="vc-word">{part.text}</mark> : part.text))
          : row.text.slice(row.indent)}
      </span>
    </Tag>
  )
})

/** In a history row: opens the comparison on that row's version. */
export function CompareButton({ version, onOpen }: { version: number; onOpen: () => void }) {
  return (
    <button
      className="instr-compare-btn"
      aria-label={`Compare v${version}`}
      // Not every browser gives a pressed button the focus; the dialog returns it here on closing.
      onClick={e => { e.stopPropagation(); e.currentTarget.focus({ preventScroll: true }); onOpen() }}
    >
      compare
    </button>
  )
}

/**
 * A press anywhere else on a history row does what its compare button does. The button takes the
 * focus first, so closing the comparison puts it back on the row it was opened from.
 */
export function openFromRow(e: MouseEvent<HTMLElement>, open: () => void) {
  e.currentTarget.querySelector<HTMLButtonElement>('.instr-compare-btn')?.focus({ preventScroll: true })
  open()
}
