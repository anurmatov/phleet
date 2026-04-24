import { useState, useCallback } from 'react'
import { createPortal } from 'react-dom'
import { useMemoryIdCache } from '../context/MemoryIdCacheContext'
import MemoryContentView from './MemoryContentView'

// Matches full UUID or 8-char short form
const UUID_RE = /([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})/gi
const SHORT_RE = /\b([0-9a-f]{8})\b/gi

interface Segment {
  text: string
  memoryId?: string  // full UUID if this segment is a linkified memory ref
  candidates?: string[]  // multiple full UUIDs if short-form is ambiguous
}

function splitIntoSegments(text: string, ids: Set<string>): Segment[] {
  // Full UUIDs first — always link if in cache
  const segments: Segment[] = []
  let last = 0

  const allMatches: Array<{ index: number; end: number; raw: string }> = []

  let m: RegExpExecArray | null
  UUID_RE.lastIndex = 0
  while ((m = UUID_RE.exec(text)) !== null) {
    if (ids.has(m[1].toLowerCase())) {
      allMatches.push({ index: m.index, end: UUID_RE.lastIndex, raw: m[1] })
    }
  }

  SHORT_RE.lastIndex = 0
  while ((m = SHORT_RE.exec(text)) !== null) {
    const prefix = m[1].toLowerCase()
    // Only linkify if NOT already covered by a UUID match
    const alreadyCovered = allMatches.some(x => m!.index >= x.index && m!.index < x.end)
    if (!alreadyCovered) {
      // Find all full UUIDs starting with this prefix
      const matching = [...ids].filter(id => id.startsWith(prefix))
      if (matching.length > 0) {
        allMatches.push({ index: m.index, end: SHORT_RE.lastIndex, raw: m[1] })
      }
    }
  }

  // Sort by position
  allMatches.sort((a, b) => a.index - b.index)

  for (const match of allMatches) {
    if (match.index > last) {
      segments.push({ text: text.slice(last, match.index) })
    }
    const isFullUuid = match.raw.length > 8
    if (isFullUuid) {
      segments.push({ text: match.raw, memoryId: match.raw.toLowerCase() })
    } else {
      const prefix = match.raw.toLowerCase()
      const candidates = [...ids].filter(id => id.startsWith(prefix))
      if (candidates.length === 1) {
        segments.push({ text: match.raw, memoryId: candidates[0] })
      } else {
        segments.push({ text: match.raw, candidates })
      }
    }
    last = match.end
  }

  if (last < text.length) segments.push({ text: text.slice(last) })
  return segments
}

interface MemoryModalProps {
  id: string
  onClose: () => void
}

function MemoryModal({ id, onClose }: MemoryModalProps) {
  return createPortal(
    <div className="memory-modal-overlay" onClick={e => { if (e.target === e.currentTarget) onClose() }}>
      <div className="memory-modal">
        <button className="memory-modal-close" onClick={onClose}>×</button>
        <MemoryContentView id={id} onDeleted={onClose} />
      </div>
    </div>,
    document.body
  )
}

interface CandidatePickerProps {
  candidates: string[]
  onPick: (id: string) => void
  onClose: () => void
}

function CandidatePicker({ candidates, onPick, onClose }: CandidatePickerProps) {
  return createPortal(
    <div className="memory-modal-overlay" onClick={e => { if (e.target === e.currentTarget) onClose() }}>
      <div className="memory-picker">
        <div className="memory-picker-title">Multiple memories match this ID:</div>
        {candidates.map(id => (
          <button key={id} className="memory-picker-item" onClick={() => onPick(id)}>
            {id.substring(0, 8)} — {id}
          </button>
        ))}
        <button className="memory-cv-btn" onClick={onClose}>Cancel</button>
      </div>
    </div>,
    document.body
  )
}

interface MemoryTextProps {
  children: string
}

/**
 * Renders text with memory IDs linkified. Any 8-char hex sequence or full UUID
 * that resolves to a known memory in the SPA's id cache becomes a clickable link
 * that opens a MemoryContentView modal. Non-matching hex strings stay as plain text.
 */
export default function MemoryText({ children }: MemoryTextProps) {
  const { ids } = useMemoryIdCache()
  const [openId, setOpenId] = useState<string | null>(null)
  const [pickCandidates, setPickCandidates] = useState<string[] | null>(null)

  const handleClick = useCallback((seg: Segment) => {
    if (seg.memoryId) {
      setOpenId(seg.memoryId)
    } else if (seg.candidates && seg.candidates.length > 1) {
      setPickCandidates(seg.candidates)
    }
  }, [])

  if (ids.size === 0) return <>{children}</>

  const segments = splitIntoSegments(children, ids)

  return (
    <>
      {segments.map((seg, i) =>
        seg.memoryId || (seg.candidates && seg.candidates.length > 0)
          ? <button key={i} className="memory-ref-link" onClick={() => handleClick(seg)}>{seg.text}</button>
          : <span key={i}>{seg.text}</span>
      )}
      {openId && (
        <MemoryModal id={openId} onClose={() => setOpenId(null)} />
      )}
      {pickCandidates && !openId && (
        <CandidatePicker
          candidates={pickCandidates}
          onPick={id => { setPickCandidates(null); setOpenId(id) }}
          onClose={() => setPickCandidates(null)}
        />
      )}
    </>
  )
}
