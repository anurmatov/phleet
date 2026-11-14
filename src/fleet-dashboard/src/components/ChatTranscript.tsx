import { memo, useState, type ReactNode } from 'react'
import AgentMarkdown from './AgentMarkdown'
import CopyButton from './CopyButton'
import Icon from './Icon'
import { dayKey, dayLabel, statusLabel, timeLabel, type Entry, type SubmissionStatus } from '../chatTranscript'
import { attachmentContentPath, isImageAttachment, type AttachmentRef } from '../chatProtocol'
import { sizeLabel } from '../chatFiles'
import type { PendingSend } from '../chatConnection'

/** The time of day a message was written, when the conversation said. */
function Time({ at }: { at: number | null }) {
  if (at === null) return null
  return <time className="chat-time" dateTime={new Date(at).toISOString()} title={new Date(at).toLocaleString()}>{timeLabel(at)}</time>
}

/**
 * A file that went with a message. A picture is shown; anything else is a link that saves it.
 * The bytes come from the chat service on this origin, with the browser's own sign-in.
 */
function Attachment({ attachment }: { attachment: AttachmentRef }) {
  const [gone, setGone] = useState(false)
  const href = attachmentContentPath(attachment.attachmentId)
  const name = attachment.fileName || (isImageAttachment(attachment) ? 'Picture' : 'File')

  if (gone) return <span className="chat-file chat-file--gone"><Icon name="file" size={16} />{name} is no longer available</span>
  if (isImageAttachment(attachment)) {
    return (
      <a className="chat-picture" href={href} download={attachment.fileName || true} title={`Save ${name}`}>
        <img src={href} alt={name} loading="lazy" onError={() => setGone(true)} />
      </a>
    )
  }
  return (
    <a className="chat-file" href={href} download={attachment.fileName || true}>
      <Icon name="file" size={16} />
      <span className="chat-file-name">{name}</span>
      {attachment.byteSize !== null && <span className="chat-file-size">{sizeLabel(attachment.byteSize)}</span>}
    </a>
  )
}

const EntryRow = memo(function EntryRow({ entry, label }: { entry: Entry; label: string | null }) {
  if (entry.type === 'you') {
    return (
      <div className="chat-row chat-row--you">
        {entry.attachments.length > 0 && (
          <div className="chat-attachments">
            {entry.attachments.map(a => <Attachment key={a.attachmentId} attachment={a} />)}
          </div>
        )}
        {entry.text !== '' && (
          <div className="chat-bubble chat-bubble--you">
            <span className="chat-sr-only">You: </span>{entry.text}
          </div>
        )}
        {(entry.at !== null || label) && (
          <div className="chat-meta"><Time at={entry.at} />{label && <span>{label}</span>}</div>
        )}
      </div>
    )
  }
  if (entry.type === 'agent') {
    const empty = entry.text.trim() === ''
    return (
      <div className="chat-row chat-row--agent">
        {!empty && (
          <div className={`chat-bubble chat-bubble--agent chat-markdown${entry.interim ? ' chat-bubble--interim' : ''}`}>
            <span className="chat-sr-only">Agent: </span>
            <AgentMarkdown text={entry.text} copyCode />
          </div>
        )}
        {(entry.at !== null || entry.note || !empty) && (
          <div className="chat-meta">
            <Time at={entry.at} />
            {entry.note && <span>{entry.note}</span>}
            {!empty && <CopyButton text={entry.text} label="Copy reply" />}
          </div>
        )}
      </div>
    )
  }
  return <div className={`chat-system chat-system--${entry.tone}`}>{entry.text}</div>
})

const PendingRow = memo(function PendingRow({ pending, onRetry, onDiscard }: {
  pending: PendingSend
  onRetry: (submissionId: string) => void
  onDiscard: (submissionId: string) => void
}) {
  const uploading = pending.files.some(f => f.attachmentId === null)
  return (
    <div className="chat-row chat-row--you">
      {pending.files.length > 0 && (
        <div className="chat-attachments">
          {pending.files.map(f => (f.previewUrl
            ? <span key={f.id} className="chat-picture chat-picture--pending"><img src={f.previewUrl} alt={f.fileName} /></span>
            : <span key={f.id} className="chat-file"><Icon name="file" size={16} /><span className="chat-file-name">{f.fileName}</span></span>))}
        </div>
      )}
      {pending.text !== '' && (
        <div className={`chat-bubble chat-bubble--you${pending.status === 'failed' ? ' chat-bubble--failed' : ''}`}>
          <span className="chat-sr-only">You: </span>{pending.text}
        </div>
      )}
      {pending.status === 'sending' && <div className="chat-meta">{uploading ? 'Uploading…' : 'Sending…'}</div>}
      {pending.status === 'sent' && <div className="chat-meta">Sent</div>}
      {pending.status === 'failed' && (
        <div className="chat-meta chat-meta--error" role="alert">
          <span>{pending.problem}</span>
          {pending.canRetry && (
            <button type="button" className="chat-link-btn" onClick={() => onRetry(pending.submissionId)}>Retry</button>
          )}
          <button type="button" className="chat-link-btn" onClick={() => onDiscard(pending.submissionId)}>Discard</button>
        </div>
      )}
    </div>
  )
})

/**
 * The thread: what the conversation holds, in order, then what this page sent that the
 * conversation does not show yet. The person's own words are plain text; only the agent's replies
 * are read as Markdown. A line names the day wherever the day changes.
 */
export default function ChatTranscript({ entries, status, pending, onRetry, onDiscard, now = Date.now() }: {
  entries: Entry[]
  status: Record<string, SubmissionStatus>
  pending: PendingSend[]
  onRetry: (submissionId: string) => void
  onDiscard: (submissionId: string) => void
  /** The moment "Today" is judged from. */
  now?: number
}) {
  const rows: ReactNode[] = []
  let day: number | null = null
  for (const entry of entries) {
    if (entry.at !== null && dayKey(entry.at) !== day) {
      day = dayKey(entry.at)
      rows.push(<div key={`day:${entry.key}`} className="chat-day" role="separator"><span>{dayLabel(entry.at, now)}</span></div>)
    }
    rows.push(
      <EntryRow key={entry.key} entry={entry}
        label={entry.type === 'you' ? statusLabel(status[entry.submissionId]) : null} />,
    )
  }
  return (
    <>
      {rows}
      {pending.map(p => (
        <PendingRow key={`pending:${p.submissionId}`} pending={p} onRetry={onRetry} onDiscard={onDiscard} />
      ))}
    </>
  )
}
